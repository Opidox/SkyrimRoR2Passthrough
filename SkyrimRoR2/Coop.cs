using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;
using UnityEngine.Networking;

namespace SkyrimRoR2
{
    // 0.70 : la coop (jusqu'à 4 joueurs).
    //
    // Chaque joueur a son Skyrim et son RoR2. Les RoR2 se rejoignent dans une vraie partie coop de RoR2 (l'hôte lance
    // une partie ouverte, les invités s'y connectent par Steam) : RoR2 fait tout le réseau (survivants, tirs, objets,
    // or). Ce fichier ajoute ce que RoR2 ne sait pas faire :
    //  - les LIEUX : les joueurs d'un même lieu de Skyrim (même cellule, ou même monde dehors) ont le même calage dans
    //    RoR2 ; deux lieux différents sont placés loin l'un de l'autre sur la carte de RoR2 (l'hôte tient la table) ;
    //  - les ENNEMIS : l'hôte réunit les ennemis de tous les Skyrim. Un ennemi vu par plusieurs joueurs n'a qu'un double,
    //    placé par le joueur le plus proche ; les autres Skyrim le mettent à la place du double ; les dégâts vont à tous
    //    les Skyrim qui le voient (l'expérience seulement à celui qui frappe) ;
    //  - ce que les invités ne peuvent pas faire eux-mêmes (c'est l'hôte qui décide dans RoR2) : coups reçus dans
    //    Skyrim, objets gagnés, objets portés, changement de survivant, réveil après une mort ;
    //  - la MORT : un joueur qui tombe près d'un coéquipier reste à terre ; rester près de lui le relève. Seul, ou si
    //    tout le monde tombe : comme en solo (réveil au lit, objets portés perdus) ;
    //  - le COMMUN : une seule difficulté (celle de l'hôte), la charge du coffre du boss, les contenants ouverts.
    // Sans coop (réglage Mode = Solo, ou hôte sans invité), rien de tout ça ne tourne : le jeu marche comme avant.
    internal sealed class Coop
    {
        public const short MsgType = 21731;
        private const string Section = "8 - Coop";
        private const float ReportPeriod = 0.1f;   // s entre deux rapports d'un invité
        private const float PushPeriod = 0.05f;    // s entre deux envois des places d'ennemis à un invité
        private const float StatePeriod = 0.25f;   // s entre deux états envoyés à un invité
        private const float PlacesPeriod = 5f;     // s entre deux envois de la table des lieux
        private const int ActorsPerChunk = 16;
        private const int PushesPerChunk = 60;

        public static Coop Instance { get; private set; }

        private readonly ManualLogSource _log;
        private readonly ConfigEntry<string> _mode;
        private readonly ConfigEntry<string> _hostId;
        private readonly ConfigEntry<int> _waitPlayers;
        private readonly ConfigEntry<int> _waitSeconds;
        private readonly ConfigEntry<float> _reviveDistance;
        private readonly ConfigEntry<float> _reviveSeconds;
        private readonly ConfigEntry<float> _helpRange;
        private readonly ConfigEntry<float> _alonePatience;
        private readonly CoopZone _zone;

        public Coop(ManualLogSource log, ConfigFile config)
        {
            _log = log;
            Instance = this;
            _mode = config.Bind(Section, "Mode", "Solo",
                "Solo: play alone. Hote (host): you host the co-op run and your friends join you. Invite (guest): you join a friend's run (put their Steam ID in IdentifiantHote).");
            _hostId = config.Bind(Section, "IdentifiantHote", "",
                "Guest only: the host's Steam ID (SteamID64, 17 digits). The host finds it in their RoR2 log (line \"Coop : ton identifiant Steam\") or in the address of their Steam profile.");
            _waitPlayers = config.Bind(Section, "JoueursAttendus", 2,
                "Host only: the run starts when this many players (you included) are in the lobby...");
            _waitSeconds = config.Bind(Section, "AttenteMax", 120,
                "...or after this many seconds (latecomers can try to join during the run).");
            _reviveDistance = config.Bind(Section, "DistanceReleve", 2.5f, "Distance (m) to stay within from a downed teammate to revive them.");
            _reviveSeconds = config.Bind(Section, "DureeReleve", 7f, "Time (s) to stay near them to revive them.");
            _helpRange = config.Bind(Section, "DistanceAide", 40f,
                "A player who falls within this distance (m) of a standing teammate stays down (they can be revived). Further away it works like solo: wake up in bed, carried items lost.");
            _alonePatience = config.Bind(Section, "AttenteSeul", 45f,
                "Downed with no teammate within DistanceAide for this long (s): they wake up like in solo.");
            _zone = new CoopZone(log);
        }

        // ---------- Rôles ----------

        private int ModeValue
        {
            get
            {
                string mode = (_mode.Value ?? "").Trim().ToLowerInvariant();
                if (mode.StartsWith("h")) return 1;   // Hote, Host
                if (mode.StartsWith("i") || mode.StartsWith("j") || mode.StartsWith("c")) return 2;  // Invite, Join, Client
                return 0;
            }
        }

        public bool WantsHost => ModeValue == 1;
        public bool WantsJoin => ModeValue == 2;
        public bool Enabled => ModeValue != 0;
        public string HostId => (_hostId.Value ?? "").Trim();
        public int WaitPlayers => Mathf.Clamp(_waitPlayers.Value, 1, 4);
        public float WaitSeconds => Mathf.Clamp(_waitSeconds.Value, 5, 1800);

        private static NetworkClient Client => NetworkManager.singleton != null ? NetworkManager.singleton.client : null;
        public static bool IsServer => NetworkServer.active;
        public bool IsClient => Enabled && !NetworkServer.active && Client != null && Client.isConnected;
        public bool HostWithPeers => Enabled && NetworkServer.active && _peers.Count > 0;
        // La coop tourne : hôte avec au moins un invité, ou invité connecté, pendant une partie.
        public bool Active => Run.instance != null && (HostWithPeers || IsClient);
        public bool ClientActive => Run.instance != null && IsClient;

        // ---------- Les joueurs (côté hôte) ----------

