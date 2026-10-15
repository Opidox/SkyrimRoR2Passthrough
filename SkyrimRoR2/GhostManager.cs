using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoR2;
using RoR2.CharacterAI;
using SkyrimRoR2.Shared;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SkyrimRoR2
{
    // Les "doubles" : chaque ennemi Skyrim proche est représenté dans RoR2 par un monstre
    // immobile et inoffensif (un Lémurien sans intelligence), placé autour du survivant
    // exactement comme l'ennemi est placé autour du joueur dans Skyrim.
    //
    //  - Les tirs, les projectiles et les objets RoR2 touchent ces doubles.
    //  - Les dégâts ne leur font rien : ils sont envoyés à Skyrim (voir DamagePatch).
    //  - Quand l'ennemi meurt dans Skyrim, son double meurt dans RoR2, tué par le survivant :
    //    les objets "à la mort" se déclenchent, et RoR2 donne or et expérience.
    internal sealed class GhostManager
    {
        // Le monstre RoR2 utilisé pour tous les doubles (pour commencer).
        private const string GhostMasterName = "LemurianMaster";

        // Au-delà de ce délai sans nouvelle liste de Skyrim, on retire tous les doubles.
        private const float TableTimeoutSeconds = 1f;

        private sealed class Ghost
        {
            public uint FormId;
            public CharacterMaster Master;
            public CharacterBody Body;
            public HealthComponent Health;
            public float SkyrimMaxHealth;  // PV max de l'ennemi dans Skyrim (pour convertir les dégâts)
            public bool SeenThisFrame;
            public bool Passive;  // pas hostile dans Skyrim : les alliés RoR2 ne le visent pas
            public readonly List<HurtBox> Bullseyes = new List<HurtBox>();  // ses zones "cibles" d'origine

            // Déplacé par RoR2 (tiré par la Chargeuse, porté par un drone, projeté...) : voir UpdatePush.
            public bool HasPlaced;
            public Vector3 Placed;        // là où on l'a mis à l'image précédente
            public Vector3 PlacedOffset;  // petit écart normal entre "là où on le met" et ses pieds
            public bool Pushed;
            public float PushedSince;
            public float SlowSince;
            public Vector3 LastFoot;

            // La zone qui encaisse les tirs : une boîte à la taille et à l'orientation de l'ennemi Skyrim.
            public GameObject HitboxObject;
            public BoxCollider Hitbox;

            public int Elite;  // 0 aucun, 1 feu, 2 glace, 3 foudre
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        // Seuils pour reconnaître qu'autre chose que Skyrim déplace un double.
        private const float PushDistance = 0.4f;     // m d'écart avec l'endroit où on l'avait mis
        private const float PushSpeed = 3f;          // m/s donnés par une force (grappin, coup...)
        private const float MaxPushHeight = 3.5f;    // géants, dragons... : trop lourds pour être déplacés
        private const float MaxPushSeconds = 10f;
        private const float HitWindow = 1.0f;        // s après un coup pendant lesquelles RoR2 peut déplacer l'ennemi
        private readonly Dictionary<uint, float> _lastHit = new Dictionary<uint, float>();

        // Un coup du camp du joueur vient de toucher ce double (voir OnDamage du plugin).
        public void NoteHit(uint formId)
        {
            _lastHit[formId] = Time.time;
        }

        private readonly ConfigEntry<bool> _pushEnabled;
        private IntPtr _pushView;
        private bool _pushOpenTried;
        private uint _pushFrame;
        private int _pushCount;
        private int _pushLogged;

        private readonly ManualLogSource _log;
        private readonly Dictionary<uint, Ghost> _byFormId = new Dictionary<uint, Ghost>();
        private readonly Dictionary<HealthComponent, uint> _byHealth = new Dictionary<HealthComponent, uint>();
        private readonly List<ActorEntry> _entries = new List<ActorEntry>();
        private readonly List<uint> _toRemove = new List<uint>();

        // Où et quand des doubles sont apparus : leur effet d'apparition (terre qui jaillit...) ne doit pas
        // être montré dans Skyrim, l'ennemi Skyrim y est déjà.
        private static readonly List<KeyValuePair<Vector3, float>> _recentSpawns = new List<KeyValuePair<Vector3, float>>();

        public static bool NearRecentSpawn(Vector3 position)
        {
            float now = Time.unscaledTime;
            _recentSpawns.RemoveAll(s => now - s.Value > 3f);
            foreach (var spawn in _recentSpawns)
                if ((spawn.Key - position).sqrMagnitude < 4f * 4f) return true;
            return false;
        }

        private GameObject _masterPrefab;
        private bool _prefabMissingLogged;
        private uint _lastSequence;
        private float _lastTableTime = -100f;

        // Vrai pendant que NOUS tuons un double : les dégâts doivent alors passer normalement.
        internal bool AllowRealDamage { get; private set; }

        public int Count => _byFormId.Count;

        // La difficulté de Skyrim (0 au départ, +1 par donjon terminé...) : les ennemis frappent plus fort et
        // encaissent mieux. Les élites encore plus (comme dans RoR2).
        public float Difficulty { get; set; }
        private const float HitGrowth = 0.12f;     // coups reçus : x (1 + 0,12 x difficulté)
        private const float ToughGrowth = 0.10f;   // dégâts donnés : / (1 + 0,10 x difficulté)
        private const float EliteHit = 1.6f;
        private const float EliteTough = 2.5f;

        public float HitMultiplier(uint attacker)
        {
            float multiplier = 1f + HitGrowth * Difficulty;
            if (attacker != 0 && _byFormId.TryGetValue(attacker, out Ghost ghost) && ghost.Elite != 0) multiplier *= EliteHit;
            return multiplier;
        }

        public float DealtMultiplier(uint target)
        {
            float divider = 1f + ToughGrowth * Difficulty;
            if (_byFormId.TryGetValue(target, out Ghost ghost) && ghost.Elite != 0) divider *= EliteTough;
            return 1f / divider;
        }

        // Les aspects d'élite de RoR2 (effets visibles, coups qui brûlent, gèlent ou foudroient).
        private static readonly string[] EliteBuffs = { null, "bdEliteFire", "bdEliteIce", "bdEliteLightning" };

        private void SetElite(Ghost ghost, int elite)
        {
            if (ghost.Elite == elite || ghost.Body == null) return;
            if (ghost.Elite != 0)
            {
                BuffIndex old = BuffCatalog.FindBuffIndex(EliteBuffs[ghost.Elite]);
                if (old != BuffIndex.None) ghost.Body.RemoveBuff(old);
            }
            ghost.Elite = elite;
            if (elite == 0) return;
            BuffIndex buff = BuffCatalog.FindBuffIndex(EliteBuffs[elite]);
            if (buff == BuffIndex.None) { _log.LogWarning("Elite : aspect " + EliteBuffs[elite] + " introuvable"); return; }
            ghost.Body.AddBuff(buff);
            _log.LogInfo($"Ennemi Skyrim {ghost.FormId:X8} : elite ({EliteBuffs[elite]})");
        }

        public GhostManager(ManualLogSource log, ConfigFile config)
        {
            _log = log;
            _pushEnabled = ConfigMove.Bind(config, "Monde", "2 - Jeu", "DeplacerEnnemisSkyrim", true,
                "Skyrim enemies move when RoR2 moves them: pulled by the Loader's grapple, carried by a drone, knocked back by a strong hit...");
        }

        // Si cette barre de vie appartient à un double, donne l'ennemi Skyrim correspondant,
        // ses PV max dans Skyrim, et les PV max du double dans RoR2.
        public bool TryGetGhost(HealthComponent health, out uint formId, out float skyrimMaxHealth, out float ghostMaxHealth)
        {
            skyrimMaxHealth = 0f;
            ghostMaxHealth = 0f;
            if (!_byHealth.TryGetValue(health, out formId)) return false;
            if (_byFormId.TryGetValue(formId, out Ghost ghost))
            {
                skyrimMaxHealth = ghost.SkyrimMaxHealth;
                ghostMaxHealth = health != null ? health.fullHealth : 0f;
            }
            return true;
        }

        // Vrai si ce corps est le double d'un ennemi Skyrim.
        public bool IsGhost(CharacterBody body)
        {
            return body != null && body.healthComponent != null && _byHealth.ContainsKey(body.healthComponent);
        }

        // Le double d'un ennemi Skyrim, s'il existe (pour savoir qui a frappé le survivant).
        public bool TryGetBody(uint formId, out CharacterBody body)
        {
            body = null;
            if (!_byFormId.TryGetValue(formId, out Ghost ghost) || ghost.Body == null) return false;
            body = ghost.Body;
            return true;
        }

        // 0.70 (coop) : une "source" d'ennemis = le Skyrim d'un joueur. En solo, il n'y en a qu'une (le nôtre).
        // L'hôte réunit les ennemis de tous les joueurs : un même ennemi (même identifiant Skyrim) vu par plusieurs
        // joueurs n'a qu'un double, placé par le joueur le plus proche de lui (son "meneur"). Les autres Skyrim
        // recopient la place du double (comme un ennemi déplacé par RoR2).
        public sealed class Source
        {
            public int Key;                 // 0 : l'hôte (ce RoR2) ; sinon le numéro de connexion du joueur
            public bool Local;              // le Skyrim de ce RoR2 (pont local), sinon un coéquipier (réseau)
            public Vector3 Origin;          // pieds du joueur Skyrim, dans le monde de RoR2
            public CharacterBody Body;      // son survivant (peut être absent)
            public readonly List<ActorEntry> Entries = new List<ActorEntry>();
            public readonly List<KeyValuePair<uint, Vector3>> Pushes = new List<KeyValuePair<uint, Vector3>>();  // coéquipier : places voulues (monde RoR2)
        }

        private sealed class Candidate
        {
            public Source Driver;
            public ActorEntry Entry;
            public float Distance;
            public bool Dead;
            public readonly List<Source> All = new List<Source>();
        }

        private readonly Dictionary<uint, Candidate> _merged = new Dictionary<uint, Candidate>();
        private readonly Stack<Candidate> _candidatePool = new Stack<Candidate>();
        private readonly Dictionary<uint, List<Source>> _sourcesOf = new Dictionary<uint, List<Source>>();
        private readonly Dictionary<uint, float> _recentlyKilled = new Dictionary<uint, float>();
        private readonly Dictionary<uint, GameObject> _lastAttacker = new Dictionary<uint, GameObject>();
        private readonly List<Source> _single = new List<Source>();
        private readonly Source _localSource = new Source { Key = 0, Local = true };

        // Un double vient d'être tué : les Skyrim qui avaient encore cet ennemi vivant (pour qu'il y meure aussi).
        public Action<uint, List<Source>> Killed;

        // Les Skyrim qui voient cet ennemi (dernière synchronisation).
        public List<Source> SourcesOf(uint formId) => _sourcesOf.TryGetValue(formId, out List<Source> list) ? list : null;

        // Qui a frappé ce double en dernier (pour lui donner la mort : objets "à la mort", or).
        public void NoteAttacker(uint formId, GameObject attacker)
        {
            if (attacker != null) _lastAttacker[formId] = attacker;
        }

        // Appelé à chaque image (solo, ou hôte sans coéquipier) : met les doubles en accord avec la liste de Skyrim.
        public void Sync(BridgeWriter bridge, CharacterBody survivor, TerrainMirror terrain = null)
        {
            Source local = ReadLocal(bridge, survivor, terrain);
            if (local == null)
            {
                RemoveAll();
                return;
            }
            _single.Clear();
            _single.Add(local);
            SyncSources(_single, survivor);
        }

        // Notre Skyrim comme source d'ennemis (null : pas de liste récente, ou pas de survivant).
        public Source ReadLocal(BridgeWriter bridge, CharacterBody survivor, TerrainMirror terrain)
        {
            _localSource.Entries.Clear();
            if (survivor == null || !bridge.ReadActors(_localSource.Entries, out uint sequence)) return null;
            if (sequence != _lastSequence)
            {
                _lastSequence = sequence;
                _lastTableTime = Time.unscaledTime;
            }
            if (Time.unscaledTime - _lastTableTime > TableTimeoutSeconds) return null;  // Skyrim ne répond plus
            // Les positions envoyées par Skyrim sont relatives aux pieds du joueur Skyrim : on les place donc à partir
            // de ses pieds recopiés dans RoR2 (calage fixe du décor), et non à partir du survivant. Sinon, quand le
            // survivant décollait sur une bosse, tous les ennemis étaient placés trop haut dans RoR2... et ceux que
            // RoR2 déplaçait étaient renvoyés trop bas dans Skyrim, sous le sol.
            Vector3 origin = survivor.footPosition;
            if (terrain != null && terrain.TryGetSkyrimFeet(out Vector3 skyrimFeet)) origin = skyrimFeet;
            _localSource.Origin = origin;
            _localSource.Body = survivor;
            return _localSource;
        }

        // Toutes les sources à la fois (hôte d'une partie coop, ou solo avec une seule source).
        public void SyncSources(List<Source> sources, CharacterBody fallbackKiller)
        {
            foreach (Candidate c in _merged.Values) { c.All.Clear(); c.Driver = null; c.Dead = false; _candidatePool.Push(c); }
            _merged.Clear();
            foreach (Source source in sources)
            {
                source.Pushes.Clear();
                foreach (ActorEntry entry in source.Entries)
                {
                    if (!_merged.TryGetValue(entry.FormId, out Candidate c))
                    {
                        c = _candidatePool.Count > 0 ? _candidatePool.Pop() : new Candidate();
                        _merged[entry.FormId] = c;
                    }
                    c.All.Add(source);
                    if ((entry.Flags & BridgeInfo.ActorDead) != 0) c.Dead = true;
                    float distance = new Vector3(entry.RelX, entry.RelY, entry.RelZ).sqrMagnitude;
                    if (c.Driver == null || distance < c.Distance)
                    {
                        c.Driver = source;
                        c.Entry = entry;
                        c.Distance = distance;
                    }
                }
            }

            float now = Time.unscaledTime;
            if (_recentlyKilled.Count > 0)
            {
                _toRemove.Clear();
                foreach (var pair in _recentlyKilled) if (now - pair.Value > 10f) _toRemove.Add(pair.Key);
                foreach (uint id in _toRemove) _recentlyKilled.Remove(id);
            }

            foreach (Ghost ghost in _byFormId.Values) ghost.SeenThisFrame = false;
            _sourcesOf.Clear();
            _pushCount = 0;
            foreach (var pair in _merged)
            {
                uint formId = pair.Key;
                Candidate c = pair.Value;
                ActorEntry entry = c.Entry;
                Vector3 origin = c.Driver.Origin;
                Vector3 position = origin + new Vector3(entry.RelX, entry.RelY, entry.RelZ);
                _sourcesOf[formId] = new List<Source>(c.All);

                _byFormId.TryGetValue(formId, out Ghost ghost);
                if (ghost != null && ghost.Body == null)
                {
                    Forget(ghost);  // le double a disparu côté RoR2 (changement d'étape...)
                    ghost = null;
                }

                if (c.Dead)
                {
                    if (ghost != null)
                    {
                        KillGhost(ghost, KillerFor(formId, fallbackKiller));
                        _recentlyKilled[formId] = now;
                        // Les Skyrim où il est encore vivant : il y meurt aussi.
                        var alive = new List<Source>();
                        foreach (Source source in c.All)
                            foreach (ActorEntry e in source.Entries)
                                if (e.FormId == formId && (e.Flags & BridgeInfo.ActorDead) == 0) { alive.Add(source); break; }
                        if (alive.Count > 0) Killed?.Invoke(formId, alive);
                    }
                    continue;
                }

                if (ghost == null)
                {
                    if (_recentlyKilled.ContainsKey(formId)) continue;  // vient de mourir : pas de nouveau double
                    ghost = Spawn(formId, position);
                    if (ghost == null) continue;
                }

                ghost.SeenThisFrame = true;
                SetPassive(ghost, (entry.Flags & BridgeInfo.ActorPassive) != 0);
                ghost.SkyrimMaxHealth = entry.MaxHealth;
                ReleaseInputs(ghost.Body);  // à chaque image : aucun bouton enfoncé, aucun déplacement
                UpdateHitbox(ghost, entry);
                SetElite(ghost, (int)((entry.Flags >> BridgeInfo.ActorEliteShift) & BridgeInfo.ActorEliteMask));

                // Déplacé par RoR2 : on le laisse faire, et c'est Skyrim qui suit.
                bool pushed = UpdatePush(ghost, entry, c.Driver);
                if (!pushed)
                {
                    TeleportHelper.TeleportBody(ghost.Body, position);
                    if (ghost.Body.characterMotor != null) ghost.Body.characterMotor.velocity = Vector3.zero;  // ne tombe pas
                    ghost.Placed = position;
                    ghost.HasPlaced = true;
                }
                // Les autres joueurs qui voient cet ennemi : leur Skyrim le met à la place du double.
                if (c.All.Count > 1)
                {
                    Vector3 foot = pushed ? ghost.Body.footPosition : position;
                    foreach (Source source in c.All)
                        if (source != c.Driver) Emit(source, formId, foot);
                }
            }
            PublishPushes();

            // Les doubles dont l'ennemi n'est plus dans aucune liste (trop loin) : on les retire sans les tuer.
            _toRemove.Clear();
            foreach (Ghost ghost in _byFormId.Values)
            {
                if (!ghost.SeenThisFrame) _toRemove.Add(ghost.FormId);
            }
            foreach (uint formId in _toRemove) Remove(_byFormId[formId]);
        }

        // Le corps qui "tue" le double : le dernier joueur (ou allié) qui l'a frappé, sinon celui donné.
        private CharacterBody KillerFor(uint formId, CharacterBody fallback)
        {
            if (_lastAttacker.TryGetValue(formId, out GameObject attacker) && attacker != null)
            {
                CharacterBody body = attacker.GetComponent<CharacterBody>();
                // Un drone, une tourelle : c'est son propriétaire (le survivant) qui tue, comme avant.
                CharacterMaster owner = body != null && !body.isPlayerControlled && body.master != null && body.master.minionOwnership != null
                    ? body.master.minionOwnership.ownerMaster : null;
                if (owner != null && owner.GetBody() != null) body = owner.GetBody();
                if (body != null && body.isPlayerControlled && body.healthComponent != null && body.healthComponent.alive) return body;
            }
            if (fallback != null) return fallback;
            foreach (PlayerCharacterMasterController player in PlayerCharacterMasterController.instances)
            {
                CharacterBody body = player != null && player.master != null ? player.master.GetBody() : null;
                if (body != null) return body;
            }
            return null;
        }

        // Une place voulue pour un ennemi, vers le Skyrim de ce joueur.
        private void Emit(Source source, uint formId, Vector3 foot)
        {
            if (source.Local) AddPush(formId, foot - source.Origin);
            else source.Pushes.Add(new KeyValuePair<uint, Vector3>(formId, foot));
        }

        // 0.70 (coop, invité) : les places voulues envoyées par l'hôte (monde RoR2), recopiées vers notre Skyrim.
        public void PublishExternal(List<KeyValuePair<uint, Vector3>> pushes, Vector3 origin)
        {
            _pushCount = 0;
            if (pushes != null)
                foreach (var push in pushes) AddPush(push.Key, push.Value - origin);
            PublishPushes();
        }

        // Vrai si le double est en train d'être déplacé par RoR2 : on ne le replace pas sur l'ennemi Skyrim,
        // c'est au contraire l'ennemi Skyrim qui est amené là où est le double.
        private bool UpdatePush(Ghost ghost, ActorEntry entry, Source driver)
        {
            Vector3 origin = driver.Origin;
            if (!_pushEnabled.Value)
            {
                ghost.Pushed = false;
                return false;
            }
            CharacterMotor motor = ghost.Body.characterMotor;
            Vector3 foot = ghost.Body.footPosition;
            float now = Time.time;

            if (!ghost.Pushed)
            {
                if (!ghost.HasPlaced || entry.Height > MaxPushHeight) return false;
                Vector3 gap = foot - ghost.Placed;
                bool moved = (gap - ghost.PlacedOffset).sqrMagnitude > PushDistance * PushDistance;
                bool forced = motor != null && motor.velocity.sqrMagnitude > PushSpeed * PushSpeed;
                // Seulement juste après un coup du survivant ou de ses alliés (grappin, coup puissant...) :
                // sinon c'est RoR2 qui recale le double dans son décor (spawn dans un mur, chute d'une marche),
                // et l'ennemi Skyrim s'envolait ou passait à travers les murs.
                bool hitRecently = _lastHit.TryGetValue(ghost.FormId, out float hitAt) && now - hitAt < HitWindow;
                // Il ne fait que tomber (trou dans le décor recopié, marche) sans avoir été poussé : ce n'est pas un
                // déplacement à recopier, sinon l'ennemi Skyrim le suivait à travers le sol.
                Vector3 drift = gap - ghost.PlacedOffset;
                bool onlyFalling = !forced && drift.y < -0.2f && new Vector2(drift.x, drift.z).magnitude < 0.3f;
                if ((!moved && !forced) || !hitRecently || onlyFalling)
                {
                    // On apprend le petit écart normal (la façon dont RoR2 pose ce monstre au sol).
                    ghost.PlacedOffset = Vector3.Lerp(ghost.PlacedOffset, gap, 0.2f);
                    return false;
                }
                ghost.Pushed = true;
                ghost.PushedSince = now;
                ghost.SlowSince = -1f;
                ghost.LastFoot = foot;
                _pushLogged++;
                if (_pushLogged <= 10 || _pushLogged % 50 == 0)
                    _log.LogInfo($"Ennemi Skyrim {ghost.FormId:X8} deplace par RoR2 ({(forced ? "force" : "deplacement")}, total {_pushLogged})");
            }

            // Il ralentit et touche le sol : RoR2 a fini de le déplacer, Skyrim reprend la main.
            float speed = (foot - ghost.LastFoot).magnitude / Mathf.Max(Time.deltaTime, 0.001f);
            ghost.LastFoot = foot;
            // "Au sol" pour de vrai : RoR2 le dit ET il y a bien du sol juste sous ses pieds.
            // (Porté par un drone, RoR2 peut le croire au sol : il était alors relâché puis repris sans arrêt,
            // et l'ennemi Skyrim tombait un peu puis remontait d'un coup.)
            bool grounded = (motor == null || motor.isGrounded) &&
                            Physics.Raycast(foot + Vector3.up * 0.3f, Vector3.down, 0.9f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
            if (speed < 0.5f && grounded)
            {
                if (ghost.SlowSince < 0f) ghost.SlowSince = now;
            }
            else ghost.SlowSince = -1f;

            Vector3 relative = foot - origin;
            bool lost = relative.sqrMagnitude > 80f * 80f || relative.y < -40f;  // tombé dans le vide, parti trop loin
            // Il tombe et il n'y a plus aucun sol sous lui (trou dans le décor recopié) : RoR2 le perd, Skyrim reprend
            // la main tout de suite (l'ennemi Skyrim reste sur son vrai sol).
            if (!lost && motor != null && motor.velocity.y < -2f &&
                !Physics.Raycast(foot + Vector3.up * 0.3f, Vector3.down, 40f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
            {
                lost = true;
                _log.LogInfo($"Ennemi Skyrim {ghost.FormId:X8} : plus de sol sous son double dans RoR2, rendu a Skyrim");
            }
            if ((ghost.SlowSince >= 0f && now - ghost.SlowSince > 0.4f) || now - ghost.PushedSince > MaxPushSeconds || lost)
            {
                ghost.Pushed = false;
                ghost.HasPlaced = false;
                return false;
            }

            Emit(driver, ghost.FormId, foot);
            return true;
        }

        private void AddPush(uint formId, Vector3 relative)
        {
            if (_pushCount >= BridgeInfo.PushMax || !OpenPush()) return;
            IntPtr at = IntPtr.Add(_pushView, 16 + _pushCount * 16);
            Marshal.WriteInt32(at, 0, unchecked((int)formId));
            WriteFloat(at, 4, relative.x);   // est
            WriteFloat(at, 8, relative.z);   // nord
            WriteFloat(at, 12, relative.y);  // haut
            _pushCount++;
        }

        // Une fois par image : le nombre d'ennemis à déplacer et un numéro d'image (Skyrim sait ainsi que RoR2 tourne).
        private void PublishPushes()
        {
            if (!OpenPush()) return;
            Marshal.WriteInt32(_pushView, 4, _pushCount);
            Marshal.WriteInt32(_pushView, 8, unchecked((int)++_pushFrame));
            Marshal.WriteInt32(_pushView, 0, unchecked((int)BridgeInfo.PushMagic));
        }

        private static void WriteFloat(IntPtr at, int offset, float value)
        {
            Marshal.WriteInt32(at, offset, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
        }

        private bool OpenPush()
        {
            if (_pushView != IntPtr.Zero) return true;
            if (_pushOpenTried) return false;
            _pushOpenTried = true;
            IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.PushMapSize, BridgeInfo.PushMapName);
            if (mapping != IntPtr.Zero) _pushView = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.PushMapSize);
            if (_pushView == IntPtr.Zero) _log.LogWarning("Ennemis deplaces : pont impossible a ouvrir");
            return _pushView != IntPtr.Zero;
        }

        // Un double "paisible" (animal, passant) n'est pas une cible pour les drones, tourelles et objets qui visent
        // tout seuls : ses zones de coups ne sont pas dans la liste des cibles de RoR2 ("bullseye").
        // Les tirs du survivant le touchent quand même. S'il devient hostile dans Skyrim, il redevient une cible.
        private void SetPassive(Ghost ghost, bool passive)
        {
            if (ghost.Passive == passive || ghost.Body == null || ghost.Body.hurtBoxGroup == null) return;
            ghost.Passive = passive;
            if (passive)
            {
                ghost.Bullseyes.Clear();
                foreach (HurtBox box in ghost.Body.hurtBoxGroup.hurtBoxes)
                    if (box != null && box.isBullseye) ghost.Bullseyes.Add(box);
            }
            foreach (HurtBox box in ghost.Bullseyes)
            {
                if (box == null) continue;
                box.isBullseye = !passive;
                // La liste des cibles n'est mise à jour qu'à l'activation : on le désactive puis réactive.
                if (box.enabled) { box.enabled = false; box.enabled = true; }
            }
            _log.LogInfo($"Ennemi Skyrim {ghost.FormId:X8} : {(passive ? "paisible (ignore par les allies)" : "hostile (cible des allies)")}");
        }

        public void RemoveAll()
        {
            if (_byFormId.Count == 0) return;
            _toRemove.Clear();
            _toRemove.AddRange(_byFormId.Keys);
            foreach (uint formId in _toRemove) Remove(_byFormId[formId]);
        }

        private Ghost Spawn(uint formId, Vector3 position)
        {
            if (_masterPrefab == null)
            {
                _masterPrefab = MasterCatalog.FindMasterPrefab(GhostMasterName);
                if (_masterPrefab == null)
                {
                    if (!_prefabMissingLogged)
                    {
                        _log.LogError("Monstre introuvable pour les doubles : " + GhostMasterName);
                        _prefabMissingLogged = true;
                    }
                    return null;
                }
            }

            _recentSpawns.Add(new KeyValuePair<Vector3, float>(position, Time.unscaledTime));
            CharacterMaster master = new MasterSummon
            {
                masterPrefab = _masterPrefab,
                position = position,
                rotation = Quaternion.identity,
                teamIndexOverride = TeamIndex.Monster,
                ignoreTeamMemberLimit = true,
                useAmbientLevel = true,
            }.Perform();

            CharacterBody body = master != null ? master.GetBody() : null;
            if (body == null || body.healthComponent == null)
            {
                if (master != null) Object.Destroy(master.gameObject);
                return null;
            }

            // Pas d'intelligence : on la supprime complètement (elle ne pourra plus appuyer sur rien).
            foreach (BaseAI ai in master.GetComponents<BaseAI>())
            {
                ai.enabled = false;
                Object.Destroy(ai);
            }
            ReleaseInputs(body);

            // Le décor RoR2 est intangible : les doubles ne doivent pas se blesser en "tombant".
            body.bodyFlags |= CharacterBody.BodyFlags.IgnoreFallDamage;

            var ghost = new Ghost { FormId = formId, Master = master, Body = body, Health = body.healthComponent };
            try { CreateHitbox(ghost); }
            catch (Exception e) { _log.LogWarning("Double : boite de Skyrim impossible (" + e.GetType().Name + " : " + e.Message + "), on garde celle du Lemurien"); }
            _byFormId[formId] = ghost;
            _byHealth[body.healthComponent] = formId;
            _log.LogInfo("Double cree pour l'ennemi Skyrim " + formId.ToString("X8"));
            return ghost;
        }

        // ---------- La boîte de Skyrim ----------
        //
        // Le Lémurien a ses propres zones de coups (sa taille à lui) : un rat ou un dragon avaient donc la taille
        // d'un Lémurien, et les tirs passaient à travers. On éteint ses zones et on lui en donne une seule : une boîte
        // à la taille de la boîte de collision de Skyrim, tournée comme l'ennemi, posée sur ses pieds.
        private static readonly BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private int _hitboxLogged;
        private int _killErrors;

        private static void SetMember(object target, string name, object value)
        {
            Type type = target.GetType();
            FieldInfo field = type.GetField(name, AnyMember);
            if (field != null && field.FieldType.IsInstanceOfType(value)) { field.SetValue(target, value); return; }
            if (field != null) { try { field.SetValue(target, Convert.ChangeType(value, field.FieldType)); return; } catch (Exception) { } }
            PropertyInfo property = type.GetProperty(name, AnyMember);
            MethodInfo setter = property != null ? property.GetSetMethod(true) : null;
            if (setter != null)
            {
                object converted = property.PropertyType.IsInstanceOfType(value) ? value : Convert.ChangeType(value, property.PropertyType);
                setter.Invoke(target, new[] { converted });
                return;
            }
            // Propriété automatique sans setter : son champ caché.
            FieldInfo backing = type.GetField("<" + name + ">k__BackingField", AnyMember);
            backing?.SetValue(target, value);
        }

        private void CreateHitbox(Ghost ghost)
        {
            CharacterBody body = ghost.Body;
            HurtBoxGroup group = body.hurtBoxGroup;
            if (group == null || group.hurtBoxes == null) return;

            bool trigger = false;
            foreach (HurtBox box in group.hurtBoxes)
            {
                if (box == null) continue;
                Collider original = box.GetComponent<Collider>();
                if (original != null)
                {
                    trigger = original.isTrigger;
                    original.enabled = false;  // la forme du Lémurien ne compte plus
                }
                if (box.isBullseye)
                {
                    box.isBullseye = false;     // c'est notre boîte que les alliés et les tirs à tête chercheuse visent
                    if (box.enabled) { box.enabled = false; box.enabled = true; }
                }
            }

            var holder = new GameObject("SkyrimRoR2_Hitbox");
            holder.SetActive(false);
            holder.layer = LayerIndex.entityPrecise.intVal;
            holder.transform.SetParent(body.transform, false);
            BoxCollider collider = holder.AddComponent<BoxCollider>();
            collider.isTrigger = trigger;
            collider.size = Vector3.one;
            HurtBox hurt = holder.AddComponent<HurtBox>();
            hurt.healthComponent = body.healthComponent;
            hurt.hurtBoxGroup = group;
            hurt.isBullseye = true;
            SetMember(hurt, "collider", collider);
            SetMember(hurt, "teamIndex", TeamIndex.Monster);

            // Ajoutée à la liste du groupe (équipe, cibles), et devient sa zone principale.
            var boxes = new List<HurtBox>(group.hurtBoxes) { hurt };
            group.hurtBoxes = boxes.ToArray();
            SetMember(hurt, "indexInGroup", (short)(boxes.Count - 1));
            group.mainHurtBox = hurt;
            SetMember(body, "mainHurtBox", hurt);

            holder.SetActive(true);
            ghost.HitboxObject = holder;
            ghost.Hitbox = collider;
        }

        private void UpdateHitbox(Ghost ghost, ActorEntry entry)
        {
            if (ghost.Hitbox == null || ghost.Body == null) return;
            float height = Mathf.Clamp(entry.Height, 0.2f, 40f);
            float width = entry.Width > 0.05f ? entry.Width : height * 0.4f;
            float length = entry.Length > 0.05f ? entry.Length : height * 0.4f;
            Transform t = ghost.HitboxObject.transform;
            t.position = ghost.Body.footPosition + Vector3.up * (height * 0.5f);
            t.rotation = Quaternion.Euler(0f, entry.Yaw * Mathf.Rad2Deg, 0f);
            Vector3 scale = t.parent != null ? t.parent.lossyScale : Vector3.one;
            ghost.Hitbox.size = new Vector3(width / Mathf.Max(scale.x, 0.01f), height / Mathf.Max(scale.y, 0.01f), length / Mathf.Max(scale.z, 0.01f));
            if (_hitboxLogged < 15)
            {
                _hitboxLogged++;
                _log.LogInfo($"Double {entry.FormId:X8} : boite de Skyrim {width:F1} x {length:F1} x {height:F1} m");
            }
        }

        // Relâche tous les "boutons" d'un double : il ne peut ni attaquer, ni sauter, ni bouger.
        private static void ReleaseInputs(CharacterBody body)
        {
            InputBankTest bank = body != null ? body.inputBank : null;
            if (bank == null) return;
            bank.skill1.PushState(false);
            bank.skill2.PushState(false);
            bank.skill3.PushState(false);
            bank.skill4.PushState(false);
            bank.jump.PushState(false);
            bank.sprint.PushState(false);
            bank.moveVector = Vector3.zero;
        }

        // Vrai si cet objet (un attaquant) est un double.
        public bool IsGhostObject(GameObject attacker)
        {
            if (attacker == null) return false;
            CharacterBody body = attacker.GetComponent<CharacterBody>();
            return IsGhost(body);
        }

        // L'ennemi est mort dans Skyrim : le double meurt aussi, tué par le survivant.
        private void KillGhost(Ghost ghost, CharacterBody survivor)
        {
            Forget(ghost);
            if (ghost.Body == null || ghost.Body.healthComponent == null) return;

            AllowRealDamage = true;
            try
            {
                // Son intelligence a été retirée : RoR2 ne doit plus la chercher à sa mort (erreur, et pas d'or donné).
                if (ghost.Master != null) { try { SetMember(ghost.Master, "aiComponents", new BaseAI[0]); } catch (Exception) { } }
                GameObject killer = survivor != null ? survivor.gameObject : null;
                try { ghost.Body.healthComponent.Suicide(killer, killer); }
                catch (Exception e)
                {
                    // RoR2 cherche l'intelligence (retirée) du double à sa mort : l'erreur ne doit pas couper la suite.
                    if (_killErrors++ < 3) _log.LogInfo("Double tue (erreur RoR2 ignoree : " + e.GetType().Name + ")");
                }
            }
            finally
            {
                AllowRealDamage = false;
            }
            _log.LogInfo("Ennemi Skyrim " + ghost.FormId.ToString("X8") + " mort : double tue");
        }

        // L'ennemi est parti (trop loin) : on efface son double, sans récompense.
        private void Remove(Ghost ghost)
        {
            Forget(ghost);
            if (ghost.Master != null) Object.Destroy(ghost.Master.gameObject);
            if (ghost.Body != null) Object.Destroy(ghost.Body.gameObject);
        }

        private void Forget(Ghost ghost)
        {
            _byFormId.Remove(ghost.FormId);
            _lastHit.Remove(ghost.FormId);
            _lastAttacker.Remove(ghost.FormId);
            if ((object)ghost.Health != null) _byHealth.Remove(ghost.Health);
        }
    }
}
