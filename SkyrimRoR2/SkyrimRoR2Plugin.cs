using System;
using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // Cette ligne présente le mod à BepInEx : son identifiant, son nom, sa version.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class SkyrimRoR2Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "opidox.skyrimror2";
        public const string PluginName = "SkyrimRoR2";
        public const string PluginVersion = "0.73.9";

        // Au-delà de ce délai sans nouvelles de Skyrim, on rend la main au clavier de RoR2.
        private const float InputTimeoutSeconds = 0.5f;

        // Pour que le "crochet" (voir plus bas) puisse retrouver le mod.
        internal static SkyrimRoR2Plugin Instance { get; private set; }

        private BridgeWriter _bridge;
        private uint _sequence;

        // Les doubles des ennemis Skyrim (voir GhostManager.cs).
        private GhostManager _ghosts;
        private int _damageEventsSent;

        // Les monstres normaux de RoR2 (voir MonsterBlocker.cs), réglable dans r2modman.
        private MonsterBlocker _monsterBlocker;
        private BepInEx.Configuration.ConfigEntry<bool> _blockRoR2Monsters;

        // La carte RoR2 rendue intangible (voir MapNeutralizer.cs), réglable dans r2modman.
        private MapNeutralizer _mapNeutralizer;
        private BepInEx.Configuration.ConfigEntry<bool> _neutralizeMap;

        // Le relief de Skyrim recopié dans RoR2 (voir TerrainMirror.cs), réglable dans r2modman.
        private TerrainMirror _terrain;
        private GroundSnap _groundSnap;
        private NavMirror _nav;
        private BepInEx.Configuration.ConfigEntry<bool> _mirrorTerrain;
        private BepInEx.Configuration.ConfigEntry<bool> _showTerrain;
        private BepInEx.Configuration.ConfigEntry<float> _airControl;

        // Les variantes de compétences choisies dans Skyrim (voir LoadoutApplier.cs).
        private LoadoutApplier _loadouts;

        // Partie automatique demandée par Skyrim (voir AutoRun.cs).
        private AutoRun _autoRun;
        private BepInEx.Configuration.ConfigEntry<bool> _quitWithSkyrim;
        private bool _pendingSurvivorChange;
        private float _bodyMissingSince = -1f;

        // Filet de sécurité : si après un changement de survivant on se retrouve sans corps (survivant pas
        // jouable...), on revient au Commando au bout de 3 secondes.
        private void WatchMissingBody(CharacterMaster master)
        {
            bool missing = Run.instance != null && master != null && master.GetBody() == null &&
                           !master.IsDeadAndOutOfLivesServer() && _autoRun != null && _autoRun.SkyrimConnected &&
                           Time.unscaledTime - _autoRun.LastChangeTime < 15f;  // seulement juste après un changement
            if (!missing) { _bodyMissingSince = -1f; return; }
            if (_bodyMissingSince < 0f) { _bodyMissingSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _bodyMissingSince < 3f) return;

            _bodyMissingSince = -1f;
            Logger.LogWarning("Plus de survivant depuis 3 secondes : retour au Commando");
            if (_autoRun.SurvivorOverride != null) _autoRun.SurvivorOverride.Value = "Commando";
            master.bodyPrefab = BodyCatalog.FindBodyPrefab("CommandoBody");
            master.Respawn(master.deathFootPosition, Quaternion.identity);
        }
        // Le HUD de RoR2 envoyé à Skyrim (voir HudPublisher.cs).
        private HudPublisher _hud;
        private LootReceiver _loot;
        private InventorySync _inventory;
        private LevelSync _levels;  // 0.66 : le niveau RoR2 suit le niveau Skyrim
        private AudioKeeper _audio; // 0.66 : les sons de RoR2 pendant le jeu dans Skyrim
        private Coop _coop;         // 0.70 : la coop (voir Coop.cs)
        private Func<uint, bool, Vector3, Vector3, Vector3?> _coopOffset;
        private SurvivorStreamer[] _teammateViews;  // 0.70 : les coéquipiers filmés pour notre Skyrim
        internal LootReceiver LootForSync => _loot;
        internal LoadoutApplier LoadoutsForSync => _loadouts;
        internal AutoRun AutoRunForSync => _autoRun;
        internal BepInEx.Logging.ManualLogSource Logger_ => Logger;

        // Les bras du survivant filmés pour Skyrim (voir ViewmodelStreamer.cs).
        private ViewmodelStreamer _viewmodel;
        private WorldStreamer _world;
        private float _nextSlowLog;
        private AllyMirror _allies;
        private EffectMirror _effects;
        private SpriteMirror _sprites;

        // Pour calculer la vitesse du survivant.
        private const float MaxPlausibleSpeed = 80f;   // m/s ; au-delà, c'est une téléportation
        private const float VelocitySmoothing = 0.5f;  // 1 = pas de lissage
        private const float DashMargin = 1.5f;         // m/s d'avance pour reconnaître une ruée
        private Vector3 _lastPosition;
        private CharacterBody _lastBody;
        private bool _hadLastPosition;
        private Vector3 _smoothedVelocity;

        // Petites téléportations (blink...) : au-delà de MaxPlausibleSpeed mais moins de 60 m d'un coup.
        private const float MaxBlinkDistance = 60f;
        private const float MinBlinkDistance = 1.5f;  // en dessous : un mouvement normal sur une image très courte (RoR2 caché tourne très vite)
        private Vector3 _blinkTotal;

        // Le mod vient de replacer le survivant lui-même (TerrainMirror) : à ne pas transmettre à Skyrim.
        internal static float ReplacedUntil;
        internal static void MarkReplaced() => ReplacedUntil = Time.unscaledTime + 0.15f;
        private float _podCooldownUntil;  // capsule du début de partie : on ignore sa chute
        private float _lastSampleTime;    // moment du dernier vrai déplacement mesuré
        private Vector3 _rawVelocity;     // dernière vitesse mesurée (sans lissage)

        // Dernières commandes reçues de Skyrim.
        private SkyrimInput _input;
        private bool _driving;
        private uint _lastInputSequence;
        private float _lastInputTime = -100f;
        private int _inputFrame = -1;

        // État des boutons : cette image-ci et l'image précédente
        // (RoR2 a besoin des deux pour savoir si on vient d'appuyer).
        private const int Jump = 0, Skill1 = 1, Skill2 = 2, Skill3 = 3, Skill4 = 4, Equipment = 5, ButtonCount = 6;
        private readonly bool[] _buttonNow = new bool[ButtonCount];
        private readonly bool[] _buttonBefore = new bool[ButtonCount];

        // Appelé une seule fois, quand RoR2 charge le mod au démarrage.
        private void Awake()
        {
            Camera.onPreCull += PlaceRoR2Camera;
            Instance = this;
            Logger.LogInfo("Bonjour depuis SkyrimRoR2 ! Le mod est bien charge.");
            ScreenKeeper.Save(Logger);  // taille d'écran du RoR2 normal, remise à la fermeture

            // RoR2 doit continuer à tourner quand c'est la fenêtre de Skyrim qui est active.
            Application.runInBackground = true;

            _bridge = new BridgeWriter();
            if (_bridge.IsOpen)
                Logger.LogInfo("Pont ouvert : " + BridgeInfo.MapName);
            else
                Logger.LogError("Impossible d'ouvrir le pont (erreur Windows " + _bridge.LastError + ")");

            // Pose les crochets : notre code s'exécute juste après la lecture du clavier par RoR2,
            // et juste avant que l'état du survivant ne lise les boutons (saut, compétences).
            var harmony = new Harmony(PluginGUID);
            InstallPatch(harmony, typeof(PlayerInputPatch), "lecture du clavier RoR2");
            InstallPatch(harmony, typeof(GatherInputsPatch), "lecture des boutons par le survivant");
            InstallPatch(harmony, typeof(PerformInputsPatch), "declenchement du saut et des competences");
            InstallPatch(harmony, typeof(DamagePatch), "degats infliges aux doubles des ennemis Skyrim");
            InstallPatch(harmony, typeof(GameOverPatch), "pas de fin de partie RoR2 a la mort (Skyrim recharge)");
            Tracked.Install(harmony, Logger);
            InstallPatch(harmony, typeof(LevelSync.GiveExperiencePatch), "pas d'experience RoR2 des ennemis (niveau suivi sur Skyrim)");
            _audio = new AudioKeeper(Logger, Config);
            _audio.Install(harmony);

            _ghosts = new GhostManager(Logger, Config);

            _blockRoR2Monsters = ConfigMove.Bind(Config, "Combat", "2 - Jeu", "DesactiverMonstresRoR2", true,
                "Empeche les monstres normaux de RoR2 d'apparaitre : les ennemis viennent de Skyrim.");
            _monsterBlocker = new MonsterBlocker(Logger);

            _neutralizeMap = ConfigMove.Bind(Config, "Monde", "2 - Jeu", "NeutraliserCarteRoR2", true,
                "Rend la carte de RoR2 intangible et pose un sol plat invisible : c'est Skyrim qui fournit le monde.");
            _mapNeutralizer = new MapNeutralizer(Logger);

            _mirrorTerrain = ConfigMove.Bind(Config, "Monde", "2 - Jeu", "ReliefSkyrim", true,
                "Recopie le relief de Skyrim autour du joueur (surface invisible sous le survivant) : pentes, rochers et murs " +
                "existent aussi pour les competences. Sinon, le survivant marche sur un petit plateau plat.");
            _showTerrain = ConfigMove.Bind(Config, "Verification", "9 - Verification", "AfficherRelief", true,
                "Affiche le relief recopie de Skyrim dans RoR2 (sols gris clair, murs gris fonce, transparents), pour verifier " +
                "qu'il colle au monde de Skyrim. Change en direct. N'apparait pas sur les mains filmees pour Skyrim.");
            _terrain = new TerrainMirror(Logger);
            _groundSnap = new GroundSnap(Logger, Config);
            _skillSource = new SkillSource(Logger, _bridge, GetLocalBody);
            _nav = new NavMirror(Logger);
            _airControl = ConfigMove.Bind(Config, "Mouvement", "2 - Jeu", "ControleEnLair", 0.7f,
                "Controle de la direction en l'air (sauts, grappin, chutes) avec les touches de deplacement. " +
                "0.25 = comme RoR2 d'origine, 1 = autant qu'au sol. 0 = ne pas toucher.");

            _coop = new Coop(Logger, Config);
            _coopOffset = _coop.OffsetFor;
            _autoRun = new AutoRun(Logger);
            _autoRun.SurvivorOverride = ConfigMove.Bind(Config, "Lancement", "1 - Lancement", "Survivant", "",
                "Survivant joue (vide = celui de SkyrimRoR2.ini). Nom anglais ou francais : Commando, Huntress, Bandit, Toolbot (MUL-T), " +
                "Engi, Mage (Artificer), Merc, Treebot (REX), Loader, Croco (Acrid), Captain, Railgunner, VoidSurvivor, Seeker, FalseSon, Chef... " +
                "Change en pleine partie si on modifie ce reglage pendant le jeu.");
            _autoRun.SurvivorOverride.SettingChanged += (sender, args) => _pendingSurvivorChange = true;
            _quitWithSkyrim = ConfigMove.Bind(Config, "Lancement", "1 - Lancement", "QuitterAvecSkyrim", true,
                "Ferme RoR2 tout seul 10 secondes apres la fermeture de Skyrim (sinon sa fenetre cachee reapparait).");
            _hud = new HudPublisher(Logger);
            _loot = new LootReceiver(Logger);
            _inventory = new InventorySync(Logger);
            _levels = new LevelSync(Logger, Config);
            _loadouts = new LoadoutApplier(Logger);
            _viewmodel = new ViewmodelStreamer(Logger, Config);
            _world = new WorldStreamer(Logger, Config);
            _survivorView = new SurvivorStreamer(Logger, Config);
            _survivorView.Terrain = _terrain;  // 0.66 : caché derrière le décor recopié de Skyrim
            _teammateViews = new SurvivorStreamer[BridgeInfo.TeammateViewMapNames.Length];
            for (int i = 0; i < _teammateViews.Length; i++)
                _teammateViews[i] = new SurvivorStreamer(Logger, Config, BridgeInfo.TeammateViewMapNames[i], "coequipier " + (i + 1)) { Terrain = _terrain };
            _allies = new AllyMirror(Logger, Config);
            _allies.ShowWholeBody = () => _viewmodel?.ShowWholeBody();
            _allies.HideAgain = b => _viewmodel?.HideAgain(b);
            _effects = new EffectMirror(Logger, Config);
            // Les vrais effets de RoR2 (particules, traînées, lasers), branchés sur les tirs et alliés affichés.
            _sprites = new SpriteMirror(Logger, Config);
            _sprites.RegisterName = n => _allies.AddName(n);
            _sprites.ShotFrame = id => _allies.ShotFrameOf(id);
            _sprites.BodyScale = b => _allies.BodyScale(b);
            _allies.SpritesCover = go => _sprites.Covers(go);
            _allies.PrepareSprites = go => _sprites.PrepareFrom(go);
            _allies.PrepareEffects = n => _sprites.PrepareEffectCatalog(n);
            _allies.ExportSprites = n => _sprites.ExportQueued(n);
            _effects.Covered = go => _sprites.Covers(go);
        }

        private void InstallPatch(Harmony harmony, Type patch, string what)
        {
            try
            {
                harmony.PatchAll(patch);
                Logger.LogInfo("Crochet installe : " + what);
            }
            catch (Exception e)
            {
                Logger.LogError("Crochet impossible (" + what + ") : " + e.Message);
            }
        }

        // Pause de Skyrim (menu, inventaire, console, autre fenêtre...) : le survivant reste figé dans RoR2.
        // Sinon, mis en pause en plein saut, il continuait de tomber dans RoR2 pendant toute la pause et prenait
        // d'énormes dégâts de chute à la reprise.
        private bool _frozen;
        private Vector3 _frozenAt;

        private void FreezeWhileSkyrimPaused(CharacterBody body)
        {
            RefreshInput();
            bool connected = _bridge != null && _bridge.IsOpen && Time.unscaledTime - _lastInputTime < InputTimeoutSeconds;
            bool paused = connected && _input.Active != 1 && body != null && body.characterMotor != null &&
                          body.healthComponent != null && body.healthComponent.alive && !AutoRun.InVehicle(body);
            if (!paused)
            {
                if (_frozen) Logger.LogInfo("Pause de Skyrim terminee : survivant libere");
                _frozen = false;
                return;
            }
            CharacterMotor motor = body.characterMotor;
            if (!_frozen)
            {
                _frozen = true;
                _frozenAt = body.footPosition;  // TeleportBody place les pieds
                Logger.LogInfo("Pause de Skyrim : survivant fige dans RoR2");
            }
            motor.velocity = Vector3.zero;
            if ((body.footPosition - _frozenAt).sqrMagnitude > 0.0025f)
            {
                MarkReplaced();
                TeleportHelper.TeleportBody(body, _frozenAt);
                motor.velocity = Vector3.zero;
            }
        }

        // Une variante à plusieurs charges (Flash phasique : 3) remplacée par une variante à une charge (Flash) gardait
        // ses charges : "3 sur 1", le Flash s'utilisait trois fois avant de recharger. On les ramène au maximum.
        private static void ClampSkillStocks(CharacterBody body)
        {
            SkillLocator locator = body != null ? body.skillLocator : null;
            if (locator == null) return;
            foreach (GenericSkill skill in new[] { locator.primary, locator.secondary, locator.utility, locator.special })
            {
                if (skill != null && skill.maxStock > 0 && skill.stock > skill.maxStock) skill.stock = skill.maxStock;
            }
        }

        // Appelé à chaque image : on publie l'état du survivant pour Skyrim.
        // Réglages changés dans r2modman pendant la partie : relus toutes les 2 s (cadrage des mains...).
        private float _nextConfigCheck;
        private DateTime _configStamp;
        private void ReloadConfigIfChanged()
        {
            if (Time.unscaledTime < _nextConfigCheck) return;
            _nextConfigCheck = Time.unscaledTime + 2f;
            try
            {
                DateTime stamp = System.IO.File.GetLastWriteTimeUtc(Config.ConfigFilePath);
                if (_configStamp == default) { _configStamp = stamp; return; }
                if (stamp == _configStamp) return;
                _configStamp = stamp;
                // Nos propres enregistrements changent aussi la date : on ne relit que si le contenu diffère.
                string text = System.IO.File.ReadAllText(Config.ConfigFilePath);
                if (text == _configText) return;
                Config.Reload();
                _configText = System.IO.File.ReadAllText(Config.ConfigFilePath);
                Logger.LogInfo("Reglages : fichier change dans r2modman, relu pendant la partie");
            }
            catch (Exception) { }
        }
        private string _configText;

        private void Update()
        {
            ReloadConfigIfChanged();
            if (_bridge == null || !_bridge.IsOpen) return;

            var data = new BridgeData
            {
                Magic = BridgeInfo.MagicValue,
                Version = BridgeInfo.VersionValue,
                Sequence = ++_sequence,
            };

            CharacterBody body = GetLocalBody();
            FreezeWhileSkyrimPaused(body);
            ClampSkillStocks(body);
            if (body != null)
            {
                Vector3 position = body.corePosition;
                data.InRun = 1;
                data.PosX = position.x;
                data.PosY = position.y;
                data.PosZ = position.z;

                HealthComponent health = body.healthComponent;
                if (health != null)
                {
                    // Vie + bouclier (et bouclier du vide) : avec la Transcendance, presque toute la vie est du bouclier.
                    data.Health = health.combinedHealth;
                    data.MaxHealth = health.fullCombinedHealth;
                }

                // Vitesse = déplacement depuis l'image précédente / temps écoulé, lissée un peu.
                Vector3 velocity = Vector3.zero;

                // Dans la capsule du début (et une seconde après en être sorti), la chute
                // de la capsule ne doit pas être recopiée dans Skyrim : on n'envoie rien.
                bool inPod = AutoRun.InVehicle(body);
                if (inPod) _podCooldownUntil = Time.unscaledTime + 1f;
                // Nos propres remises en place du survivant (décor) ne sont pas des déplacements à copier dans Skyrim.
                bool replaced = Time.unscaledTime < ReplacedUntil;
                bool ignoreMotion = inPod || Time.unscaledTime < _podCooldownUntil || replaced;

                // Le survivant ne bouge qu'aux pas de physique (50 par seconde) : quand RoR2 tourne
                // plus vite que ça (fenêtre cachée), beaucoup d'images n'ont aucun déplacement.
                // On mesure donc le déplacement depuis le dernier vrai mouvement, pas depuis l'image d'avant.
                bool sampled = false;
                float now = Time.time;
                float elapsed = now - _lastSampleTime;
                if (!ignoreMotion && _hadLastPosition && body == _lastBody)
                {
                    Vector3 delta = position - _lastPosition;
                    float distance = delta.magnitude;
                    if (distance > 0.0001f && elapsed > 0.0001f)
                    {
                        sampled = true;
                        if (distance <= MaxPlausibleSpeed * elapsed)
                        {
                            velocity = delta / elapsed;  // déplacement normal
                        }
                        else if (distance >= MinBlinkDistance && distance <= MaxBlinkDistance)
                        {
                            // Petite téléportation (blink de la Huntress...) : on la transmet à Skyrim.
                            _blinkTotal += new Vector3(delta.x, 0f, delta.z);
                            Logger.LogInfo($"Teleportation courte transmise a Skyrim : {distance:F1} m");
                        }
                        // Sinon : changement d'étape, réapparition... on l'ignore.
                    }
                    else if (elapsed < 0.1f)
                    {
                        velocity = _rawVelocity;  // pas encore de nouveau pas de physique : on garde la mesure d'avant
                    }
                }
                else
                {
                    sampled = true;
                }

                if (sampled)
                {
                    _rawVelocity = velocity;
                    _lastPosition = position;
                    _lastSampleTime = now;
                    _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, velocity, VelocitySmoothing);
                }
                else if (elapsed >= 0.1f)
                {
                    _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, Vector3.zero, VelocitySmoothing);  // immobile
                }
                _lastBody = body;
                _hadLastPosition = true;

                // Le survivant est-il en l'air (saut, chute) ? Skyrim s'en sert pour sauter aussi.
                data.SurvivorAirborne = !ignoreMotion && body.characterMotor != null && !body.characterMotor.isGrounded ? 1 : 0;

                // Vitesse "voulue" : la direction demandée x la vitesse du survivant
                // (sa statistique, qui compte déjà le sprint et les objets).
                // Elle ne dépend pas des murs de la carte RoR2, invisibles dans Skyrim.
                Vector3 intended = Vector3.zero;
                if (body.inputBank != null)
                {
                    Vector3 direction = body.inputBank.moveVector;
                    direction.y = 0f;
                    if (direction.sqrMagnitude > 1f) direction.Normalize();
                    intended = direction * body.moveSpeed;
                }

                // On envoie la vitesse voulue... sauf si la vraie est nettement plus grande :
                // c'est alors une ruée de compétence (glissade, saut de la Huntress...).
                Vector3 actualFlat = new Vector3(_smoothedVelocity.x, 0f, _smoothedVelocity.z);
                Vector3 horizontal = actualFlat.magnitude > intended.magnitude + DashMargin ? actualFlat : intended;

                if (ignoreMotion)
                {
                    horizontal = Vector3.zero;
                    _smoothedVelocity = Vector3.zero;
                }
                data.VelX = horizontal.x;
                data.VelY = _smoothedVelocity.y;  // la vraie vitesse verticale : sert à détecter les sauts
                data.VelZ = horizontal.z;
            }
            else
            {
                _hadLastPosition = false;
                _smoothedVelocity = Vector3.zero;
            }

            // Mort "pour de bon" (plus de vie de rechange) : Skyrim tuera aussi le joueur.
            CharacterMaster master = LocalUserManager.GetFirstLocalUser()?.cachedMaster;
            // Objets de la sauvegarde Skyrim, et retour du survivant après un rechargement (avant de dire s'il est mort).
            try { _inventory?.Update(_bridge, master); }
            catch (Exception e) { Logger.LogWarning("Objets de la sauvegarde : " + e.Message); }
            try { _levels?.Update(_bridge, master); }
            catch (Exception e) { Logger.LogWarning("Niveau : " + e.Message); }
            data.SurvivorDead = (master != null && master.IsDeadAndOutOfLivesServer()) || InventorySync.SurvivorDown ? 1 : 0;
            // 0.70 (coop) : à terre, on attend d'être relevé ; seulement "tombé pour de bon" quand la coop le dit.
            if (_coop != null && _coop.Active) data.SurvivorDead = _coop.LocalDead ? 1 : 0;

            data.BlinkTotalX = _blinkTotal.x;
            data.BlinkTotalZ = _blinkTotal.z;
            data.Scope = ScopeLevel(GetLocalBody());
            data.Survivor = SurvivorNameOf(GetLocalBody());
            data.Cloaked = body != null && body.hasCloakBuff ? 1 : 0;
            _bridge.Write(data);

            // Les doubles des ennemis Skyrim suivent la liste envoyée par Skyrim (et la difficulté de Skyrim).
            if (_ghosts != null && _bridge.ReadCommand(out SkyrimCommand orders)) _ghosts.Difficulty = Mathf.Max(0f, orders.Difficulty);
            if (_coop != null && _coop.ClientActive)
            {
                // 0.70 (coop, invité) : les doubles sont chez l'hôte (voir Coop.ClientUpdate).
            }
            else if (_coop != null && _coop.HostWithPeers && _ghosts != null)
            {
                // 0.70 (coop, hôte) : les ennemis de tous les Skyrim.
                _ghosts.SyncSources(_coop.Sources(_ghosts.ReadLocal(_bridge, body, _terrain)), body);
                _coop.SendPushes();
            }
            else _ghosts?.Sync(_bridge, body, _terrain);

            // La carte RoR2 ne gêne plus : Skyrim fournit les murs et le relief.
            if (_neutralizeMap != null && _neutralizeMap.Value)
            {
                // Le relief de Skyrim d'abord : tant qu'il est là, le petit plateau plat est rangé.
                bool hasGround = _bridge.ReadCommand(out SkyrimCommand ground);
                float groundGap = hasGround ? ground.GroundGap : -1f;
                if (_terrain != null)
                {
                    _terrain.PlaceId = _input.PlaceId;
                    _terrain.PlaceInterior = _input.Interior == 1;
                    _terrain.CoopOffset = _coop != null && _coop.Active ? _coopOffset : null;  // 0.70 : calage commun en coop
                }
                _terrain?.Update(body, _mirrorTerrain != null && _mirrorTerrain.Value, groundGap, hasGround ? ground.VerticalSpeed : 0f,
                    _showTerrain != null && _showTerrain.Value, hasGround ? ground.CeilingHeight : -1f);
                _mapNeutralizer.Update(body, groundGap, _terrain != null && _terrain.Active);
                // Les chemins des alliés (tourelles, drones...) dans le décor de Skyrim.
                _nav?.Update(body, _terrain != null && _terrain.DecorActive);
            }

            // Pas de monstres RoR2 "normaux" : les ennemis viennent de Skyrim.
            if (_ghosts != null && _blockRoR2Monsters != null && _blockRoR2Monsters.Value && UnityEngine.Networking.NetworkServer.active)
                _monsterBlocker.Update(_ghosts);  // (0.70 : seulement l'hôte, les monstres sont à lui)

            // 0.70 : la coop (hôte ou invité).
            try
            {
                if (_coop != null)
                {
                    bool hasOrders = _bridge.ReadCommand(out SkyrimCommand coopCommand);
                    _coop.SetLocalPlace(_input.PlaceId);
                    Vector3 coopFeet = Vector3.zero;
                    bool feetKnown = _terrain != null && _terrain.TryGetSkyrimFeet(out coopFeet);
                    _coop.SetLocalFeet(feetKnown, feetKnown ? coopFeet : Vector3.zero);
                    _coop.Update(_bridge, body, _terrain, _ghosts, _input, coopCommand, hasOrders);
                }
            }
            catch (Exception e) { Logger.LogWarning("Coop : " + e.Message); }

            // Les coups reçus dans Skyrim blessent le survivant.
            ApplySkyrimHits(body);

            // Menus passés tout seuls quand Skyrim le demande.
            _autoRun?.Update(_bridge, data.SurvivorDead == 1);
            _autoRun?.LeavePod(body);
            if (UnityEngine.Networking.NetworkServer.active) WatchMissingBody(master);
            if (_pendingSurvivorChange && _autoRun != null)
            {
                _pendingSurvivorChange = false;
                _autoRun.ChangeSurvivorNow(_autoRun.SurvivorOverride.Value);
            }
            _autoRun?.PreventPause();
            _autoRun?.WatchSkyrimGone(_quitWithSkyrim.Value);

            // Les variantes de compétences choisies dans le menu des pouvoirs de Skyrim.
            _loadouts?.Update(_bridge, body);

            // Le HUD (vie, compétences, objets...) pour que Skyrim l'affiche.
            _hud?.Update(_bridge, body, master);

            // Les objets RoR2 gagnés dans Skyrim (coffres...).
            _loot?.Update(_bridge, body);

            // La caméra de RoR2 regarde là où regarde Skyrim (sinon, au départ, elle fixe le ciel
            // collée au survivant, qui devient transparent).
            AlignRoR2Camera(body);

            // Les bras du survivant, filmés pour la première personne de Skyrim.
            _viewmodel?.Update(body, _autoRun != null && _autoRun.SkyrimConnected && !ThirdPersonView);
        }

        // Équilibrage : un coup qui retirerait 10 % des PV du double (un Lémurien du niveau
        // de la partie) retire 10 % des PV de l'ennemi Skyrim... divisé par sa "robustesse".
        // Un bandit (~100 PV) a une robustesse de 1 : autant de coups qu'un Lémurien.
        // Un géant (~1000 PV) environ 2,6, un dragon (~3000 PV) environ 4,5 : ils restent coriaces.
        private const float ReferenceSkyrimHealth = 150f;

        private static float ConvertToSkyrimDamage(float ror2Damage, float skyrimMax, float ghostMax)
        {
            if (!(ror2Damage > 0f)) return 0f;
            if (!(skyrimMax > 0f) || !(ghostMax > 0f)) return ror2Damage;  // infos manquantes : dégâts bruts

            float fraction = ror2Damage / ghostMax;
            float toughness = Mathf.Max(1f, Mathf.Sqrt(skyrimMax / ReferenceSkyrimHealth));
            return fraction * skyrimMax / toughness;
        }

        private readonly List<HitEvent> _hits = new List<HitEvent>();
        private int _hitsApplied;
        private bool _applyingSkyrimHit;  // vrai pendant qu'on applique un coup venu de Skyrim
        private int _ignoredGhostDamage;  // dégâts sur un double qui ne viennent pas du joueur

        // Chaque coup reçu par le joueur dans Skyrim devient un vrai coup RoR2 sur le survivant :
        // armure, bouclier, Tougher Times et compagnie s'appliquent normalement.
        private void ApplySkyrimHits(CharacterBody body)
        {
            _bridge.ReadNewHits(_hits);
            if (_hits.Count == 0) return;
            if (body == null || body.healthComponent == null || !body.healthComponent.alive) return;

            // 0.70 (coop, invité) : c'est l'hôte qui blesse notre survivant.
            if (_coop != null && _coop.ClientActive)
            {
                foreach (HitEvent hit in _hits) if (hit.Amount > 0f) _coop.SendHit(hit);
                return;
            }
            foreach (HitEvent hit in _hits) ApplyHit(body, hit);
        }

        // Un coup reçu dans un Skyrim (le nôtre, ou celui d'un invité pour l'hôte) blesse ce survivant.
        internal void ApplyHit(CharacterBody body, HitEvent hit)
        {
            if (body == null || body.healthComponent == null || !body.healthComponent.alive) return;
            {
                if (!(hit.Amount > 0f)) return;

                // Si l'attaquant a un double, c'est lui qui "frappe" : les objets qui ripostent le visent.
                GameObject attacker = null;
                if (hit.AttackerFormId != 0 && _ghosts != null && _ghosts.TryGetBody(hit.AttackerFormId, out CharacterBody ghost))
                    attacker = ghost.gameObject;

                // Équilibrage : Skyrim envoie la PART des PV perdue (0.1 = 10 %),
                // on retire la même part des PV max du survivant (avant armure et objets).
                // Part de la vie TOTALE (vie + bouclier) : avec la Transcendance, la vie seule vaut 1 PV et les coups
                // ne faisaient presque rien (survivant immortel).
                float amount = (hit.Flags & BridgeInfo.HitFraction) != 0
                    ? hit.Amount * body.healthComponent.fullCombinedHealth
                    : hit.Amount;
                // Difficulté et élites : les ennemis Skyrim frappent plus fort.
                if (_ghosts != null) amount *= _ghosts.HitMultiplier(hit.AttackerFormId);

                var damage = new DamageInfo
                {
                    damage = amount,
                    attacker = attacker,
                    inflictor = attacker,
                    position = body.corePosition,
                    force = Vector3.zero,
                    procCoefficient = 1f,
                    crit = false,
                    damageColorIndex = DamageColorIndex.Default,
                };
                _applyingSkyrimHit = true;
                try
                {
                    body.healthComponent.TakeDamage(damage);
                }
                finally
                {
                    _applyingSkyrimHit = false;
                }

                _hitsApplied++;
                if (_hitsApplied <= 10 || _hitsApplied % 50 == 0)
                    Logger.LogInfo($"Coup recu de Skyrim : {amount:F1} PV RoR2 (attaquant {hit.AttackerFormId:X8}, total {_hitsApplied})");
            }
        }

        // Le nom du survivant joué, comme dans le catalogue de RoR2 ("Loader"...).
        private static string SurvivorNameOf(CharacterBody body)
        {
            if (body == null) return "";
            SurvivorDef def = SurvivorCatalog.FindSurvivorDefFromBody(body.gameObject);
            return def != null ? def.cachedName ?? "" : "";
        }

        // Quelle attaque a fait ces dégâts (pour l'expérience dans Skyrim) :
        // 0 M1, 1 M2, 2 Maj, 3 R (d'après la "source" des dégâts de RoR2), 4 un allié, -1 autre (objets...).
        private static System.Reflection.FieldInfo _sourceField;
        private static bool _sourceSearched;

        private SkillSource _skillSource;
        private readonly List<CharacterBody> _teammates = new List<CharacterBody>();
        private SurvivorStreamer _survivorView;  // 0.65 : le survivant filmé pour la 3e personne de Skyrim
        private bool _thirdCameraKnown;          // la caméra de Skyrim (dans RoR2), pour viser à la 3e personne
        private Vector3 _thirdCamera;

        // À la 3e personne, on vise et on se déplace selon la CAMÉRA de Skyrim (le viseur est au centre de l'écran),
        // pas selon l'orientation du personnage Skyrim.
        private bool ThirdPersonView => _input.ThirdPerson == 1;
        private float ViewYaw => ThirdPersonView ? _input.CameraYaw : _input.Yaw;
        private float ViewPitch => ThirdPersonView ? _input.CameraPitch : _input.Pitch;

        private void AttackSlot(DamageInfo damageInfo, out int slot, out int variant, out int skillCode) =>
            AttackSlot(damageInfo, GetLocalBody(), out slot, out variant, out skillCode);

        // (0.70 : pour n'importe quel survivant, celui d'un invité compris)
        private void AttackSlot(DamageInfo damageInfo, CharacterBody survivor, out int slot, out int variant, out int skillCode)
        {
            slot = -1;
            variant = 0;
            skillCode = 0;
            if (survivor == null || damageInfo.attacker == null) return;
            if (damageInfo.attacker != survivor.gameObject) { slot = 4; return; }  // drone, tourelle, balise...
            try
            {
                object combo = damageInfo.damageType;
                if (!_sourceSearched)
                {
                    _sourceSearched = true;
                    _sourceField = combo.GetType().GetField("damageSource", AllInstance);
                    Logger.LogInfo("Experience : source des degats " + (_sourceField != null ? "trouvee" : "introuvable (tout compte comme M1)"));
                }
                string source = _sourceField?.GetValue(combo)?.ToString() ?? "";
                if (source.Contains("Primary")) slot = 0;
                else if (source.Contains("Secondary")) slot = 1;
                else if (source.Contains("Utility")) slot = 2;
                else if (source.Contains("Special")) slot = 3;
                if (slot < 0)
                {
                    // Objet ou équipement : la compétence dépend de ce qui a frappé (voir SkillSource).
                    skillCode = _skillSource != null ? _skillSource.Classify(damageInfo, survivor, source) : 0;
                    if (skillCode != 0) slot = 5;
                    return;
                }
                if (survivor.skillLocator == null) return;
                GenericSkill skill = slot == 0 ? survivor.skillLocator.primary : slot == 1 ? survivor.skillLocator.secondary :
                                     slot == 2 ? survivor.skillLocator.utility : survivor.skillLocator.special;
                if (skill != null && skill.skillFamily != null && skill.skillFamily.variants != null)
                {
                    var variants = skill.skillFamily.variants;
                    for (int i = 0; i < variants.Length; i++)
                        if (variants[i].skillDef == skill.baseSkill) { variant = i; break; }
                }
            }
            catch (Exception) { }
        }

        // Appelé (par DamagePatch) juste avant que RoR2 n'applique des dégâts à quelqu'un.
        internal void OnDamage(HealthComponent victim, DamageInfo damageInfo)
        {
            if (_ghosts == null || _ghosts.AllowRealDamage || damageInfo == null) return;

            // Filet de sécurité : un double ne blesse JAMAIS le survivant par lui-même.
            // Seuls les coups venus de Skyrim (envoyés par ApplySkyrimHits) passent.
            if (!_applyingSkyrimHit && _ghosts.IsGhostObject(damageInfo.attacker))
            {
                damageInfo.damage = 0f;
                damageInfo.rejected = true;
                return;
            }

            if (!_ghosts.TryGetGhost(victim, out uint formId, out float skyrimMax, out float ghostMax)) return;

            // Seuls les coups du camp du joueur (le survivant, ses drones, ses tourelles...) comptent.
            // Le reste (chute, vide, décor, autres monstres...) est ignoré : sinon les ennemis Skyrim
            // mouraient "tout seuls".
            if (damageInfo.attacker == null || TeamComponent.GetObjectTeam(damageInfo.attacker) != TeamIndex.Player)
            {
                _ignoredGhostDamage++;
                if (_ignoredGhostDamage <= 10 || _ignoredGhostDamage % 100 == 0)
                {
                    string source = damageInfo.attacker != null ? damageInfo.attacker.name : "aucun (chute, vide, decor...)";
                    Logger.LogInfo($"Degats ignores sur un double : {damageInfo.damage:F1} de {source} (type {damageInfo.damageType}, total {_ignoredGhostDamage})");
                }
                damageInfo.damage = 0f;
                damageInfo.rejected = true;
                return;
            }

            _ghosts.NoteHit(formId);  // RoR2 peut le déplacer un instant (grappin, coup puissant...)
            _ghosts.NoteAttacker(formId, damageInfo.attacker);

            // C'est un double : on envoie les dégâts au vrai ennemi dans Skyrim...
            float ror2Damage = damageInfo.damage * (damageInfo.crit ? 2f : 1f);
            float amount = ConvertToSkyrimDamage(ror2Damage, skyrimMax, ghostMax) * _ghosts.DealtMultiplier(formId);
            // 0.70 (coop, hôte) : à tous les Skyrim qui voient cet ennemi (l'expérience à celui qui frappe).
            if (amount > 0f && _coop != null && _coop.RouteDamage(_bridge, _ghosts, formId, amount, damageInfo.crit, damageInfo.attacker, b =>
                {
                    AttackSlot(damageInfo, b, out int s, out int v, out int c);
                    return (s, v, c);
                }))
            {
                _damageEventsSent++;
            }
            else if (amount > 0f)
            {
                AttackSlot(damageInfo, out int attackSlot, out int attackVariant, out int skillCode);
                _bridge.WriteDamage(formId, amount, damageInfo.crit, attackSlot, attackVariant, skillCode);
                _damageEventsSent++;
                if (_damageEventsSent <= 10 || _damageEventsSent % 100 == 0)
                    Logger.LogInfo($"Degats envoyes a Skyrim : {ror2Damage:F1} RoR2 -> {amount:F1} Skyrim sur {formId:X8} (PV max {skyrimMax:F0}, total {_damageEventsSent})");
            }

            // ...et le double, lui, ne perd rien : seul Skyrim décide quand l'ennemi meurt.
            damageInfo.damage = 0f;
        }

        // Filets de sécurité : on réapplique aussi les commandes à chaque pas de physique
        // et en fin d'image.
        private void FixedUpdate()
        {
            ApplySkyrimInput();
            // Le survivant reste collé au sol sur les petits reliefs de Skyrim (voir GroundSnap.cs).
            try { _groundSnap?.Step(GetLocalBody(), _driving && !_frozen && _terrain != null && _terrain.Active); }
            catch (Exception e) { Logger.LogWarning("Sol : " + e.Message); }
        }

        private void LateUpdate()
        {
            ApplySkyrimInput();
            // À la 3e personne, on voit tout le survivant : la tête reste.
            _viewmodel?.HideHead(GetLocalBody(), _autoRun != null && _autoRun.SkyrimConnected && !ThirdPersonView);
            StreamWorld();
        }

        // Les alliés, tirs et effets filmés depuis la caméra de Skyrim (voir WorldStreamer.cs).
        private void StreamWorld()
        {
            if (_world == null) return;
            CharacterBody body = GetLocalBody();
            bool connected = _autoRun != null && _autoRun.SkyrimConnected && _driving;
            try { _audio?.Update(connected); }
            catch (Exception e) { Logger.LogWarning("Sons : " + e.Message); }
            bool known = false;
            Vector3 camera = Vector3.zero;
            Vector4 frustum = Vector4.zero;
            if (connected && _bridge != null && _bridge.ReadCommand(out SkyrimCommand command) && command.FrustumRight > command.FrustumLeft &&
                _terrain != null && _terrain.TryMapSkyrim(new Vector3(command.CameraX, command.CameraY, command.CameraZ), out camera))
            {
                known = true;
                frustum = new Vector4(command.FrustumLeft, command.FrustumRight, command.FrustumTop, command.FrustumBottom);
            }
            float sin = Mathf.Sin(_input.Yaw), cos = Mathf.Cos(_input.Yaw), cosPitch = Mathf.Cos(_input.Pitch);
            Vector3 forward = new Vector3(sin * cosPitch, -Mathf.Sin(_input.Pitch), cos * cosPitch);
            // 0.65 : à la 3e personne, le survivant filmé depuis la caméra de Skyrim (voir SurvivorStreamer).
            _thirdCameraKnown = known;
            _thirdCamera = camera;
            try
            {
                float cs = Mathf.Sin(_input.CameraYaw), cc = Mathf.Cos(_input.CameraYaw), cp = Mathf.Cos(_input.CameraPitch);
                Vector3 cameraForward = new Vector3(cs * cp, -Mathf.Sin(_input.CameraPitch), cc * cp);
                _survivorView?.Update(body, connected, known, camera, cameraForward, frustum, _input);
                // 0.70 (coop) : les coéquipiers proches, filmés eux aussi depuis notre caméra (en 1re comme en 3e personne).
                if (_teammateViews != null)
                {
                    _teammates.Clear();
                    if (_coop != null && _coop.Active && body != null)
                    {
                        foreach (PlayerCharacterMasterController player in PlayerCharacterMasterController.instances)
                        {
                            CharacterBody mate = player != null && player.master != null ? player.master.GetBody() : null;
                            if (mate == null || mate == body || mate.healthComponent == null || !mate.healthComponent.alive) continue;
                            if ((mate.corePosition - body.corePosition).sqrMagnitude > 80f * 80f) continue;
                            _teammates.Add(mate);
                        }
                        _teammates.Sort((a, b) => (a.corePosition - body.corePosition).sqrMagnitude.CompareTo((b.corePosition - body.corePosition).sqrMagnitude));
                    }
                    for (int i = 0; i < _teammateViews.Length; i++)
                        _teammateViews[i].Update(i < _teammates.Count ? _teammates[i] : null, connected, known, camera, cameraForward, frustum, _input, true);
                }
            }
            catch (Exception e) { Logger.LogWarning("3e personne : " + e.Message); }
            // Première personne (caméra de Skyrim à moins de 0,7 m des pieds, à l'horizontale) : les tirs du
            // survivant partent du canon qu'on voit dans l'image des mains.
            Func<Vector3, Vector3?> firstPerson = null;
            if (known && body != null && _viewmodel != null && _terrain.TryGetSkyrimFeet(out Vector3 feetNow))
            {
                Vector3 flat = camera - feetNow; flat.y = 0f;
                if (flat.sqrMagnitude < 0.7f * 0.7f)
                {
                    Quaternion rot = Quaternion.LookRotation(forward, Vector3.up);
                    Vector3 cam = camera; Vector4 fr = frustum; ViewmodelStreamer vm = _viewmodel;
                    firstPerson = p => vm.TryMatchScreen(p, cam, rot, fr, out Vector3 r) ? r : (Vector3?)null;
                }
            }
            if (_allies != null) _allies.FirstPersonMap = firstPerson;
            if (_sprites != null) _sprites.FirstPersonMap = firstPerson;
            Tracked.Cleanup();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _allies?.Update(body, _terrain, connected);
            _sprites?.Update(body, _terrain, connected);  // après les tirs : leurs décalages sont à jour
            _effects?.Update(body, _terrain, connected);
            WorldStreamer.NativeEffects = _effects != null && _effects.Enabled;
            long allies = watch.ElapsedMilliseconds;
            _world.Update(body, connected, known, camera, forward, frustum, _input.Yaw, _input.Pitch, _allies == null || !_allies.Enabled);
            long total = watch.ElapsedMilliseconds;
            // Diagnostic : si notre travail ralentit RoR2, on le dit (une fois toutes les 10 s au plus).
            if (total > 25 && Time.unscaledTime >= _nextSlowLog)
            {
                _nextSlowLog = Time.unscaledTime + 10f;
                Logger.LogWarning($"Lenteur : modeles {allies} ms, image des tirs {total - allies} ms ({Tracked.Projectiles.Count} projectiles, " +
                                  $"{Tracked.Effects.Count} effets, {Tracked.Tracers.Count} balles tracantes suivis)");
            }
        }

        // Juste avant que la caméra de RoR2 dessine son image, on la place nous-mêmes : 4 m derrière le
        // survivant, un peu au-dessus, tournée comme le regard de Skyrim. Le survivant est donc toujours bien
        // en vue (assez loin pour que RoR2 ne l'efface pas), et RoR2 charge son apparence (Bandit, Opérateur,
        // Vagabonde...). Ça ne change que l'image de la fenêtre RoR2, pas la visée.
        private Camera _sceneCamera;
        private float _nextSceneCameraSearch;

        private void PlaceRoR2Camera(Camera camera)
        {
            if (!_driving || camera == null) return;
            if (_sceneCamera == null && Time.unscaledTime >= _nextSceneCameraSearch)
            {
                _nextSceneCameraSearch = Time.unscaledTime + 2f;
                CameraRigController rig = LocalUserManager.GetFirstLocalUser()?.cameraRigController;
                if (rig != null)
                {
                    _sceneCamera = FindSceneCamera(rig);
                    Logger.LogInfo("Camera RoR2 placee derriere le survivant : " + (_sceneCamera != null ? "oui (" + _sceneCamera.name + ")" : "camera introuvable"));
                }
            }
            if (camera != _sceneCamera) return;
            CharacterBody body = GetLocalBody();
            if (body == null) return;

            // Une vraie caméra de 3e personne : derrière le survivant, dans le sens du regard de Skyrim
            // (en haut / en bas compris), qui se rapproche quand un mur ou le relief de Skyrim est derrière lui
            // (au lieu de passer à travers), et qui glisse en douceur au lieu de sauter d'une image à l'autre.
            Vector3 aim = body.inputBank != null ? body.inputBank.aimDirection : body.transform.forward;
            if (aim.sqrMagnitude < 0.0001f) aim = body.transform.forward;
            aim.Normalize();
            Vector3 target = body.corePosition + Vector3.up * 0.6f;
            Vector3 desired = target - aim * CameraDistance + Vector3.up * CameraHeight;
            Vector3 toCamera = desired - target;
            float length = toCamera.magnitude;
            if (length > 0.01f && Physics.SphereCast(target, 0.3f, toCamera / length, out RaycastHit hit, length,
                    LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                desired = target + toCamera / length * Mathf.Max(0.6f, hit.distance - 0.1f);

            float now = Time.unscaledTime;
            bool jump = !_cameraPlaced || body != _cameraBody || (desired - _cameraPosition).sqrMagnitude > 25f || now - _cameraTime > 0.5f;
            _cameraPosition = jump ? desired : Vector3.Lerp(_cameraPosition, desired, 1f - Mathf.Exp(-CameraSmoothing * Time.unscaledDeltaTime));
            _cameraPlaced = true;
            _cameraBody = body;
            _cameraTime = now;
            // Tournée exactement comme le regard : le survivant reste au centre (juste sous la ligne de visée).
            camera.transform.SetPositionAndRotation(_cameraPosition, Quaternion.LookRotation(aim, Vector3.up));
        }

        // La caméra qui filme le monde : "sceneCam" (champ ou propriété selon la version de RoR2), sinon,
        // parmi les caméras de la caméra du joueur, celle qui voit le décor (pas celle de l'interface).
        private static Camera FindSceneCamera(CameraRigController rig)
        {
            object value = typeof(CameraRigController).GetField("sceneCam", AllInstance)?.GetValue(rig)
                           ?? typeof(CameraRigController).GetProperty("sceneCam", AllInstance)?.GetValue(rig);
            if (value is Camera named && named != null) return named;
            int world = LayerIndex.world.mask.value;
            Camera best = null;
            foreach (Camera candidate in rig.GetComponentsInChildren<Camera>(true))
            {
                if (candidate.orthographic || (candidate.cullingMask & world) == 0) continue;
                if (best == null || candidate.depth < best.depth) best = candidate;
            }
            return best;
        }

        private const float CameraDistance = 4f;    // mètres derrière le survivant
        private const float CameraHeight = 0.6f;    // mètres au-dessus de la ligne de visée
        private const float CameraSmoothing = 18f;  // plus grand = suit plus vite
        private bool _cameraPlaced;
        private CharacterBody _cameraBody;
        private Vector3 _cameraPosition;
        private float _cameraTime;

        // Lit le pont une seule fois par image, même si on est appelé plusieurs fois.
        private void RefreshInput()
        {
            if (Time.frameCount == _inputFrame) return;
            _inputFrame = Time.frameCount;

            bool valid = _bridge != null && _bridge.IsOpen && _bridge.ReadInput(out _input);
            if (valid && _input.Sequence != _lastInputSequence)
            {
                _lastInputSequence = _input.Sequence;
                _lastInputTime = Time.unscaledTime;
            }
            bool fresh = Time.unscaledTime - _lastInputTime < InputTimeoutSeconds;
            bool driving = valid && fresh && _input.Active == 1;

            if (driving != _driving)
            {
                _driving = driving;
                Logger.LogInfo(driving ? "Skyrim prend les commandes" : "Skyrim rend les commandes au clavier");
            }

            // L'image précédente devient "avant", puis on note l'état actuel.
            Array.Copy(_buttonNow, _buttonBefore, ButtonCount);
            _buttonNow[Jump] = driving && _input.Jump != 0;
            _buttonNow[Skill1] = driving && _input.Skill1 != 0;
            _buttonNow[Skill2] = driving && _input.Skill2 != 0;
            _buttonNow[Skill3] = driving && _input.Skill3 != 0;
            _buttonNow[Skill4] = driving && _input.Skill4 != 0;
            _buttonNow[Equipment] = driving && _input.Equipment != 0;

            // Journal : une ligne à chaque nouvel appui reçu de Skyrim.
            for (int i = 0; i < ButtonCount; i++)
            {
                if (_buttonNow[i] && !_buttonBefore[i])
                    Logger.LogInfo("Recu de Skyrim : " + ButtonNames[i] + SkillState(i));
            }
        }

        private static readonly string[] ButtonNames = { "Saut", "Competence1", "Competence2", "Competence3", "Competence4", "Equipement" };

        // Pour le journal : la compétence et ses charges au moment de l'appui (ex. Flash de la Chasseresse).
        private static string SkillState(int button)
        {
            if (button < Skill1 || button > Skill4) return "";
            CharacterBody body = GetLocalBody();
            SkillLocator locator = body != null ? body.skillLocator : null;
            if (locator == null) return "";
            GenericSkill skill = button == Skill1 ? locator.primary : button == Skill2 ? locator.secondary : button == Skill3 ? locator.utility : locator.special;
            if (skill == null || skill.skillDef == null) return "";
            return $" ({skill.skillDef.skillName} : {skill.stock}/{skill.maxStock} charges, recharge {skill.cooldownRemaining:F1} s)";
        }

        // Remplace les commandes du clavier RoR2 par celles de Skyrim.
        internal void ApplySkyrimInput()
        {
            RefreshInput();
            if (!_driving) return;

            CharacterBody body = GetLocalBody();
            if (body == null) return;
            InputBankTest bank = body.inputBank;
            if (bank == null) return;

            // Direction de la caméra Skyrim -> directions dans le monde de RoR2.
            // (nord de Skyrim = avant de Unity, est de Skyrim = droite de Unity)
            float sin = Mathf.Sin(ViewYaw);
            float cos = Mathf.Cos(ViewYaw);
            Vector3 forward = new Vector3(sin, 0f, cos);
            Vector3 right = new Vector3(cos, 0f, -sin);

            Vector3 move = forward * _input.MoveForward + right * _input.MoveRight;
            if (move.sqrMagnitude > 1f) move.Normalize();
            bank.moveVector = move;

            // Contrôle en l'air (grappin, sauts...) : réglable dans r2modman.
            if (_airControl != null && _airControl.Value > 0f && body.characterMotor != null)
                body.characterMotor.airControl = Mathf.Clamp(_airControl.Value, 0f, 1f);

            float cosPitch = Mathf.Cos(ViewPitch);
            bank.aimDirection = ConvergedAim(body, new Vector3(sin * cosPitch, -Mathf.Sin(ViewPitch), cos * cosPitch));

            // On écrit directement "maintenant" et "avant" : peu importe combien de fois
            // RoR2 a touché aux boutons entre-temps, il voit exactement l'état de Skyrim.
            SetButton(ref bank.jump, Jump);
            SetButton(ref bank.skill1, Skill1);
            SetButton(ref bank.skill2, Skill2);
            SetButton(ref bank.skill3, Skill3);
            SetButton(ref bank.skill4, Skill4);
            SetButton(ref bank.activateEquipment, Equipment);

            // Sprint : seulement en avançant, comme dans RoR2.
            body.isSprinting = _input.Sprint != 0 && _input.MoveForward > 0f;

            // 0.73.8 : la Méditation de la Chercheuse lit les directions "brutes" (haut, bas, gauche, droite) et non la
            // direction de marche : nos touches n'y arrivaient pas. On les remplit aussi (champs cherchés par leur nom).
            SetRawMove(bank);
        }

        private static System.Reflection.FieldInfo[] _rawMoveFields;
        private static System.Reflection.FieldInfo _rawMoveData;
        private static bool _rawMoveSearched;
        private readonly bool[] _rawMoveBefore = new bool[4];

        private void SetRawMove(InputBankTest bank)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            if (!_rawMoveSearched)
            {
                _rawMoveSearched = true;
                Type type = typeof(InputBankTest);
                _rawMoveFields = new[] { type.GetField("rawMoveUp", flags), type.GetField("rawMoveDown", flags), type.GetField("rawMoveLeft", flags), type.GetField("rawMoveRight", flags) };
                _rawMoveData = type.GetField("rawMoveData", flags);
                var found = new List<string>();
                foreach (var f in _rawMoveFields) if (f != null) found.Add(f.Name);
                if (_rawMoveData != null) found.Add(_rawMoveData.Name);
                Logger.LogInfo("Commandes : directions brutes (Meditation) : " + (found.Count > 0 ? string.Join(", ", found) : "introuvables dans cette version de RoR2"));
            }
            bool[] now = { _input.MoveForward > 0.5f, _input.MoveForward < -0.5f, _input.MoveRight < -0.5f, _input.MoveRight > 0.5f };
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    var field = _rawMoveFields[i];
                    if (field == null) continue;
                    object state = field.GetValue(bank);
                    Type st = state.GetType();
                    st.GetField("down", flags)?.SetValue(state, now[i]);
                    st.GetField("wasDown", flags)?.SetValue(state, _rawMoveBefore[i]);
                    field.SetValue(bank, state);
                    _rawMoveBefore[i] = now[i];
                }
                if (_rawMoveData != null && _rawMoveData.FieldType == typeof(Vector2))
                    _rawMoveData.SetValue(bank, new Vector2(_input.MoveRight, _input.MoveForward));
            }
            catch (Exception) { }
        }

        // La lunette du Railgunner est-elle levée ? (son état "Scope ... Active" : 1 légère, 2 lourde)
        private static int ScopeLevel(CharacterBody body)
        {
            if (body == null) return 0;
            foreach (EntityStateMachine machine in body.GetComponents<EntityStateMachine>())
            {
                string state = machine.state != null ? machine.state.GetType().FullName : "";
                if (state.IndexOf("Scope", StringComparison.Ordinal) < 0 || state.IndexOf("Active", StringComparison.Ordinal) < 0) continue;
                return state.IndexOf("Heavy", StringComparison.Ordinal) >= 0 ? 2 : 1;
            }
            return 0;
        }

        // Le viseur de Skyrim part des yeux du joueur Skyrim ; les tirs de RoR2 partent du survivant (plus bas
        // ou plus haut) : en visant parallèlement, ils passaient à côté de ce qu'on voit dans Skyrim.
        // On cherche donc ce que vise Skyrim (même ligne de visée, depuis ses yeux, dans le décor recopié),
        // puis le survivant vise ce point précis.
        private const float SkyrimEyeHeight = 1.75f;
        private const float AimRange = 500f;

        private Vector3 ConvergedAim(CharacterBody body, Vector3 direction)
        {
            try
            {
                Vector3 feet = _terrain != null && _terrain.TryGetSkyrimFeet(out Vector3 skyrimFeet) ? skyrimFeet : body.footPosition;
                Vector3 eye = feet + Vector3.up * SkyrimEyeHeight;
                float skip = 0.5f;
                if (ThirdPersonView && _thirdCameraKnown)
                {
                    // 3e personne : le viseur est au centre de l'écran de la caméra, derrière le survivant.
                    // On ignore ce qui est entre la caméra et lui.
                    eye = _thirdCamera;
                    skip = Mathf.Max(0.5f, Vector3.Dot(body.corePosition - eye, direction) + 0.3f);
                }
                int mask = LayerIndex.world.mask | LayerIndex.entityPrecise.mask;
                float best = AimRange;
                Vector3 target = eye + direction * AimRange;
                foreach (RaycastHit hit in Physics.RaycastAll(eye, direction, AimRange, mask, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || hit.distance < skip || hit.distance >= best) continue;
                    HurtBox hurtBox = hit.collider.GetComponent<HurtBox>();
                    if (hurtBox != null && hurtBox.healthComponent == body.healthComponent) continue;  // soi-même
                    if (hit.collider.transform.IsChildOf(body.transform)) continue;
                    best = hit.distance;
                    target = hit.point;
                }
                Vector3 aim = target - body.aimOrigin;
                return aim.sqrMagnitude > 0.25f ? aim.normalized : direction;
            }
            catch (Exception)
            {
                return direction;
            }
        }

        // Un appui = un seul tir pour les compétences "un clic, un tir" (fusil du Bandit, mines du Railgunner...).
        // RoR2 note "cet appui a déjà servi" sur le bouton, mais sa propre lecture du clavier (vide) efface
        // cette note à chaque image : l'appui de Skyrim, toujours enfoncé, redevenait neuf et vidait toutes
        // les charges d'un coup. On garde donc nous-mêmes la note jusqu'au relâchement.
        private readonly bool[] _pressUsed = new bool[ButtonCount];

        private void SetButton(ref InputBankTest.ButtonState button, int index)
        {
            bool now = _buttonNow[index];
            if (!now) _pressUsed[index] = false;
            else if (button.hasPressBeenClaimed) _pressUsed[index] = true;
            button.down = now;
            button.wasDown = _buttonBefore[index] || _pressUsed[index];
            button.hasPressBeenClaimed = now && _pressUsed[index];
        }

        // La caméra de RoR2 regarde dans la même direction que Skyrim. C'est important : depuis la 1.5,
        // RoR2 ne charge l'apparence d'un survivant (Bandit, Opérateur, Vagabonde...) que quand SA caméra le voit.
        // La fonction a changé de nom selon les versions : on cherche ce qui existe.
        private System.Reflection.MethodInfo _setLookVector;
        private object _lookTarget;                       // l'objet sur lequel appeler la fonction (la caméra ou son mode)
        private System.Reflection.FieldInfo _pitchYawField;  // sinon : le champ "pitch/yaw" à écrire directement
        private object _pitchYawOwner;
        private bool _lookSearched;
        private bool _lookWarned;
        private float _nextLookSearch;
        private CharacterBody _lookBody;

        private const System.Reflection.BindingFlags AllInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        private static System.Reflection.MethodInfo FindLookMethod(Type type)
        {
            foreach (System.Reflection.MethodInfo m in type.GetMethods(AllInstance))
            {
                if (m.Name.IndexOf("LookVector", StringComparison.OrdinalIgnoreCase) < 0 && m.Name.IndexOf("LookDirection", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(Vector3)) return m;
            }
            return null;
        }

        private void SearchLook(CameraRigController rig)
        {
            _setLookVector = FindLookMethod(typeof(CameraRigController));
            if (_setLookVector != null) { _lookTarget = rig; _lookSearched = true; Logger.LogInfo("Camera RoR2 alignee sur Skyrim : oui (" + _setLookVector.Name + ")"); return; }

            // Sinon on fouille un niveau plus loin : le "mode" de la caméra et ses données.
            var seen = new List<string>();
            foreach (System.Reflection.FieldInfo f in typeof(CameraRigController).GetFields(AllInstance))
            {
                object value;
                try { value = f.GetValue(rig); } catch { continue; }
                if (value == null || value is UnityEngine.Object || f.FieldType.IsPrimitive) continue;
                Type t = value.GetType();
                System.Reflection.MethodInfo m = FindLookMethod(t);
                if (m != null) { _setLookVector = m; _lookTarget = value; break; }
                foreach (System.Reflection.FieldInfo inner in t.GetFields(AllInstance))
                {
                    if (inner.FieldType.Name == "PitchYawPair") { _pitchYawField = inner; _pitchYawOwner = value; break; }
                }
                if (_pitchYawField != null) break;
                seen.Add(f.Name + ":" + t.Name);
            }
            if (_setLookVector != null) Logger.LogInfo("Camera RoR2 alignee sur Skyrim : oui (" + _lookTarget.GetType().Name + "." + _setLookVector.Name + ")");
            else if (_pitchYawField != null) Logger.LogInfo("Camera RoR2 alignee sur Skyrim : oui (champ " + _pitchYawOwner.GetType().Name + "." + _pitchYawField.Name + ")");
            else
            {
                var names = new List<string>();
                foreach (System.Reflection.MethodInfo m in typeof(CameraRigController).GetMethods(AllInstance))
                    if (m.DeclaringType == typeof(CameraRigController) && (m.Name.Contains("Pitch") || m.Name.Contains("Yaw") || m.Name.Contains("Look"))) names.Add(m.Name);
                if (!_lookWarned)  // une seule fois : sinon le journal en etait rempli
                {
                    _lookWarned = true;
                    Logger.LogWarning("Camera RoR2 alignee sur Skyrim : impossible. Fonctions : " + string.Join(", ", names) + " ; champs : " + string.Join(", ", seen));
                }
            }
            _lookSearched = true;
        }

        private void AlignRoR2Camera(CharacterBody body)
        {
            if (!_driving || body == null || body.inputBank == null) return;
            CameraRigController rig = LocalUserManager.GetFirstLocalUser()?.cameraRigController;
            if (rig == null) return;
            if (body != _lookBody) { _lookBody = body; _lookSearched = false; _setLookVector = null; _lookTarget = null; _pitchYawField = null; _pitchYawOwner = null; }
            // Le mode de la caméra peut changer (nouveau survivant) : on recherche de temps en temps si besoin.
            if (!_lookSearched || (_setLookVector == null && _pitchYawField == null && Time.unscaledTime >= _nextLookSearch))
            {
                _nextLookSearch = Time.unscaledTime + 10f;
                if (!_lookSearched || _lookTarget == null) SearchLook(rig);
            }
            Vector3 aim = body.inputBank.aimDirection;
            try
            {
                if (_setLookVector != null)
                {
                    _setLookVector.Invoke(_lookTarget, new object[] { aim });
                }
                else if (_pitchYawField != null)
                {
                    float pitch = -Mathf.Atan2(aim.y, new Vector2(aim.x, aim.z).magnitude) * Mathf.Rad2Deg;
                    float yaw = Mathf.Atan2(aim.x, aim.z) * Mathf.Rad2Deg;
                    object pair = Activator.CreateInstance(_pitchYawField.FieldType, pitch, yaw);
                    _pitchYawField.SetValue(_pitchYawOwner, pair);
                }
            }
            catch { }
        }

        // Le survivant contrôlé par le joueur sur ce PC (absent au menu).
        private static CharacterBody GetLocalBody()
        {
            return LocalUserManager.GetFirstLocalUser()?.cachedBody;
        }

        // Appelé quand le jeu se ferme : on range proprement.
        private void OnDestroy()
        {
            Camera.onPreCull -= PlaceRoR2Camera;
            _ghosts?.RemoveAll();
            _bridge?.Dispose();
            _bridge = null;
            Instance = null;
        }
    }

    // Le "crochet" : Harmony fait appeler notre code juste après la méthode de RoR2
    // qui lit le clavier et la souris du joueur.
    [HarmonyPatch(typeof(PlayerCharacterMasterController), "Update")]
    internal static class PlayerInputPatch
    {
        private static void Postfix()
        {
            SkyrimRoR2Plugin.Instance?.ApplySkyrimInput();
        }
    }

    // Mort du survivant : pas de fin de partie tant que Skyrim est là (c'est Skyrim qui recharge sa sauvegarde).
    [HarmonyPatch(typeof(Run), "BeginGameOver")]
    internal static class GameOverPatch
    {
        private static bool Prefix()
        {
            return !InventorySync.BlockGameOver();
        }
    }

    // Juste AVANT que RoR2 n'applique des dégâts : on détourne ceux qui visent un double.
    [HarmonyPatch(typeof(HealthComponent), nameof(HealthComponent.TakeDamage))]
    internal static class DamagePatch
    {
        private static void Prefix(HealthComponent __instance, DamageInfo damageInfo)
        {
            SkyrimRoR2Plugin.Instance?.OnDamage(__instance, damageInfo);
        }
    }

    // Juste AVANT que l'état principal du survivant ne recopie les boutons.
    [HarmonyPatch(typeof(EntityStates.GenericCharacterMain), "GatherInputs")]
    internal static class GatherInputsPatch
    {
        private static void Prefix()
        {
            SkyrimRoR2Plugin.Instance?.ApplySkyrimInput();
        }
    }

    // Juste AVANT que l'état principal ne déclenche le saut et les compétences.
    [HarmonyPatch(typeof(EntityStates.GenericCharacterMain), "PerformInputs")]
    internal static class PerformInputsPatch
    {
        private static void Prefix()
        {
            SkyrimRoR2Plugin.Instance?.ApplySkyrimInput();
        }
    }
}