        private sealed class Peer
        {
            public int Key;                  // 0 : l'hôte ; sinon le numéro de connexion
            public bool Local;
            public NetworkConnection Conn;
            public NetworkUser User;
            public string Name = "?";
            public bool Announced;
            public float CreatedAt = Time.unscaledTime;
            public CharacterMaster Master;
            public CharacterBody Body;
            // Son Skyrim (rapports)
            public uint Place;
            public bool Interior;
            public Vector3 Origin;
            public bool HasOrigin;
            public float LastReport = -100f;
            public ushort ReportFrame;
            public int SkyrimLevel;
            public float BonusHealth;
            public uint ChargeChest;
            public float ChargeProgress;
            public readonly GhostManager.Source Source = new GhostManager.Source();
            // Vie en plus appliquée
            public CharacterBody BonusBody;
            public float BonusApplied;
            // Mort
            public int State;                // 0 debout, 1 à terre, 2 tombé (comme en solo)
            public Vector3 DownAt;
            public float Progress;
            public float AloneSince = -1f;
            public Peer Helper;              // qui le relève en ce moment
            public uint ReviveSeen, RequestSeen;  // (hôte) compteurs de son Skyrim quand il est tombé
            // Envois
            public float NextPush, NextState;
            public ushort PushFrame;
            public readonly Dictionary<uint, List<byte[]>> Inventory = new Dictionary<uint, List<byte[]>>();
        }

        private readonly Dictionary<int, Peer> _peers = new Dictionary<int, Peer>();
        private readonly Peer _local = new Peer { Key = 0, Local = true };
        private readonly List<Peer> _all = new List<Peer>();
        private readonly List<GhostManager.Source> _sources = new List<GhostManager.Source>();

        private Peer PeerOf(NetworkConnection conn)
        {
            if (conn == null) return null;
            if (_peers.TryGetValue(conn.connectionId, out Peer peer)) return peer;
            peer = new Peer { Key = conn.connectionId, Conn = conn };
            peer.Source.Key = peer.Key;
            peer.Source.Local = false;
            _peers[conn.connectionId] = peer;
            return peer;
        }

        // ---------- Les lieux ----------

        private sealed class PlaceEntry
        {
            public uint Place;
            public bool Interior;
            public Vector3 Offset;
            public Vector3 Center;
        }

        private readonly Dictionary<uint, PlaceEntry> _places = new Dictionary<uint, PlaceEntry>();
        private readonly Dictionary<uint, float> _claimed = new Dictionary<uint, float>();
        private bool _placesDirty;
        private float _nextPlaces;

        // Règle de calage pour TerrainMirror : le calage commun du lieu, ou null (pas encore connu : on garde le naturel).
        public Vector3? OffsetFor(uint place, bool interior, Vector3 natural, Vector3 feet)
        {
            if (place == 0) return null;
            if (_places.TryGetValue(place, out PlaceEntry entry)) return entry.Offset;
            if (NetworkServer.active) return Assign(place, interior, natural, feet).Offset;
            float now = Time.unscaledTime;
            if (!_claimed.TryGetValue(place, out float last) || now - last > 2f)
            {
                _claimed[place] = now;
                BinaryWriter w = Packet.Begin(CoopKind.Claim, out MemoryStream stream);
                w.Write(place); w.Write(interior); Packet.Vector(w, natural); Packet.Vector(w, feet);
                SendToServer(stream);
            }
            return null;
        }

        // L'hôte donne un calage à un nouveau lieu : le calage naturel du premier joueur qui y entre, décalé si ce lieu
        // tomberait sur un autre (deux lieux différents ne se mélangent jamais dans RoR2).
        private PlaceEntry Assign(uint place, bool interior, Vector3 natural, Vector3 feet)
        {
            if (_places.TryGetValue(place, out PlaceEntry known)) return known;
            Vector3 offset = natural;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Vector3 center = feet + (offset - natural);
                bool clash = false;
                foreach (PlaceEntry other in _places.Values)
                {
                    float radius = other.Interior && interior ? 3000f : 12000f;
                    if (new Vector2(other.Center.x - center.x, other.Center.z - center.z).magnitude < radius) { clash = true; break; }
                }
                if (!clash) break;
                offset.x += 15000f;
            }
            var entry = new PlaceEntry { Place = place, Interior = interior, Offset = offset, Center = feet + (offset - natural) };
            _places[place] = entry;
            _placesDirty = true;
            _log.LogInfo($"Coop : lieu {place:X8} ({(interior ? "interieur" : "exterieur")}) cale dans RoR2" +
                         ((offset - natural).sqrMagnitude > 1f ? $", decale de {(offset - natural).magnitude:F0} m (un autre lieu etait la)" : ""));
            return entry;
        }

        private void SendPlaces(Peer only)
        {
            BinaryWriter w = Packet.Begin(CoopKind.Places, out MemoryStream stream);
            w.Write((ushort)_places.Count);
            foreach (PlaceEntry e in _places.Values)
            {
                w.Write(e.Place); w.Write(e.Interior); Packet.Vector(w, e.Offset); Packet.Vector(w, e.Center);
            }
            if (only != null) SendTo(only, stream);
            else foreach (Peer peer in _peers.Values) SendTo(peer, stream);
        }

        // ---------- Réseau ----------

        private NetworkClient _registeredClient;
        private bool _serverRegistered;

        private void RegisterHandlers()
        {
            try
            {
                if (NetworkServer.active)
                {
                    if (!_serverRegistered || !NetworkServer.handlers.ContainsKey(MsgType))
                    {
                        NetworkServer.RegisterHandler(MsgType, OnServerMessage);
                        _serverRegistered = true;
                        _log.LogInfo("Coop : messages des invites ecoutes");
                    }
                }
                else _serverRegistered = false;
                NetworkClient client = Client;
                if (client != null && !NetworkServer.active && client != _registeredClient)
                {
                    client.RegisterHandler(MsgType, OnClientMessage);
                    _registeredClient = client;
                    _log.LogInfo("Coop : messages de l'hote ecoutes");
                }
            }
            catch (Exception e)
            {
                if (_netErrors++ < 5) _log.LogWarning("Coop : ecoute impossible (" + e.Message + ")");
            }
        }

        private int _netErrors;

        private void SendToServer(MemoryStream stream)
        {
            NetworkClient client = Client;
            if (client == null || !client.isConnected) return;
            try { client.Send(MsgType, new CoopMessage(stream.ToArray())); }
            catch (Exception e) { if (_netErrors++ < 10) _log.LogWarning("Coop : envoi a l'hote impossible (" + e.Message + ")"); }
        }

        private void SendTo(Peer peer, MemoryStream stream)
        {
            if (peer == null || peer.Local || peer.Conn == null) return;
            try { peer.Conn.Send(MsgType, new CoopMessage(stream.ToArray())); }
            catch (Exception e) { if (_netErrors++ < 10) _log.LogWarning("Coop : envoi a " + peer.Name + " impossible (" + e.Message + ")"); }
        }

