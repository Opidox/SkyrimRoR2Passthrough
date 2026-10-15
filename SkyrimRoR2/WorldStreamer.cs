using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoR2;
using RoR2.Projectile;
using SkyrimRoR2.Shared;
using UnityEngine;
using UnityEngine.Rendering;

namespace SkyrimRoR2
{
    // Ce que RoR2 voit depuis la caméra de Skyrim : les alliés (drones, tourelles, alliés achetés...), les tirs
    // (balles, projectiles, mines, explosions) et les effets des attaques. Filmé exactement depuis la caméra
    // de Skyrim (même position dans le décor recopié, même direction, même champ de vision), sur fond
    // transparent, puis affiché par Skyrim sur tout l'écran, sous les mains du survivant.
    //
    // Même technique que les mains (ViewmodelStreamer) : les objets à montrer passent un instant sur un calque
    // à nous, on les filme deux fois (fond noir puis fond blanc, pour la transparence), puis ils reviennent.
    // Les alliés cachés derrière un mur de Skyrim ne sont pas filmés.
    internal sealed class WorldStreamer
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        private const float NearClip = 0.15f;
        private const float FarClip = 250f;
        private const float EffectRange = 150f;
        private static readonly int FadeId = Shader.PropertyToID("_Fade");

        private readonly ManualLogSource _log;
        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<int> _height;
        private readonly ConfigEntry<int> _fps;
        private readonly ConfigEntry<bool> _flip;
        private readonly ConfigEntry<float> _light;

        private IntPtr _view;
        private bool _openTried;
        private Camera _camera;
        private Light _sun;
        private RenderTexture _black, _white;        // l'image envoyée
        private RenderTexture _blackBig, _whiteBig;  // filmée en plus grand puis réduite : bords lisses
        private readonly ConfigEntry<int> _supersample;
        private readonly ConfigEntry<float> _ambient;
        private readonly ConfigEntry<float> _saturation;
        private readonly ConfigEntry<float> _exposure;
        private int _layer = -1;
        private float _nextFrame;
        private int _ready;
        private uint _sequence;
        private bool _wasActive;
        private bool _loggedFirst;

        // Les objets à filmer (rescannés 10 fois par seconde).
        private float _nextScan;
        private readonly List<Component> _effects = new List<Component>();
        private readonly List<ProjectileController> _projectiles = new List<ProjectileController>();

        private readonly List<Renderer> _renderers = new List<Renderer>();
        private readonly List<int> _savedLayers = new List<int>();
        private readonly List<bool> _savedForceOff = new List<bool>();
        private readonly List<MaterialPropertyBlock> _savedBlocks = new List<MaterialPropertyBlock>();
        private MaterialPropertyBlock _opaque;
        private readonly List<Renderer> _scratch = new List<Renderer>();

        private sealed class Capture
        {
            public byte[] Black = new byte[0], White = new byte[0];
            public int Left, Width, Height;
            public float Yaw, Pitch;   // direction de la caméra Skyrim au moment de la prise de vue
        }
        private float _yaw, _pitch;
        private bool _includeAllies = true;
        public static bool NativeEffects;  // les effets reconnus sont rejoués par Skyrim : on ne les filme plus  // faux quand les alliés sont affichés avec leur vrai modèle dans Skyrim
        private readonly List<Capture> _captures = new List<Capture>();
        private byte[] _output = new byte[0];

        public WorldStreamer(ManualLogSource log, ConfigFile config)
        {
            _log = log;
            _enabled = ConfigMove.Bind(config, "Monde", "4 - Effets", "ImageEffets2D", false,
                "Films RoR2 effects (trails, particles, muzzle flashes...) and shows them as a picture over Skyrim. Off by default: allies, shots and projectiles are real 3D and effects are replayed by Skyrim.");
            _height = ConfigMove.Bind(config, "Monde", "5 - Ancienne image 2D", "ImageAlliesHauteur", 720, "Height (pixels) of the picture of allies and shots sent to Skyrim.");
            _fps = ConfigMove.Bind(config, "Monde", "5 - Ancienne image 2D", "ImageAlliesParSeconde", 60, "Frames per second of allies and shots sent to Skyrim.");
            _flip = ConfigMove.Bind(config, "Monde", "5 - Ancienne image 2D", "RetournerImageAllies", true, "Flips the picture vertically (change it if it shows upside down).");
            _light = ConfigMove.Bind(config, "Monde", "5 - Ancienne image 2D", "SoleilAllies", 0.9f, "Strength of the sun (from above, fixed in the world) lighting the filmed allies.");
            _ambient = ConfigMove.Bind(config, "Monde", "5 - Ancienne image 2D", "AmbianceAllies", 0.45f, "Ambient light on allies (also lights the shaded sides).");
            _saturation = ConfigMove.Bind(config, "Monde", "5 - Ancienne image 2D", "SaturationAllies", 0.75f, "Ally colors: 1 = vivid RoR2 colors, lower = duller, like Skyrim.");
            _exposure = ConfigMove.Bind(config, "Monde", "5 - Ancienne image 2D", "LuminositeAllies", 0.85f, "Final brightness of allies (1 = unchanged).");
            _supersample = ConfigMove.Bind(config, "Monde", "5 - Ancienne image 2D", "LissageAllies", 2, "Films the picture twice as big then shrinks it: smooth edges (1 = off, faster).");
        }

