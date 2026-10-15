using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;
using UnityEngine.Rendering;

namespace SkyrimRoR2
{
    // 0.65 : la vue à la 3e personne FILMÉE. Quand Skyrim est à la 3e personne, on filme le survivant de RoR2
    // (son vrai modèle, son skin, ses animations, ses effets) exactement depuis la caméra de Skyrim : même
    // position dans le décor recopié, même direction, même champ de vision. Fond transparent (filmé sur noir
    // puis sur blanc), éclairé comme Skyrim (soleil, lune, intérieur). Skyrim affiche l'image par-dessus son
    // monde et cache son propre personnage.
    //
    // Pour rester léger en 1080p, on ne filme que le rectangle de l'écran où se trouve le survivant (la caméra
    // est "recadrée" dessus) : Skyrim le pose au bon endroit.
    internal sealed class SurvivorStreamer
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        private const float NearClip = 0.05f;
        private const float FarClip = 60f;
        private const float Margin = 0.03f;  // marge autour du survivant (part de l'écran)
        private static readonly int FadeId = Shader.PropertyToID("_Fade");
        private const string Section = "6 - Troisieme personne";

        private readonly ManualLogSource _log;
        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<int> _height;
        private readonly ConfigEntry<int> _fps;
        private readonly ConfigEntry<int> _antialiasing;
        private readonly ConfigEntry<float> _saturation;
        private readonly ConfigEntry<float> _exposure;
        private readonly ConfigEntry<bool> _flip;
        private readonly ConfigEntry<bool> _hideBehindDecor;
        private readonly ConfigEntry<float> _decorMargin;

        // 0.66 : le relief et le décor recopiés de Skyrim (branché par le plugin). Dessinés APRÈS le survivant, de la
        // couleur du fond (noir sur la prise noire, blanc sur la blanche) : là où un mur ou un rocher passe devant
        // lui, l'image devient transparente, et le vrai mur de Skyrim se voit à travers.
        public TerrainMirror Terrain;
        private CommandBuffer _occlusion;
        private bool _occlusionAttached;
        private Material _occluder;
        private bool _occluderTried;
        private readonly Plane[] _planes = new Plane[6];
        private bool _occlusionLogged;

        private IntPtr _view;
        private bool _openTried;
        private Camera _camera;
        private Light _sun;
        private RenderTexture _black, _white;          // filmées avec lissage (MSAA)
        private RenderTexture _blackOut, _whiteOut;    // les mêmes, "résolues" : ce qu'on relit
        private int _layer = -1;
        private float _nextFrame;
        private int _ready;
        private uint _sequence;
        private bool _wasActive;
        private bool _loggedFirst;
        private int _errors;

        private readonly List<Renderer> _renderers = new List<Renderer>();
        private readonly List<Renderer> _scratch = new List<Renderer>();
        private readonly List<int> _savedLayers = new List<int>();
        private readonly List<bool> _savedForceOff = new List<bool>();
        private readonly List<MaterialPropertyBlock> _savedBlocks = new List<MaterialPropertyBlock>();
        private MaterialPropertyBlock _opaque;
        private byte[] _output = new byte[0];

        private sealed class Capture
        {
            public byte[] Black = new byte[0], White = new byte[0];
            public int Left, Width, Height;
            public Rect Screen;  // place à l'écran (0 à 1, haut = 0)
            public float Started;
        }
        private readonly List<Capture> _captures = new List<Capture>();

        public bool Active { get; private set; }

        private readonly string _mapName;  // 0.70 : le pont de cette image (notre survivant, ou un coéquipier)
        private readonly string _who;