        private void OnServerMessage(NetworkMessage message)
        {
            try
            {
                CoopMessage packet = message.ReadMessage<CoopMessage>();
                if (packet?.Data == null || packet.Data.Length == 0) return;
                Peer peer = PeerOf(message.conn);
                using (var r = new BinaryReader(new MemoryStream(packet.Data), Encoding.UTF8))
                    HandleFromClient(peer, (CoopKind)r.ReadByte(), r);
            }
            catch (Exception e)
            {
                if (_netErrors++ < 20) _log.LogWarning("Coop : message d'un invite illisible (" + e.Message + ")");
            }
        }

        private void OnClientMessage(NetworkMessage message)
        {
            try
            {
                CoopMessage packet = message.ReadMessage<CoopMessage>();
                if (packet?.Data == null || packet.Data.Length == 0) return;
                using (var r = new BinaryReader(new MemoryStream(packet.Data), Encoding.UTF8))
                    HandleFromHost((CoopKind)r.ReadByte(), r);
            }
            catch (Exception e)
            {
                if (_netErrors++ < 20) _log.LogWarning("Coop : message de l'hote illisible (" + e.Message + ")");
            }
        }

        // ---------- Côté hôte : messages des invités ----------

        private void HandleFromClient(Peer peer, CoopKind kind, BinaryReader r)
        {
            switch (kind)
            {
                case CoopKind.Report:
                    {
                        ushort frame = r.ReadUInt16();
                        int chunk = r.ReadByte();
                        int chunks = r.ReadByte();
                        peer.Place = r.ReadUInt32();
                        peer.Interior = r.ReadBoolean();
                        peer.Origin = Packet.Vector(r);
                        peer.HasOrigin = r.ReadBoolean();
                        peer.SkyrimLevel = r.ReadInt32();
                        peer.BonusHealth = r.ReadSingle();
                        peer.ChargeChest = r.ReadUInt32();
                        peer.ChargeProgress = r.ReadSingle();
                        int count = r.ReadByte();
                        if (chunk == 0 || frame != peer.ReportFrame) { peer.Source.Entries.Clear(); peer.ReportFrame = frame; }
                        for (int i = 0; i < count; i++) peer.Source.Entries.Add(Packet.Actor(r));
                        if (chunk >= chunks - 1) peer.LastReport = Time.unscaledTime;
                        break;
                    }
                case CoopKind.Claim:
                    {
                        uint place = r.ReadUInt32();
                        bool interior = r.ReadBoolean();
                        Vector3 natural = Packet.Vector(r);
                        Vector3 feet = Packet.Vector(r);
                        Assign(place, interior, natural, feet);
                        SendPlaces(peer);
                        break;
                    }
                case CoopKind.Hit:
                    {
                        var hit = new HitEvent { AttackerFormId = r.ReadUInt32(), Amount = r.ReadSingle(), Flags = r.ReadUInt32() };
                        CharacterBody body = BodyOf(peer);
                        if (body != null) SkyrimRoR2Plugin.Instance?.ApplyHit(body, hit);
                        break;
                    }
                case CoopKind.Loot:
                    {
                        int tier = r.ReadInt32();
                        int count = r.ReadInt32();
                        CharacterBody body = BodyOf(peer);
                        SkyrimRoR2Plugin.Instance?.LootForSync?.GiveFor(body, tier, count, (t, name) =>
                        {
                            BinaryWriter w = Packet.Begin(CoopKind.Pickup, out MemoryStream stream);
                            w.Write(t); Packet.Text(w, name);
                            SendTo(peer, stream);
                        });
                        break;
                    }
                case CoopKind.Inventory:
                    {
                        uint request = r.ReadUInt32();
                        bool replace = r.ReadBoolean();
                        int chunk = r.ReadByte();
                        int chunks = r.ReadByte();
                        int length = r.ReadUInt16();
                        byte[] part = r.ReadBytes(length);
                        if (!peer.Inventory.TryGetValue(request, out List<byte[]> parts)) peer.Inventory[request] = parts = new List<byte[]>();
                        while (parts.Count <= chunk) parts.Add(null);
                        parts[chunk] = part;
                        if (parts.Count < chunks || parts.Any(p => p == null)) break;
                        peer.Inventory.Remove(request);
                        string text = Encoding.UTF8.GetString(parts.SelectMany(p => p).ToArray());
                        CharacterMaster master = MasterOf(peer);
                        if (master != null && master.inventory != null)
                        {
                            InventorySync.ApplyText(master.inventory, text, replace, _log, peer.Name);
                            BinaryWriter w = Packet.Begin(CoopKind.InventoryDone, out MemoryStream stream);
                            w.Write(request);
                            SendTo(peer, stream);
                        }
                        break;
                    }
                case CoopKind.Respawn:
                    if (peer.State == 2) Respawn(peer, peer.HasOrigin ? peer.Origin : (MasterOf(peer)?.deathFootPosition ?? Vector3.zero));
                    break;
                case CoopKind.Survivor:
                    {
                        string name = Packet.Text(r);
                        CharacterMaster master = MasterOf(peer);
                        if (master != null) SkyrimRoR2Plugin.Instance?.AutoRunForSync?.ChangeSurvivorFor(master, name);
                        break;
                    }
                case CoopKind.Loadout:
                    {
                        int what = r.ReadByte();
                        int slot = r.ReadInt32();
                        uint value = r.ReadUInt32();
                        SkyrimRoR2Plugin.Instance?.LoadoutsForSync?.ApplyLoadoutFor(BodyOf(peer), what, slot, value);
                        break;
                    }
                case CoopKind.Opened:
                    {
                        uint id = r.ReadUInt32();
                        _zone.PushOpened(id);  // dans le Skyrim de l'hôte
                        foreach (Peer other in _peers.Values) if (other != peer) SendId(other, CoopKind.RemoteOpened, id);
                        break;
                    }
                case CoopKind.Finished:
                    {
                        uint id = r.ReadUInt32();
                        _zone.PushFinished(id);
                        foreach (Peer other in _peers.Values) if (other != peer) SendId(other, CoopKind.RemoteFinished, id);
                        break;
                    }
            }
        }

        private void SendId(Peer peer, CoopKind kind, uint id)
        {
            BinaryWriter w = Packet.Begin(kind, out MemoryStream stream);
            w.Write(id);
            SendTo(peer, stream);
        }

        private CharacterMaster MasterOf(Peer peer) => peer.Local ? LocalMaster : peer.User != null ? peer.User.master : null;

        private CharacterBody BodyOf(Peer peer)
        {
            CharacterMaster master = MasterOf(peer);
            return master != null ? master.GetBody() : null;
        }

        private static CharacterMaster LocalMaster => LocalUserManager.GetFirstLocalUser()?.cachedMaster;

        // ---------- Côté invité : messages de l'hôte ----------

