using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // 0.66 : les objets de RoR2 qui doivent être SOLIDES dans Skyrim (le cube de détritus de la Vagabonde).
    //
    // AllyMirror les signale au moment où il les affiche (Consider), avec la taille qu'ils ont dans Skyrim.
    // Un objet n'est envoyé qu'une fois immobile (il ne vole plus) : Skyrim pose alors à sa place une boite de
    // collision invisible (voir skse/src/Solids.cpp). Huitième pont : en-tête de 16 octets (magic, séquence,
    // nombre), puis SolidMax entrées de 32 octets : id, centre (monde Skyrim, m : 3 floats),
    // demi-côtés (m : 3 floats), angle autour de la verticale (radians).
    internal sealed class SolidMirror
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        private static readonly string[] SolidNames = { "JunkCube" };  // noms (en partie) des objets à rendre solides
        private const float StillSpeed = 0.15f;   // m/s : en dessous, l'objet est posé
        private const float StillTime = 0.25f;    // s d'immobilité avant d'être solide

        private readonly ManualLogSource _log;
        private IntPtr _view;
        private bool _openTried;
        private uint _sequence;

        private struct Seen
        {
            public Vector3 Center;
            public float Time;
            public float StillSince;
        }
        private readonly Dictionary<int, Seen> _seen = new Dictionary<int, Seen>();
        private readonly List<(int id, Vector3 center, Vector3 half, float yaw)> _frame = new List<(int, Vector3, Vector3, float)>();
        private readonly HashSet<string> _logged = new HashSet<string>();
        private readonly List<Renderer> _renderers = new List<Renderer>();

        public SolidMirror(ManualLogSource log) { _log = log; }

        public static bool IsSolid(string name)
        {
            foreach (string key in SolidNames)
                if (name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // root : l'objet de RoR2 ; shownRoot : où sa racine est affichée dans Skyrim (monde RoR2) ;
        // scale : sa taille dans Skyrim par rapport à RoR2.
        public void Consider(string name, int id, Transform root, Vector3 shownRoot, float scale, TerrainMirror terrain)
        {
            if (root == null || terrain == null || !IsSolid(name)) return;
            if (_frame.Count >= BridgeInfo.SolidMax) return;
            _renderers.Clear();
            root.GetComponentsInChildren(false, _renderers);
            bool any = false;
            Bounds bounds = default;
            foreach (Renderer r in _renderers)
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (!any) return;
            Vector3 center = shownRoot + (bounds.center - root.position) * scale;
            Vector3 half = bounds.extents * scale;
            if (half.x < 0.1f || half.y < 0.1f || half.z < 0.1f || half.magnitude > 5f) return;

            float now = Time.time;
            if (!_seen.TryGetValue(id, out Seen seen)) seen = new Seen { Center = center, Time = now, StillSince = now };
            float dt = Mathf.Max(now - seen.Time, 0.0001f);
            if ((center - seen.Center).magnitude / dt > StillSpeed) seen.StillSince = now;
            seen.Center = center;
            seen.Time = now;
            _seen[id] = seen;
            if (now - seen.StillSince < StillTime) return;

            // 0.73 : aussi une vraie boîte dans RoR2, à la taille affichée : c'est RoR2 qui déplace le survivant (et
            // donc le joueur de Skyrim), il faut que LUI puisse monter dessus. Elle bloque aussi les ennemis.
            Solid(id, center, half);
            if (!terrain.TryMapToSkyrim(center, out Vector3 skyrim)) return;
            // Repères : RoR2 (X est, Y hauteur, Z nord) -> Skyrim (X est, Y nord, Z hauteur).
            _frame.Add((id, skyrim, new Vector3(half.x, half.z, half.y), 0f));
            if (_logged.Add(name))
                _log.LogInfo($"Solides : {name} solide dans Skyrim ({half.x * 2f:F2} x {half.z * 2f:F2} x {half.y * 2f:F2} m)");
        }

        // Les boîtes de collision posées dans RoR2 (une par objet solide), et celles vues à cette image.
        private readonly Dictionary<int, GameObject> _boxes = new Dictionary<int, GameObject>();
        private readonly HashSet<int> _boxesSeen = new HashSet<int>();

        private void Solid(int id, Vector3 center, Vector3 half)
        {
            _boxesSeen.Add(id);
            try
            {
                if (!_boxes.TryGetValue(id, out GameObject box) || box == null)
                {
                    box = new GameObject("SkyrimRoR2_Solide");
                    box.layer = LayerIndex.world.intVal;
                    box.AddComponent<BoxCollider>();
                    _boxes[id] = box;
                }
                box.transform.position = center;
                box.transform.rotation = Quaternion.identity;
                box.GetComponent<BoxCollider>().size = half * 2f;
            }
            catch (Exception) { }
        }

        public void Flush()
        {
            if (_boxes.Count > 0)
            {
                var gone = new List<int>();
                foreach (var pair in _boxes) if (!_boxesSeen.Contains(pair.Key) || pair.Value == null) gone.Add(pair.Key);
                foreach (int key in gone)
                {
                    if (_boxes[key] != null) UnityEngine.Object.Destroy(_boxes[key]);
                    _boxes.Remove(key);
                }
            }
            _boxesSeen.Clear();
            if (Open())
            {
                int count = Mathf.Min(_frame.Count, BridgeInfo.SolidMax);
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = IntPtr.Add(_view, BridgeInfo.SolidHeaderSize + i * BridgeInfo.SolidEntrySize);
                    var (id, center, half, yaw) = _frame[i];
                    Marshal.WriteInt32(entry, 0, id);
                    WriteFloat(entry, 4, center.x);
                    WriteFloat(entry, 8, center.y);
                    WriteFloat(entry, 12, center.z);
                    WriteFloat(entry, 16, half.x);
                    WriteFloat(entry, 20, half.y);
                    WriteFloat(entry, 24, half.z);
                    WriteFloat(entry, 28, yaw);
                }
                Marshal.WriteInt32(_view, 8, count);
                Marshal.WriteInt32(_view, 4, unchecked((int)++_sequence));
                Marshal.WriteInt32(_view, 0, unchecked((int)BridgeInfo.SolidMagic));
            }
            _frame.Clear();
            // On oublie les objets disparus depuis un moment.
            if (_seen.Count > 64)
            {
                float now = Time.time;
                var old = new List<int>();
                foreach (var pair in _seen) if (now - pair.Value.Time > 5f) old.Add(pair.Key);
                foreach (int key in old) _seen.Remove(key);
            }
        }

        private static void WriteFloat(IntPtr at, int offset, float value) =>
            Marshal.WriteInt32(at, offset, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        private bool Open()
        {
            if (_view != IntPtr.Zero) return true;
            if (_openTried) return false;
            _openTried = true;
            IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.SolidMapSize, BridgeInfo.SolidMapName);
            if (mapping != IntPtr.Zero) _view = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.SolidMapSize);
            if (_view == IntPtr.Zero) _log.LogWarning("Solides : pont impossible a ouvrir");
            return _view != IntPtr.Zero;
        }
    }
}
