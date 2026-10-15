using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;
using UnityEngine.Rendering;

namespace SkyrimRoR2
{
    // Les "mains" du survivant en première personne dans Skyrim.
    //
    // Une petite caméra invisible est posée à hauteur des yeux du survivant et regarde là où vise Skyrim.
    // Elle ne filme QUE le survivant (ses bras, son arme), sur fond transparent, avec ses vraies
    // animations RoR2 (tirs, rechargements, compétences). L'image est recopiée dans un second pont
    // ("Local\SkyrimRoR2_Mains"), et Skyrim l'affiche par-dessus son écran, comme dans un jeu de tir.
    //
    // Tous les réglages sont dans r2modman (section "Mains") pour ajuster le cadrage de chaque survivant.
    internal sealed class ViewmodelStreamer
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        private readonly ManualLogSource _log;
        private readonly IntPtr _view;

        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<int> _width;
        private readonly ConfigEntry<int> _height;
        private readonly ConfigEntry<int> _fps;
        private readonly ConfigEntry<float> _angleDown;
        private readonly ConfigEntry<float> _angleUp;
        private readonly ConfigEntry<float> _back;
        private readonly ConfigEntry<float> _up;
        private readonly ConfigEntry<float> _right;
        private readonly ConfigEntry<float> _nearClip;
        private readonly ConfigEntry<bool> _flip;
        private readonly ConfigEntry<int> _layerSetting;
        private readonly ConfigEntry<float> _lightIntensity;
        private readonly ConfigEntry<float> _zoom;
        private readonly ConfigEntry<bool> _hideHead;

        // ---- Cadrage par survivant ----
        // Chaque survivant tient son arme à sa façon : il a sa propre section "Mains - <survivant>"
        // dans r2modman, créée la première fois qu'on le joue (avec les valeurs générales comme point de départ).
        private sealed class Profile
        {
            public ConfigEntry<float> Forward, Up, Right, Zoom, ViewDown, ViewUp, Turn, Tilt, WeaponSize, WeaponTilt;
            public ConfigEntry<bool> HideBody, FollowModel, FollowWeapon;
        }

        private readonly ConfigFile _config;
        private readonly Dictionary<string, Profile> _profiles = new Dictionary<string, Profile>();
        private Profile _profile;

        // Cadrages préparés pour chaque survivant (d'après tes captures). Ils sont appliqués une fois,
        // puis tu peux les retoucher dans r2modman. Augmenter PresetVersion réapplique les cadrages préparés.
        private const int PresetVersion = 10;

        //                       Avance  Hauteur Decalage Zoom  VueBas VueHaut Rotation CacherCorps Inclinaison SuivreModele TailleArme InclinaisonArme [SuivreArme]
        private static readonly Dictionary<string, float[]> Presets = new Dictionary<string, float[]>
        {
            { "Commando",     new[] {  0.25f,  0.00f,  0.00f, 0.60f, 52f, 33f,   0f, 1f,  0f, 0f, 1.0f,  0f } },
            { "Huntress",     new[] {  0.05f, -0.15f,  0.00f, 0.78f, 50f, 33f,   0f, 1f,  0f, 0f, 1.0f,  0f } },
            { "Bandit2",      new[] {  0.25f,  0.00f,  0.00f, 0.60f, 52f, 33f,   0f, 1f,  0f, 0f, 1.0f,  0f } },
            { "Engi",         new[] {  0.50f, -0.05f,  0.00f, 0.55f, 65f, 33f,   0f, 1f,  0f, 0f, 1.0f,  0f } },
            { "Mage",         new[] { -0.15f, -0.05f,  0.00f, 0.78f, 50f, 33f,   0f, 1f,  0f, 0f, 1.0f,  0f } },
            { "Merc",         new[] {  0.10f,  0.00f,  0.00f, 0.55f, 60f, 33f,   0f, 1f,  0f, 0f, 1.0f,  0f } },
            // REX : la caméra derrière l'arme (0.60.0) n'allait pas du tout, retour à l'ancien cadrage (0.60.1).
            { "Treebot",      new[] {  0.10f,  0.35f,  0.30f, 0.50f, 48f, 33f, -20f, 1f,  0f, 0f, 1.0f,  0f, 0f } },
            // MUL-T : tout le corps caché sauf les bras et ses armes (sa tête n'est pas portée par l'os "Head").
            { "Toolbot",      new[] {  0.25f,  0.00f,  0.00f, 0.60f, 52f, 33f,   0f, 1f,  0f, 0f, 1.0f,  0f } },
            { "Loader",       new[] {  0.10f,  0.00f,  0.00f, 0.50f, 55f, 33f,   0f, 1f,  0f, 1f, 1.0f,  0f } },
            { "Croco",        new[] {  0.90f, -0.50f,  0.00f, 0.50f, 58f, 33f,   0f, 1f,  0f, 0f, 1.0f,  0f } },
            { "Captain",      new[] {  0.25f,  0.00f,  0.00f, 0.60f, 55f, 33f,   0f, 1f,  0f, 0f, 1.0f, 35f } },  // c'est l'arme qui pivote vers le bas, plus la caméra
            { "VoidSurvivor", new[] {  0.15f,  0.00f,  0.00f, 0.60f, 58f, 33f,   0f, 1f,  0f, 0f, 1.0f,  0f } },
            { "Seeker",       new[] {  0.40f, -0.20f,  0.00f, 0.50f, 68f, 33f,   0f, 0f,  0f, 0f, 1.0f,  0f } },  // parfait (tête seule cachée)
            { "FalseSon",     new[] {  0.00f,  0.00f,  0.00f, 0.75f, 52f, 33f,   0f, 0f,  0f, 0f, 1.5f,  0f } },  // pas mal (tête seule cachée)
            { "DroneTech",    new[] {  0.25f,  0.00f,  0.00f, 0.60f, 52f, 33f,   0f, 1f,  0f, 0f, 1.0f,  0f } },  // Opérateur
            { "Drifter",      new[] {  0.25f,  0.00f,  0.00f, 0.50f, 52f, 33f,   0f, 1f,  0f, 0f, 1.0f,  0f } },  // Vagabonde
        };

        // Cadrages refaits pour un seul survivant : réappliqués à ce survivant seulement (les autres gardent tes retouches).
        private static readonly Dictionary<string, int> PresetRevisions = new Dictionary<string, int>
        {
            { "Treebot", 12 },  // 0.60.1 : retour à l'ancien cadrage
            { "Toolbot", 11 },  // 0.73.1 : MUL-T avait "CacherCorps = false" : seule sa tête était visée, et elle restait à l'écran
        };