        private readonly List<KeyValuePair<uint, Vector3>> _pushes = new List<KeyValuePair<uint, Vector3>>();
        private readonly List<KeyValuePair<uint, Vector3>> _pushesBuilding = new List<KeyValuePair<uint, Vector3>>();
        private float _pushesTime = -100f;
        private ushort _pushesFrame;
        private int _myState;
        private bool _myKeep;
        private float _myRevive = -1f;
        private string _myLabel = "";
        private int _players = 1;
        private float _hostDifficulty = -1f;
        private uint _remoteChest;
        private float _remoteProgress;
        private float _lastStateTime = -100f;
        private readonly Queue<(int tier, string name)> _pickups = new Queue<(int, string)>();
        private readonly List<(uint formId, float amount, uint flags, int slot, int variant, int code)> _damages =
            new List<(uint, float, uint, int, int, int)>();

        private void HandleFromHost(CoopKind kind, BinaryReader r)
        {
            switch (kind)
            {
                case CoopKind.Places:
                    {
                        int count = r.ReadUInt16();
                        for (int i = 0; i < count; i++)
                        {
                            var e = new PlaceEntry { Place = r.ReadUInt32(), Interior = r.ReadBoolean(), Offset = Packet.Vector(r), Center = Packet.Vector(r) };
                            _places[e.Place] = e;
                        }
                        break;
                    }
                case CoopKind.Ghosts:
                    {
                        ushort frame = r.ReadUInt16();
                        int chunk = r.ReadByte();
                        int chunks = r.ReadByte();
                        int count = r.ReadUInt16();
                        if (chunk == 0 || frame != _pushesFrame) { _pushesBuilding.Clear(); _pushesFrame = frame; }
                        for (int i = 0; i < count; i++) _pushesBuilding.Add(new KeyValuePair<uint, Vector3>(r.ReadUInt32(), Packet.Vector(r)));
                        if (chunk >= chunks - 1)
                        {
                            _pushes.Clear();
                            _pushes.AddRange(_pushesBuilding);
                            _pushesTime = Time.unscaledTime;
                        }
                        break;
                    }
                case CoopKind.Damage:
                    _damages.Add((r.ReadUInt32(), r.ReadSingle(), r.ReadUInt32(), r.ReadSByte(), r.ReadByte(), r.ReadByte()));
                    break;
                case CoopKind.Pickup:
                    _pickups.Enqueue((r.ReadInt32(), Packet.Text(r)));
                    break;
                case CoopKind.State:
                    _players = r.ReadByte();
                    _hostDifficulty = r.ReadSingle();
                    _myKeep = r.ReadBoolean();
                    _myState = r.ReadByte();
                    _myRevive = r.ReadSingle();
                    _myLabel = Packet.Text(r);
                    _remoteChest = r.ReadUInt32();
                    _remoteProgress = r.ReadSingle();
                    _lastStateTime = Time.unscaledTime;
                    break;
                case CoopKind.Text:
                    _zone.ShowText(Packet.Text(r));
                    break;
                case CoopKind.RemoteOpened:
                    _zone.PushOpened(r.ReadUInt32());
                    break;
                case CoopKind.RemoteFinished:
                    _zone.PushFinished(r.ReadUInt32());
                    break;
                case CoopKind.InventoryDone:
                    InventoryDone = r.ReadUInt32();
                    InventoryDoneTime = Time.unscaledTime;
                    break;
            }
        }

        // Dernière demande d'objets portés appliquée par l'hôte (voir InventorySync).
        public uint InventoryDone { get; private set; }
        public float InventoryDoneTime { get; private set; } = -100f;

        // ---------- Requêtes des invités (appelées par les autres parties du mod) ----------

        public void SendHit(HitEvent hit)
        {
            BinaryWriter w = Packet.Begin(CoopKind.Hit, out MemoryStream stream);
            w.Write(hit.AttackerFormId); w.Write(hit.Amount); w.Write(hit.Flags);
            SendToServer(stream);
        }

        public void SendLoot(int tier, int count)
        {
            BinaryWriter w = Packet.Begin(CoopKind.Loot, out MemoryStream stream);
            w.Write(tier); w.Write(count);
            SendToServer(stream);
        }