        // camera : position de la caméra Skyrim dans le monde de RoR2 ; forward : sa direction ;
        // frustum : bords de l'image à 1 m devant elle (gauche, droite, haut, bas).
        public void Update(CharacterBody survivor, bool connected, bool cameraKnown, Vector3 camera, Vector3 forward, Vector4 frustum,
            float yaw, float pitch, bool includeAllies)
        {
            _includeAllies = includeAllies;
            _yaw = yaw;
            _pitch = pitch;
            if (!_enabled.Value || !connected || survivor == null || !cameraKnown || frustum.y <= frustum.x || frustum.z <= frustum.w)
            {
                SetActive(false);
                return;
            }
            float now = Time.unscaledTime;
            if (now < _nextFrame) return;
            _nextFrame = now + 1f / Mathf.Clamp(_fps.Value, 5, 60);
            if (!Open()) return;

            try
            {
                Scan(now);
                Gather(survivor, camera);
                if (_renderers.Count == 0)
                {
                    SetActive(false);
                    return;
                }
                Setup(frustum);
                _camera.transform.SetPositionAndRotation(camera, Quaternion.LookRotation(forward, Vector3.up));
                Render();
            }
            catch (Exception e)
            {
                _log.LogWarning("Monde : image des allies impossible (" + e.Message + ")");
                _nextFrame = now + 5f;
            }
        }