        public SurvivorStreamer(ManualLogSource log, ConfigFile config, string mapName = null, string who = "survivant")
        {
            _log = log;
            _mapName = mapName ?? BridgeInfo.SurvivorViewMapName;
            _who = who;
            _enabled = config.Bind(Section, "Activer", true,
                "A la 3e personne dans Skyrim, filme le survivant de RoR2 (avec son skin) depuis la camera de Skyrim et l'affiche a la place du personnage Skyrim.");
            _height = config.Bind(Section, "Hauteur", 1080,
                "Hauteur de l'ecran filme, en pixels (1080 = net en 1080p ; 720 = plus leger). Seul le rectangle du survivant est envoye.");
            _fps = config.Bind(Section, "ImagesParSeconde", 60, "Images par seconde envoyees a Skyrim (120 maximum).");
            _antialiasing = config.Bind(Section, "Lissage", 4, "Lissage des bords (1 = aucun, 2, 4 ou 8).");
            _saturation = config.Bind(Section, "Saturation", 0.85f, "Couleurs : 1 = couleurs vives de RoR2, plus bas = plus ternes, comme Skyrim.");
            _exposure = config.Bind(Section, "Luminosite", 1.0f, "Luminosite finale du survivant (1 = telle quelle).");
            _flip = config.Bind(Section, "RetournerImage", true, "Retourne l'image verticalement (a changer si le survivant apparait a l'envers).");
            _hideBehindDecor = config.Bind(Section, "CacheParDecor", true,
                "Le survivant est cache par les murs, rochers et objets de Skyrim places devant lui (d'apres le decor recopie).");
            _decorMargin = config.Bind(Section, "MargeDecor", 0.25f,
                "Marge (m) : un mur doit etre au moins a cette distance devant le survivant pour le cacher (evite de couper les pieds dans le sol).");
            _maxHeight = config.Bind(Section, "TailleMax", 2.0f,
                "Hauteur maximum du survivant a l'ecran (metres). Un plus grand (MUL-T, REX, Acrid) est ramene a cette taille. 10 = taille de RoR2.");
        }

        // camera : position de la caméra de Skyrim dans RoR2 ; forward : sa direction ; frustum : bords de l'image
        // à 1 m devant elle (gauche, droite, haut, bas). input : lumière de Skyrim.
        // force (0.70, coop) : un coéquipier, filmé même quand Skyrim est en première personne.
        public void Update(CharacterBody survivor, bool connected, bool cameraKnown, Vector3 camera, Vector3 forward, Vector4 frustum, SkyrimInput input,
            bool force = false)
        {
            Active = _enabled.Value && connected && (force || input.ThirdPerson == 1) && survivor != null;
            if (!Active || !cameraKnown || frustum.y <= frustum.x || frustum.z <= frustum.w)
            {
                SetActive(false);
                return;
            }
            float now = Time.unscaledTime;
            if (now < _nextFrame) return;
            _nextFrame = now + 1f / Mathf.Clamp(_fps.Value, 10, 120);
            if (!Open()) return;

            try
            {
                Gather(survivor);
                if (_renderers.Count == 0) { SetActive(false); return; }
                // 0.73.1 : un grand survivant (MUL-T, REX, Acrid...) est ramené à une taille humaine. On ne peut pas
                // rétrécir son modèle sans toucher à RoR2 : on recule plutôt la caméra autour de ses pieds, ce qui
                // revient au même à l'écran. Le décor qui le cache est agrandi d'autant (voir DrawOccluders).
                _shrink = ShrinkOf(survivor);
                _feet = survivor.footPosition;
                if (_shrink < 0.999f) camera = _feet + (camera - _feet) / _shrink;
                PrepareCamera();
                _camera.transform.SetPositionAndRotation(camera, Quaternion.LookRotation(forward, Vector3.up));
                // Projection de tout l'écran de Skyrim, pour trouver où est le survivant.
                _camera.projectionMatrix = Matrix4x4.Frustum(frustum.x * NearClip, frustum.y * NearClip, frustum.w * NearClip, frustum.z * NearClip, NearClip, FarClip);
                if (!ScreenRect(out Rect area)) { SetActive(false); return; }

                // Taille de l'image : la part de l'écran couverte, à la netteté voulue.
                float aspect = (frustum.y - frustum.x) / (frustum.z - frustum.w);
                int fullHeight = Mathf.Clamp(_height.Value, 240, BridgeInfo.SurvivorMaxHeight);
                int fullWidth = Mathf.Clamp(Mathf.RoundToInt(fullHeight * aspect), 64, BridgeInfo.SurvivorMaxWidth);
                int width = Mathf.Clamp(Mathf.CeilToInt(area.width * fullWidth), 8, BridgeInfo.SurvivorMaxWidth);
                int height = Mathf.Clamp(Mathf.CeilToInt(area.height * fullHeight), 8, BridgeInfo.SurvivorMaxHeight);
                Textures(width, height);

                // La caméra recadrée sur ce rectangle (même point de vue, champ réduit).
                float left = frustum.x + (frustum.y - frustum.x) * area.xMin;
                float right = frustum.x + (frustum.y - frustum.x) * area.xMax;
                float bottom = frustum.w + (frustum.z - frustum.w) * area.yMin;
                float top = frustum.w + (frustum.z - frustum.w) * area.yMax;
                _camera.projectionMatrix = Matrix4x4.Frustum(left * NearClip, right * NearClip, bottom * NearClip, top * NearClip, NearClip, FarClip);
                ApplyLight(input);
                Render(new Rect(area.xMin, 1f - area.yMax, area.width, area.height), input, now);
            }
            catch (Exception e)
            {
                if (++_errors <= 5) _log.LogWarning("3e personne : image du survivant impossible (" + e.Message + ")");
                _nextFrame = now + 2f;
            }
        }