        private Profile GetProfile(CharacterBody body)
        {
            string name = body.name.Replace("(Clone)", "").Replace("Body", "").Trim();
            if (_profiles.TryGetValue(name, out Profile profile)) return profile;

            string section = "Mains - " + name;
            profile = new Profile
            {
                Forward = _config.Bind(section, "Avance", _back.Value, "Moves the camera forward in front of the face (meters). Negative = backwards (the head is hidden, you see the arms from behind)."),
                Up = _config.Bind(section, "Hauteur3D", _up.Value, "Moves the camera up (+) or down (-) (meters)."),
                Right = _config.Bind(section, "Decalage", _right.Value, "Moves the camera right (+) or left (-) (meters)."),
                Zoom = _config.Bind(section, "Zoom", _zoom.Value, "Size of the arms on screen (smaller = smaller arms)."),
                ViewDown = _config.Bind(section, "VueBas", _angleDown.Value, "How far down the camera sees (degrees)."),
                ViewUp = _config.Bind(section, "VueHaut", _angleUp.Value, "How far up the camera sees (degrees). Bigger = arms lower."),
                Turn = _config.Bind(section, "Rotation", 0f, "Turns the camera right (+) or left (-) (degrees), for survivors who hold their weapon to the side."),
                HideBody = _config.Bind(section, "CacherCorps", true, "Hides the whole body except the arms (and what they hold). Otherwise only the head is hidden."),
                Tilt = _config.Bind(section, "Inclinaison", 0f, "Raises (+) or lowers (-) the camera's gaze (degrees). Raising it makes the weapon point straighter ahead."),
                FollowModel = _config.Bind(section, "SuivreModele", false, "The camera turns with the survivor's body (not with the aim): you can no longer see behind them when they turn."),
                WeaponSize = _config.Bind(section, "TailleArme", 1f, "Enlarges (>1) or shrinks (<1) the weapon held in hand (1 = normal)."),
                WeaponTilt = _config.Bind(section, "InclinaisonArme", 0f, "Points the weapon lower (+) or higher (-), in degrees (picture only, the aim does not change)."),
                FollowWeapon = _config.Bind(section, "SuivreArme", false, "The camera sits behind and above the weapon (not at the head): for survivors whose weapon is far from the head (REX)."),
            };

            // Cadrage préparé : appliqué une seule fois (tes retouches ensuite sont gardées).
            ConfigEntry<int> version = _config.Bind(section, "VersionCadrage", 0, "Do not change: used to apply the prepared framings only once.");
            int needed = PresetRevisions.TryGetValue(name, out int revision) ? Math.Max(PresetVersion, revision) : PresetVersion;
            if (version.Value < needed)
            {
                if (Presets.TryGetValue(name, out float[] p))
                {
                    profile.Forward.Value = p[0]; profile.Up.Value = p[1]; profile.Right.Value = p[2]; profile.Zoom.Value = p[3];
                    profile.ViewDown.Value = p[4]; profile.ViewUp.Value = p[5]; profile.Turn.Value = p[6];
                    profile.HideBody.Value = p.Length < 8 || p[7] > 0.5f;
                    if (p.Length >= 11) { profile.Tilt.Value = p[8]; profile.FollowModel.Value = p[9] > 0.5f; profile.WeaponSize.Value = p[10]; }
                    if (p.Length >= 12) profile.WeaponTilt.Value = p[11];
                    profile.FollowWeapon.Value = p.Length >= 13 && p[12] > 0.5f;
                    _log.LogInfo("Mains : cadrage prepare applique pour " + name);
                }
                version.Value = needed;
            }

            _profiles[name] = profile;
            _log.LogInfo("Mains : cadrage du survivant '" + name + "' dans r2modman, section \"" + section + "\"");
            return profile;
        }

        private Camera _camera;

        // Un point du monde de RoR2 vu par la caméra des mains -> le point de Skyrim (en coordonnées RoR2) qui
        // apparaît au même endroit de l'écran, à la même distance. Sert à faire partir les tirs du canon qu'on voit
        // à l'écran en première personne (l'image des mains couvre tout l'écran de Skyrim).
        //   skyCam : caméra de Skyrim (monde de RoR2) ; skyRot : son orientation ; frustum : bords de son image
        //   à 1 m (gauche, droite, haut, bas). Faux si le point n'est pas dans l'image des mains (ou trop loin).
        public bool TryMatchScreen(Vector3 point, Vector3 skyCam, Quaternion skyRot, Vector4 frustum, out Vector3 result)
        {
            result = point;
            if (_camera == null || !_enabled.Value) return false;
            Vector3 v = _camera.WorldToViewportPoint(point);
            if (v.z < 0.05f || v.z > 4f || v.x < -0.2f || v.x > 1.2f || v.y < -0.2f || v.y > 1.2f) return false;
            float x = Mathf.Lerp(frustum.x, frustum.y, v.x) * v.z;
            float y = Mathf.Lerp(frustum.w, frustum.z, v.y) * v.z;
            result = skyCam + skyRot * new Vector3(x, y, v.z);
            return true;
        }
        private Light _light;
        private RenderTexture _target;       // filmé sur fond noir
        private RenderTexture _targetWhite;  // filmé sur fond blanc (la différence donne la transparence exacte)
        private int _layer = -1;
        private float _nextFrame;
        // Plusieurs images peuvent être "en route" depuis la carte graphique en même temps :
        // pas besoin d'attendre la précédente pour filmer la suivante (mouvements plus fluides).
        private sealed class Capture
        {
            public byte[] Black = new byte[0], White = new byte[0];
            public int Left, Width, Height;
            public float Started;
            public string Survivor;
        }
        private readonly HashSet<string> _diagnosed = new HashSet<string>();

        // Depuis la mise à jour 1.5, RoR2 charge l'apparence (les matières) des survivants en différé.
        // Chez Bandit, l'Opérateur et la Vagabonde, ce chargement ne se termine pas (morceaux sans matière,
        // éteints) : on redemande à RoR2 d'appliquer l'apparence du survivant.
        private float _nextSkinFix;
        private Transform _skinModel;
        private int _skinTries;

        // Surveille seulement (ne touche à rien : redemander l'apparence à RoR2 faisait planter le jeu).
        // Au début, tous les survivants n'ont pas encore leur apparence : c'est normal, elle arrive vite.
        // Si elle manque encore après 5 secondes, on l'écrit une fois dans le journal.
        private void WatchMissingSkin(Transform model)
        {
            if (model != _skinModel) { _skinModel = model; _skinTries = 0; _nextSkinFix = Time.unscaledTime + 5f; }
            if (_skinTries > 0 || Time.unscaledTime < _nextSkinFix) return;
            _skinTries = 1;
            int missing = 0;
            foreach (Renderer r in _renderers)
            {
                if (r == null || !r.gameObject.activeInHierarchy) continue;
                Material[] materials = r.sharedMaterials;
                if (materials.Length == 0 || Array.TrueForAll(materials, m => m == null)) missing++;
            }
            if (missing > 0)
                _log.LogWarning($"Mains : {model.name} n'a toujours pas son apparence apres 5 s ({missing} morceau(x) sans matiere) : RoR2 ne l'a pas chargee");
            else
                _log.LogInfo($"Mains : apparence de {model.name} bien chargee");
        }

        private bool UsesArm(Renderer r)
        {
            if (r is SkinnedMeshRenderer skin && skin.bones != null && skin.bones.Length > 0)
            {
                foreach (Transform b in skin.bones) if (b != null && _keepBones.Contains(b)) return true;
                return false;
            }
            for (Transform t = r.transform; t != null; t = t.parent) if (_keepBones.Contains(t)) return true;
            return false;
        }
        private readonly List<ShadowCastingMode> _savedShadows = new List<ShadowCastingMode>();
        private readonly List<bool> _savedForceOff = new List<bool>();
        private readonly List<bool> _savedEnabled = new List<bool>();
        private readonly List<Material[]> _savedMaterials = new List<Material[]>();
        private readonly Dictionary<Renderer, Material> _normalMaterial = new Dictionary<Renderer, Material>();