        private bool Open()
        {
            if (_view != IntPtr.Zero) return true;
            if (_openTried) return false;
            _openTried = true;
            IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.ViewMapSize, BridgeInfo.WorldViewMapName);
            if (mapping != IntPtr.Zero) _view = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.ViewMapSize);
            if (_view == IntPtr.Zero) _log.LogWarning("Monde : pont des images des allies impossible a ouvrir");
            return _view != IntPtr.Zero;
        }

        private void SetActive(bool active)
        {
            if (_view == IntPtr.Zero || _wasActive == active) return;
            _wasActive = active;
            Marshal.WriteInt32(_view, 20, active ? 1 : 0);
        }

        private void Scan(float now)
        {
            if (now < _nextScan) return;
            _nextScan = now + 0.1f;
            _effects.Clear();
            foreach (EffectComponent effect in Tracked.Effects) if (Tracked.Alive(effect)) _effects.Add(effect);
            _projectiles.Clear();
            foreach (ProjectileController projectile in Tracked.Projectiles) if (Tracked.Alive(projectile)) _projectiles.Add(projectile);
        }

        // Ce qu'il faut filmer : alliés visibles depuis Skyrim, projectiles, effets proches.
        private void Gather(CharacterBody survivor, Vector3 camera)
        {
            _renderers.Clear();
            int world = LayerIndex.world.mask;
            foreach (CharacterMaster master in CharacterMaster.readOnlyInstancesList)
            {
                if (!_includeAllies) break;
                if (master == null || master.teamIndex != TeamIndex.Player) continue;
                CharacterBody body = master.GetBody();
                if (body == null || body == survivor || body.healthComponent == null || !body.healthComponent.alive) continue;
                Transform model = body.modelLocator != null ? body.modelLocator.modelTransform : null;
                if (model == null) continue;
                Vector3 core = body.corePosition;
                if ((core - camera).sqrMagnitude > FarClip * FarClip) continue;
                // Caché derrière un mur de Skyrim (le décor recopié) : pas filmé.
                if (Physics.Linecast(camera, core, out RaycastHit hit, world, QueryTriggerInteraction.Ignore) &&
                    hit.collider != null && hit.collider.gameObject.name.StartsWith("SkyrimRoR2_") &&
                    (hit.point - core).sqrMagnitude > 1f)
                    continue;
                _scratch.Clear();
                model.GetComponentsInChildren(false, _scratch);
                foreach (Renderer r in _scratch) if (r is SkinnedMeshRenderer || r is MeshRenderer) Add(r);
            }
            foreach (ProjectileController projectile in _projectiles)
            {
                if (projectile == null) continue;
                if ((projectile.transform.position - camera).sqrMagnitude > EffectRange * EffectRange) continue;
                // Avec les vrais modèles dans Skyrim, on ne filme plus que leurs traînées et particules.
                // (Sauf les particules en objets 3D d'un projectile sans fantôme : converties elles aussi.)
                AddAll(projectile.gameObject, !_includeAllies, !_includeAllies && projectile.ghost == null);
                if (projectile.ghost != null) AddAll(projectile.ghost.gameObject, !_includeAllies, !_includeAllies);
            }
            foreach (Component effect in _effects)
            {
                if (effect == null) continue;
                if ((effect.transform.position - camera).sqrMagnitude > EffectRange * EffectRange) continue;
                if (NativeEffects && EffectMirror.Family(effect.gameObject.name) != 0) continue;  // rejoué par Skyrim
                if (GhostManager.NearRecentSpawn(effect.transform.position)) continue;  // apparition d'un double
                AddAll(effect.gameObject);
            }
        }

        private void AddAll(GameObject holder, bool skipMeshes = false, bool skipMeshParticles = false)
        {
            _scratch.Clear();
            holder.GetComponentsInChildren(false, _scratch);
            foreach (Renderer r in _scratch)
            {
                if (skipMeshes && (r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                if (skipMeshParticles && r is ParticleSystemRenderer p && p.renderMode == ParticleSystemRenderMode.Mesh) continue;
                Add(r);
            }
        }

        private void Add(Renderer r)
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) return;
            if (r.gameObject.name.StartsWith("SkyrimRoR2_")) return;  // notre décor recopié
            _renderers.Add(r);
        }

        private void Setup(Vector4 frustum)
        {
            if (_layer < 0)
            {
                _layer = LayerMask.NameToLayer("PostProcess");
                if (_layer < 0) _layer = LayerMask.NameToLayer("UI");
                if (_layer < 0) _layer = 5;
            }
            float aspect = (frustum.y - frustum.x) / (frustum.z - frustum.w);
            int height = Mathf.Clamp(_height.Value, 120, BridgeInfo.ViewMaxHeight);
            int width = Mathf.Clamp(Mathf.RoundToInt(height * aspect), 64, BridgeInfo.ViewMaxWidth);
            int factor = Mathf.Clamp(_supersample.Value, 1, 2);
            if (_blackBig == null || _blackBig.width != width * factor || _blackBig.height != height * factor)
            {
                if (_blackBig != null) _blackBig.Release();
                if (_whiteBig != null) _whiteBig.Release();
                _blackBig = new RenderTexture(width * factor, height * factor, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { antiAliasing = 1 };
                _blackBig.Create();
                _whiteBig = new RenderTexture(width * factor, height * factor, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { antiAliasing = 1 };
                _whiteBig.Create();
            }
            if (_black == null || _black.width != width || _black.height != height)
            {
                if (_black != null) _black.Release();
                if (_white != null) _white.Release();
                _black = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { antiAliasing = 1 };
                _black.Create();
                _white = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { antiAliasing = 1 };
                _white.Create();
            }
            if (_camera == null)
            {
                var holder = new GameObject("SkyrimRoR2_CameraMonde");
                UnityEngine.Object.DontDestroyOnLoad(holder);
                _camera = holder.AddComponent<Camera>();
                _camera.enabled = false;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.renderingPath = RenderingPath.UsePlayerSettings;
                _camera.allowHDR = false;
                _camera.allowMSAA = false;
                var lightHolder = new GameObject("SkyrimRoR2_LumiereMonde");
                lightHolder.transform.SetParent(holder.transform, false);
                lightHolder.transform.localRotation = Quaternion.Euler(40f, -25f, 0f);
                _sun = lightHolder.AddComponent<Light>();
                _sun.type = LightType.Directional;
                _sun.color = Color.white;
                _sun.shadows = LightShadows.None;
            }
            _camera.cullingMask = 1 << _layer;
            _sun.cullingMask = 1 << _layer;
            // Le soleil est fixe dans le monde (pas accroché à la caméra) : les côtés à l'ombre restent à l'ombre.
            _sun.transform.rotation = Quaternion.Euler(55f, 35f, 0f);
            _sun.intensity = Mathf.Clamp(_light.Value, 0f, 5f);
            _camera.nearClipPlane = NearClip;
            _camera.farClipPlane = FarClip;
            _camera.projectionMatrix = Matrix4x4.Frustum(frustum.x * NearClip, frustum.y * NearClip, frustum.w * NearClip, frustum.z * NearClip, NearClip, FarClip);
        }

        private void Render()
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
                    // RoR2 efface les modèles proches de SA caméra : pour la nôtre, bien visibles.
                    r.GetPropertyBlock(_savedBlocks[i]);
                    r.GetPropertyBlock(_opaque);
                    _opaque.SetFloat(FadeId, 1f);
                    r.SetPropertyBlock(_opaque);
                }
            }
            // Une lumière ambiante douce le temps de la prise de vue (la fenêtre RoR2 n'en souffre pas).
            UnityEngine.Rendering.SphericalHarmonicsL2 savedProbe = RenderSettings.ambientProbe;
            var probe = new UnityEngine.Rendering.SphericalHarmonicsL2();
            float ambient = Mathf.Clamp(_ambient.Value, 0f, 2f);
            probe.AddAmbientLight(new Color(ambient, ambient * 0.97f, ambient * 0.92f));
            try
            {
                RenderSettings.ambientProbe = probe;
                _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                _camera.targetTexture = _blackBig;
                _camera.Render();
                _camera.backgroundColor = new Color(1f, 1f, 1f, 0f);
                _camera.targetTexture = _whiteBig;
                _camera.Render();
                _camera.targetTexture = _blackBig;
                // Réduction : chaque pixel = la moyenne des 4 filmés (bords lisses, sans escaliers).
                Graphics.Blit(_blackBig, _black);
                Graphics.Blit(_whiteBig, _white);
            }
            finally
            {
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
            foreach (Capture c in _captures) if (c.Left == 0) { capture = c; break; }
            if (capture == null)
            {
                if (_captures.Count >= 4) return;  // la carte graphique est en retard : on saute cette image
                capture = new Capture();
                _captures.Add(capture);
            }
            capture.Width = _black.width;
            capture.Height = _black.height;
            capture.Left = 2;
            capture.Yaw = _yaw;
            capture.Pitch = _pitch;
            Capture current = capture;
            AsyncGPUReadback.Request(_black, 0, TextureFormat.RGBA32, request => OnReadback(request, current, false));
            AsyncGPUReadback.Request(_white, 0, TextureFormat.RGBA32, request => OnReadback(request, current, true));
        }

        // Couleurs ternies et luminosité réglée, pour ressembler davantage à l'image de Skyrim.
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

        private void OnReadback(AsyncGPUReadbackRequest request, Capture capture, bool white)
        {
            if (capture.Left <= 0) return;
            int width = capture.Width, height = capture.Height, size = width * height * 4;
            if (request.hasError || _view == IntPtr.Zero) { capture.Left = 0; return; }
            if (capture.Black.Length != size) { capture.Black = new byte[size]; capture.White = new byte[size]; }
            request.GetData<byte>().CopyTo(white ? capture.White : capture.Black);
            if (--capture.Left > 0) return;
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
                    int difference = (whiteImage[i] - black[i]) + (whiteImage[i + 1] - black[i + 1]) + (whiteImage[i + 2] - black[i + 2]);
                    if (difference < 60)
                    {
                        Grade(black[i], black[i + 1], black[i + 2], o, saturation, exposure);
                        _output[o + 3] = 255;
                        continue;
                    }
                    int alpha = 255 - (difference > 765 ? 255 : difference / 3);
                    if (alpha < 12) { _output[o] = 0; _output[o + 1] = 0; _output[o + 2] = 0; _output[o + 3] = 0; continue; }
                    int r = black[i] * 255 / alpha, g = black[i + 1] * 255 / alpha, b = black[i + 2] * 255 / alpha;
                    Grade(r > 255 ? 255 : r, g > 255 ? 255 : g, b > 255 ? 255 : b, o, saturation, exposure);
                    _output[o + 3] = (byte)alpha;
                }
            }
            int next = 1 - _ready;
            Marshal.Copy(_output, 0, IntPtr.Add(_view, BridgeInfo.ViewHeaderSize + next * BridgeInfo.ViewFrameSize), size);
            Marshal.WriteInt32(_view, 4, width);
            Marshal.WriteInt32(_view, 8, height);
            Marshal.WriteInt32(_view, 16, next);
            // Skyrim décale l'image si la caméra a tourné depuis (l'image ne "traîne" plus derrière le regard).
            Marshal.WriteInt32(_view, 24, BitConverter.ToInt32(BitConverter.GetBytes(capture.Yaw), 0));
            Marshal.WriteInt32(_view, 28, BitConverter.ToInt32(BitConverter.GetBytes(capture.Pitch), 0));
            Marshal.WriteInt32(_view, 12, unchecked((int)++_sequence));
            Marshal.WriteInt32(_view, 0, unchecked((int)BridgeInfo.ViewMagic));
            _wasActive = true;
            Marshal.WriteInt32(_view, 20, 1);
            _ready = next;
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                _log.LogInfo($"Monde : premiere image des allies et des tirs envoyee a Skyrim ({width} x {height})");
            }
        }
    }
}
