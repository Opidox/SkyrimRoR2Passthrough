using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoR2;
using RoR2.Projectile;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // Les vrais effets de RoR2 dans Skyrim : particules (flammes, étincelles, fumées, orbes d'énergie), traînées
    // et rayons (lasers, traits), recopiés à chaque image.
    //
    // Pour chaque matière d'effet de RoR2, ModelExporter fabrique un "sprite" : un carré avec la texture et les
    // couleurs exactes de RoR2 (palette recomposée). Ici, à chaque image, on lit toutes les particules vivantes
    // (position, taille, rotation, opacité), les points des traînées et des lignes, et on envoie à Skyrim la liste
    // des sprites à poser. Skyrim les tourne vers sa caméra et les dessine avec son shader d'effet : ils sont
    // cachés par le décor, comme le reste du monde.
    //
    // On ne prend que les effets "à nous" : tirs, effets d'attaque, balles traçantes, et ce que portent le
    // survivant et ses alliés (lance-flammes, lasers, réacteurs...). Le décor de la carte RoR2 n'est jamais recopié.
    internal sealed class SpriteMirror
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        private const string FileSuffix = "_A";
        private const float Range = 90f;           // mètres autour du survivant
        private const int MaxPerSystem = 96;       // particules au plus par système
        private const int MaxTrailSegments = 16;
        private const int MaxLineSegments = 32;

        private readonly ManualLogSource _log;
        private readonly ConfigEntry<bool> _enabled;
        private IntPtr _view;
        private bool _openTried;
        private uint _sequence;
        private int _count;
        private readonly byte[] _entry = new byte[BridgeInfo.SpriteEntrySize];
        private bool _own;

        // Ce qu'on suit, et à quoi c'est rattaché (pour suivre la taille du survivant ou d'un tir dans Skyrim).
        private sealed class Source
        {
            public Component Renderer;     // ParticleSystem, TrailRenderer ou LineRenderer
            public CharacterBody Body;     // porté par un survivant ou un allié (sinon null)
            public int ShotId;             // apparence d'un tir (identifiant de sa racine), sinon 0
            public Transform ShotRoot;
            public bool AtMuzzle;          // effet né contre le survivant (flamme de tir...) : suit l'image des mains
            public Component Tracer;       // balle traçante (son départ suit l'arme vue à l'écran en première personne)
        }

        // Balles traçantes du survivant en première personne : le départ est ramené au canon vu à l'écran, puis le
        // décalage s'efface le long du trajet (la balle arrive pile sur la cible).
        private readonly Dictionary<int, (Vector3 start, Vector3 offset, float length)> _bends = new Dictionary<int, (Vector3, Vector3, float)>();
        private bool _bending;
        private (Vector3 start, Vector3 offset, float length) _bend;
        private static System.Reflection.FieldInfo _startField, _endField;
        private readonly List<Source> _sources = new List<Source>();
        private float _nextScan;

        // Les matières déjà vues : clé du sprite, numéro dans la table des noms (-1 : pas encore prêt).
        private readonly Dictionary<Material, string> _keys = new Dictionary<Material, string>();
        private readonly Dictionary<string, int> _names = new Dictionary<string, int>();
        private readonly HashSet<string> _knownBySkyrim = new HashSet<string>();  // fichiers déjà là au lancement de Skyrim
        private readonly HashSet<string> _exported = new HashSet<string>();
        private readonly HashSet<string> _failed = new HashSet<string>();
        private readonly Queue<(string key, Material material, Color tint)> _toExport = new Queue<(string, Material, Color)>();
        private readonly HashSet<string> _queued = new HashSet<string>();
        private readonly DateTime _started = System.Diagnostics.Process.GetCurrentProcess().StartTime;
        private int _exports;

        private ParticleSystem.Particle[] _particles = new ParticleSystem.Particle[256];
        private Vector3[] _points = new Vector3[256];

        // Branchés par le plugin.
        public Action<string> RegisterName;                       // ajoute un nom à la liste que Skyrim prépare (corps.txt)
        public Func<int, (Vector3 shift, float scale)?> ShotFrame; // décalage et taille d'un tir dans Skyrim
        public Func<CharacterBody, float> BodyScale;              // taille d'un survivant ou allié dans Skyrim
        public Func<Vector3, Vector3?> FirstPersonMap;            // première personne : point vu dans l'image des mains -> Skyrim
        private bool _mapped;                                     // ce que porte le survivant suit l'image des mains
        private Vector3 _framePivot, _frameShift;
        private float _frameScale = 1f;

        public SpriteMirror(ManualLogSource log, ConfigFile config)
        {
            _log = log;
            _enabled = ConfigMove.Bind(config, "Monde", "4 - Effets", "EffetsRoR2", true,
                "Copies the real RoR2 effects into Skyrim (particles, trails, lasers) with their textures and colors. A new kind of effect appears at the next Skyrim launch (time to convert it).");
            _skyrimBeams = ConfigMove.Bind(config, "Monde", "4 - Effets", "LasersSkyrim", true,
                "(Trial) RoR2 lasers are drawn with the look of Skyrim spells (lightning, fire, frost, red drain, green healing), depending on their color. false = lasers copied from RoR2, as before.");
        }

        private readonly ConfigEntry<bool> _skyrimBeams;
        private int _beamFamily = -1;
        private bool _isTracer;  // -1 : pas un laser façon Skyrim ; sinon la famille (voir BeamFamily)

        // Famille de sort de Skyrim d'après la couleur du laser : 0 foudre (blanc, bleu), 1 feu (orange, jaune),
        // 2 givre (cyan), 3 drain (rouge, violet), 4 soin (vert).
        private static int BeamFamily(Material material, Color start)
        {
            Color tint = material != null && material.HasProperty("_TintColor") ? material.GetColor("_TintColor")
                       : material != null && material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            Color c = new Color(tint.r * start.r, tint.g * start.g, tint.b * start.b);
            Color.RGBToHSV(c, out float h, out float sat, out float v);
            if (sat < 0.25f || v < 0.05f) return 0;
            float deg = h * 360f;
            if (deg < 18f || deg >= 255f) return 3;
            if (deg < 65f) return 1;
            if (deg < 160f) return 4;
            if (deg < 200f) return 2;
            return 0;
        }

        public bool Enabled => _enabled.Value;

        // ---------- Image par image ----------

        public void Update(CharacterBody survivor, TerrainMirror terrain, bool connected)
        {
            if (!Open()) return;
            _count = 0;
            if (_enabled.Value && connected && survivor != null && terrain != null)
            {
                float now = Time.unscaledTime;
                if (now >= _nextScan)
                {
                    _nextScan = now + 0.25f;
                    Scan(survivor);
                }
                Vector3 center = survivor.corePosition;
                foreach (Source source in _sources)
                {
                    if (_count >= BridgeInfo.SpriteMax) break;
                    if (source.Renderer == null) continue;
                    try
                    {
                        _own = source.Body == survivor;  // porté par le survivant : caché près de l'œil en première personne
                        _mapped = (_own || source.AtMuzzle) && FirstPersonMap != null;
                        Frame(source, survivor, out Vector3 pivot, out Vector3 shift, out float scale);
                        _framePivot = pivot; _frameShift = shift; _frameScale = scale;
                        _bending = source.Tracer != null && BendOf(source.Tracer, survivor, out _bend);
                        if (source.Renderer is ParticleSystem ps) AddParticles(ps, terrain, center, pivot, shift, scale);
                        else if (source.Renderer is TrailRenderer trail) AddTrail(trail, terrain, center, pivot, shift, scale);
                        else if (source.Renderer is LineRenderer line) { _isTracer = source.Tracer != null; AddLine(line, terrain, center, pivot, shift, scale); }
                        else if (source.Renderer is MonoBehaviour decal) AddDecal(decal, terrain, center);
                    }
                    catch (Exception) { }
                }
                ExportSome(2);
            }
            Marshal.WriteInt32(_view, 8, _count);
            Marshal.WriteInt32(_view, 12, _names.Count);
            Marshal.WriteInt32(_view, 4, unchecked((int)++_sequence));
            Marshal.WriteInt32(_view, 0, unchecked((int)BridgeInfo.SpriteMagic));
        }

        // Où ramener un effet : un tir est décalé et réduit comme son modèle ; ce que porte un survivant ou un allié
        // suit sa taille dans Skyrim (autour de ses pieds).
        private void Frame(Source source, CharacterBody survivor, out Vector3 pivot, out Vector3 shift, out float scale)
        {
            pivot = Vector3.zero;
            shift = Vector3.zero;
            scale = 1f;
            if (source.ShotId != 0 && source.ShotRoot != null && ShotFrame != null)
            {
                var frame = ShotFrame(source.ShotId);
                if (frame.HasValue)
                {
                    pivot = source.ShotRoot.position;
                    shift = frame.Value.shift;
                    scale = frame.Value.scale;
                }
            }
            else if (source.Body != null && BodyScale != null)
            {
                pivot = source.Body.footPosition;
                scale = BodyScale(source.Body);
            }
        }

        private Vector3 Place(Vector3 position, Vector3 pivot, Vector3 shift, float scale)
        {
            Vector3 placed = pivot + (position - pivot) * scale + shift;
            if (_bending) placed += _bend.offset * (1f - Mathf.Clamp01((position - _bend.start).magnitude / _bend.length));
            return placed;
        }

        private bool BendOf(Component tracer, CharacterBody survivor, out (Vector3 start, Vector3 offset, float length) bend)
        {
            int id = tracer.GetInstanceID();
            if (_bends.TryGetValue(id, out bend)) return bend.offset != Vector3.zero;
            if (_bends.Count > 500) _bends.Clear();
            bend = (Vector3.zero, Vector3.zero, 1f);
            try
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                if (_startField == null)
                {
                    _startField = tracer.GetType().GetField("startPos", flags);
                    _endField = tracer.GetType().GetField("endPos", flags);
                }
                if (FirstPersonMap != null && _startField?.GetValue(tracer) is Vector3 start && _endField?.GetValue(tracer) is Vector3 end &&
                    (start - survivor.corePosition).sqrMagnitude < 3f * 3f)
                {
                    Vector3? seen = FirstPersonMap(start);
                    if (seen.HasValue && Finite(seen.Value))
                        bend = (start, seen.Value - start, Mathf.Max(1f, (end - start).magnitude));
                }
            }
            catch (Exception) { }
            _bends[id] = bend;
            return bend.offset != Vector3.zero;
        }

        // ---------- Ce qu'on suit ----------

        private void Scan(CharacterBody survivor)
        {
            _sources.Clear();
            var seen = new HashSet<int>();
            void Add(GameObject holder, CharacterBody body, Transform shotRoot)
            {
                if (holder == null || !holder.activeInHierarchy) return;
                foreach (ParticleSystem ps in holder.GetComponentsInChildren<ParticleSystem>(false))
                    if (seen.Add(ps.GetInstanceID())) _sources.Add(new Source { Renderer = ps, Body = body, ShotRoot = shotRoot, ShotId = shotRoot != null ? shotRoot.GetInstanceID() : 0 });
                foreach (TrailRenderer trail in holder.GetComponentsInChildren<TrailRenderer>(false))
                    if (seen.Add(trail.GetInstanceID())) _sources.Add(new Source { Renderer = trail, Body = body, ShotRoot = shotRoot, ShotId = shotRoot != null ? shotRoot.GetInstanceID() : 0 });
                foreach (LineRenderer line in holder.GetComponentsInChildren<LineRenderer>(false))
                    if (seen.Add(line.GetInstanceID())) _sources.Add(new Source { Renderer = line, Body = body, ShotRoot = shotRoot, ShotId = shotRoot != null ? shotRoot.GetInstanceID() : 0 });
                // Les "décalques" projetés au sol (flaques d'Acrid, zones...) : un carré à plat, à leur taille.
                foreach (MonoBehaviour behaviour in holder.GetComponentsInChildren<MonoBehaviour>(false))
                    if (behaviour != null && behaviour.GetType().Name == "Decal" && seen.Add(behaviour.GetInstanceID()))
                        _sources.Add(new Source { Renderer = behaviour });
            }
            Vector3 center = survivor.corePosition;
            bool Near(Vector3 p) => (p - center).sqrMagnitude < Range * Range;

            foreach (ProjectileController projectile in Tracked.Projectiles)
            {
                if (!Tracked.Alive(projectile) || !Near(projectile.transform.position)) continue;
                Transform root = projectile.ghost != null ? projectile.ghost.transform : projectile.transform;
                Add(root.gameObject, null, root);
                if (projectile.ghost != null) Add(projectile.gameObject, null, root);  // zones (flaques...) portées par le projectile
            }
            foreach (Tracer tracer in Tracked.Tracers)
                if (Tracked.Alive(tracer) && Near(tracer.transform.position))
                {
                    int before = _sources.Count;
                    Add(tracer.gameObject, null, null);
                    for (int i = before; i < _sources.Count; i++) _sources[i].Tracer = tracer;
                }
            foreach (EffectComponent effect in Tracked.Effects)
                if (Tracked.Alive(effect) && Near(effect.transform.position))
                {
                    int before = _sources.Count;
                    Add(effect.gameObject, null, null);
                    bool atMuzzle = (effect.transform.position - center).sqrMagnitude < 2.5f * 2.5f;
                    for (int i = before; i < _sources.Count; i++) _sources[i].AtMuzzle = atMuzzle;
                }
            foreach (CharacterBody body in CharacterBody.readOnlyInstancesList)
            {
                if (body == null || body.teamComponent == null || body.teamComponent.teamIndex != TeamIndex.Player) continue;
                if (!Near(body.corePosition)) continue;
                Transform model = body.modelLocator != null ? body.modelLocator.modelTransform : null;
                if (model != null) Add(model.gameObject, body, null);
                Add(body.gameObject, body, null);
            }
        }

        // ---------- Particules ----------

        private void AddParticles(ParticleSystem ps, TerrainMirror terrain, Vector3 center, Vector3 pivot, Vector3 shift, float scale)
        {
            if (ps.particleCount == 0) return;
            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (renderer == null || !renderer.enabled || renderer.renderMode == ParticleSystemRenderMode.Mesh || renderer.renderMode == ParticleSystemRenderMode.None) return;
            Material material = renderer.sharedMaterial;
            if (material == null || ModelExporter.IsDistortion(material)) return;
            ParticleSystem.MainModule main = ps.main;
            int name = NameOf(material, Tint(main.startColor));
            if (name < 0) return;

            if (_particles.Length < ps.particleCount) _particles = new ParticleSystem.Particle[Mathf.Min(ps.particleCount, 2048)];
            int n = ps.GetParticles(_particles);
            Transform space = main.simulationSpace == ParticleSystemSimulationSpace.Local ? ps.transform
                : main.simulationSpace == ParticleSystemSimulationSpace.Custom ? main.customSimulationSpace : null;
            float sizeScale = main.scalingMode == ParticleSystemScalingMode.Shape ? 1f : Mathf.Abs(ps.transform.lossyScale.x);
            int step = n > MaxPerSystem ? Mathf.CeilToInt(n / (float)MaxPerSystem) : 1;
            byte kind = renderer.renderMode == ParticleSystemRenderMode.Stretch ? (byte)1
                : renderer.renderMode == ParticleSystemRenderMode.HorizontalBillboard ? (byte)2
                : renderer.renderMode == ParticleSystemRenderMode.VerticalBillboard ? (byte)3 : (byte)0;
            // 0.73.4 : un cercle "posé dans le monde" (anneaux des frappes du Capitaine) n'est pas tourné vers la
            // caméra : il est à plat, comme dans RoR2 (il sortait debout, en grandes arches).
            if (kind == 0 && (renderer.alignment == ParticleSystemRenderSpace.World || renderer.alignment == ParticleSystemRenderSpace.Local)) kind = 2;
            for (int i = 0; i < n && _count < BridgeInfo.SpriteMax; i += step)
            {
                ParticleSystem.Particle p = _particles[i];
                int level = Level(p.GetCurrentColor(ps).a / 255f);
                if (level < 0) continue;
                Vector3 world = space != null ? space.TransformPoint(p.position) : p.position;
                if ((world - center).sqrMagnitude > Range * Range) continue;
                Vector3 size = p.GetCurrentSize3D(ps) * sizeScale * scale;
                float width = size.x, height = size.y;
                if (width < 0.005f && height < 0.005f) continue;
                // 0.73.4 : pas de particule géante face à la caméra (grandes plaques pâles du laser du Démon du vide et du
                // Fils illégitime). Les cercles à plat (zones au sol) gardent leur taille.
                if (kind != 2) { width = Mathf.Min(width, 3f); height = Mathf.Min(height, 3f); }
                Vector3 axis = Vector3.up;
                byte shape = kind;
                if (kind == 1)
                {
                    Vector3 velocity = p.totalVelocity;
                    if (space != null) velocity = space.TransformVector(velocity);
                    float speed = velocity.magnitude;
                    if (speed < 0.01f) shape = 0;
                    else
                    {
                        axis = velocity / speed;
                        height = size.x * renderer.lengthScale + speed * renderer.velocityScale * scale;
                    }
                }
                float roll = (main.startRotation3D ? p.rotation3D.z : p.rotation) * Mathf.Deg2Rad;
                Write(name, shape, level, Place(world, pivot, shift, scale), width, height, -roll, axis, terrain);
            }
        }

        // ---------- Traînées et lignes (rayons, lasers) ----------

        private void AddTrail(TrailRenderer trail, TerrainMirror terrain, Vector3 center, Vector3 pivot, Vector3 shift, float scale)
        {
            if (!trail.enabled || trail.positionCount < 1) return;
            Material material = trail.sharedMaterial;
            if (material == null || ModelExporter.IsDistortion(material)) return;
            int name = NameOf(material, trail.startColor);
            if (name < 0) return;
            int n = Mathf.Min(trail.positionCount, _points.Length - 1);
            trail.GetPositions(_points);
            _points[n] = trail.transform.position;  // la tête de la traînée
            n++;
            int step = Mathf.Max(1, Mathf.CeilToInt((n - 1) / (float)MaxTrailSegments));
            for (int i = 0; i + 1 < n && _count < BridgeInfo.SpriteMax; i += step)
            {
                int j = Mathf.Min(i + step, n - 1);
                float u = 1f - (i + j) * 0.5f / Mathf.Max(1, n - 1);  // 0 = tête (récente), 1 = queue
                float width = trail.widthMultiplier * trail.widthCurve.Evaluate(u) * scale;
                float alpha = trail.colorGradient.Evaluate(u).a;
                Segment(name, _points[i], _points[j], width, alpha, terrain, center, pivot, shift, scale);
            }
        }

        private void AddLine(LineRenderer line, TerrainMirror terrain, Vector3 center, Vector3 pivot, Vector3 shift, float scale)
        {
            if (!line.enabled || line.positionCount < 2) return;
            Material material = line.sharedMaterial;
            if (material == null || ModelExporter.IsDistortion(material)) return;
            int name = NameOf(material, line.startColor);
            if (name < 0) return;
            // 0.73.9 : seulement les vrais lasers, pas les balles traçantes (tirs du Commando...) : un crash de Skyrim a suivi
            // le premier tir du Commando avec l'apparence de sort.
            _beamFamily = _skyrimBeams.Value && !_isTracer ? BeamFamily(material, line.startColor) : -1;
            int n = Mathf.Min(line.positionCount, _points.Length);
            line.GetPositions(_points);
            if (!line.useWorldSpace)
                for (int i = 0; i < n; i++) _points[i] = line.transform.TransformPoint(_points[i]);
            int step = Mathf.Max(1, Mathf.CeilToInt((n - 1) / (float)MaxLineSegments));
            for (int i = 0; i + 1 < n && _count < BridgeInfo.SpriteMax; i += step)
            {
                int j = Mathf.Min(i + step, n - 1);
                float u = (i + j) * 0.5f / Mathf.Max(1, n - 1);
                float width = line.widthMultiplier * line.widthCurve.Evaluate(u) * scale;
                float alpha = line.colorGradient.Evaluate(u).a;
                Segment(name, _points[i], _points[j], width, alpha, terrain, center, pivot, shift, scale);
            }
            _beamFamily = -1;
        }

        private void Segment(int name, Vector3 a, Vector3 b, float width, float alpha, TerrainMirror terrain, Vector3 center, Vector3 pivot, Vector3 shift, float scale)
        {
            int level = Level(alpha);
            if (level < 0 || width < 0.005f) return;
            a = Place(a, pivot, shift, scale);
            b = Place(b, pivot, shift, scale);
            Vector3 axis = b - a;
            float length = axis.magnitude;
            if (length < 0.01f) return;
            // 0.73 : un rayon long (laser, frappe venue du ciel...) est coupé en morceaux de 6 m au plus, et seule la
            // partie à portée du survivant est envoyée : avant, son milieu tombait hors de portée et rien ne s'affichait.
            Vector3 direction = axis / length;
            float closest = Mathf.Clamp(Vector3.Dot(center - a, direction), 0f, length);
            float t0 = Mathf.Max(0f, closest - Range), t1 = Mathf.Min(length, closest + Range);
            if (t1 - t0 < 0.01f) return;
            int pieces = Mathf.Clamp(Mathf.CeilToInt((t1 - t0) / 6f), 1, 40);
            float pieceLength = (t1 - t0) / pieces;
            // 0.73.3 : les gros rayons (laser du Fils illégitime, rayon rouge du Démon du vide) étaient énormes et, au départ
            // du survivant, couvraient tout l'écran d'un flash blanc. Largeur limitée, et rien dans les 3 premiers mètres
            // d'un rayon porté par le survivant.
            width = Mathf.Min(width, 0.5f);
            for (int i = 0; i < pieces && _count < BridgeInfo.SpriteMax; i++)
            {
                Vector3 middle = a + direction * (t0 + (i + 0.5f) * pieceLength);
                if ((middle - center).sqrMagnitude > Range * Range) continue;
                if (_own && (middle - center).sqrMagnitude < 3f * 3f) continue;
                // 5 : laser façon sort de Skyrim (Skyrim prend l'apparence d'un de ses sorts), sinon 4 : texture dans la longueur
                Write(name, _beamFamily >= 0 ? (byte)5 : (byte)4, _beamFamily >= 0 ? 0 : level, middle,
                      _beamFamily >= 0 ? Mathf.Max(width, 0.15f) : width, pieceLength, 0f, direction, terrain);
            }
        }

        // ---------- Décalques au sol (ThreeEyedGames.Decal : une boîte qui projette sa matière sur le sol) ----------

        private static System.Reflection.FieldInfo _decalMaterial;

        private void AddDecal(MonoBehaviour decal, TerrainMirror terrain, Vector3 center)
        {
            if (!decal.enabled || !decal.gameObject.activeInHierarchy) return;
            if (_decalMaterial == null || _decalMaterial.DeclaringType != decal.GetType())
                _decalMaterial = decal.GetType().GetField("Material", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Material material = _decalMaterial?.GetValue(decal) as Material;
            if (material == null || ModelExporter.IsDistortion(material)) return;
            Color tint = material.HasProperty("_TintColor") ? material.GetColor("_TintColor") : material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            int name = NameOf(material, tint);
            if (name < 0) return;
            Transform t = decal.transform;
            Vector3 size = t.lossyScale;
            float width = Mathf.Abs(size.x), depth = Mathf.Abs(size.z);
            if (width < 0.05f || depth < 0.05f || width > 60f || depth > 60f) return;
            if ((t.position - center).sqrMagnitude > Range * Range) return;
            Vector3 right = t.right;
            float roll = Mathf.Atan2(right.z, right.x);  // Unity (x, z) -> Skyrim (x, y)
            _own = false; _mapped = false; _bending = false;
            Write(name, 2, 1, t.position + Vector3.up * 0.05f, width, depth, roll, Vector3.up, terrain);
        }

        // Opacité -> niveau du sprite (0 = opaque ... 3 = presque effacé ; -1 = invisible).
        private static int Level(float alpha)
        {
            if (alpha > 0.8f) return 0;
            if (alpha > 0.5f) return 1;
            if (alpha > 0.26f) return 2;
            if (alpha > 0.06f) return 3;
            return -1;
        }

        private static Color Tint(ParticleSystem.MinMaxGradient gradient)
        {
            switch (gradient.mode)
            {
                case ParticleSystemGradientMode.Color: return gradient.color;
                case ParticleSystemGradientMode.TwoColors: return Color.Lerp(gradient.colorMin, gradient.colorMax, 0.5f);
                case ParticleSystemGradientMode.Gradient: return gradient.gradient != null ? gradient.gradient.Evaluate(0.5f) : Color.white;
                case ParticleSystemGradientMode.TwoGradients: return gradient.gradientMax != null ? gradient.gradientMax.Evaluate(0.5f) : Color.white;
                default: return Color.white;
            }
        }

        private void Write(int name, byte shape, int level, Vector3 ror2, float width, float height, float roll, Vector3 axis, TerrainMirror terrain)
        {
            if (!Finite(ror2) || !Finite(axis) || !Finite(width) || !Finite(height) || !Finite(roll)) return;
            bool own = _own;
            if (_mapped)
            {
                // Première personne : là où on le voit dans l'image des mains (au canon), et pas caché.
                // Position réelle dans RoR2 (avant la mise à la taille de Skyrim), puis sa place à l'écran.
                float k = _frameScale > 0.01f ? _frameScale : 1f;
                Vector3 real = _framePivot + (ror2 - _framePivot - _frameShift) / k;
                Vector3? seen = FirstPersonMap(real);
                if (seen.HasValue && Finite(seen.Value))
                {
                    ror2 = seen.Value; own = false;
                    width /= k; height /= k;  // à l'écran, il garde sa taille vue dans l'image des mains
                    // 0.73.4 : collé à l'arme en première personne, un effet ne doit pas boucher l'écran (flashs blancs).
                    width = Mathf.Min(width, 0.8f); height = Mathf.Min(height, shape >= 4 ? 6f : 0.8f);
                }
            }
            if (!terrain.TryMapToSkyrim(ror2, out Vector3 skyrim) || !Finite(skyrim)) return;
            Array.Clear(_entry, 0, _entry.Length);
            BitConverter.GetBytes(name).CopyTo(_entry, 0);
            _entry[4] = shape;
            _entry[5] = (byte)level;
            _entry[6] = own ? (byte)1 : (byte)0;
            _entry[7] = shape == 5 ? (byte)Mathf.Clamp(_beamFamily, 0, 4) : (byte)0;  // 0.73.5 : famille du sort de Skyrim
            Put(8, skyrim.x); Put(12, skyrim.y); Put(16, skyrim.z);
            Put(20, Mathf.Min(width, 60f)); Put(24, Mathf.Min(height, 200f));
            Put(28, roll);
            Put(32, axis.x); Put(36, axis.z); Put(40, axis.y);  // Unity (x, y, z) -> Skyrim (x, z, y)
            Marshal.Copy(_entry, 0, IntPtr.Add(_view, BridgeInfo.SpriteEntriesOffset + _count * BridgeInfo.SpriteEntrySize), _entry.Length);
            _count++;
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        private void Put(int offset, float value) => BitConverter.GetBytes(value).CopyTo(_entry, offset);

        // ---------- Les sprites (une matière de RoR2 -> un sprite pour Skyrim) ----------

        // Le numéro du sprite de cette matière dans la table des noms, ou -1 s'il n'est pas encore prêt
        // (il est alors converti en arrière-plan).
        private int NameOf(Material material, Color tint)
        {
            if (!_keys.TryGetValue(material, out string key))
            {
                key = ModelExporter.SpriteKey(material, new Color(tint.r, tint.g, tint.b, 1f));
                _keys[material] = key;
            }
            if (_names.TryGetValue(key, out int index)) return index;
            if (_failed.Contains(key)) return -1;
            if (!_exported.Contains(key))
            {
                if (ModelExporter.SpriteExported(key, FileSuffix)) Known(key);
                else
                {
                    Queue(key, material, new Color(tint.r, tint.g, tint.b, 1f));
                    return -1;
                }
            }
            return AddName(key);
        }

        private void Known(string key)
        {
            _exported.Add(key);
            if (ModelExporter.SpriteTime(key, FileSuffix) < _started) _knownBySkyrim.Add(key);
            for (int level = 0; level < ModelExporter.SpriteAlpha.Length; level++) RegisterName?.Invoke(ModelExporter.SpriteFile(key, level));
        }

        private int AddName(string key)
        {
            if (_names.Count >= BridgeInfo.SpriteNames) return -1;
            int index = _names.Count;
            _names[key] = index;
            byte[] text = Encoding.ASCII.GetBytes(key);
            var slot = new byte[BridgeInfo.SpriteNameSize];
            Array.Copy(text, slot, Math.Min(text.Length, BridgeInfo.SpriteNameSize - 1));
            Marshal.Copy(slot, 0, IntPtr.Add(_view, BridgeInfo.SpriteNamesOffset + index * BridgeInfo.SpriteNameSize), slot.Length);
            return index;
        }

        private void Queue(string key, Material material, Color tint)
        {
            if (_queued.Add(key)) _toExport.Enqueue((key, material, tint));
        }

        private void ExportSome(int budget)
        {
            while (budget-- > 0 && _toExport.Count > 0)
            {
                (string key, Material material, Color tint) = _toExport.Dequeue();
                if (material == null) continue;
                if (ModelExporter.ExportSprite(key, FileSuffix, material, tint, _log))
                {
                    Known(key);
                    if (++_exports <= 5 || _exports % 50 == 0)
                        _log.LogInfo($"Sprites : {_exports} effets de RoR2 convertis (dernier : {material.name}) ; visibles au prochain lancement de Skyrim");
                }
                else _failed.Add(key);
            }
        }

        // Un objet dont tous les effets (particules, traînées, lignes) sont déjà connus de Skyrim : inutile de
        // le remplacer par un effet de Skyrim ou un trait lumineux.
        public bool Covers(GameObject holder)
        {
            if (!_enabled.Value || holder == null) return false;
            bool any = false;
            foreach (Renderer renderer in holder.GetComponentsInChildren<Renderer>(false))
            {
                if (renderer is ParticleSystemRenderer psr && (psr.renderMode == ParticleSystemRenderMode.Mesh || psr.renderMode == ParticleSystemRenderMode.None)) continue;
                if (!(renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer)) continue;
                Material material = renderer.sharedMaterial;
                if (material == null || ModelExporter.IsDistortion(material)) continue;
                if (!_keys.TryGetValue(material, out string key) || !_knownBySkyrim.Contains(key)) return false;
                any = true;
            }
            return any;
        }

        // ---------- Pré-conversion (appelée pour chaque apparence de tir, et pour les effets du jeu) ----------

        // Prépare d'avance les sprites des effets d'un objet (sans le créer dans la scène).
        public void PrepareFrom(GameObject prefab)
        {
            if (prefab == null) return;
            try
            {
                foreach (ParticleSystem ps in prefab.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
                    if (renderer == null || renderer.renderMode == ParticleSystemRenderMode.Mesh || renderer.renderMode == ParticleSystemRenderMode.None) continue;
                    Prepare(renderer.sharedMaterial, Tint(ps.main.startColor));
                }
                foreach (TrailRenderer trail in prefab.GetComponentsInChildren<TrailRenderer>(true)) Prepare(trail.sharedMaterial, trail.startColor);
                foreach (LineRenderer line in prefab.GetComponentsInChildren<LineRenderer>(true)) Prepare(line.sharedMaterial, line.startColor);
                foreach (MonoBehaviour behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour.GetType().Name != "Decal") continue;
                    var field = behaviour.GetType().GetField("Material", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (!(field?.GetValue(behaviour) is Material material)) continue;
                    Color tint = material.HasProperty("_TintColor") ? material.GetColor("_TintColor") : material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
                    Prepare(material, tint);
                }
            }
            catch (Exception) { }
        }

        private void Prepare(Material material, Color tint)
        {
            if (material == null || ModelExporter.IsDistortion(material)) return;
            string key = ModelExporter.SpriteKey(material, new Color(tint.r, tint.g, tint.b, 1f));
            if (!_keys.ContainsKey(material)) _keys[material] = key;
            if (_exported.Contains(key) || _failed.Contains(key)) return;
            if (ModelExporter.SpriteExported(key, FileSuffix)) Known(key);
            else Queue(key, material, new Color(tint.r, tint.g, tint.b, 1f));
        }

        private bool _effectsListed;
        private List<GameObject> _effectPrefabs;
        private int _effectIndex;

        // Les effets du catalogue de RoR2 (explosions, impacts, soins...), quelques-uns à chaque appel.
        public bool PrepareEffectCatalog(int count)
        {
            if (!_effectsListed)
            {
                _effectsListed = true;
                _effectPrefabs = new List<GameObject>();
                try
                {
                    Type catalog = typeof(EffectComponent).Assembly.GetType("RoR2.EffectCatalog");
                    const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    object total = catalog?.GetProperty("effectCount", flags)?.GetValue(null) ?? catalog?.GetField("effectCount", flags)?.GetValue(null);
                    MethodInfo get = catalog?.GetMethod("GetEffectDef", flags);
                    if (total is int n && get != null)
                    {
                        Type indexType = get.GetParameters()[0].ParameterType;
                        for (int i = 0; i < n; i++)
                        {
                            object def = get.Invoke(null, new[] { Enum.ToObject(indexType, i) });
                            if (def == null) continue;
                            object prefab = def.GetType().GetField("prefab")?.GetValue(def) ?? def.GetType().GetProperty("prefab")?.GetValue(def);
                            if (prefab is GameObject go) _effectPrefabs.Add(go);
                        }
                    }
                    _log.LogInfo($"Sprites : {_effectPrefabs.Count} effets du jeu a preparer");
                }
                catch (Exception e)
                {
                    _log.LogWarning("Sprites : catalogue des effets illisible (" + e.Message + ")");
                }
            }
            if (_effectPrefabs == null || _toExport.Count > 8) return _effectPrefabs == null || _effectIndex >= _effectPrefabs.Count;
            for (int i = 0; i < count && _effectIndex < _effectPrefabs.Count; i++) PrepareFrom(_effectPrefabs[_effectIndex++]);
            return _effectIndex >= _effectPrefabs.Count && _toExport.Count == 0;
        }

        // Pendant la pré-conversion, on avance aussi les sprites en attente (plus vite qu'en jeu).
        public void ExportQueued(int budget) => ExportSome(budget);

        private bool Open()
        {
            if (_view != IntPtr.Zero) return true;
            if (_openTried) return false;
            _openTried = true;
            IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.SpriteMapSize, BridgeInfo.SpriteMapName);
            if (mapping != IntPtr.Zero) _view = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.SpriteMapSize);
            if (_view == IntPtr.Zero) _log.LogWarning("Sprites : pont impossible a ouvrir");
            return _view != IntPtr.Zero;
        }
    }
}