        // RoR2 (CharacterModel.baseRendererInfos) : chaque morceau du modèle et sa matière normale.
        private void ReadNormalLook(Transform model)
        {
            CharacterModel characterModel = model.GetComponent<CharacterModel>();
            if (characterModel == null) return;
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                if (!(typeof(CharacterModel).GetField("baseRendererInfos", flags)?.GetValue(characterModel) is Array infos)) return;
                foreach (object info in infos)
                {
                    if (info == null) continue;
                    Type type = info.GetType();
                    Renderer renderer = type.GetField("renderer", flags)?.GetValue(info) as Renderer;
                    Material material = type.GetField("defaultMaterial", flags)?.GetValue(info) as Material;
                    if (renderer != null && material != null && material.shader != null && material.shader.isSupported) _normalMaterial[renderer] = material;
                }
                if (_diagnosed.Add(model.name + "#look"))
                    _log.LogInfo($"Mains : {model.name} eteint par RoR2 : {_normalMaterial.Count} morceau(x) rallume(s) avec leur vraie matiere");
            }
            catch (Exception e)
            {
                if (_diagnosed.Add(model.name + "#lookerr")) _log.LogWarning("Mains : apparence normale illisible (" + e.Message + ")");
            }
        }
        private float _nextDiagnose;
        private readonly HashSet<string> _diagnosedEmpty = new HashSet<string>();
        private readonly List<MaterialPropertyBlock> _savedBlocks = new List<MaterialPropertyBlock>();
        private MaterialPropertyBlock _opaqueBlock;
        private static readonly int FadeId = Shader.PropertyToID("_Fade");

        // Toutes les 5 secondes, si l'image est vide : on écrit dans le journal ce que voit la caméra,
        // pour comprendre pourquoi un survivant reste invisible.
        private void Diagnose(CharacterBody body, Transform model)
        {
            if (_emptySince < 0f || Time.unscaledTime - _emptySince < 2f || Time.unscaledTime < _nextDiagnose) return;
            if (!_diagnosedEmpty.Add(model.name)) return;  // une fois par survivant
            _nextDiagnose = Time.unscaledTime + 5f;
            Vector3 cam = _camera.transform.position;
            _log.LogWarning($"Mains : image vide pour {model.name} (corps cache {_shrunkBody}) ; camera a {Vector3.Distance(cam, body.corePosition):F2} m du corps, " +
                            $"tete {(_head != null ? Vector3.Distance(_head.position, body.corePosition).ToString("F2") + " m" : "absente")}, " +
                            $"torse {(_chest != null ? Vector3.Distance(_chest.position, body.corePosition).ToString("F2") + " m" : "absent")}, echelle {model.lossyScale.x:F2}");
            foreach (Renderer r in _renderers)
            {
                if (r == null) continue;
                var shaders = new List<string>();
                foreach (Material m in r.sharedMaterials) shaders.Add(m != null && m.shader != null ? m.shader.name : "aucun");
                Bounds bounds = r.bounds;
                _log.LogWarning($"   - {r.name} ({r.GetType().Name}) : allume {r.enabled}, actif {r.gameObject.activeInHierarchy}, " +
                                $"a {Vector3.Distance(bounds.center, cam):F2} m de la camera, taille {bounds.size.magnitude:F2} m, " +
                                $"echelle {r.transform.lossyScale.x:F3}, matieres [{string.Join(", ", shaders)}]");
            }
        }
        private float _emptySince = -1f;
        private readonly List<Capture> _captures = new List<Capture>();
        private const int MaxInFlight = 3;
        private int _sentThisSecond;
        private int _gameFrames;
        private float _fpsWindowStart;
        private uint _sequence;
        private int _ready = 1;
        private byte[] _pixels = new byte[0];

        private byte[] _output = new byte[0];

        private Transform _cachedModel;
        private float _eyeHeight = 0.6f;  // hauteur des yeux au-dessus du centre du corps (gardée pendant une roulade)

        // Une compétence où le survivant roule sur lui-même (Roulé-boulé du CHEF).
        private static bool IsRolling(CharacterBody body)
        {
            foreach (EntityStateMachine machine in body.GetComponents<EntityStateMachine>())
            {
                object state = machine != null ? machine.state : null;
                string name = state != null ? state.GetType().Name : "";
                if (name.IndexOf("RolyPoly", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Rolling", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private Transform _headModel;
        private Transform _head;
        private Transform _chest;

        // ---- La tête du survivant cachée (comme dans un vrai jeu de tir) ----
        private Transform _headBoneModel;
        private bool _headHidden;

        // Appelé en fin d'image (après les animations) : la tête est rétrécie à presque rien,
        // pour ne jamais la voir de l'intérieur. Remise normale dès que les mains ne sont plus filmées.
        // Les os rétrécis (tête seule, ou tout le corps sauf les bras) et leur taille d'origine.
        private readonly List<Transform> _shrunk = new List<Transform>();
        private readonly List<Vector3> _shrunkScales = new List<Vector3>();
        private bool _shrunkBody;

        public void HideHead(CharacterBody body, bool skyrimConnected)
        {
            bool wanted = _enabled.Value && _hideHead.Value && skyrimConnected && body != null &&
                          body.modelLocator != null && body.modelLocator.modelTransform != null;
            Transform model = wanted ? body.modelLocator.modelTransform : null;
            bool hideBody = wanted && GetProfile(body).HideBody.Value;

            float weaponSize = model != null ? Mathf.Clamp(GetProfile(body).WeaponSize.Value, 0.2f, 4f) : 1f;
            if (model != _headBoneModel || hideBody != _shrunkBody)
            {
                RestoreHead();
                _headBoneModel = model;
                _shrunkBody = hideBody;
                if (model != null) { ChooseBonesToShrink(model, hideBody); ChooseWeaponBones(model); }
                _nextRendererScan = 0f;  // refaire la liste des morceaux filmés
            }
            if (!wanted || (_shrunk.Count == 0 && _weapons.Count == 0)) { RestoreHead(); return; }

            if (!_headHidden)
            {
                _shrunkScales.Clear();
                foreach (Transform bone in _shrunk) _shrunkScales.Add(bone != null ? bone.localScale : Vector3.one);
                _weaponScales.Clear();
                foreach (Transform bone in _weapons) _weaponScales.Add(bone != null ? bone.localScale : Vector3.one);
                _headHidden = true;
            }
            foreach (Transform bone in _shrunk)
                if (bone != null) bone.localScale = Vector3.one * 0.001f;
            for (int i = 0; i < _weapons.Count && i < _weaponScales.Count; i++)
                if (_weapons[i] != null) _weapons[i].localScale = _weaponScales[i] * weaponSize;

            // L'arme pivote vers le bas autour de l'axe "droite" du regard (après les animations, à chaque image).
            float weaponTilt = Mathf.Clamp(GetProfile(body).WeaponTilt.Value, -90f, 90f);
            if (Mathf.Abs(weaponTilt) > 0.01f)
            {
                Vector3 aim = body.inputBank != null ? body.inputBank.aimDirection : body.transform.forward;
                Vector3 flat = new Vector3(aim.x, 0f, aim.z);
                if (flat.sqrMagnitude < 0.0001f) flat = body.transform.forward;
                Vector3 rightAxis = Vector3.Cross(Vector3.up, flat.normalized).normalized;
                Quaternion turn = Quaternion.AngleAxis(weaponTilt, rightAxis);
                foreach (Transform w in _weapons)
                    if (w != null) w.rotation = turn * w.rotation;
                _lastTilt = turn;
            }
            else _lastTilt = Quaternion.identity;
        }

        private Quaternion _lastTilt = Quaternion.identity;
        private bool _shownForExport;

        // Le survivant en 3D dans Skyrim a besoin du corps ENTIER : on remet un instant la taille normale
        // des os rétrécis pour la vue à la première personne (et l'arme droite), le temps de le recopier...
        public void ShowWholeBody()
        {
            if (!_headHidden || _shownForExport) return;
            _shownForExport = true;
            for (int i = 0; i < _shrunk.Count && i < _shrunkScales.Count; i++)
                if (_shrunk[i] != null) _shrunk[i].localScale = _shrunkScales[i];
            Quaternion untilt = Quaternion.Inverse(_lastTilt);
            for (int i = 0; i < _weapons.Count && i < _weaponScales.Count; i++)
            {
                if (_weapons[i] == null) continue;
                _weapons[i].localScale = _weaponScales[i];
                _weapons[i].rotation = untilt * _weapons[i].rotation;
            }
        }

        // ...puis on les rétrécit de nouveau pour la vue à la première personne.
        public void HideAgain(CharacterBody body)
        {
            if (!_shownForExport) return;
            _shownForExport = false;
            if (!_headHidden) return;
            float weaponSize = body != null ? Mathf.Clamp(GetProfile(body).WeaponSize.Value, 0.2f, 4f) : 1f;
            foreach (Transform bone in _shrunk)
                if (bone != null) bone.localScale = Vector3.one * 0.001f;
            for (int i = 0; i < _weapons.Count && i < _weaponScales.Count; i++)
            {
                if (_weapons[i] == null) continue;
                _weapons[i].localScale = _weaponScales[i] * weaponSize;
                _weapons[i].rotation = _lastTilt * _weapons[i].rotation;
            }
        }

        // La position de l'arme repérée (la première, si elle est bien près du survivant).
        private bool _anchorLogged;
        private bool TryWeaponAnchor(CharacterBody body, out Vector3 position)
        {
            position = Vector3.zero;
            const float reach = 5f;  // m : au-dela, ce n'est pas l'arme qu'il tient
            foreach (Transform weapon in _weapons)
            {
                if (weapon == null || Vector3.Distance(weapon.position, body.corePosition) > reach) continue;
                position = weapon.position;
                if (!_anchorLogged)
                {
                    _anchorLogged = true;
                    _log.LogInfo($"Mains : camera placee derriere l'arme {weapon.name} (a {Vector3.Distance(weapon.position, body.corePosition):F2} m du corps)");
                }
                return true;
            }
            return false;
        }

        // L'arme tenue en main (pour "TailleArme") : les objets du modèle dont le nom désigne une arme.
        private readonly List<Transform> _weapons = new List<Transform>();
        private readonly List<Vector3> _weaponScales = new List<Vector3>();
        private static readonly string[] WeaponWords =
        {
            "weapon", "gun", "cannon", "bow", "sword", "blade", "hammer", "club", "mace", "staff", "spear", "pistol", "rifle", "knife", "axe",
        };

        private void ChooseWeaponBones(Transform model)
        {
            _weapons.Clear();
            foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
            {
                string name = t.name.ToLowerInvariant();
                bool weapon = false;
                foreach (string word in WeaponWords) if (name.Contains(word)) { weapon = true; break; }
                if (!weapon || t == model || name.Contains("hitbox") || name.Contains("muzzle") || name.Contains("elbow") ||
                    name.Contains("ik") || name.Contains("control") || name.Contains("placement") || name.Contains("target")) continue;
                bool parentAlready = false;
                foreach (Transform w in _weapons) if (t.IsChildOf(w)) { parentAlready = true; break; }
                if (!parentAlready) _weapons.Add(t);
            }
            var names = new List<string>();
            foreach (Transform w in _weapons) names.Add(w.name);
            _log.LogInfo("Mains : arme(s) reperee(s) : " + (names.Count > 0 ? string.Join(", ", names) : "aucune"));
        }

        private void RestoreHead()
        {
            if (_headHidden)
            {
                for (int i = 0; i < _shrunk.Count && i < _shrunkScales.Count; i++)
                    if (_shrunk[i] != null) _shrunk[i].localScale = _shrunkScales[i];
                for (int i = 0; i < _weapons.Count && i < _weaponScales.Count; i++)
                    if (_weapons[i] != null) _weapons[i].localScale = _weaponScales[i];
            }
            _headHidden = false;
        }

        // Mots qui désignent les bras et ce qu'ils tiennent (gardés visibles en mode "corps caché").
        private static readonly string[] ArmWords =
        {
            "hand", "arm", "clavicle", "shoulder", "elbow", "wrist", "finger", "thumb", "index", "pinky", "palm",
            "weapon", "gun", "cannon", "bow", "sword", "blade", "hammer", "club", "fist", "claw", "gauntlet", "pistol", "rifle",
            "bag", "sack",  // le sac de la Vagabonde
        };

        private static bool IsArmBone(string name)
        {
            // 0.73.1 : "JiggleHandle.Head" (MUL-T) contient "hand" : sa tête et ses jambes étaient gardées comme des bras.
            name = name.ToLowerInvariant().Replace("handle", "");
            foreach (string word in ArmWords) if (name.Contains(word)) return true;
            return false;
        }

        // Tête seule : l'os "head". Corps caché : tout ce qui n'est ni un bras (avec ce qu'il tient),
        // ni sur le chemin entre la racine et les bras (bassin, colonne, poitrine, qu'on ne peut pas
        // rétrécir sans rétrécir aussi les bras). Jambes, tête, cou, cape, cheveux... disparaissent.
        // Les os des bras gardés (corps caché) : sert aussi à ne filmer que les morceaux qui en dépendent.
        private readonly HashSet<Transform> _keepBones = new HashSet<Transform>();

        private void ChooseBonesToShrink(Transform model, bool hideBody)
        {
            _shrunk.Clear();
            _keepBones.Clear();
            Transform head = FindHeadBone(model);

            if (hideBody)
            {
                var bones = new HashSet<Transform>();
                foreach (SkinnedMeshRenderer skin in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (skin.bones != null) foreach (Transform b in skin.bones) if (b != null) bones.Add(b);

                // Les os des bras, et tous les os qu'ils contiennent.
                var keep = new HashSet<Transform>();
                foreach (Transform b in bones)
                {
                    if (!IsArmBone(b.name)) continue;
                    foreach (Transform child in b.GetComponentsInChildren<Transform>(true)) keep.Add(child);
                }
                // Le chemin de la racine jusqu'aux bras.
                var path = new HashSet<Transform>();
                foreach (Transform k in keep)
                    for (Transform t = k.parent; t != null && t != model.parent; t = t.parent) path.Add(t);

                if (keep.Count >= 4)
                {
                    _keepBones.Clear();
                    foreach (Transform k in keep) _keepBones.Add(k);
                    var tops = new List<string>();
                    foreach (Transform k in keep) if (k.parent == null || !keep.Contains(k.parent)) tops.Add(k.name);
                    _log.LogInfo("Mains : bras gardes a partir de : " + string.Join(", ", tops));
                    foreach (Transform b in bones)
                    {
                        if (keep.Contains(b) || path.Contains(b)) continue;
                        if (b.parent != null && !keep.Contains(b.parent) && !path.Contains(b.parent) && bones.Contains(b.parent)) continue;  // son parent est déjà rétréci
                        _shrunk.Add(b);
                    }
                    if (head != null && !_shrunk.Contains(head) && !keep.Contains(head)) _shrunk.Add(head);
                    _log.LogInfo($"Mains : corps cache sauf les bras ({_shrunk.Count} os retrecis, {keep.Count} os de bras gardes)");
                    // Les os rétrécis peuvent servir de "centre" à un morceau du modèle : Unity croirait alors ce morceau
                    // minuscule et ne le dessinerait plus (Bandit, Vagabonde... visibles une image puis plus rien).
                    foreach (SkinnedMeshRenderer skin in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        skin.updateWhenOffscreen = true;
                    return;
                }
                _log.LogInfo("Mains : pas de bras reconnus sur ce modele, seule la tete est cachee");
            }

            if (head != null) _shrunk.Add(head);
            _log.LogInfo("Mains : os de la tete " + (head != null ? head.name : "introuvable"));
        }

        // L'os de la tête : celui qui s'appelle "head" parmi les os qui déforment le modèle.
        private static Transform FindHeadBone(Transform model)
        {
            Transform best = null;
            foreach (SkinnedMeshRenderer skin in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skin.bones == null) continue;
                foreach (Transform bone in skin.bones)
                {
                    if (bone == null) continue;
                    string name = bone.name.ToLowerInvariant();
                    if (name == "head" || name.EndsWith(".head") || name.EndsWith("_head") || name.EndsWith(" head")) return bone;
                    if (best == null && name.Contains("head") && !name.Contains("center") && !name.Contains("end") && !IsArmBone(name)) best = bone;
                }
            }
            return best;
        }
        private float _nextRendererScan;
        private readonly List<Renderer> _renderers = new List<Renderer>();
        private readonly List<int> _savedLayers = new List<int>();
        private bool _loggedFirst;

        public ViewmodelStreamer(ManualLogSource log, ConfigFile config)
        {
            _log = log;
            _config = config;
            _enabled = config.Bind("Mains", "Activer", true, "Films the survivor's arms and weapon to show them in first person in Skyrim.");
            _width = config.Bind("Mains", "Largeur", 960, "Picture width in pixels (bigger = sharper, but heavier). 1280 maximum.");
            _height = config.Bind("Mains", "Hauteur", 540, "Picture height in pixels. 720 maximum.");
            _fps = config.Bind("Mains", "ImagesParSeconde", 60, "Frames per second sent to Skyrim (120 maximum).");
            // Les bras sont SOUS les yeux (environ 40 degres plus bas) : une camera normale regardant droit devant
            // ne les voit pas. L'image va donc de "AngleBas" sous l'horizon (bas de l'ecran) a "AngleHaut" au-dessus.
            _angleDown = config.Bind("Mains", "VueBas", 52f, "How far down the camera sees, in degrees below the horizon (bottom of the screen). Bigger = arms higher and smaller.");
            _angleUp = config.Bind("Mains", "VueHaut", 33f, "How far up the camera sees, in degrees above the horizon (top of the screen). Bigger = arms lower, and nothing is cut off when looking up.");
            _back = config.Bind("Mains", "Avance", 0.25f, "Starting value for new survivors (each then has its own 'Mains - <survivor>' section). Moves the camera forward in front of the face (meters).");
            _up = config.Bind("Mains", "Hauteur3D", 0.0f, "Moves the camera up (+) or down (-) (meters).");
            _right = config.Bind("Mains", "Decalage", 0.0f, "Moves the camera right (+) or left (-) (meters).");
            _nearClip = config.Bind("Mains", "DistanceMinimale", 0.05f, "Anything closer to the camera than this (in meters) is cut off.");
            _hideHead = config.Bind("Mains", "CacherTete", true, "Hides the survivor's head (so you never see it from the inside when looking down).");
            _zoom = config.Bind("Mains", "Zoom", 0.75f, "Size of the arms on screen: 1 = normal, smaller = smaller arms (wider view), bigger = bigger arms.");
            _lightIntensity = config.Bind("Mains", "Lumiere", 1.2f, "Strength of the light on the arms (0 to 5).");
            _layerSetting = config.Bind("Mains", "Calque", -1, "Unity layer used for filming (-1 = automatic). Only change it if other things show up in the picture.");
            _flip = config.Bind("Mains", "RetournerImage", true, "Flips the picture vertically (change it if the hands show upside down).");
            // Les anciens réglages gardaient 20 images par seconde : on passe une fois à 60.
            ConfigEntry<int> settingsVersion = config.Bind("Mains", "VersionReglages", 0, "Do not change.");
            if (settingsVersion.Value < 1)
            {
                if (_fps.Value < 60) _fps.Value = 60;
                settingsVersion.Value = 1;
            }

            IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.ViewMapSize, BridgeInfo.ViewMapName);
            if (mapping != IntPtr.Zero)
                _view = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.ViewMapSize);
            if (_view == IntPtr.Zero)
                _log.LogError("Mains : impossible d'ouvrir le pont des images (erreur Windows " + Marshal.GetLastWin32Error() + ")");
        }

        public void Update(CharacterBody body, bool skyrimConnected)
        {
            if (_view == IntPtr.Zero) return;

            bool active = _enabled.Value && skyrimConnected && body != null && body.modelLocator != null && body.modelLocator.modelTransform != null;
            Marshal.WriteInt32(_view, 20, active ? 1 : 0);
            float now = Time.unscaledTime;
            if (!active) return;
            int inFlight = 0;
            foreach (Capture c in _captures)
            {
                if (c.Left > 0 && now - c.Started > 1f) c.Left = 0;  // jamais revenue : on la libère
                if (c.Left > 0) inFlight++;
            }
            if (inFlight >= MaxInFlight) return;

            _gameFrames++;
            // Rythme régulier : on vise l'heure prévue (et non "maintenant + intervalle"), sinon une image sur deux
            // était sautée quand RoR2 tourne à peu près à la même vitesse que le réglage.
            float interval = 1f / Mathf.Clamp(_fps.Value, 5, 120);
            if (now + 0.002f < _nextFrame) return;
            _nextFrame = now - _nextFrame > interval ? now + interval : _nextFrame + interval;

            try
            {
                _profile = GetProfile(body);
                string survivorName = body.name.Replace("(Clone)", "").Replace("Body", "").Trim();
                if (_testsDone.Add(survivorName)) { _testName = survivorName; _testAt = Time.unscaledTime + 3f; }
                Setup();
                if (_camera == null || _layer < 0) return;
                Render(body);
            }
            catch (Exception e)
            {
                _log.LogError("Mains : " + e.Message);
                _nextFrame = now + 5f;
            }
        }

        private void Setup()
        {
            int width = Mathf.Clamp(_width.Value, 64, BridgeInfo.ViewMaxWidth);
            int height = Mathf.Clamp(_height.Value, 64, BridgeInfo.ViewMaxHeight);

            if (_layer < 0)
            {
                // Un calque de Unity rien que pour le survivant pendant qu'on le filme. RoR2 les utilise tous :
                // on prend "UI" (les menus de RoR2 s'affichent sans caméra, donc ce calque est presque vide),
                // et la caméra ne voit qu'à quelques mètres : rien d'autre ne risque d'apparaître.
                int wanted = _layerSetting.Value;
                // "PostProcess" ne contient que des zones d'effets, sans rien à dessiner
                // (sur "UI", les traînées de balles de RoR2 apparaissaient aussi).
                _layer = wanted >= 0 && wanted <= 31 ? wanted : LayerMask.NameToLayer("PostProcess");
                if (_layer < 0) _layer = LayerMask.NameToLayer("UI");
                if (_layer < 0) _layer = 5;
                _log.LogInfo("Mains : calque " + _layer + " (" + LayerMask.LayerToName(_layer) + ") utilise pour filmer le survivant");
            }

            if (_target == null || _target.width != width || _target.height != height)
            {
                if (_target != null) _target.Release();
                if (_targetWhite != null) _targetWhite.Release();
                _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { antiAliasing = 1 };
                _target.Create();
                _targetWhite = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { antiAliasing = 1 };
                _targetWhite.Create();
            }

            if (_camera == null)
            {
                var holder = new GameObject("SkyrimRoR2_CameraMains");
                UnityEngine.Object.DontDestroyOnLoad(holder);
                _camera = holder.AddComponent<Camera>();
                _camera.enabled = false;  // on la déclenche nous-mêmes
                _camera.clearFlags = CameraClearFlags.SolidColor;
                // Le survivant est filmé deux fois, sur fond noir puis sur fond blanc : là où les deux images
                // diffèrent, c'est le fond (transparent) ; là où elles sont pareilles, c'est le survivant.
                // Aucune couleur n'est "interdite" (fini le magenta) et les effets lumineux gardent leur transparence.
                _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                // Même mode de rendu que RoR2 (ses matériaux ne savent se dessiner que de cette façon).
                _camera.renderingPath = RenderingPath.UsePlayerSettings;

                // Notre propre lumière, qui n'éclaire que le survivant filmé
                // (les lumières de la carte RoR2 ne touchent pas ce calque : il serait tout noir).
                var lightHolder = new GameObject("SkyrimRoR2_LumiereMains");
                lightHolder.transform.SetParent(holder.transform, false);
                lightHolder.transform.localRotation = Quaternion.Euler(35f, -20f, 0f);  // un peu au-dessus, à gauche
                _light = lightHolder.AddComponent<Light>();
                _light.type = LightType.Directional;
                _light.color = Color.white;
                _light.shadows = LightShadows.None;
                _camera.allowHDR = false;
                _camera.allowMSAA = false;
            }
            _camera.targetTexture = _target;
            _camera.cullingMask = 1 << _layer;
            if (_light != null)
            {
                _light.cullingMask = 1 << _layer;
                _light.intensity = Mathf.Clamp(_lightIntensity.Value, 0f, 5f);
            }
            float near = Mathf.Clamp(_nearClip.Value, 0.01f, 2f);
            const float far = 6f;  // seulement le survivant, tout près
            _camera.nearClipPlane = near;
            _camera.farClipPlane = far;

            // Image "décentrée" vers le bas : du dessous des yeux jusqu'un peu au-dessus de l'horizon.
            float bottom = -Mathf.Tan(Mathf.Clamp(_profile.ViewDown.Value, 5f, 85f) * Mathf.Deg2Rad) * near;
            float top = Mathf.Tan(Mathf.Clamp(_profile.ViewUp.Value, -40f, 60f) * Mathf.Deg2Rad) * near;
            if (top <= bottom + 0.0001f) top = bottom + 0.0001f;
            float zoom = Mathf.Clamp(_profile.Zoom.Value, 0.2f, 3f);  // plus petit = vue plus large = bras plus petits
            bottom /= zoom;
            top /= zoom;
            float halfWidth = (top - bottom) * 0.5f * width / height;
            _camera.projectionMatrix = Matrix4x4.Frustum(-halfWidth, halfWidth, bottom, top, near, far);
        }

        private void Render(CharacterBody body)
        {
            Transform model = body.modelLocator.modelTransform;
            float now = Time.unscaledTime;
            if (model != _cachedModel || now >= _nextRendererScan)
            {
                _cachedModel = model;
                _nextRendererScan = now + 2f;  // les armes et effets peuvent apparaître : on rescanne de temps en temps
                _renderers.Clear();
                model.GetComponentsInChildren(true, _renderers);
                // Seulement le corps et l'arme : pas les éclairs de tir ni les traînées (ils éblouissent).
                _renderers.RemoveAll(r => !(r is SkinnedMeshRenderer || r is MeshRenderer));
                // Corps caché : on ne filme que les morceaux accrochés aux bras. Les autres (la plante de REX,
                // une cape, un sac...) ne sont pas filmés du tout, même s'ils ne suivent pas les os.
                if (_shrunkBody && _headBoneModel == model && _keepBones.Count > 0)
                {
                    int before = _renderers.Count;
                    _renderers.RemoveAll(r => !UsesArm(r));
                    if (_renderers.Count < before && _diagnosed.Add(model.name + "#bras"))
                        _log.LogInfo($"Mains : {before - _renderers.Count} morceau(x) du corps non filme(s) (pas accroches aux bras)");
                }
                // Les animations continuent même si la caméra de RoR2 ne voit pas le survivant.
                foreach (Animator animator in model.GetComponentsInChildren<Animator>(true))
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                if (_diagnosed.Add(model.name))
                {
                    int on = 0;
                    foreach (Renderer r in _renderers) if (r != null && r.enabled && r.gameObject.activeInHierarchy && !r.forceRenderingOff) on++;
                    _log.LogInfo($"Mains : modele {model.name} : {_renderers.Count} morceaux, {on} affiches");
                }
            }
            foreach (Renderer r in _renderers)
                if (r is SkinnedMeshRenderer skin && !skin.updateWhenOffscreen) skin.updateWhenOffscreen = true;
            WatchMissingSkin(model);

            // La caméra : aux yeux du survivant, tournée là où vise Skyrim.
            Vector3 aim = body.inputBank != null ? body.inputBank.aimDirection : body.transform.forward;
            if (aim.sqrMagnitude < 0.001f) aim = body.transform.forward;
            aim.Normalize();
            // Les yeux : la tête du modèle si on la trouve, sinon un peu au-dessus du point de visée de RoR2
            // (qui est plutôt au niveau de la poitrine).
            Vector3 eye = body.inputBank != null ? body.inputBank.aimOrigin + Vector3.up * 0.3f : body.corePosition + Vector3.up * 0.6f;
            if (model != _headModel)
            {
                _headModel = model;
                ChildLocator locator = model.GetComponent<ChildLocator>();
                _head = locator != null ? (locator.FindChild("Head") ?? locator.FindChild("HeadCenter")) : null;
                _chest = locator != null ? (locator.FindChild("Chest") ?? locator.FindChild("Pelvis")) : null;
                _log.LogInfo("Mains : tete du survivant " + (_head != null ? "trouvee" : "introuvable (position estimee)") +
                             ", torse " + (_chest != null ? "trouve" : "introuvable"));
            }
            // 0.73 : pendant une roulade (Roulé-boulé du CHEF...), la tête et le torse tournent sur eux-mêmes : la caméra
            // les suivait et l'image tournait. Elle reste alors à la hauteur des yeux d'avant la roulade.
            bool rolling = IsRolling(body);
            if (rolling)
                eye = body.corePosition + Vector3.up * _eyeHeight;
            else
            {
                // Le modèle se penche quand le survivant s'arrête (petit élan) : la caméra suit le torse,
                // comme ça les bras ne "s'envolent" pas devant elle.
                if (_chest != null && Vector3.Distance(_chest.position, body.corePosition) < 2f) { eye.x = _chest.position.x; eye.z = _chest.position.z; }
                // La tête bouge avec les animations (elle part en avant quand on vise vers le bas) :
                // on ne prend que sa hauteur, la position à l'horizontale reste celle du corps.
                if (_head != null && Vector3.Distance(_head.position, body.corePosition) < 2f) eye.y = Mathf.Max(eye.y, _head.position.y + 0.3f);
                _eyeHeight = Mathf.Clamp(eye.y - body.corePosition.y, 0.2f, 2.5f);
            }

            // On avance à l'horizontale (pas dans le sens du regard) : en regardant vers le bas,
            // la caméra ne plonge pas dans le corps.
            Vector3 flatForward = new Vector3(aim.x, 0f, aim.z);
            if (_profile.FollowModel.Value) flatForward = new Vector3(model.forward.x, 0f, model.forward.z);  // tourne avec le corps
            flatForward = flatForward.sqrMagnitude > 0.0001f ? flatForward.normalized : body.transform.forward;
            Vector3 right = Vector3.Cross(Vector3.up, flatForward).normalized;
            // En visant vers le bas, la tête du survivant part en avant : la caméra avance aussi pour rester devant.
            float pitch = Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg;
            float lookingDown = Mathf.Clamp01(-pitch / 90f);
            // Caméra derrière l'arme (REX...) : 70 cm derrière et 50 cm au-dessus, l'arme apparaît en bas de l'image.
            if (_profile.FollowWeapon.Value && TryWeaponAnchor(body, out Vector3 weapon))
                eye = weapon - flatForward * 0.85f + Vector3.up * 0.5f;
            _camera.transform.position = eye + flatForward * (0.15f + _profile.Forward.Value + lookingDown * 0.45f) + Vector3.up * _profile.Up.Value + right * _profile.Right.Value;

            // La caméra suit le regard vers le haut et le bas, mais peu et sans dépasser 25 degrés :
            // sinon on voit le dessus du crâne et l'image se déforme.
            float cameraPitch = Mathf.Clamp(pitch * 0.4f, -25f, 25f) + Mathf.Clamp(_profile.Tilt.Value, -45f, 45f);
            Vector3 lookForward = Quaternion.Euler(0f, _profile.Turn.Value, 0f) * flatForward;
            _camera.transform.rotation = Quaternion.LookRotation(lookForward, Vector3.up) * Quaternion.Euler(-cameraPitch, 0f, 0f);

            // Le survivant passe un instant sur notre calque, on filme, puis il revient sur le sien.
            _savedLayers.Clear();
            foreach (Renderer renderer in _renderers)
            {
                if (renderer == null) { _savedLayers.Add(-1); continue; }
                _savedLayers.Add(renderer.gameObject.layer);
                renderer.gameObject.layer = _layer;
            }
            ForceOpaque(model);
            // Certains survivants (Bandit, Opérateur, Vagabonde...) sont dessinés par RoR2 "en ombre seulement"
            // ou "forcés invisibles" quand sa caméra est tout près d'eux : pour notre caméra, on les rallume.
            _savedShadows.Clear();
            _savedForceOff.Clear();
            _savedEnabled.Clear();
            _savedMaterials.Clear();
            // Depuis RoR2 1.5, certains (Bandit, Opérateur, Vagabonde...) sont carrément ÉTEINTS quand la caméra
            // de RoR2 est tout près : si tout le corps est éteint, on rallume ses morceaux le temps de filmer.
            bool bodyOn = false;
            foreach (Renderer renderer in _renderers)
                if (renderer is SkinnedMeshRenderer && renderer.enabled && renderer.gameObject.activeInHierarchy) { bodyOn = true; break; }
            // Les morceaux "normaux" du modèle et leur vraie matière, d'après RoR2 lui-même (CharacterModel) :
            // éteint, RoR2 leur met une matière vide (d'où le rose) et cache aussi l'arme.
            _normalMaterial.Clear();
            if (!bodyOn) ReadNormalLook(model);
            foreach (Renderer renderer in _renderers)
            {
                if (renderer == null) { _savedShadows.Add(ShadowCastingMode.On); _savedForceOff.Add(false); _savedEnabled.Add(false); _savedMaterials.Add(null); continue; }
                _savedShadows.Add(renderer.shadowCastingMode);
                _savedForceOff.Add(renderer.forceRenderingOff);
                _savedEnabled.Add(renderer.enabled);
                _savedMaterials.Add(null);
                if (!bodyOn && _normalMaterial.TryGetValue(renderer, out Material normal))
                {
                    renderer.enabled = true;
                    _savedMaterials[_savedMaterials.Count - 1] = renderer.sharedMaterials;
                    Material[] list = renderer.sharedMaterials;
                    if (list.Length == 0) list = new Material[1];
                    for (int m = 0; m < list.Length; m++) list[m] = normal;
                    renderer.sharedMaterials = list;
                }
                else if (!bodyOn && _normalMaterial.Count == 0 && renderer is SkinnedMeshRenderer && renderer.sharedMaterial != null)
                    renderer.enabled = true;  // RoR2 ne dit rien : on rallume au moins le corps tel quel
                if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.forceRenderingOff = false;
            }
            // RoR2 efface le survivant (propriété "_Fade" des matières) selon la distance de SA caméra :
            // pour la nôtre, on le force bien visible pendant la prise de vue, puis on remet comme avant.
            if (_opaqueBlock == null) _opaqueBlock = new MaterialPropertyBlock();
            while (_savedBlocks.Count < _renderers.Count) _savedBlocks.Add(new MaterialPropertyBlock());
            for (int i = 0; i < _renderers.Count; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer == null) continue;
                renderer.GetPropertyBlock(_savedBlocks[i]);
                renderer.GetPropertyBlock(_opaqueBlock);
                _opaqueBlock.SetFloat(FadeId, 1f);
                renderer.SetPropertyBlock(_opaqueBlock);
            }
            Diagnose(body, model);
            try
            {
                _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                _camera.targetTexture = _target;
                _camera.Render();
                _camera.backgroundColor = new Color(1f, 1f, 1f, 0f);
                _camera.targetTexture = _targetWhite;
                _camera.Render();
                _camera.targetTexture = _target;
            }
            finally
            {
                for (int i = _renderers.Count - 1; i >= 0; i--)  // à l'envers : plusieurs morceaux peuvent partager un objet
                {
                    if (_renderers[i] != null && _savedLayers[i] >= 0) _renderers[i].gameObject.layer = _savedLayers[i];
                    if (_renderers[i] != null && i < _savedBlocks.Count)
                        _renderers[i].SetPropertyBlock(_savedBlocks[i].isEmpty ? null : _savedBlocks[i]);
                    if (_renderers[i] != null && i < _savedShadows.Count)
                    {
                        _renderers[i].shadowCastingMode = _savedShadows[i];
                        _renderers[i].forceRenderingOff = _savedForceOff[i];
                        if (i < _savedMaterials.Count && _savedMaterials[i] != null) _renderers[i].sharedMaterials = _savedMaterials[i];
                        if (i < _savedEnabled.Count) _renderers[i].enabled = _savedEnabled[i];
                    }
                }
            }

            // On récupère l'image sans bloquer le jeu (la carte graphique la rend un peu plus tard).
            Capture capture = null;
            foreach (Capture c in _captures) if (c.Left == 0) { capture = c; break; }
            if (capture == null) { capture = new Capture(); _captures.Add(capture); }
            capture.Width = _target.width;
            capture.Height = _target.height;
            capture.Left = 2;
            capture.Started = Time.unscaledTime;
            capture.Survivor = body.name.Replace("(Clone)", "").Replace("Body", "").Trim();
            Capture current = capture;
            AsyncGPUReadback.Request(_target, 0, TextureFormat.RGBA32, request => OnReadback(request, current, false));
            AsyncGPUReadback.Request(_targetWhite, 0, TextureFormat.RGBA32, request => OnReadback(request, current, true));
        }

        // RoR2 rend le survivant transparent quand SA caméra est trop près de lui.
        // Pour notre caméra, on le veut toujours bien opaque.
        private FieldInfo _fadeField;
        private MethodInfo _updateMaterials;
        private bool _fadeSearched;

        private void ForceOpaque(Transform model)
        {
            // Certains survivants s'effacent avec un autre système ("DitherModel") : on le force aussi à opaque.
            foreach (MonoBehaviour behaviour in model.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || behaviour.GetType().Name != "DitherModel") continue;
                FieldInfo fade = behaviour.GetType().GetField("fade", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                try { if (fade != null && fade.FieldType == typeof(float)) fade.SetValue(behaviour, 1f); } catch { }
            }

            CharacterModel characterModel = model.GetComponent<CharacterModel>();
            if (characterModel == null) return;
            if (!_fadeSearched)
            {
                _fadeSearched = true;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _fadeField = typeof(CharacterModel).GetField("fade", flags);
                _updateMaterials = typeof(CharacterModel).GetMethod("UpdateMaterials", flags, null, Type.EmptyTypes, null);
                _log.LogInfo("Mains : transparence de RoR2 " + (_fadeField != null && _updateMaterials != null ? "neutralisee" : "non neutralisable"));
            }
            if (_fadeField == null || _updateMaterials == null) return;
            try
            {
                if (_fadeField.GetValue(characterModel) is float fade && fade < 0.999f)
                {
                    _fadeField.SetValue(characterModel, 1f);
                    _updateMaterials.Invoke(characterModel, null);
                }
            }
            catch { }
        }

        private void OnReadback(AsyncGPUReadbackRequest request, Capture capture, bool white)
        {
            if (capture.Left <= 0) return;  // abandonnée entre-temps
            int width = capture.Width, height = capture.Height;
            int size = width * height * 4;
            if (request.hasError || _view == IntPtr.Zero) { capture.Left = 0; return; }
            if (capture.Black.Length != size) { capture.Black = new byte[size]; capture.White = new byte[size]; }
            request.GetData<byte>().CopyTo(white ? capture.White : capture.Black);
            if (--capture.Left > 0) return;  // on attend la deuxième image
            if (_output.Length != size) _output = new byte[size];
            byte[] black = capture.Black, whiteImage = capture.White;

            // Noir et blanc comparés : alpha = 1 - (blanc - noir) ; couleur = noir / alpha.
            int row = width * 4;
            bool flip = _flip.Value;
            int visible = 0;
            for (int y = 0; y < height; y++)
            {
                int from = (flip ? height - 1 - y : y) * row;
                int to = y * row;
                for (int x = 0; x < row; x += 4)
                {
                    int i = from + x, o = to + x;
                    int difference = (whiteImage[i] - black[i]) + (whiteImage[i + 1] - black[i + 1]) + (whiteImage[i + 2] - black[i + 2]);
                    if (difference < 60)  // le survivant (cas le plus courant, sans calcul)
                    {
                        _output[o] = black[i]; _output[o + 1] = black[i + 1]; _output[o + 2] = black[i + 2]; _output[o + 3] = 255;
                        visible++;
                        continue;
                    }
                    int alpha = 255 - (difference > 765 ? 255 : difference / 3);
                    if (alpha < 12)  // le fond
                    {
                        _output[o] = 0; _output[o + 1] = 0; _output[o + 2] = 0; _output[o + 3] = 0;
                        continue;
                    }
                    int r = black[i] * 255 / alpha, g = black[i + 1] * 255 / alpha, b = black[i + 2] * 255 / alpha;
                    _output[o] = (byte)(r > 255 ? 255 : r);
                    _output[o + 1] = (byte)(g > 255 ? 255 : g);
                    _output[o + 2] = (byte)(b > 255 ? 255 : b);
                    _output[o + 3] = (byte)alpha;
                    visible++;
                }
            }
            WatchEmptyImage(visible, capture.Survivor);

            _sentThisSecond++;
            if (Time.unscaledTime - _fpsWindowStart >= 10f)
            {
                float span = Time.unscaledTime - _fpsWindowStart;
                _log.LogInfo($"Mains : {_sentThisSecond / span:F0} images par seconde envoyees a Skyrim (RoR2 tourne a {_gameFrames / span:F0} images par seconde)");
                _sentThisSecond = 0;
                _gameFrames = 0;
                _fpsWindowStart = Time.unscaledTime;
            }

            // On écrit dans l'image libre, puis on annonce qu'elle est prête.
            int next = 1 - _ready;
            Marshal.Copy(_output, 0, IntPtr.Add(_view, BridgeInfo.ViewHeaderSize + next * BridgeInfo.ViewFrameSize), size);
            Marshal.WriteInt32(_view, 4, width);
            Marshal.WriteInt32(_view, 8, height);
            Marshal.WriteInt32(_view, 16, next);
            Marshal.WriteInt32(_view, 12, unchecked((int)++_sequence));
            Marshal.WriteInt32(_view, 0, unchecked((int)BridgeInfo.ViewMagic));
            _ready = next;
            // Image de test : environ 3 secondes après chaque nouveau survivant (une par survivant).
            if (_testName != null && Time.unscaledTime >= _testAt)
            {
                SaveTestImage(width, height, _testName);
                _testName = null;
            }

            if (!_loggedFirst)
            {
                _loggedFirst = true;
                _log.LogInfo($"Mains : premiere image envoyee a Skyrim ({width} x {height})");
            }
        }

        // Image vide pendant 2 secondes : on note le survivant, la prochaine image décrira en détail
        // chaque morceau du modèle dans le journal (pour trouver pourquoi il reste invisible).
        private void WatchEmptyImage(int visible, string survivor)
        {
            if (visible > 200) { _emptySince = -1f; return; }
            if (_emptySince < 0f) _emptySince = Time.unscaledTime;
        }

        // Enregistre la première image en PNG (sans le fond), pour voir ce que filme vraiment la caméra.
        private string _testName;
        private float _testAt;
        private readonly HashSet<string> _testsDone = new HashSet<string>();

        private void SaveTestImage(int width, int height, string survivor)
        {
            try
            {
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimRoR2", "mains_test_" + survivor + ".png");
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                var flipped = new byte[_output.Length];
                int row = width * 4;
                for (int y = 0; y < height; y++) Buffer.BlockCopy(_output, y * row, flipped, (height - 1 - y) * row, row);
                texture.LoadRawTextureData(flipped);
                texture.Apply();
                System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
                UnityEngine.Object.Destroy(texture);
                _log.LogInfo("Mains : image de test enregistree dans " + path);
            }
            catch (Exception e)
            {
                _log.LogWarning("Mains : image de test impossible : " + e.Message);
            }
        }
    }
}
