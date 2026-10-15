using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoR2;
using RoR2.Projectile;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // Les alliés de RoR2 (drones, tourelles, alliés achetés...) affichés dans Skyrim avec leur VRAI modèle.
    //
    // La première fois qu'on voit un type d'allié, son modèle est converti au format de Skyrim (ModelExporter).
    // Ensuite, à chaque image, on envoie à Skyrim la liste des alliés : quel modèle, où, tourné comment.
    // Skyrim pose le modèle dans son monde : il est éclairé, fait de l'ombre et passe derrière les murs.
    internal sealed class AllyMirror
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        private readonly ManualLogSource _log;
        private readonly ConfigEntry<bool> _enabled;
        private IntPtr _view;
        private bool _openTried;
        private uint _sequence;
        private readonly Dictionary<string, int> _versions = new Dictionary<string, int>();   // modèles convertis
        private readonly Dictionary<string, List<string>> _bonePaths = new Dictionary<string, List<string>>();  // os de chaque modèle
        private readonly Dictionary<int, Transform[]> _boneCache = new Dictionary<int, Transform[]>();       // os de chaque allié
        private readonly byte[] _pose = new byte[BridgeInfo.AllyPoseSize];
        private const string FileSuffix = "_A";  // nom de fichier des modèles animés (différent des anciens)
        private readonly Dictionary<string, float> _firstSeen = new Dictionary<string, float>();
        // Conversions ratées (modèle encore invisible au moment de la conversion...) : on réessaie un peu plus tard.
        private readonly Dictionary<string, int> _failures = new Dictionary<string, int>();
        private readonly Dictionary<string, float> _retryAt = new Dictionary<string, float>();
        private readonly byte[] _entry = new byte[BridgeInfo.AllyEntrySize];
        private SolidMirror _solids;  // 0.66 : cube de détritus solide dans Skyrim

        public AllyMirror(ManualLogSource log, ConfigFile config)
        {
            _log = log;
            _solids = new SolidMirror(log);
            _enabled = ConfigMove.Bind(config, "Monde", "3 - Modeles 3D", "ModelesAllies", true,
                "Affiche les allies dans Skyrim avec leur vrai modele RoR2 (converti au format de Skyrim). Sinon, ils sont filmes par-dessus l'image.");
            _survivorLikeCommando = ConfigMove.Bind(config, "Monde", "3 - Modeles 3D", "SurvivantTailleCommando", true,
                "Dans Skyrim, tous les survivants sont ramenes a la meme hauteur (HauteurSurvivant), comme un personnage de Skyrim.");
            _survivorHeight = ConfigMove.Bind(config, "Monde", "3 - Modeles 3D", "HauteurSurvivant", 1.8f,
                "Hauteur des survivants dans Skyrim, en metres (un personnage de Skyrim fait environ 1,8 m).");
            _allySize = ConfigMove.Bind(config, "Monde", "3 - Modeles 3D", "TailleAllies", 0.5f,
                "Taille des allies (drones, tourelles...) dans Skyrim. 1 = taille RoR2, 0.5 = moitie.");
            _shotSize = ConfigMove.Bind(config, "Monde", "3 - Modeles 3D", "TailleTirs", 0.75f,
                "Taille des tirs et objets poses (grenades, mines, pylones...) dans Skyrim, en plus de la taille de celui qui " +
                "les lance (un survivant ramene a 1,8 m lance des tirs a sa mesure). 1 = meme proportion que dans RoR2.");
            _preconvert = ConfigMove.Bind(config, "Monde", "3 - Modeles 3D", "PreconvertirTirs", true,
                "Convertit d'avance, en arriere-plan pendant la partie, les modeles de tous les tirs de RoR2. " +
                "Skyrim ne voit les fichiers qu'au lancement suivant : apres une premiere partie, tous les tirs sont prets.");
            // 0.66 : les objets ajoutés dans Skyrim ne reçoivent pas la lumière des lampes : ils brillent un peu d'eux-mêmes.
            ModelExporter.MinLight = Mathf.Clamp01(ConfigMove.Bind(config, "Monde", "3 - Modeles 3D", "LumiereMinimum", 0.3f,
                "Lumiere minimum des modeles dans Skyrim (autels, imprimantes, drones, allies), de 0 a 1. Skyrim ne les eclaire " +
                "pas avec ses lampes : sans ca, ils sortent presque noirs dans les donjons. Compte pour les modeles convertis ensuite.").Value);
        }

        // 0.63 : le survivant n'est plus converti en modèle 3D pour Skyrim (abandonné : une vue filmée à la troisième
        // personne le remplacera). Le reste du code est gardé, éteint.
        private static readonly bool Survivor3D = false;
        private readonly ConfigEntry<float> _shotSize;
        private readonly ConfigEntry<bool> _preconvert;
        private readonly ConfigEntry<bool> _survivorLikeCommando;
        private readonly ConfigEntry<float> _allySize;
        private readonly ConfigEntry<float> _survivorHeight;
        private readonly Dictionary<string, float> _survivorScales = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _heights = new Dictionary<string, float>();

        // Tous les survivants à peu près à la même taille dans Skyrim (celle d'un personnage de Skyrim, comme le
        // Commando) : on mesure la hauteur de leur modèle RoR2 (tous ses morceaux) et on la ramène à la hauteur voulue.
        // (La "capsule" de collision ne servait à rien : RoR2 donne la même à tous les survivants.)
        private float SurvivorScale(CharacterBody survivor, Transform model)
        {
            if (!_survivorLikeCommando.Value) return 1f;
            string key = survivor.name;
            if (_survivorScales.TryGetValue(key, out float scale)) return scale;
            // La hauteur du modèle converti (mesurée sur ses vrais sommets, corps entier) ; 0 s'il a été converti
            // avant (lancement précédent) : on le reconvertit une fois pour la connaître.
            if (!_heights.TryGetValue(survivor.name.Replace("(Clone)", "").Trim(), out float height)) return 1f;
            float wanted = Mathf.Clamp(_survivorHeight.Value, 0.5f, 5f);
            scale = height > 0.3f ? Mathf.Clamp(wanted / height, 0.3f, 1.5f) : 1f;
            _survivorScales[key] = scale;
            _log.LogInfo($"Modeles : {survivor.name.Replace("(Clone)", "")} mesure {height:F2} m -> x{scale:F2} pour faire {wanted:F2} m dans Skyrim");
            return scale;
        }

        // Branchés par le plugin (effets de RoR2 en sprites, voir SpriteMirror).
        public Func<GameObject, bool> SpritesCover;   // ces effets sont-ils déjà dessinés en sprites ?
        public Action<GameObject> PrepareSprites;     // préparer d'avance les sprites d'un tir
        public Func<int, bool> PrepareEffects;        // préparer quelques effets du catalogue (vrai quand fini)
        public Action<int> ExportSprites;             // convertir quelques sprites en attente

        public void AddName(string name) => AddToBodyList(name);

        // Décalage et taille de chaque tir affiché cette image (pour que ses particules et traînées suivent).
        private readonly Dictionary<int, (Vector3 shift, float scale)> _shotShift = new Dictionary<int, (Vector3, float)>();
        public (Vector3 shift, float scale)? ShotFrameOf(int id) => _shotShift.TryGetValue(id, out var frame) ? frame : ((Vector3, float)?)null;

        // Taille dans Skyrim d'un survivant ou d'un allié (ses effets suivent sa taille, autour de ses pieds).
        private CharacterBody _lastSurvivor;
        public float BodyScale(CharacterBody body)
        {
            if (body == null) return 1f;
            if (body == _lastSurvivor) return Survivor3D && _survivorScales.TryGetValue(body.name, out float s) ? s : 1f;
            return Mathf.Clamp(_allySize.Value, 0.1f, 3f);
        }

        // Pour recopier le survivant entier (la vue à la première personne rétrécit tout sauf ses bras).
        public Action ShowWholeBody;
        public Action<CharacterBody> HideAgain;

        public bool Enabled => _enabled.Value;

        private bool _listWritten;

        // La liste de tous les personnages de RoR2 (alliés possibles), pour que Skyrim prépare leurs fichiers
        // de modèles dès son lancement (il ne voit pas les fichiers créés pendant la partie).
        private void WriteBodyList()
        {
            if (_listWritten) return;
            _listWritten = true;
            try
            {
                // Les noms déjà connus (les fantômes trouvés lors des parties précédentes) sont gardés.
                string file = System.IO.Path.Combine(ModelExporter.Folder, "corps.txt");
                if (System.IO.File.Exists(file))
                    foreach (string line in System.IO.File.ReadAllLines(file)) AddToBodyList(line.Trim());
                foreach (GameObject prefab in BodyCatalog.allBodyPrefabs)
                    if (prefab != null) AddToBodyList(Clean(prefab.name));
                // Les projectiles (leur apparence est un "fantôme" à part ; ceux chargés à la demande sont ajoutés
                // par la pré-conversion) et le trait des balles traçantes.
                for (int i = 0; i < ProjectileCatalog.projectilePrefabCount; i++)
                {
                    GameObject prefab = ProjectileCatalog.GetProjectilePrefab(i);
                    ProjectileController controller = prefab != null ? prefab.GetComponent<ProjectileController>() : null;
                    GameObject ghost = controller != null ? controller.ghostPrefab : null;
                    if (ghost != null) AddToBodyList(Clean(ghost.name));
                }
                AddToBodyList(StreakName);
                SaveBodyList(true);
                _log.LogInfo($"Modeles : liste de {_bodyList.Count} personnages ecrite pour Skyrim");
            }
            catch (Exception e)
            {
                _log.LogWarning("Modeles : liste des personnages impossible a ecrire (" + e.Message + ")");
            }
        }

        private const string StreakName = "SkyrimRoR2Trait";
        private const float ProjectileRange = 120f;
        private float _nextScan;
        private readonly List<ProjectileController> _projectiles = new List<ProjectileController>();
        private readonly List<Component> _tracers = new List<Component>();
        private readonly List<EffectComponent> _movingEffects = new List<EffectComponent>();
        private static System.Reflection.FieldInfo _headField, _tailField;
        private bool _streakReady;

        private readonly Dictionary<string, bool> _grows = new Dictionary<string, bool>();

        private bool GrowsIn(string name, Transform root)
        {
            if (_grows.TryGetValue(name, out bool grows)) return grows;
            grows = false;
            foreach (ParticleSystemRenderer r in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
                if (r.renderMode == ParticleSystemRenderMode.Mesh) { grows = true; break; }
            _grows[name] = grows;
            return grows;
        }

        // Journal : chaque sorte de tir vue une fois, avec ce qui le dessine (pour comprendre ceux qui n'apparaissent pas).
        private readonly HashSet<string> _seenShots = new HashSet<string>();

        // 0.73 : quelques tirs trop gros dans Skyrim par rapport au reste (retours de test) : taille à part.
        private static readonly (string key, float factor)[] ShotSizes =
        {
            ("RailgunnerMine", 0.5f),   // mines du Railgunner
            ("DroneBall", 0.5f),        // noyau d'amplification de l'Opérateur
            ("LunarSpike", 0.5f),       // pieux lunaires du Fils illégitime
            ("LunarStake", 0.5f),
        };

        internal static float SizeOf(string name)
        {
            foreach (var (key, factor) in ShotSizes)
                if (name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return factor;
            return 1f;
        }

        private void DescribeShot(string name, ProjectileController projectile, bool ownModel)
        {
            try
            {
                var parts = new List<string>();
                GameObject holder = ownModel ? projectile.gameObject : projectile.ghost.gameObject;
                foreach (Renderer r in holder.GetComponentsInChildren<Renderer>(true))
                {
                    string kind = r.GetType().Name.Replace("Renderer", "");
                    if (r is ParticleSystemRenderer p) kind += "(" + p.renderMode + ")";
                    string shader = r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-";
                    parts.Add($"{r.name}:{kind}:{shader}{(r.enabled && r.gameObject.activeInHierarchy ? "" : ":cache")}");
                    if (parts.Count >= 8) break;
                }
                _log.LogInfo($"Tir vu : {name} (fantome : {(ownModel ? "non" : "oui")}) -> {(parts.Count == 0 ? "rien de dessine" : string.Join(", ", parts))}");
            }
            catch (Exception) { }
        }

        private static string Clean(string name)
        {
            name = name.Replace("(Clone)", "").Trim();
            // Les objets recyclés par RoR2 portent un numéro ("EngiMineGhost_002") : c'est le même modèle.
            name = System.Text.RegularExpressions.Regex.Replace(name, @"_\d{3}$", "");
            return name.Length > 40 ? name.Substring(0, 40) : name;
        }

        public void Update(CharacterBody survivor, TerrainMirror terrain, bool connected)
        {
            if (!Open()) return;
            _lastSurvivor = survivor;
            if (survivor != null) WriteBodyList();
            SaveBodyList(false);
            int count = 0;
            if (_enabled.Value && connected && survivor != null && terrain != null)
            {
                // Tous les corps du camp du joueur : drones, tourelles... et aussi ceux qui n'ont pas de
                // "cerveau" (les balises du Capitaine, par exemple), qu'on ne trouvait pas avant.
                foreach (CharacterBody body in CharacterBody.readOnlyInstancesList)
                {
                    if (count >= BridgeInfo.AllyMax) break;
                    if (body == null || body.teamComponent == null || body.teamComponent.teamIndex != TeamIndex.Player) continue;
                    if (body == null || body == survivor || body.healthComponent == null || !body.healthComponent.alive) continue;
                    if (body.isPlayerControlled) continue;  // 0.70 (coop) : un coéquipier est filmé, pas converti en 3D
                    Transform model = body.modelLocator != null ? body.modelLocator.modelTransform : null;
                    if (model == null) continue;
                    string name = body.name.Replace("(Clone)", "").Trim();
                    if (name.Length > 36) name = name.Substring(0, 36);
                    // 0.73 : un skin à part (tourelles violettes de l'Ingénieur...) a son propre modèle.
                    int skin = (int)LoadoutApplier.BodySkin(body);
                    if (skin > 0 && skin < 100) name += "_s" + skin;
                    // 0.73 : le modèle d'un lancement précédent est repris tel quel (cacheable). Avant, il était
                    // refait à chaque partie : Skyrim gardait l'ancien fichier, mais recevait les pièces dans le nouvel
                    // ordre, et les tourelles sortaient en morceaux éparpillés.
                    // 0.73.3 : refait à chaque partie, comme avant (le modèle gardé d'avance figeait la tourelle mobile).
                    if (!Ready(name, model)) continue;
                    if (!terrain.TryMapToSkyrim(model.position, out Vector3 skyrim)) continue;
                    Write(count, name, skyrim, model.rotation, body.GetInstanceID(), Mathf.Clamp(_allySize.Value, 0.1f, 3f));
                    WritePose(count, name, model, body.GetInstanceID());
                    _solids.Consider(name, body.GetInstanceID(), model, model.position, Mathf.Clamp(_allySize.Value, 0.1f, 3f), terrain);
                    count++;
                }
                count = AddSurvivor(survivor, count);
                count = AddShots(survivor, terrain, count);
            }
            if (survivor != null) Preconvert(Time.unscaledTime);
            _solids.Flush();
            Marshal.WriteInt32(_view, 8, count);
            Marshal.WriteInt32(_view, 4, unchecked((int)++_sequence));
            Marshal.WriteInt32(_view, 0, unchecked((int)BridgeInfo.AllyMagic));
        }

        // Le survivant lui-même. Sa position est donnée par rapport à ses pieds : Skyrim le pose exactement
        // sur le joueur (drapeau "c'est le joueur" à l'octet 108 de l'entrée).
        private int AddSurvivor(CharacterBody survivor, int count)
        {
            if (!Survivor3D || count >= BridgeInfo.AllyMax) return count;
            Transform model = survivor.modelLocator != null ? survivor.modelLocator.modelTransform : null;
            if (model == null) return count;
            string name = survivor.name.Replace("(Clone)", "").Trim();
            if (name.Length > 40) name = name.Substring(0, 40);
            ShowWholeBody?.Invoke();
            try
            {
                if (!_versions.ContainsKey(name))
                {
                    // Bandit, Opérateur, Vagabonde... : quand la caméra de RoR2 est tout près, RoR2 les éteint ou n'en
                    // dessine que l'ombre, et leur apparence (matières) n'est chargée qu'au moment où on les voit.
                    // On ne touche PLUS à leurs réglages (ça cassait la vue à la première personne) : on attend que
                    // toute leur apparence soit chargée, puis on recopie tous les morceaux, même éteints.
                    foreach (SkinnedMeshRenderer r in model.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                        if (r.sharedMesh != null && r.sharedMaterial == null) return count;  // pas encore chargé : plus tard
                    if (!Ready(name, model, 1f, true, true)) return count;
                }
                float scale = SurvivorScale(survivor, model);
                Vector3 d = (model.position - survivor.footPosition) * scale;
                int id = survivor.GetInstanceID();
                Write(count, name, new Vector3(d.x, d.z, d.y), model.rotation, id, -scale);
                WritePose(count, name, model, id);
                return count + 1;
            }
            finally
            {
                HideAgain?.Invoke(survivor);
            }
        }

        // Les tirs : projectiles, grenades, mines, missiles (leur vrai modèle) et balles traçantes (un trait lumineux).
        private int AddShots(CharacterBody survivor, TerrainMirror terrain, int count)
        {
            float now = Time.unscaledTime;
            if (now >= _nextScan)
            {
                _nextScan = now + 0.05f;
                _projectiles.Clear();
                foreach (ProjectileController projectile in Tracked.Projectiles) if (Tracked.Alive(projectile)) _projectiles.Add(projectile);
                // Les effets qui volent vers une cible (flèches et glaive de la Chasseresse, éclairs...) : des
                // "orbes" de RoR2, dessinés par un effet qui se déplace. On montre leur modèle comme un tir.
                _movingEffects.Clear();
                foreach (EffectComponent effect in Tracked.Effects)
                    if (Tracked.Alive(effect) && effect.GetComponent<RoR2.Orbs.OrbEffect>() != null) _movingEffects.Add(effect);
                _tracers.Clear();
                foreach (Tracer tracer in Tracked.Tracers) if (Tracked.Alive(tracer)) _tracers.Add(tracer);
            }
            Vector3 center = survivor.corePosition;
            _shotShift.Clear();
            float shotSize = Mathf.Clamp(_shotSize.Value, 0.1f, 3f);
            foreach (EffectComponent effect in _movingEffects)
            {
                if (count >= BridgeInfo.AllyMax) return count;
                if (effect == null) continue;
                Transform root = effect.transform;
                if ((root.position - center).sqrMagnitude > ProjectileRange * ProjectileRange) continue;
                string name = Clean(root.name);
                if (name.Length == 0 || _preFailed.Contains(name) || !Ready(name, root, 0f, false, false, true)) continue;
                if (!terrain.TryMapToSkyrim(root.position, out Vector3 skyrim)) continue;
                int id = root.GetInstanceID();
                _shotShift[id] = (Vector3.zero, shotSize);
                Write(count, name, skyrim, root.rotation, id, shotSize);
                WritePose(count, name, root, id);
                count++;
            }
            foreach (ProjectileController projectile in _projectiles)
            {
                if (count >= BridgeInfo.AllyMax) return count;
                if (projectile == null) continue;
                // Seulement l'apparence du projectile (son "fantôme") : l'objet lui-même ne contient que ses zones
                // de collision et de dégâts (les carrés et sphères blanches).
                // Sans fantôme (piliers du mur de glace de l'Artificière...) : l'objet lui-même, en ne gardant que
                // ce qui est vraiment dessiné (les zones de collision sont écartées par le filtre "Visible").
                // On attend un peu qu'il ait fini d'apparaître avant de le convertir.
                bool ownModel = projectile.ghost == null;
                Transform root = ownModel ? projectile.transform : projectile.ghost.transform;
                if ((root.position - center).sqrMagnitude > ProjectileRange * ProjectileRange) continue;
                string name = Clean(root.name);
                if (name.Length > 0 && _seenShots.Add(name)) DescribeShot(name, projectile, ownModel);
                // Les objets faits de particules (piliers du mur de glace...) poussent en apparaissant :
                // on attend qu'ils aient leur vraie taille avant de les recopier.
                if (name.Length == 0 || !Ready(name, root, ownModel || GrowsIn(name, root) ? 0.8f : 0f, false, false, true)) continue;
                int id = root.GetInstanceID();
                ShotFrame frame = FrameOf(id, root.position, projectile.owner, survivor);
                Vector3 shown = frame.Apply(root.position, Mathf.Clamp01(Mathf.Max(
                    (root.position - frame.Start).magnitude / ShotBlendDistance, (now - frame.Born) / ShotBlendTime)));
                float size = frame.Scale * shotSize * SizeOf(name);
                _shotShift[id] = (shown - root.position, size);
                if (!terrain.TryMapToSkyrim(shown, out Vector3 skyrim)) continue;
                Write(count, name, skyrim, root.rotation, id, size);
                WritePose(count, name, root, id);
                _solids.Consider(name, id, root, shown, size, terrain);
                count++;
                // Ce que porte l'objet du tir lui-même (bouclier-bulle de l'Ingénieur...), à sa vraie taille.
                if (!ownModel && count < BridgeInfo.AllyMax && HasFx(name, projectile.transform))
                {
                    string fx = name + FxSuffix;
                    Transform fxBody = projectile.transform;
                    if (Ready(fx, fxBody, 0.3f, false, false, true) && terrain.TryMapToSkyrim(fxBody.position + (shown - root.position), out Vector3 fxAt))
                    {
                        int fxId = fxBody.GetInstanceID();
                        Write(count, fx, fxAt, fxBody.rotation, fxId, 1f);
                        WritePose(count, fx, fxBody, fxId);
                        count++;
                    }
                }
            }
            if (_tracers.Count > 0 && !_streakReady)
            {
                _streakReady = true;
                try
                {
                    ModelExporter.ExportStreak(StreakName + FileSuffix, new Color(1f, 0.85f, 0.45f), _log);
                    _versions[StreakName] = 1;
                    _bonePaths[StreakName] = new List<string> { "" };
                }
                catch (Exception e)
                {
                    _log.LogWarning("Modeles : trait lumineux impossible (" + e.Message + ")");
                }
            }
            if (!_versions.ContainsKey(StreakName)) return count;
            foreach (Component tracer in _tracers)
            {
                if (count >= BridgeInfo.AllyMax) return count;
                if (tracer == null) continue;
                if (SpritesCover != null && SpritesCover(tracer.gameObject)) continue;  // sa vraie ligne est déjà dessinée
                if (_headField == null)
                {
                    _headField = tracer.GetType().GetField("headTransform");
                    _tailField = tracer.GetType().GetField("tailTransform");
                }
                Transform head = _headField?.GetValue(tracer) as Transform;
                Transform tail = _tailField?.GetValue(tracer) as Transform;
                Vector3 front = head != null ? head.position : tracer.transform.position;
                Vector3 direction = head != null && tail != null ? head.position - tail.position : tracer.transform.forward;
                if (direction.sqrMagnitude < 0.0001f) direction = tracer.transform.forward;
                if ((front - center).sqrMagnitude > ProjectileRange * ProjectileRange) continue;
                int id = tracer.GetInstanceID();
                // Le trait part de l'arme de celui qui tire (tourelle, survivant), ramenée à sa taille dans Skyrim,
                // puis rejoint peu à peu le vrai trajet : il arrive pile sur la cible.
                if (!_tracerPath.TryGetValue(id, out (Vector3 start, Vector3 end) path)) path = TracerPath(tracer, tail != null ? tail.position : front);
                ShotFrame frame = FrameOf(id, path.start, NearestShooter(path.start, survivor), survivor);
                float length = Mathf.Max(1f, (path.end - path.start).magnitude);
                Vector3 shown = frame.Apply(front, Mathf.Clamp01((front - path.start).magnitude / length));
                if (!terrain.TryMapToSkyrim(shown, out Vector3 skyrim)) continue;
                Write(count, StreakName, skyrim, Quaternion.LookRotation(direction.normalized, Vector3.up), id);
                WritePose(count, StreakName, tracer.transform, id);
                count++;
            }
            return count;
        }

        private static readonly List<Renderer> _drawn = new List<Renderer>();
        private static bool HasDrawnMesh(Transform root)
        {
            _drawn.Clear();
            root.GetComponentsInChildren(false, _drawn);
            foreach (Renderer r in _drawn)
                if (r != null && r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer)) return true;
            return false;
        }

        // Le modèle de cet allié est-il converti ? On attend un peu après son apparition (pose stable).
        private bool Ready(string name, Transform model, float wait = 1f, bool includeHidden = false, bool persistent = false, bool cacheable = false)
        {
            if (_versions.ContainsKey(name)) return true;
            if (cacheable && LoadCached(name)) return true;
            if (_retryAt.TryGetValue(name, out float retry) && Time.unscaledTime < retry) return false;
            if (!_firstSeen.TryGetValue(name, out float since))
            {
                _firstSeen[name] = Time.unscaledTime;
                return false;
            }
            if (Time.unscaledTime - since < wait) return false;
            try
            {
                var bones = new List<string>();
                if (ModelExporter.Export(model, name + FileSuffix, _log, bones, includeHidden, name.EndsWith(FxSuffix)))
                {
                    AddToBodyList(name);  // 0.73 : nouveau nom (skin, objet à part...) : préparé par Skyrim au prochain lancement
                    _heights[name] = ModelExporter.LastHeight;
                    _versions[name] = 1;
                    _bonePaths[name] = bones;
                    return true;
                }
                _log.LogWarning("Modeles : rien a convertir pour " + name);
            }
            catch (Exception e)
            {
                _log.LogWarning("Modeles : conversion de " + name + " impossible (" + e.Message + ")");
            }
            int tries = _failures.TryGetValue(name, out int previous) ? previous + 1 : 1;
            _failures[name] = tries;
            // 1 s, 2 s, 4 s... puis abandon après 6 essais (le modèle n'a vraiment rien à montrer).
            // (Le survivant : on réessaie toutes les 3 s sans abandonner, son apparence peut se charger plus tard.)
            _retryAt[name] = persistent ? Time.unscaledTime + 3f : tries >= 6 ? float.MaxValue : Time.unscaledTime + Mathf.Pow(2f, tries - 1);
            return false;
        }

        // Un tir déjà converti lors d'un lancement précédent : on garde le même fichier (Skyrim le connaît déjà).
        private readonly HashSet<string> _cacheChecked = new HashSet<string>();

        // Objets fixes : on ne reprend la conversion déjà faite que si elle est d'un seul bloc (sinon, ancienne
        // conversion "en morceaux" : on la refait).
        private readonly HashSet<string> _flatStatics = new HashSet<string>();
        private bool LoadCachedFlat(string name)
        {
            var bones = new List<string>();
            if (!ModelExporter.TryLoadCached(name + FileSuffix, bones, out float height)) return false;
            if (bones.Count > 1) { _log.LogInfo($"Modeles : {name} etait en {bones.Count} pieces, reconverti d'un seul bloc"); return false; }
            _cacheChecked.Add(name);
            _versions[name] = 1;
            _bonePaths[name] = bones;
            _heights[name] = height;
            return true;
        }

        private bool LoadCached(string name)
        {
            if (!_cacheChecked.Add(name)) return false;  // déjà regardé sur le disque : pas trouvé
            var bones = new List<string>();
            if (!ModelExporter.TryLoadCached(name + FileSuffix, bones, out float height)) return false;
            _versions[name] = 1;
            _bonePaths[name] = bones;
            _heights[name] = height;
            return true;
        }

        // ---------- Taille et point de départ des tirs ----------
        //
        // Dans Skyrim, les survivants sont ramenés à 1,8 m (REX : x0,54) et les alliés à TailleAllies : leurs tirs
        // partaient donc de la hauteur de leur arme dans RoR2, bien au-dessus du modèle (REX, tourelle TR12).
        // Chaque tir garde, à sa naissance, le décalage entre l'arme de RoR2 et l'arme du modèle de Skyrim ;
        // ce décalage s'efface ensuite en quelques mètres (ou une fraction de seconde) : il touche au bon endroit.

        private const float ShotBlendDistance = 8f;   // mètres
        private const float ShotBlendTime = 0.35f;    // secondes (les mines et grenades posées tout près)

        private struct ShotFrame
        {
            public Vector3 Start;    // position de départ dans RoR2
            public Vector3 Offset;   // décalage au départ (arme du modèle de Skyrim - arme de RoR2)
            public float Scale;      // taille de celui qui a tiré, dans Skyrim
            public float Born;

            // done : 0 au départ, 1 quand le décalage est effacé.
            public Vector3 Apply(Vector3 position, float done) => position + Offset * (1f - done);
        }

        internal static bool IsFinite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
            float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        private readonly Dictionary<int, ShotFrame> _shotFrames = new Dictionary<int, ShotFrame>();
        private readonly Dictionary<int, (Vector3, Vector3)> _tracerPath = new Dictionary<int, (Vector3, Vector3)>();
        private static System.Reflection.FieldInfo _startField, _endField;

        // Première personne : point de RoR2 -> point de Skyrim qui apparaît au même endroit de l'écran
        // (là où on voit le canon dans l'image des mains). Nul en troisième personne.
        public Func<Vector3, Vector3?> FirstPersonMap;

        private ShotFrame FrameOf(int id, Vector3 start, GameObject owner, CharacterBody survivor)
        {
            if (_shotFrames.TryGetValue(id, out ShotFrame frame)) return frame;
            if (_shotFrames.Count > 600) { _shotFrames.Clear(); _tracerPath.Clear(); }
            frame = new ShotFrame { Start = start, Scale = 1f, Born = Time.unscaledTime };
            CharacterBody body = owner != null ? owner.GetComponent<CharacterBody>() : null;
            if (body != null && body.teamComponent != null && body.teamComponent.teamIndex == TeamIndex.Player)
            {
                float scale = 1f;
                if (body == survivor)
                {
                    if (Survivor3D && _survivorScales.TryGetValue(survivor.name, out float s)) scale = s;
                }
                else scale = Mathf.Clamp(_allySize.Value, 0.1f, 3f);
                Vector3 feet = body.footPosition;
                frame.Scale = scale;
                frame.Offset = (feet + (start - feet) * scale) - start;
                // En première personne, le tir part du canon qu'on voit à l'écran.
                Vector3? seen = body == survivor ? FirstPersonMap?.Invoke(start) : null;
                if (seen.HasValue && IsFinite(seen.Value)) frame.Offset = seen.Value - start;
            }
            _shotFrames[id] = frame;
            return frame;
        }

        // Début et fin du trajet d'une balle traçante (champs privés de RoR2, sinon sa première position vue).
        private (Vector3, Vector3) TracerPath(Component tracer, Vector3 fallback)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            if (_startField == null)
            {
                _startField = tracer.GetType().GetField("startPos", flags);
                _endField = tracer.GetType().GetField("endPos", flags);
            }
            Vector3 start = fallback, end = fallback + tracer.transform.forward * 30f;
            try
            {
                if (_startField != null && _startField.GetValue(tracer) is Vector3 a) start = a;
                if (_endField != null && _endField.GetValue(tracer) is Vector3 b) end = b;
            }
            catch (Exception) { }
            var path = (start, end);
            _tracerPath[tracer.GetInstanceID()] = path;
            return path;
        }

        // Qui a tiré cette balle ? Le corps du camp du joueur le plus proche de son point de départ (6 m au plus).
        private static GameObject NearestShooter(Vector3 start, CharacterBody survivor)
        {
            CharacterBody best = null;
            float bestDistance = 6f * 6f;
            foreach (CharacterBody body in CharacterBody.readOnlyInstancesList)
            {
                if (body == null || body.teamComponent == null || body.teamComponent.teamIndex != TeamIndex.Player) continue;
                float d = (body.corePosition - start).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = body; }
            }
            return best != null ? best.gameObject : null;
        }

        // ---------- Pré-conversion des tirs ----------
        //
        // Skyrim ne relit pas un modèle créé (ou changé) pendant la partie : un tir converti la première fois qu'on
        // le voit n'apparaît qu'au lancement suivant. On convertit donc d'avance, un par un en arrière-plan, les
        // tirs de RoR2 (ceux des survivants d'abord). Chaque tir est créé loin sous le monde, "débranché" (sans ses
        // scripts, sans collision : il ne fait rien), converti au bout d'un instant, puis supprimé.

        private List<(string key, GameObject prefab)> _preQueue;  // key : nom du projectile (pour l'ordre)
        private readonly HashSet<string> _preSeen = new HashSet<string>();
        private readonly HashSet<string> _preFailed = new HashSet<string>();
        private GameObject _preInstance;
        private string _preName;  // tir en cours de pré-conversion (null : aucun)
        private bool _preFlat;   // objet fixe : converti d'un seul bloc
        private bool _preEffectsOnly;                       // en cours : les formes d'effet de l'objet du tir (_FX)
        private readonly List<(string name, GameObject prefab)> _preFx = new List<(string, GameObject)>();
        // Les objets fixes posés dans Skyrim (autels de RoR2 dans les donjons, capsule lunaire) : convertis en premier.
        private readonly List<(string name, GameObject prefab)> _preStatic = new List<(string, GameObject)>();
        private static readonly (string name, string path)[] StaticModels =
        {
            ("ShrineChance", "RoR2/Base/ShrineChance/ShrineChance.prefab"),
            ("ShrineRestack", "RoR2/Base/ShrineRestack/ShrineRestack.prefab"),
            ("ShrineCombat", "RoR2/Base/ShrineCombat/ShrineCombat.prefab"),
            ("ShrineBlood", "RoR2/Base/ShrineBlood/ShrineBlood.prefab"),
            ("ShrineBoss", "RoR2/Base/ShrineBoss/ShrineBoss.prefab"),
            ("LunarChest", "RoR2/Base/LunarChest/LunarChest.prefab"),
            // 0.61 : imprimantes et drones cassés posés dans Skyrim
            ("Duplicator", "RoR2/Base/Duplicator/Duplicator.prefab"),
            ("DuplicatorLarge", "RoR2/Base/DuplicatorLarge/DuplicatorLarge.prefab"),
            ("DuplicatorMilitary", "RoR2/Base/DuplicatorMilitary/DuplicatorMilitary.prefab"),
            ("DuplicatorWild", "RoR2/Base/DuplicatorWild/DuplicatorWild.prefab"),
            ("Drone1Broken", "RoR2/Base/Drones/Drone1Broken.prefab"),
            ("Drone2Broken", "RoR2/Base/Drones/Drone2Broken.prefab"),
            ("MissileDroneBroken", "RoR2/Base/Drones/MissileDroneBroken.prefab"),
            ("Turret1Broken", "RoR2/Base/Drones/Turret1Broken.prefab"),
            ("FlameDroneBroken", "RoR2/Base/Drones/FlameDroneBroken.prefab"),
            ("EmergencyDroneBroken", "RoR2/Base/Drones/EmergencyDroneBroken.prefab"),
            ("MegaDroneBroken", "RoR2/Base/Drones/MegaDroneBroken.prefab"),
            // 0.71 : la porte bleue du Bazar (après un coffre de boss, parfois)
            ("PortalShop", "RoR2/Base/PortalShop/PortalShop.prefab"),
        };

        // 0.71 : les couronnes des élites dans Skyrim : le modèle de l'aspect d'élite (EliteCrown1 feu, 2 glace, 3 foudre).
        private static readonly string[] EliteAspects = { "EliteFireEquipment", "EliteIceEquipment", "EliteLightningEquipment" };

        private static GameObject PickupModelOf(EquipmentDef def)
        {
            if (def == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                if (typeof(EquipmentDef).GetField("pickupModelPrefab", flags)?.GetValue(def) is GameObject direct && direct != null) return direct;
                if (typeof(EquipmentDef).GetProperty("pickupModelPrefab", flags)?.GetValue(def) is GameObject property && property != null) return property;
                object reference = typeof(EquipmentDef).GetField("pickupModelReference", flags)?.GetValue(def);
                object key = reference?.GetType().GetProperty("RuntimeKey")?.GetValue(reference);
                if (key != null && key.ToString().Length > 0) return LoadAddressable(key);
            }
            catch (Exception) { }
            return null;
        }

        // Nom du modèle des formes d'effet portées par l'objet du tir lui-même (voir HasEffectMesh).
        private const string FxSuffix = "_FX";
        private readonly Dictionary<string, bool> _hasFx = new Dictionary<string, bool>();
        private bool HasFx(string name, Transform projectile)
        {
            if (!_hasFx.TryGetValue(name, out bool has))
            {
                has = ModelExporter.HasEffectMesh(projectile);
                _hasFx[name] = has;
                if (has) _log.LogInfo($"Modeles : {name} : formes d'effet portees par le projectile lui-meme -> {name}{FxSuffix}");
            }
            return has;
        }
        private float _preReadyAt, _preNext;
        private int _preDone, _preGone, _preEmpty;
        private bool _preLogged;
        private int _preReported;

        private void Preconvert(float now)
        {
            if (!_preconvert.Value) return;
            if (_preQueue == null)
            {
                BuildPreconvertQueue();
                _preNext = now + 20f;  // on laisse la partie démarrer tranquillement
                return;
            }
            if (_preName != null && _preInstance == null)
            {
                // La copie a disparu avant d'être convertie (une particule qui se détruit en finissant...).
                _preFailed.Add(_preName);
                if (++_preGone <= 20) _log.LogInfo("Pre-conversion : " + _preName + " a disparu avant d'etre converti");
                _preName = null;
                _preFlat = false;
            }
            if (_preInstance != null)
            {
                if (now < _preReadyAt) return;
                try
                {
                    var bones = new List<string>();
                    int drawn = 0, total = 0;
                    foreach (Renderer r in _preInstance.GetComponentsInChildren<Renderer>(true))
                    {
                        total++;
                        if (r.enabled && r.gameObject.activeInHierarchy) drawn++;
                    }
                    if (ModelExporter.Export(_preInstance.transform, _preName + FileSuffix, _log, bones, false, _preEffectsOnly, _preFlat))
                    {
                        if (!_versions.ContainsKey(_preName))
                        {
                            _versions[_preName] = 1;
                            _bonePaths[_preName] = bones;
                            _heights[_preName] = ModelExporter.LastHeight;
                        }
                        _preDone++;
                    }
                    else
                    {
                        _preFailed.Add(_preName);
                        if (++_preEmpty <= 40) _log.LogInfo($"Pre-conversion : rien a convertir pour {_preName} ({drawn} morceaux affiches sur {total})");
                    }
                }
                catch (Exception e)
                {
                    _preFailed.Add(_preName);
                    _log.LogWarning("Pre-conversion : " + _preName + " impossible (" + e.Message + ")");
                }
                finally
                {
                    UnityEngine.Object.Destroy(_preInstance);
                    _preInstance = null;
                    _preName = null;
                    _preFlat = false;
                }
                return;
            }
            if (now < _preNext) return;
            while (_preStatic.Count > 0)
            {
                var (staticName, staticPrefab) = _preStatic[0];
                _preStatic.RemoveAt(0);
                bool flat = _flatStatics.Contains(staticName);
                if (_versions.ContainsKey(staticName) || _preFailed.Contains(staticName)) continue;
                if (flat ? LoadCachedFlat(staticName) : LoadCached(staticName)) continue;
                _preInstance = SpawnInert(staticPrefab);
                if (_preInstance == null) { _preFailed.Add(staticName); continue; }
                _preName = staticName;
                _preFlat = flat;
                _preEffectsOnly = false;
                _preReadyAt = now + 0.5f;
                _preNext = _preReadyAt + 0.3f;
                return;
            }
            int loads = 0;
            while (_preQueue.Count > 0 && loads < 3)  // au plus 3 apparences chargées par image : pas d'à-coups
            {
                GameObject projectile = _preQueue[0].prefab;
                _preQueue.RemoveAt(0);
                loads++;
                GameObject look = LookOf(projectile);
                if (look == null) continue;
                string name = Clean(look.name);
                if (name.Length == 0 || !_preSeen.Add(name)) continue;
                AddToBodyList(name);
                if (look != projectile && ModelExporter.HasEffectMesh(projectile.transform))
                {
                    _hasFx[name] = true;
                    AddToBodyList(name + FxSuffix);
                    _preFx.Add((name + FxSuffix, projectile));
                }
                PrepareSprites?.Invoke(look);  // ses particules, traînées et rayons
                if (look != projectile) PrepareSprites?.Invoke(projectile);  // et ceux de l'objet du tir (flaques...)
                if (_versions.ContainsKey(name) || _preFailed.Contains(name) || LoadCached(name)) continue;
                _preInstance = SpawnInert(look);
                if (_preInstance == null) { _preFailed.Add(name); continue; }
                _preName = name;
                _preFlat = false;
                _preEffectsOnly = false;
                // Les particules "en objets 3D" (cristaux...) ont besoin d'un instant pour apparaître ; le reste,
                // on le convertit tout de suite (certaines copies s'éteignent vite).
                _preReadyAt = now + (GrowsIn(name, _preInstance.transform) ? 0.8f : 0.1f);
                _preNext = _preReadyAt + 0.3f;
                return;
            }
            if (_preQueue.Count > 0) return;
            // Puis les formes d'effet portées par les projectiles eux-mêmes (bouclier-bulle...).
            while (_preFx.Count > 0)
            {
                var (fx, prefab) = _preFx[0];
                _preFx.RemoveAt(0);
                if (_versions.ContainsKey(fx) || _preFailed.Contains(fx) || LoadCached(fx)) continue;
                _preInstance = SpawnInert(prefab);
                if (_preInstance == null) { _preFailed.Add(fx); continue; }
                _preName = fx;
                _preFlat = false;
                _preEffectsOnly = true;
                _preReadyAt = now + 0.3f;
                _preNext = _preReadyAt + 0.3f;
                return;
            }
            // Puis les effets du jeu (explosions, impacts, soins...) : leurs sprites.
            bool effectsDone = PrepareEffects == null || PrepareEffects(4);
            ExportSprites?.Invoke(3);
            if (!effectsDone) return;
            SaveBodyList(true);
            if (!_preLogged)
            {
                _preLogged = true;
                _log.LogInfo($"Pre-conversion des tirs terminee : {_preDone} convertis cette fois, {_preEmpty} sans modele, " +
                             $"{_preGone} disparus, {_ghostsLoaded} fantomes charges a la demande (prets au prochain lancement de Skyrim)");
            }
            else if (_preDone > 0 && _preDone % 25 == 0 && _preDone != _preReported)
            {
                _preReported = _preDone;
                _log.LogInfo($"Pre-conversion : {_preDone} tirs convertis, {_preQueue.Count} encore a voir");
            }
        }

        private void BuildPreconvertQueue()
        {
            foreach (var (staticName, path) in StaticModels)
            {
                GameObject prefab = null;
                try { prefab = LoadAddressable(path); }
                catch (Exception e) { _log.LogWarning("Autels : " + path + " impossible a charger (" + e.Message + ")"); }
                if (prefab == null) { _log.LogWarning("Autels : modele introuvable : " + path); continue; }
                AddToBodyList(staticName);
                _flatStatics.Add(staticName);
                _preStatic.Add((staticName, prefab));
            }
            for (int i = 0; i < EliteAspects.Length; i++)
            {
                string crown = "EliteCrown" + (i + 1);
                try
                {
                    EquipmentDef def = EquipmentCatalog.GetEquipmentDef(EquipmentCatalog.FindEquipmentIndex(EliteAspects[i]));
                    GameObject model = PickupModelOf(def);
                    if (model == null) { _log.LogWarning("Elites : modele de " + EliteAspects[i] + " introuvable (pas de couronne)"); continue; }
                    AddToBodyList(crown);
                    _flatStatics.Add(crown);
                    _preStatic.Add((crown, model));
                }
                catch (Exception e) { _log.LogWarning("Elites : couronne " + crown + " impossible a preparer (" + e.Message + ")"); }
            }
            // Les drones qu'on peut réparer dans Skyrim : leur corps est converti d'avance (sinon le drone réparé
            // restait invisible jusqu'au lancement suivant de Skyrim).
            foreach (string masterName in new[] { "Drone1Master", "Drone2Master", "DroneMissileMaster", "Turret1Master", "FlameDroneMaster", "EmergencyDroneMaster", "MegaDroneMaster" })
            {
                try
                {
                    GameObject master = MasterCatalog.FindMasterPrefab(masterName);
                    GameObject body = master != null ? master.GetComponent<CharacterMaster>()?.bodyPrefab : null;
                    if (body == null) { _log.LogWarning("Drones : corps de " + masterName + " introuvable"); continue; }
                    // Comme pour un allié vivant : on convertit son MODELE (l'enfant indiqué par ModelLocator), pas le corps entier.
                    Transform model = body.GetComponent<ModelLocator>()?.modelTransform;
                    if (model == null) { _log.LogWarning("Drones : modele de " + body.name + " introuvable"); continue; }
                    AddToBodyList(body.name);
                    _preStatic.Add((body.name, model.gameObject));
                }
                catch (Exception e) { _log.LogWarning("Drones : " + masterName + " impossible a preparer (" + e.Message + ")"); }
            }
            _preQueue = new List<(string, GameObject)>();
            var later = new List<(string, GameObject)>();
            // Les noms des survivants ("Engi", "Mage", "Treebot"...) : leurs tirs passent en premier.
            var survivors = new List<string>();
            foreach (SurvivorDef def in SurvivorCatalog.allSurvivorDefs)
            {
                if (def == null || def.bodyPrefab == null) continue;
                string body = def.bodyPrefab.name.Replace("Body", "");
                if (body.Length >= 4) survivors.Add(body);
            }
            survivors.AddRange(new[] { "Syringe", "FMJ", "Arrow", "Huntress", "Mage", "Seedpod", "Spirit", "PalmBlast", "UnseenHand", "SoulSpiral",
                "Evis", "Spider", "Cryo", "Lunar", "Proliferation", "Cleaver", "Junk", "Bandit", "Shiv", "Tazer" });
            try
            {
                for (int i = 0; i < ProjectileCatalog.projectilePrefabCount; i++)
                {
                    GameObject prefab = ProjectileCatalog.GetProjectilePrefab(i);
                    if (prefab == null) continue;
                    string name = prefab.name;
                    bool mine = survivors.Exists(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                    (mine ? _preQueue : later).Add((name, prefab));
                }
            }
            catch (Exception e)
            {
                _log.LogWarning("Pre-conversion : liste des tirs impossible (" + e.Message + ")");
            }
            int first = _preQueue.Count;
            _preQueue.AddRange(later);
            _log.LogInfo($"Pre-conversion : {_preQueue.Count} tirs a verifier ({first} de survivants en premier)");
        }

        // L'apparence d'un projectile : son "fantôme". Depuis RoR2 1.5, il n'est souvent plus rangé directement
        // dans le projectile (ghostPrefab vide) mais chargé à la demande (une "référence d'asset" Addressables) :
        // on le charge nous-mêmes, par sa clé. Pas de fantôme du tout : le projectile est sa propre apparence.
        private GameObject LookOf(GameObject projectile)
        {
            if (projectile == null) return null;
            ProjectileController controller = projectile.GetComponent<ProjectileController>();
            if (controller == null) return projectile;
            if (controller.ghostPrefab != null) return controller.ghostPrefab;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            bool hasReference = false;
            foreach (FieldInfo field in controller.GetType().GetFields(flags))
            {
                if (field.Name.IndexOf("ghost", StringComparison.OrdinalIgnoreCase) < 0 || field.FieldType.Name.IndexOf("AssetReference", StringComparison.Ordinal) < 0) continue;
                object reference = field.GetValue(controller);
                if (reference == null) continue;
                try
                {
                    MethodInfo valid = reference.GetType().GetMethod("RuntimeKeyIsValid", Type.EmptyTypes);
                    if (valid != null && !(valid.Invoke(reference, null) is bool ok && ok)) continue;
                    hasReference = true;
                    object key = reference.GetType().GetProperty("RuntimeKey")?.GetValue(reference);
                    GameObject ghost = LoadAddressable(key);
                    if (ghost != null)
                    {
                        _ghostsLoaded++;
                        return ghost;
                    }
                }
                catch (Exception e)
                {
                    if (++_ghostErrors <= 5) _log.LogWarning("Pre-conversion : fantome de " + projectile.name + " impossible a charger (" + e.Message + ")");
                }
            }
            return hasReference ? null : projectile;  // une référence ratée : le projectile seul n'a rien à montrer
        }

        private int _ghostsLoaded, _ghostErrors;
        private static MethodInfo _loadAsset;
        private static bool _loadSearched;

        // Addressables.LoadAssetAsync<GameObject>(clé).WaitForCompletion(), sans dépendre de la bibliothèque.
        private static GameObject LoadAddressable(object key)
        {
            if (!_loadSearched)
            {
                _loadSearched = true;
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type = assembly.GetType("UnityEngine.AddressableAssets.Addressables");
                    if (type == null) continue;
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (method.Name != "LoadAssetAsync" || !method.IsGenericMethodDefinition) continue;
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(object)) continue;
                        _loadAsset = method.MakeGenericMethod(typeof(GameObject));
                        break;
                    }
                    break;
                }
            }
            if (_loadAsset == null || key == null) return null;
            object handle = _loadAsset.Invoke(null, new[] { key });
            object result = handle?.GetType().GetMethod("WaitForCompletion", Type.EmptyTypes)?.Invoke(handle, null);
            return result as GameObject;
        }

        // La liste pour Skyrim (corps.txt) : complétée avec les vrais noms des fantômes, à mesure qu'on les charge.
        private readonly List<string> _bodyList = new List<string>();
        private readonly HashSet<string> _bodyNames = new HashSet<string>();
        private bool _bodyListDirty;
        private float _bodyListSaved;

        private void AddToBodyList(string name)
        {
            if (name.Length == 0 || !_bodyNames.Add(name)) return;
            _bodyList.Add(name);
            _bodyListDirty = true;
            SaveBodyList(false);
        }

        private void SaveBodyList(bool now)
        {
            if (!_bodyListDirty || (!now && Time.unscaledTime - _bodyListSaved < 15f)) return;
            _bodyListDirty = false;
            _bodyListSaved = Time.unscaledTime;
            try
            {
                System.IO.Directory.CreateDirectory(ModelExporter.Folder);
                System.IO.File.WriteAllLines(System.IO.Path.Combine(ModelExporter.Folder, "corps.txt"), _bodyList);
            }
            catch (Exception e)
            {
                _log.LogWarning("Modeles : liste des personnages impossible a ecrire (" + e.Message + ")");
            }
        }

        // Une copie du tir qui ne fait rien : sans ses scripts (dégâts, sons, réseau, autodestruction),
        // sans collision ni physique, loin sous le monde. Ses modèles et particules restent visibles.
        private GameObject SpawnInert(GameObject prefab)
        {
            var holder = new GameObject("SkyrimRoR2_Preconversion");
            holder.SetActive(false);  // tant qu'il est éteint, aucun script de la copie ne démarre
            try
            {
                GameObject copy = UnityEngine.Object.Instantiate(prefab, new Vector3(0f, -4000f, 0f), Quaternion.identity, holder.transform);
                foreach (Joint joint in copy.GetComponentsInChildren<Joint>(true)) UnityEngine.Object.DestroyImmediate(joint);
                // Un script dont un autre a besoin (RequireComponent) ne part qu'après lui : on retire d'abord ceux
                // dont plus personne n'a besoin, en plusieurs passes (sinon Unity remplit le journal d'erreurs).
                for (int pass = 0; pass < 8; pass++)
                {
                    var scripts = new List<MonoBehaviour>(copy.GetComponentsInChildren<MonoBehaviour>(true));
                    scripts.RemoveAll(x => x == null);
                    if (scripts.Count == 0) break;
                    bool removed = false;
                    foreach (MonoBehaviour script in scripts)
                    {
                        if (NeededByOthers(script, scripts)) continue;
                        UnityEngine.Object.DestroyImmediate(script);
                        removed = true;
                    }
                    if (!removed) break;
                }
                // Une particule réglée pour détruire son objet en finissant emporterait la copie avant sa conversion.
                foreach (ParticleSystem particles in copy.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ParticleSystem.MainModule main = particles.main;
                    main.stopAction = ParticleSystemStopAction.None;
                }
                foreach (Collider collider in copy.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
                foreach (Rigidbody body in copy.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(body);
                foreach (Light light in copy.GetComponentsInChildren<Light>(true)) light.enabled = false;
                copy.transform.SetParent(null, true);
                return copy;
            }
            catch (Exception e)
            {
                _log.LogWarning("Pre-conversion : copie de " + prefab.name + " impossible (" + e.Message + ")");
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(holder);
            }
        }

        private static readonly Dictionary<Type, Type[]> _requires = new Dictionary<Type, Type[]>();

        // Un autre script encore là (sur le même objet) a-t-il besoin de celui-ci ?
        private static bool NeededByOthers(MonoBehaviour script, List<MonoBehaviour> scripts)
        {
            if (script == null) return false;
            Type type = script.GetType();
            foreach (MonoBehaviour other in scripts)
            {
                if (other == null || other == script || other.gameObject != script.gameObject) continue;
                foreach (Type needed in Requires(other.GetType()))
                    if (needed != null && needed.IsAssignableFrom(type)) return true;
            }
            return false;
        }

        private static Type[] Requires(Type type)
        {
            if (_requires.TryGetValue(type, out Type[] known)) return known;
            var list = new List<Type>();
            try
            {
                foreach (object attribute in type.GetCustomAttributes(typeof(RequireComponent), true))
                    if (attribute is RequireComponent r) list.AddRange(new[] { r.m_Type0, r.m_Type1, r.m_Type2 });
            }
            catch (Exception) { }
            list.RemoveAll(t => t == null);
            known = list.ToArray();
            _requires[type] = known;
            return known;
        }

        // scale : taille du modèle dans Skyrim (octet 108). Négative = c'est le survivant lui-même.
        private void Write(int index, string name, Vector3 skyrim, Quaternion rotation, int id, float scale = 1f)
        {
            Array.Clear(_entry, 0, _entry.Length);
            byte[] text = Encoding.ASCII.GetBytes(name + FileSuffix);
            Array.Copy(text, _entry, Math.Min(text.Length, 47));
            Put(48, skyrim.x); Put(52, skyrim.y); Put(56, skyrim.z);
            // Rotation Unity -> Skyrim : on échange les axes Y et Z des deux côtés.
            Matrix4x4 m = Matrix4x4.Rotate(rotation);
            int[] swap = { 0, 2, 1 };
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                    Put(60 + (row * 3 + column) * 4, m[swap[row], swap[column]]);
            BitConverter.GetBytes(id).CopyTo(_entry, 96);
            BitConverter.GetBytes(_versions[name]).CopyTo(_entry, 100);
            BitConverter.GetBytes(_bonePaths.TryGetValue(name, out List<string> paths) ? paths.Count : 0).CopyTo(_entry, 104);
            Put(108, scale);
            Marshal.Copy(_entry, 0, IntPtr.Add(_view, BridgeInfo.AllyHeaderSize + index * BridgeInfo.AllyEntrySize), _entry.Length);
        }

        private void Put(int offset, float value) => BitConverter.GetBytes(value).CopyTo(_entry, offset);

        // La pose de chaque pièce animée : où est son os par rapport à la racine du modèle, et tourné comment.
        private void WritePose(int index, string name, Transform model, int id)
        {
            if (!_bonePaths.TryGetValue(name, out List<string> paths)) return;
            if (!_boneCache.TryGetValue(id, out Transform[] bones) || bones.Length != paths.Count || (bones.Length > 0 && bones[0] != model))
            {
                bones = new Transform[paths.Count];
                for (int i = 0; i < paths.Count; i++) bones[i] = paths[i].Length == 0 ? model : model.Find(paths[i]);
                _boneCache[id] = bones;
            }
            Array.Clear(_pose, 0, _pose.Length);
            Quaternion inverse = Quaternion.Inverse(model.rotation);
            int[] swap = { 0, 2, 1 };
            for (int i = 0; i < bones.Length && i < BridgeInfo.AllyBones; i++)
            {
                Transform bone = bones[i] != null ? bones[i] : model;
                Vector3 offset = inverse * (bone.position - model.position);
                Matrix4x4 m = Matrix4x4.Rotate(inverse * bone.rotation);
                int at = i * 48;
                PutPose(at, offset.x); PutPose(at + 4, offset.z); PutPose(at + 8, offset.y);
                for (int row = 0; row < 3; row++)
                    for (int column = 0; column < 3; column++)
                        PutPose(at + 12 + (row * 3 + column) * 4, m[swap[row], swap[column]]);
            }
            Marshal.Copy(_pose, 0, IntPtr.Add(_view, BridgeInfo.AllyPoseOffset + index * BridgeInfo.AllyPoseSize), _pose.Length);
        }

        private void PutPose(int offset, float value) => BitConverter.GetBytes(value).CopyTo(_pose, offset);

        private bool Open()
        {
            if (_view != IntPtr.Zero) return true;
            if (_openTried) return false;
            _openTried = true;
            IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.AllyMapSize, BridgeInfo.AllyMapName);
            if (mapping != IntPtr.Zero) _view = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.AllyMapSize);
            if (_view == IntPtr.Zero) _log.LogWarning("Allies : pont impossible a ouvrir");
            return _view != IntPtr.Zero;
        }
    }
}