        // Objets portés : le texte est envoyé en morceaux (les messages réseau sont courts).
        public void SendInventory(uint request, string text, bool replace)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            const int size = 900;
            int chunks = Math.Max(1, (bytes.Length + size - 1) / size);
            for (int i = 0; i < chunks; i++)
            {
                int length = Math.Min(size, bytes.Length - i * size);
                BinaryWriter w = Packet.Begin(CoopKind.Inventory, out MemoryStream stream);
                w.Write(request); w.Write(replace); w.Write((byte)i); w.Write((byte)chunks); w.Write((ushort)Math.Max(0, length));
                if (length > 0) w.Write(bytes, i * size, length);
                SendToServer(stream);
            }
        }

        public void SendLoadout(int kind, int skillSlot, uint value)
        {
            BinaryWriter w = Packet.Begin(CoopKind.Loadout, out MemoryStream stream);
            w.Write((byte)kind); w.Write(skillSlot); w.Write(value);
            SendToServer(stream);
        }

        public void SendSurvivor(string name)
        {
            BinaryWriter w = Packet.Begin(CoopKind.Survivor, out MemoryStream stream);
            Packet.Text(w, name);
            SendToServer(stream);
        }

        public bool TryTakePickup(out int tier, out string name)
        {
            tier = 0;
            name = null;
            if (_pickups.Count == 0) return false;
            (tier, name) = _pickups.Dequeue();
            return true;
        }

        // ---------- Dégâts (hôte) ----------

        // Le joueur (clé) à qui appartient ce qui frappe : son survivant, ou un drone/une tourelle à lui. -1 : personne.
        public int KeyOfAttacker(GameObject attacker)
        {
            CharacterBody body = attacker != null ? attacker.GetComponent<CharacterBody>() : null;
            CharacterMaster master = body != null ? body.master : null;
            if (master != null && !body.isPlayerControlled && master.minionOwnership != null && master.minionOwnership.ownerMaster != null)
                master = master.minionOwnership.ownerMaster;
            if (master == null) return -1;
            if (master == LocalMaster) return 0;
            foreach (Peer peer in _peers.Values) if (MasterOf(peer) == master) return peer.Key;
            return -1;
        }

        // Les dégâts d'un coup RoR2 sur un double, envoyés à tous les Skyrim qui voient cet ennemi. L'expérience (case
        // de l'attaque) seulement au joueur qui frappe. Faux si personne d'autre que nous ne le voit (cas solo).
        public bool RouteDamage(BridgeWriter bridge, GhostManager ghosts, uint formId, float amount, bool crit, GameObject attacker,
            Func<CharacterBody, (int slot, int variant, int code)> classify)
        {
            if (!HostWithPeers) return false;
            List<GhostManager.Source> sources = ghosts.SourcesOf(formId);
            if (sources == null || sources.Count == 0) return false;
            int attackerKey = KeyOfAttacker(attacker);
            foreach (GhostManager.Source source in sources)
            {
                bool mine = source.Key == attackerKey;
                (int slot, int variant, int code) = mine && classify != null ? classify(source.Body) : (-1, 0, 0);
                if (source.Local) bridge.WriteDamage(formId, amount, crit, slot, variant, code, !mine);
                else if (_peers.TryGetValue(source.Key, out Peer peer)) SendDamage(peer, formId, amount, crit, !mine, slot, variant, code);
            }
            return true;
        }

        private void SendDamage(Peer peer, uint formId, float amount, bool crit, bool remote, int slot, int variant, int code)
        {
            BinaryWriter w = Packet.Begin(CoopKind.Damage, out MemoryStream stream);
            w.Write(formId); w.Write(amount);
            w.Write((crit ? BridgeInfo.DamageCrit : 0u) | (remote ? BridgeInfo.DamageRemote : 0u));
            w.Write((sbyte)Mathf.Clamp(slot, -1, 120)); w.Write((byte)Mathf.Clamp(variant, 0, 255)); w.Write((byte)Mathf.Clamp(code, 0, 255));
            SendTo(peer, stream);
        }

        // Un double tué : l'ennemi meurt aussi dans les Skyrim où il était encore vivant.
        private BridgeWriter _bridgeForKills;

        private void OnGhostKilled(uint formId, List<GhostManager.Source> alive)
        {
            foreach (GhostManager.Source source in alive)
            {
                if (source.Local) _bridgeForKills?.WriteDamage(formId, 1.0e7f, false, -1, 0, 0, true);
                else if (_peers.TryGetValue(source.Key, out Peer peer)) SendDamage(peer, formId, 1.0e7f, false, true, -1, 0, 0);
            }
        }

        // ---------- Image par image ----------

        private readonly List<uint> _opened = new List<uint>();
        private readonly List<uint> _finished = new List<uint>();
        private readonly List<ActorEntry> _actors = new List<ActorEntry>();
        private uint _actorSequence;
        private float _actorTime = -100f;
        private float _nextReport;
        private ushort _reportFrame;
        private uint _lastReviveSequence, _lastRequestId;
        private bool _respawnAsked;
        private Run _run;
        private float _runStart;
        private float _nextHousekeeping;
        private bool _idLogged;
        private float _lastFrame;

        // Appelé par le plugin à chaque image, après la lecture des commandes de Skyrim.
        public void Update(BridgeWriter bridge, CharacterBody body, TerrainMirror terrain, GhostManager ghosts, SkyrimInput input,
            SkyrimCommand command, bool hasCommand)
        {
            if (!Enabled) return;
            RegisterHandlers();
            LogOwnId();
            float now = Time.unscaledTime;
            float dt = Mathf.Clamp(now - _lastFrame, 0f, 0.25f);
            _lastFrame = now;
            if (ghosts != null && ghosts.Killed == null) ghosts.Killed = OnGhostKilled;
            _bridgeForKills = bridge;

            if (Run.instance != _run)
            {
                _run = Run.instance;
                _runStart = now;
                _places.Clear();
                _claimed.Clear();
                _local.State = 0;
                _myState = 0;
                _respawnAsked = false;
            }
            if (Run.instance == null)
            {
                _zone.Write(false, 1, -1f, false, false, -1f, "", 0, 0f);
                return;
            }

            _zone.ReadSkyrim(_opened, _finished, out uint chest, out float progress);
            if (NetworkServer.active) HostUpdate(bridge, body, command, hasCommand, chest, progress, now, dt);
            else if (IsClient) ClientUpdate(bridge, body, terrain, ghosts, input, command, hasCommand, chest, progress, now);
            else _zone.Write(false, 1, -1f, false, false, -1f, "", 0, 0f);
        }

        // L'identifiant Steam de ce joueur, pour que ses amis le mettent dans IdentifiantHote.
        private void LogOwnId()
        {
            if (_idLogged || !WantsHost) return;
            NetworkUser user = NetworkUser.readOnlyLocalPlayersList.FirstOrDefault();
            if (user == null) return;
            _idLogged = true;
            string id = "?";
            try
            {
                object platform = user.GetType().GetField("id")?.GetValue(user) ?? user.GetType().GetProperty("id")?.GetValue(user);
                if (platform != null)
                {
                    foreach (string member in new[] { "steamId", "value", "ID" })
                    {
                        object inner = platform.GetType().GetField(member)?.GetValue(platform) ?? platform.GetType().GetProperty(member)?.GetValue(platform);
                        if (inner == null) continue;
                        object raw = inner.GetType().GetField("value")?.GetValue(inner) ?? inner.GetType().GetField("m_SteamID")?.GetValue(inner) ?? inner;
                        id = raw.ToString();
                        if (id.Length >= 15) break;
                    }
                    if (id.Length < 15) id = platform.ToString();
                }
            }
            catch (Exception) { }
            _log.LogInfo("Coop : ton identifiant Steam (a donner a tes invites, reglage IdentifiantHote) : " + id);
        }

        // ---------- Hôte ----------

        private void HostUpdate(BridgeWriter bridge, CharacterBody body, SkyrimCommand command, bool hasCommand, uint chest, float progress, float now, float dt)
        {
            SyncPeers();
            _local.Name = NetworkUser.readOnlyLocalPlayersList.FirstOrDefault()?.userName ?? "Hote";
            _local.Master = LocalMaster;
            _local.Body = body;
            _local.ChargeChest = chest;
            _local.ChargeProgress = progress;
            if (hasCommand) _local.SkyrimLevel = command.SkyrimLevel;

            // Ce que notre Skyrim a fait : les invités le recopient.
            foreach (uint id in _opened) foreach (Peer peer in _peers.Values) SendId(peer, CoopKind.RemoteOpened, id);
            foreach (uint id in _finished) foreach (Peer peer in _peers.Values) SendId(peer, CoopKind.RemoteFinished, id);

            if (_peers.Count == 0)
            {
                _local.State = 0;
                _zone.Write(false, 1, -1f, false, false, -1f, "", 0, 0f);
                return;
            }

            _all.Clear();
            _all.Add(_local);
            _all.AddRange(_peers.Values);
            foreach (Peer peer in _all)
            {
                peer.Master = MasterOf(peer);
                peer.Body = peer.Master != null ? peer.Master.GetBody() : null;
            }
            UpdateDeaths(command, hasCommand, now, dt);
            if (now >= _nextHousekeeping)
            {
                _nextHousekeeping = now + 0.5f;
                Housekeeping(now, body);
            }
            ApplyBonusHealth();

            // La table des lieux.
            if (_placesDirty || now >= _nextPlaces)
            {
                _placesDirty = false;
                _nextPlaces = now + PlacesPeriod;
                if (_places.Count > 0) SendPlaces(null);
            }

            float difficulty = hasCommand ? Mathf.Max(0f, command.Difficulty) : 0f;
            foreach (Peer peer in _peers.Values)
            {
                if (now < peer.NextState) continue;
                peer.NextState = now + StatePeriod;
                Describe(peer, out bool downed, out float revive, out string label, out bool keep);
                (uint c, float p) = ChargeFor(peer);
                BinaryWriter w = Packet.Begin(CoopKind.State, out MemoryStream stream);
                w.Write((byte)_all.Count); w.Write(difficulty); w.Write(keep); w.Write((byte)peer.State); w.Write(revive);
                Packet.Text(w, label); w.Write(c); w.Write(p);
                SendTo(peer, stream);
            }
            {
                Describe(_local, out bool downed, out float revive, out string label, out bool keep);
                (uint c, float p) = ChargeFor(_local);
                _zone.Write(true, _all.Count, -1f, keep, downed, revive, label, c, p);  // l'hôte garde sa propre difficulté
            }
        }

        // Les invités présents (et ceux qui sont partis).
        private readonly HashSet<int> _seenPeers = new HashSet<int>();

        private void SyncPeers()
        {
            _seenPeers.Clear();
            foreach (NetworkUser user in NetworkUser.readOnlyInstancesList)
            {
                if (user == null || user.isLocalPlayer) continue;
                NetworkConnection conn = user.connectionToClient;
                if (conn == null) continue;
                bool fresh = !_peers.ContainsKey(conn.connectionId);
                Peer peer = PeerOf(conn);
                peer.User = user;
                peer.Name = string.IsNullOrEmpty(user.userName) ? "Coequipier" : user.userName;
                _seenPeers.Add(conn.connectionId);
                if (fresh || !peer.Announced)
                {
                    peer.Announced = true;
                    Broadcast(peer.Name + " a rejoint la partie");
                    if (_places.Count > 0) SendPlaces(peer);
                    _log.LogInfo($"Coop : {peer.Name} a rejoint la partie (connexion {conn.connectionId})");
                }
            }
            foreach (int key in _peers.Keys.ToList())
            {
                if (_seenPeers.Contains(key)) continue;
                Peer gone = _peers[key];
                // Pas encore de NetworkUser (connexion toute neuve) : on lui laisse le temps.
                if (gone.User == null && Time.unscaledTime - gone.CreatedAt < 10f) continue;
                _peers.Remove(key);
                if (gone.Announced)
                {
                    Broadcast(gone.Name + " a quitte la partie");
                    _log.LogInfo("Coop : " + gone.Name + " a quitte la partie");
                }
            }
        }

        public void Broadcast(string text)
        {
            _zone.ShowText(text);
            BinaryWriter w = Packet.Begin(CoopKind.Text, out MemoryStream stream);
            Packet.Text(w, text);
            foreach (Peer peer in _peers.Values) SendTo(peer, stream);
        }

        // La charge du coffre du boss d'un autre joueur dans le même lieu (la plus avancée).
        private (uint chest, float progress) ChargeFor(Peer peer)
        {
            uint chest = 0;
            float best = 0f;
            uint place = peer.Local ? _localPlace : peer.Place;
            foreach (Peer other in _all)
            {
                if (other == peer || other.ChargeChest == 0) continue;
                uint otherPlace = other.Local ? _localPlace : other.Place;
                if (otherPlace != place) continue;
                if (chest == 0 || other.ChargeProgress > best) { chest = other.ChargeChest; best = other.ChargeProgress; }
            }
            return (chest, best);
        }

        private uint _localPlace;
        public void SetLocalPlace(uint place) => _localPlace = place;

        // Ce qu'affiche le Skyrim de ce joueur : à terre, relèvement, et si sa mort remet la difficulté à 1.
        private void Describe(Peer peer, out bool downed, out float revive, out string label, out bool keep)
        {
            downed = peer.State == 1;
            revive = -1f;
            label = "";
            keep = _all.Any(o => o != peer && o.State == 0);
            if (downed)
            {
                revive = peer.Progress;
                label = peer.Helper != null ? "{} te releve|" + peer.Helper.Name : "A terre : un coequipier doit rester pres de toi";  // 0.74 : traduit par Skyrim
                return;
            }
            Peer target = _all.FirstOrDefault(o => o.State == 1 && o.Helper == peer);
            if (target != null)
            {
                revive = target.Progress;
                label = "Tu releves {}|" + target.Name;
            }
        }

        // ---------- La mort et le relèvement ----------

        private void UpdateDeaths(SkyrimCommand command, bool hasCommand, float now, float dt)
        {
            float helpRange = Mathf.Max(1f, _helpRange.Value);
            float reviveDistance = Mathf.Max(0.5f, _reviveDistance.Value);
            float reviveSeconds = Mathf.Max(0.5f, _reviveSeconds.Value);
            foreach (Peer peer in _all)
            {
                CharacterMaster master = peer.Master;
                bool dead = master != null && master.IsDeadAndOutOfLivesServer();
                if (!dead)
                {
                    if (peer.State != 0 && peer.Body != null) _log.LogInfo($"Coop : {peer.Name} est de nouveau debout");
                    if (peer.Body != null) { peer.State = 0; peer.Progress = 0f; peer.Helper = null; }
                    continue;
                }
                if (peer.State == 0)
                {
                    peer.DownAt = master.deathFootPosition;
                    peer.Progress = 0f;
                    peer.AloneSince = -1f;
                    peer.Helper = null;
                    bool helper = _all.Any(o => o != peer && o.State == 0 && o.Body != null && (o.Body.footPosition - peer.DownAt).magnitude < helpRange);
                    peer.State = helper ? 1 : 2;
                    if (peer.Local && hasCommand) { peer.ReviveSeen = command.ReviveSequence; peer.RequestSeen = command.RequestId; }
                    if (peer.Local) _respawnAsked = false;
                    Broadcast(helper ? $"{peer.Name} est a terre ! Reste pres de lui {reviveSeconds:F0} s pour le relever"
                                     : $"{peer.Name} est tombe (personne pres de lui)");
                    _log.LogInfo($"Coop : {peer.Name} mort ({(helper ? "a terre, peut etre releve" : "seul : comme en solo")})");
                }
                if (peer.State == 1)
                {
                    Peer nearest = null;
                    float best = reviveDistance;
                    bool anyNear = false;
                    foreach (Peer other in _all)
                    {
                        if (other == peer || other.State != 0 || other.Body == null) continue;
                        float distance = (other.Body.footPosition - peer.DownAt).magnitude;
                        if (distance < helpRange) anyNear = true;
                        if (distance <= best) { best = distance; nearest = other; }
                    }
                    peer.Helper = nearest;
                    if (nearest != null) peer.Progress += dt / reviveSeconds;
                    if (anyNear) peer.AloneSince = -1f;
                    else
                    {
                        if (peer.AloneSince < 0f) peer.AloneSince = now;
                        if (now - peer.AloneSince > Mathf.Max(5f, _alonePatience.Value))
                        {
                            peer.State = 2;
                            if (peer.Local && hasCommand) { peer.ReviveSeen = command.ReviveSequence; peer.RequestSeen = command.RequestId; }
                            Broadcast($"Personne n'a releve {peer.Name} : il se reveille ailleurs");
                        }
                    }
                    if (peer.State == 1 && peer.Progress >= 1f)
                    {
                        string helperName = nearest != null ? nearest.Name : "?";
                        Respawn(peer, peer.DownAt);
                        Broadcast($"{helperName} a releve {peer.Name} !");
                    }
                }
            }
            // Tout le monde est tombé : chacun se réveille comme en solo (et la difficulté repart à 1).
            if (_all.Count > 1 && _all.All(p => p.State != 0))
            {
                foreach (Peer peer in _all)
                {
                    if (peer.State != 1) continue;
                    peer.State = 2;
                    if (peer.Local && hasCommand) { peer.ReviveSeen = command.ReviveSequence; peer.RequestSeen = command.RequestId; }
                }
            }
            // Notre propre réveil (hôte) après une mort "comme en solo".
            if (_local.State == 2 && hasCommand && !_respawnAsked &&
                (command.ReviveSequence != _local.ReviveSeen || command.RequestId != _local.RequestSeen))
            {
                _respawnAsked = true;
                Vector3 at = _localFeetKnown ? _localFeet : (_local.Master != null ? _local.Master.deathFootPosition : Vector3.zero);
                Respawn(_local, at);
            }
        }

        private Vector3 _localFeet;
        private bool _localFeetKnown;
        public void SetLocalFeet(bool known, Vector3 feet) { _localFeetKnown = known; _localFeet = feet; }

        private void Respawn(Peer peer, Vector3 at)
        {
            CharacterMaster master = MasterOf(peer);
            if (master == null) return;
            try
            {
                master.Respawn(at, Quaternion.identity);
                peer.State = 0;
                peer.Progress = 0f;
                peer.Helper = null;
                _log.LogInfo($"Coop : {peer.Name} revient dans la partie");
            }
            catch (Exception e) { _log.LogWarning("Coop : retour de " + peer.Name + " impossible (" + e.Message + ")"); }
        }

        // Retardataires (arrivés en pleine partie) et capsules de départ des invités.
        private readonly HashSet<NetworkUser> _setupTried = new HashSet<NetworkUser>();
        private readonly HashSet<CharacterMaster> _respawnTried = new HashSet<CharacterMaster>();
        private readonly Dictionary<CharacterBody, float> _inPod = new Dictionary<CharacterBody, float>();

        private void Housekeeping(float now, CharacterBody hostBody)
        {
            foreach (NetworkUser user in NetworkUser.readOnlyInstancesList)
            {
                if (user == null) continue;
                CharacterMaster master = user.master;
                if (master == null)
                {
                    if (now - _runStart > 5f && _setupTried.Add(user))
                    {
                        try
                        {
                            MethodInfo setup = typeof(Run).GetMethod("SetupUserCharacterMaster", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            setup?.Invoke(Run.instance, new object[] { user });
                            _log.LogInfo("Coop : joueur arrive en cours de partie, survivant prepare (" + user.userName + ")");
                        }
                        catch (Exception e) { _log.LogWarning("Coop : retardataire impossible a preparer (" + (e.InnerException ?? e).Message + ")"); }
                    }
                    continue;
                }
                CharacterBody body = master.GetBody();
                if (body == null && !master.IsDeadAndOutOfLivesServer() && now - _runStart > 8f && _respawnTried.Add(master))
                {
                    Peer peer = _peers.Values.FirstOrDefault(p => p.User == user);
                    Vector3 at = peer != null && peer.HasOrigin ? peer.Origin
                               : hostBody != null ? hostBody.footPosition + hostBody.transform.right * 2f : Vector3.zero;
                    try
                    {
                        master.Respawn(at, Quaternion.identity);
                        _log.LogInfo("Coop : survivant du retardataire " + user.userName + " apparu");
                    }
                    catch (Exception e) { _log.LogWarning("Coop : retardataire impossible a faire apparaitre (" + e.Message + ")"); }
                }
                // Les invités ne peuvent pas sortir seuls de leur capsule de départ : l'hôte les en fait sortir.
                if (body != null && !user.isLocalPlayer)
                {
                    if (AutoRun.InVehicle(body))
                    {
                        if (!_inPod.ContainsKey(body)) _inPod[body] = now;
                        else if (now - _inPod[body] > 0.5f && AutoRun.Eject(body)) _inPod.Remove(body);
                    }
                    else _inPod.Remove(body);
                }
            }
            foreach (CharacterBody gone in _inPod.Keys.Where(b => b == null).ToList()) _inPod.Remove(gone);
        }

        // La vie gagnée dans Skyrim (Santé, enchantements...) : l'hôte l'ajoute aux survivants des invités.
        private void ApplyBonusHealth()
        {
            foreach (Peer peer in _peers.Values)
            {
                CharacterBody body = peer.Body;
                if (body == null) continue;
                if (body != peer.BonusBody) { peer.BonusBody = body; peer.BonusApplied = 0f; }
                float bonus = float.IsNaN(peer.BonusHealth) ? 0f : Mathf.Max(0f, peer.BonusHealth);
                if (Mathf.Abs(bonus - peer.BonusApplied) <= 0.5f) continue;
                body.baseMaxHealth += bonus - peer.BonusApplied;
                peer.BonusApplied = bonus;
                body.MarkAllStatsDirty();
            }
        }

        // Le niveau de l'équipe : la moyenne des niveaux Skyrim des joueurs (voir LevelSync). -1 : pas de coop.
        public int TeamSkyrimLevel(int localLevel)
        {
            if (!HostWithPeers) return -1;
            int total = Mathf.Max(0, localLevel), count = localLevel > 0 ? 1 : 0;
            foreach (Peer peer in _peers.Values)
                if (peer.SkyrimLevel > 0) { total += peer.SkyrimLevel; count++; }
            return count > 0 ? Mathf.RoundToInt((float)total / count) : -1;
        }

        // Les Skyrim des joueurs comme sources d'ennemis (le nôtre d'abord). Appelé par le plugin avant GhostManager.
        public List<GhostManager.Source> Sources(GhostManager.Source local)
        {
            _sources.Clear();
            if (local != null) _sources.Add(local);
            float now = Time.unscaledTime;
            foreach (Peer peer in _peers.Values)
            {
                if (!peer.HasOrigin || now - peer.LastReport > 1.5f) continue;
                peer.Source.Key = peer.Key;
                peer.Source.Local = false;
                peer.Source.Origin = peer.Origin;
                peer.Source.Body = BodyOf(peer);
                _sources.Add(peer.Source);
            }
            return _sources;
        }

        // Après GhostManager : les places voulues des ennemis, envoyées au Skyrim de chaque invité.
        public void SendPushes()
        {
            float now = Time.unscaledTime;
            foreach (Peer peer in _peers.Values)
            {
                if (now < peer.NextPush) continue;
                peer.NextPush = now + PushPeriod;
                List<KeyValuePair<uint, Vector3>> pushes = peer.Source.Pushes;
                if (now - peer.LastReport > 1.5f) pushes = new List<KeyValuePair<uint, Vector3>>();
                peer.PushFrame++;
                int chunks = Math.Max(1, (pushes.Count + PushesPerChunk - 1) / PushesPerChunk);
                for (int c = 0; c < chunks; c++)
                {
                    int from = c * PushesPerChunk;
                    int count = Math.Min(PushesPerChunk, pushes.Count - from);
                    BinaryWriter w = Packet.Begin(CoopKind.Ghosts, out MemoryStream stream);
                    w.Write(peer.PushFrame); w.Write((byte)c); w.Write((byte)chunks); w.Write((ushort)Math.Max(0, count));
                    for (int i = 0; i < count; i++) { w.Write(pushes[from + i].Key); Packet.Vector(w, pushes[from + i].Value); }
                    SendTo(peer, stream);
                }
            }
        }

        // ---------- Invité ----------

        public bool LocalDead => ClientActive ? _myState == 2 : (HostWithPeers && _local.State == 2);
        public bool LocalDowned => ClientActive ? _myState == 1 : (HostWithPeers && _local.State == 1);

        private void ClientUpdate(BridgeWriter bridge, CharacterBody body, TerrainMirror terrain, GhostManager ghosts, SkyrimInput input,
            SkyrimCommand command, bool hasCommand, uint chest, float progress, float now)
        {
            Vector3 feet = Vector3.zero;
            bool feetKnown = terrain != null && terrain.TryGetSkyrimFeet(out feet);
            if (!feetKnown) feet = body != null ? body.footPosition : Vector3.zero;

            // Notre Skyrim, pour l'hôte.
            if (now >= _nextReport)
            {
                _nextReport = now + ReportPeriod;
                bool fresh = false;
                if (bridge.ReadActors(_actors, out uint sequence))
                {
                    if (sequence != _actorSequence) { _actorSequence = sequence; _actorTime = now; }
                    fresh = now - _actorTime < 1f && body != null;
                }
                if (!fresh) _actors.Clear();
                _reportFrame++;
                int chunks = Math.Max(1, (_actors.Count + ActorsPerChunk - 1) / ActorsPerChunk);
                for (int c = 0; c < chunks; c++)
                {
                    int from = c * ActorsPerChunk;
                    int count = Math.Min(ActorsPerChunk, _actors.Count - from);
                    BinaryWriter w = Packet.Begin(CoopKind.Report, out MemoryStream stream);
                    w.Write(_reportFrame); w.Write((byte)c); w.Write((byte)chunks);
                    w.Write(input.PlaceId); w.Write(input.Interior == 1); Packet.Vector(w, feet); w.Write(feetKnown && body != null);
                    w.Write(hasCommand ? command.SkyrimLevel : 0); w.Write(hasCommand ? command.BonusHealth : 0f);
                    w.Write(chest); w.Write(progress);
                    w.Write((byte)Math.Max(0, count));
                    for (int i = 0; i < count; i++) Packet.Actor(w, _actors[from + i]);
                    SendToServer(stream);
                }
            }

            // Ce que notre Skyrim a fait.
            foreach (uint id in _opened) { BinaryWriter w = Packet.Begin(CoopKind.Opened, out MemoryStream s); w.Write(id); SendToServer(s); }
            foreach (uint id in _finished) { BinaryWriter w = Packet.Begin(CoopKind.Finished, out MemoryStream s); w.Write(id); SendToServer(s); }

            // Les dégâts reçus de l'hôte, pour nos ennemis.
            foreach (var d in _damages)
                bridge.WriteDamage(d.formId, d.amount, (d.flags & BridgeInfo.DamageCrit) != 0, d.slot, d.variant, d.code, (d.flags & BridgeInfo.DamageRemote) != 0);
            _damages.Clear();

            // Les places voulues de nos ennemis (envoyées par l'hôte), vers notre Skyrim.
            if (ghosts != null) ghosts.PublishExternal(now - _pushesTime < 0.3f && feetKnown ? _pushes : null, feet);

            // L'état donné par l'hôte.
            bool stateFresh = now - _lastStateTime < 2f;
            if (!stateFresh) { _myState = 0; _myRevive = -1f; _myLabel = ""; }
            _zone.Write(true, Mathf.Max(1, _players), stateFresh ? _hostDifficulty : -1f, stateFresh && _myKeep, _myState == 1,
                stateFresh ? _myRevive : -1f, stateFresh ? _myLabel : "", stateFresh ? _remoteChest : 0, _remoteProgress);

            // Tombé "comme en solo" : réveil dans Skyrim -> l'hôte nous fait revenir.
            if (hasCommand)
            {
                if (_myState != 2)
                {
                    _lastReviveSequence = command.ReviveSequence;
                    _lastRequestId = command.RequestId;
                    _respawnAsked = false;
                }
                else if (!_respawnAsked && (command.ReviveSequence != _lastReviveSequence || command.RequestId != _lastRequestId))
                {
                    _respawnAsked = true;
                    BinaryWriter w = Packet.Begin(CoopKind.Respawn, out MemoryStream stream);
                    SendToServer(stream);
                    _log.LogInfo("Coop : reveil dans Skyrim, l'hote nous fait revenir");
                }
                else if (_respawnAsked && body != null && body.healthComponent != null && body.healthComponent.alive)
                {
                    _lastReviveSequence = command.ReviveSequence;
                    _lastRequestId = command.RequestId;
                }
            }
        }
    }
}