        private bool Open()
        {
            if (_view != IntPtr.Zero) return true;
            if (_openTried) return false;
            _openTried = true;
            IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.SurvivorMapSize, _mapName);
            if (mapping != IntPtr.Zero) _view = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.SurvivorMapSize);
            if (_view == IntPtr.Zero) _log.LogWarning("3e personne : pont des images (" + _who + ") impossible a ouvrir");
            else _log.LogInfo("3e personne : pont des images (" + _who + ") ouvert");
            return _view != IntPtr.Zero;
        }

        private void SetActive(bool active)
        {
            if (_view == IntPtr.Zero || _wasActive == active) return;
            _wasActive = active;
            Marshal.WriteInt32(_view, 20, active ? 1 : 0);
        }

        private float _shrink = 1f;
        private Vector3 _feet;
        private Matrix4x4 _decorShrink = Matrix4x4.identity;
        private readonly Dictionary<string, float> _heights = new Dictionary<string, float>();
        private ConfigEntry<float> _maxHeight;

        // La taille à l'écran : 1 pour un survivant de taille humaine, moins pour un grand (hauteur mesurée une fois).
        private float ShrinkOf(CharacterBody survivor)
        {
            if (_maxHeight == null) return 1f;
            string key = survivor.name;
            if (!_heights.TryGetValue(key, out float height))
            {
                bool any = false;
                Bounds all = default;
                foreach (Renderer r in _renderers)
                {
                    if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                    if (r.bounds.size.sqrMagnitude > 20f * 20f) continue;
                    if (!any) { all = r.bounds; any = true; } else all.Encapsulate(r.bounds);
                }
                if (!any) return 1f;
                height = all.size.y;
                _heights[key] = height;
                _log.LogInfo($"3e personne : {key.Replace("(Clone)", "")} mesure {height:F2} m" +
                             (height > _maxHeight.Value ? $" -> affiche a {_maxHeight.Value:F2} m" : ""));
            }
            float max = Mathf.Clamp(_maxHeight.Value, 1f, 10f);
            return height > max ? Mathf.Clamp(max / height, 0.25f, 1f) : 1f;
        }

        // Tout ce qui dessine le survivant : corps, arme, effets accrochés à son modèle.
        private void Gather(CharacterBody survivor)
        {
            _renderers.Clear();
            Transform model = survivor.modelLocator != null ? survivor.modelLocator.modelTransform : null;
            if (model == null) return;
            _scratch.Clear();
            model.GetComponentsInChildren(false, _scratch);
            foreach (Renderer r in _scratch)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                if (r.gameObject.name.StartsWith("SkyrimRoR2_")) continue;
                _renderers.Add(r);
            }
        }

        // Le rectangle de l'écran (0 à 1, bas = 0) qui contient le survivant, avec une marge.
        private bool ScreenRect(out Rect area)
        {
            float xMin = 1f, yMin = 1f, xMax = 0f, yMax = 0f;
            bool any = false;
            Vector3 eye = _camera.transform.position;
            Vector3 look = _camera.transform.forward;
            foreach (Renderer r in _renderers)
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;  // peuvent être immenses
                Bounds b = r.bounds;
                if (b.size.sqrMagnitude > 40f * 40f) continue;
                Vector3 c = b.center, e = b.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z);
                    if (Vector3.Dot(p - eye, look) < NearClip)
                    {
                        // Un coin derrière la caméra : le survivant la touche presque, on prend tout l'écran.
                        xMin = 0f; yMin = 0f; xMax = 1f; yMax = 1f; any = true;
                        continue;
                    }
                    Vector3 v = _camera.WorldToViewportPoint(p);
                    xMin = Mathf.Min(xMin, v.x); xMax = Mathf.Max(xMax, v.x);
                    yMin = Mathf.Min(yMin, v.y); yMax = Mathf.Max(yMax, v.y);
                    any = true;
                }
            }
            area = default;
            if (!any) return false;
            xMin = Mathf.Clamp01(xMin - Margin); yMin = Mathf.Clamp01(yMin - Margin);
            xMax = Mathf.Clamp01(xMax + Margin); yMax = Mathf.Clamp01(yMax + Margin);
            if (xMax - xMin < 0.005f || yMax - yMin < 0.005f) return false;  // hors de l'écran
            area = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return true;
        }

        private void PrepareCamera()
        {
            if (_layer < 0)
            {
                _layer = LayerMask.NameToLayer("PostProcess");
                if (_layer < 0) _layer = LayerMask.NameToLayer("UI");
                if (_layer < 0) _layer = 5;
            }
            if (_camera == null)
            {
                var holder = new GameObject("SkyrimRoR2_Camera_" + _who);
                UnityEngine.Object.DontDestroyOnLoad(holder);
                _camera = holder.AddComponent<Camera>();
                _camera.enabled = false;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.renderingPath = RenderingPath.UsePlayerSettings;
                _camera.allowHDR = false;
                _camera.allowMSAA = true;
                var lightHolder = new GameObject("SkyrimRoR2_LumiereSurvivant");
                UnityEngine.Object.DontDestroyOnLoad(lightHolder);
                _sun = lightHolder.AddComponent<Light>();
                _sun.type = LightType.Directional;
                _sun.shadows = LightShadows.None;
                _sun.enabled = false;  // allumée seulement pendant la prise de vue
            }
            _camera.cullingMask = 1 << _layer;
            _sun.cullingMask = 1 << _layer;
            _camera.nearClipPlane = NearClip;
            _camera.farClipPlane = FarClip;
        }

        private void Textures(int width, int height)
        {
            int msaa = _antialiasing.Value >= 8 ? 8 : _antialiasing.Value >= 4 ? 4 : _antialiasing.Value >= 2 ? 2 : 1;
            if (_black != null && _black.width == width && _black.height == height && _black.antiAliasing == msaa) return;
            foreach (RenderTexture old in new[] { _black, _white, _blackOut, _whiteOut }) if (old != null) old.Release();
            _black = Make(width, height, msaa);
            _white = Make(width, height, msaa);
            _blackOut = Make(width, height, 1);
            _whiteOut = Make(width, height, 1);
        }

        private static RenderTexture Make(int width, int height, int msaa)
        {
            var texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { antiAliasing = msaa };
            texture.Create();
            return texture;
        }

        // Les autres lumières qui éclaireraient notre calque (soleil de la carte RoR2, lumière des mains...) sont
        // éteintes le temps de la prise de vue : seule compte la lumière de Skyrim.
        private readonly List<Light> _otherLights = new List<Light>();
        private readonly List<Light> _switchedOff = new List<Light>();
        private float _nextLightScan;

        private void OtherLights(bool off, float now)
        {
            if (off)
            {
                if (now >= _nextLightScan)
                {
                    _nextLightScan = now + 2f;
                    _otherLights.Clear();
                    _otherLights.AddRange(UnityEngine.Object.FindObjectsOfType<Light>());
                }
                _switchedOff.Clear();
                foreach (Light light in _otherLights)
                {
                    if (light == null || light == _sun || !light.enabled || (light.cullingMask & (1 << _layer)) == 0) continue;
                    light.enabled = false;
                    _switchedOff.Add(light);
                }
            }
            else
            {
                foreach (Light light in _switchedOff) if (light != null) light.enabled = true;
                _switchedOff.Clear();
            }
        }

        // La lumière de Skyrim : soleil (ou lune, ou lumière de la cellule) et lumière ambiante.
        private void ApplyLight(SkyrimInput input)
        {
            // Skyrim : x est, y nord, z haut  ->  Unity : x est, y haut, z nord.
            Vector3 toLight = new Vector3(input.SunX, input.SunZ, input.SunY);
            if (toLight.sqrMagnitude < 0.01f) toLight = new Vector3(0.3f, 0.85f, -0.4f);
            _sun.transform.rotation = Quaternion.LookRotation(-toLight.normalized, Vector3.up);
            Color color = new Color(input.SunR, input.SunG, input.SunB);
            float strength = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            _sun.intensity = Mathf.Clamp(strength, 0f, 3f);
            _sun.color = strength > 0.001f ? color / strength : Color.white;
        }

        private void Render(Rect screen, SkyrimInput input, float now)
        {
            if (_opaque == null) _opaque = new MaterialPropertyBlock();
            _savedLayers.Clear();
            _savedForceOff.Clear();
            while (_savedBlocks.Count < _renderers.Count) _savedBlocks.Add(new MaterialPropertyBlock());
            for (int i = 0; i < _renderers.Count; i++)
            {
                Renderer r = _renderers[i];
                _savedLayers.Add(r.gameObject.layer);
                _savedForceOff.Add(r.forceRenderingOff);
                r.gameObject.layer = _layer;
                r.forceRenderingOff = false;
                if (r is SkinnedMeshRenderer || r is MeshRenderer)
                {
                    // RoR2 efface le survivant quand SA caméra est trop près : pour la nôtre, bien visible.
                    r.GetPropertyBlock(_savedBlocks[i]);
                    r.GetPropertyBlock(_opaque);
                    _opaque.SetFloat(FadeId, 1f);
                    r.SetPropertyBlock(_opaque);
                }
            }
            bool occlude = BuildOcclusion();
            SphericalHarmonicsL2 savedProbe = RenderSettings.ambientProbe;
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(new Color(Mathf.Clamp(input.AmbientR, 0f, 3f), Mathf.Clamp(input.AmbientG, 0f, 3f), Mathf.Clamp(input.AmbientB, 0f, 3f)));
            try
            {
                RenderSettings.ambientProbe = probe;
                OtherLights(true, now);
                _sun.enabled = true;
                _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                if (occlude) _occluder.color = Color.black;
                _camera.targetTexture = _black;
                _camera.Render();
                _camera.backgroundColor = new Color(1f, 1f, 1f, 0f);
                if (occlude) _occluder.color = Color.white;
                _camera.targetTexture = _white;
                _camera.Render();
                _camera.targetTexture = null;
                Graphics.Blit(_black, _blackOut);  // lissage "résolu" : la carte graphique fait la moyenne
                Graphics.Blit(_white, _whiteOut);
            }
            finally
            {
                _sun.enabled = false;
                OtherLights(false, now);
                RenderSettings.ambientProbe = savedProbe;
                for (int i = _renderers.Count - 1; i >= 0; i--)
                {
                    Renderer r = _renderers[i];
                    if (r == null) continue;
                    r.gameObject.layer = _savedLayers[i];
                    r.forceRenderingOff = _savedForceOff[i];
                    if (r is SkinnedMeshRenderer || r is MeshRenderer) r.SetPropertyBlock(_savedBlocks[i].isEmpty ? null : _savedBlocks[i]);
                }
            }

            Capture capture = null;
            foreach (Capture c in _captures)
            {
                if (c.Left > 0 && now - c.Started > 1f) c.Left = 0;  // jamais revenue : on la libère
                if (c.Left == 0 && capture == null) capture = c;
            }
            if (capture == null)
            {
                if (_captures.Count >= 4) return;  // la carte graphique est en retard : on saute cette image
                capture = new Capture();
                _captures.Add(capture);
            }
            capture.Width = _blackOut.width;
            capture.Height = _blackOut.height;
            capture.Screen = screen;
            capture.Started = now;
            capture.Left = 2;
            Capture current = capture;
            AsyncGPUReadback.Request(_blackOut, 0, TextureFormat.RGBA32, request => OnReadback(request, current, false));
            AsyncGPUReadback.Request(_whiteOut, 0, TextureFormat.RGBA32, request => OnReadback(request, current, true));
        }

        // Le décor de Skyrim devant le survivant (voir Terrain plus haut). Faux s'il n'y a rien à cacher.
        private bool BuildOcclusion()
        {
            if (_occlusion == null) _occlusion = new CommandBuffer { name = "SkyrimRoR2_DecorDevantSurvivant" };
            _occlusion.Clear();
            if (!_occlusionAttached)
            {
                _camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _occlusion);
                _occlusionAttached = true;
            }
            if (!_hideBehindDecor.Value || Terrain == null || OccluderMaterial() == null) return false;

            // Le centre du survivant, pour la marge.
            Vector3 eye = _camera.transform.position;
            Vector3 center = Vector3.zero;
            int parts = 0;
            foreach (Renderer r in _renderers)
            {
                if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                center += r.bounds.center;
                parts++;
            }
            if (parts == 0) return false;
            center /= parts;
            // Tout le décor est "repoussé" un peu plus loin de la caméra (agrandi autour d'elle : à l'écran, rien ne
            // bouge), pour qu'un sol ou un mur juste derrière les pieds ne coupe pas le survivant.
            float distance = Mathf.Max(0.5f, (center - eye).magnitude);
            float k = Mathf.Clamp(1f + Mathf.Max(0f, _decorMargin.Value) / distance, 1f, 1.5f);
            Matrix4x4 push = Matrix4x4.Translate(eye) * Matrix4x4.Scale(new Vector3(k, k, k)) * Matrix4x4.Translate(-eye);
            float grow = 1f / Mathf.Max(0.05f, _shrink);
            _decorShrink = _shrink < 0.999f
                ? Matrix4x4.Translate(_feet) * Matrix4x4.Scale(new Vector3(grow, grow, grow)) * Matrix4x4.Translate(-_feet)
                : Matrix4x4.identity;
            GeometryUtility.CalculateFrustumPlanes(_camera, _planes);
            int count = 0;
            Terrain.ForEachOccluder((mesh, world) =>
            {
                Matrix4x4 matrix = push * _decorShrink * world;
                if (!GeometryUtility.TestPlanesAABB(_planes, WorldBounds(mesh.bounds, matrix))) return;
                for (int sub = 0; sub < mesh.subMeshCount; sub++) _occlusion.DrawMesh(mesh, matrix, _occluder, sub);
                count++;
            });
            if (count > 0 && !_occlusionLogged)
            {
                _occlusionLogged = true;
                _log.LogInfo($"3e personne : survivant cache par le decor de Skyrim ({count} morceaux devant la camera)");
            }
            return count > 0;
        }

        private static Bounds WorldBounds(Bounds local, Matrix4x4 m)
        {
            Vector3 center = m.MultiplyPoint3x4(local.center);
            Vector3 e = local.extents;
            Vector3 extents = new Vector3(
                Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
            return new Bounds(center, extents * 2f);
        }

        // Une matière simple, sans lumière, d'une seule couleur, testée contre la profondeur du survivant.
        private Material OccluderMaterial()
        {
            if (_occluder != null || _occluderTried) return _occluder;
            _occluderTried = true;
            // 0.73 : RoR2 ne trouve pas ces shaders par leur nom (Shader.Find rendait null partout). On cherche aussi
            // parmi les shaders déjà chargés en mémoire ; "UI/Default" (celui de l'interface de RoR2) est toujours là.
            Shader[] loaded = null;
            try { loaded = Resources.FindObjectsOfTypeAll<Shader>(); } catch (Exception) { }
            foreach (string name in new[] { "Hidden/Internal-Colored", "Unlit/Color", "Sprites/Default", "UI/Default" })
            {
                Shader shader = null;
                try { shader = Shader.Find(name); } catch (Exception) { }  // RoR2 lève une erreur pour un nom inconnu
                if (shader == null && loaded != null)
                    foreach (Shader s in loaded)
                        if (s != null && s.name == name && s.isSupported) { shader = s; break; }
                if (shader == null) continue;
                _occluder = new Material(shader) { name = "SkyrimRoR2_DecorCache", hideFlags = HideFlags.HideAndDontSave, color = Color.black };
                if (_occluder.HasProperty("_SrcBlend")) _occluder.SetInt("_SrcBlend", (int)BlendMode.One);
                if (_occluder.HasProperty("_DstBlend")) _occluder.SetInt("_DstBlend", (int)BlendMode.Zero);
                if (_occluder.HasProperty("_Cull")) _occluder.SetInt("_Cull", (int)CullMode.Off);
                if (_occluder.HasProperty("_ZWrite")) _occluder.SetInt("_ZWrite", 0);
                if (_occluder.HasProperty("_ZTest")) _occluder.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                if (_occluder.HasProperty("unity_GUIZTestMode")) _occluder.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);  // UI/Default
                if (_occluder.HasProperty("_MainTex")) _occluder.SetTexture("_MainTex", Texture2D.whiteTexture);
                _log.LogInfo("3e personne : decor qui cache le survivant dessine avec " + name);
                return _occluder;
            }
            _log.LogWarning("3e personne : aucune matiere pour cacher le survivant derriere le decor");
            return null;
        }

        private void OnReadback(AsyncGPUReadbackRequest request, Capture capture, bool white)
        {
            if (capture.Left <= 0) return;
            int width = capture.Width, height = capture.Height, size = width * height * 4;
            if (request.hasError || _view == IntPtr.Zero) { capture.Left = 0; return; }
            if (capture.Black.Length != size) { capture.Black = new byte[size]; capture.White = new byte[size]; }
            request.GetData<byte>().CopyTo(white ? capture.White : capture.Black);
            if (--capture.Left > 0) return;
            if (!Active) return;  // repassé à la 1re personne entre-temps
            if (_output.Length != size) _output = new byte[size];
            byte[] black = capture.Black, whiteImage = capture.White;
            int row = width * 4;
            bool flip = _flip.Value;
            int saturation = Mathf.RoundToInt(Mathf.Clamp01(_saturation.Value) * 256f);
            int exposure = Mathf.RoundToInt(Mathf.Clamp(_exposure.Value, 0f, 2f) * 256f);
            for (int y = 0; y < height; y++)
            {
                int from = (flip ? height - 1 - y : y) * row, to = y * row;
                for (int x = 0; x < row; x += 4)
                {
                    int i = from + x, o = to + x;
                    // Fond noir et fond blanc : la différence donne la transparence de chaque pixel.
                    int difference = (whiteImage[i] - black[i]) + (whiteImage[i + 1] - black[i + 1]) + (whiteImage[i + 2] - black[i + 2]);
                    if (difference >= 750) { _output[o] = 0; _output[o + 1] = 0; _output[o + 2] = 0; _output[o + 3] = 0; continue; }
                    if (difference < 30)
                    {
                        Grade(black[i], black[i + 1], black[i + 2], o, saturation, exposure);
                        _output[o + 3] = 255;
                        continue;
                    }
                    int alpha = 255 - (difference > 765 ? 255 : difference / 3);
                    if (alpha < 8) { _output[o] = 0; _output[o + 1] = 0; _output[o + 2] = 0; _output[o + 3] = 0; continue; }
                    int r = black[i] * 255 / alpha, g = black[i + 1] * 255 / alpha, b = black[i + 2] * 255 / alpha;
                    Grade(r > 255 ? 255 : r, g > 255 ? 255 : g, b > 255 ? 255 : b, o, saturation, exposure);
                    _output[o + 3] = (byte)alpha;
                }
            }
            int next = 1 - _ready;
            Marshal.Copy(_output, 0, IntPtr.Add(_view, BridgeInfo.ViewHeaderSize + next * BridgeInfo.SurvivorFrameSize), size);
            Marshal.WriteInt32(_view, 4, width);
            Marshal.WriteInt32(_view, 8, height);
            Marshal.WriteInt32(_view, 16, next);
            WriteFloat(24, capture.Screen.xMin);
            WriteFloat(28, capture.Screen.yMin);
            WriteFloat(32, capture.Screen.xMax);
            WriteFloat(36, capture.Screen.yMax);
            Marshal.WriteInt32(_view, 12, unchecked((int)++_sequence));
            Marshal.WriteInt32(_view, 0, unchecked((int)BridgeInfo.ViewMagic));
            _wasActive = true;
            Marshal.WriteInt32(_view, 20, 1);
            _ready = next;
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                _log.LogInfo($"3e personne : premiere image ({_who}) envoyee a Skyrim ({width} x {height}, " +
                             $"ecran {capture.Screen.xMin:F2}-{capture.Screen.xMax:F2} x {capture.Screen.yMin:F2}-{capture.Screen.yMax:F2})");
            }
        }

        private void WriteFloat(int offset, float value) =>
            Marshal.WriteInt32(_view, offset, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        private void Grade(int r, int g, int b, int o, int saturation, int exposure)
        {
            int grey = (r * 77 + g * 150 + b * 29) >> 8;
            r = (grey + (((r - grey) * saturation) >> 8)) * exposure >> 8;
            g = (grey + (((g - grey) * saturation) >> 8)) * exposure >> 8;
            b = (grey + (((b - grey) * saturation) >> 8)) * exposure >> 8;
            _output[o] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
            _output[o + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
            _output[o + 2] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
        }
    }
}
