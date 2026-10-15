using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // Le décor de Skyrim recopié avec ses VRAIES formes de collision (voir Decor.cpp côté Skyrim).
    //
    // Skyrim envoie, pour chaque objet fixe autour du joueur (sol, maisons, arches, rochers, arbres...),
    // ses triangles de collision dans le monde de Skyrim (mètres). Ici, chaque objet devient une surface
    // invisible sur le calque du décor : le survivant marche dessus, passe sous les arches, son grappin
    // s'y accroche, ses tirs s'y arrêtent. Un objet n'est construit qu'une fois, puis gardé tant que
    // Skyrim le garde : rien n'est reconstruit sous les pieds du survivant.
    // Repères : Skyrim X = est, Y = nord, Z = hauteur ; RoR2 X = est, Y = hauteur, Z = nord.
    internal sealed class DecorMirror
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        private const float StaleSeconds = 3f;          // plus de nouvelles de Skyrim depuis 3 s : décor retiré
        private const int TrianglesPerFrame = 40000;    // construits au plus par image (pas d'à-coups)
        private static readonly Color DecorColor = new Color(0.55f, 0.7f, 0.85f, 0.4f);

        private sealed class Piece
        {
            public GameObject Object;
            public Mesh Mesh;
            public Vector3 Center;    // monde Skyrim en repères RoR2 (m)
        }

        private sealed class Pending
        {
            public ulong Id;
            public float[] Triangles;
        }

        private readonly ManualLogSource _log;
        private readonly Dictionary<ulong, Piece> _pieces = new Dictionary<ulong, Piece>();
        private readonly Dictionary<ulong, Pending> _pending = new Dictionary<ulong, Pending>();
        private readonly Queue<ulong> _order = new Queue<ulong>();
        private IntPtr _view;
        private bool _openTried;
        private uint _lastSequence;
        private uint _lastHeartbeat;
        private float _lastAlive = -100f;
        private bool _shown = true;
        private bool _visible;
        private Material _material;
        private int _updates;
        private bool _logged;

        public DecorMirror(ManualLogSource log)
        {
            _log = log;
        }

        // Vrai quand Skyrim envoie son décor et qu'il est (au moins en partie) construit.
        public bool Active => _pieces.Count > 0 && Time.time - _lastAlive < StaleSeconds;

        // 0.66 : chaque morceau de décor recopié (forme, place dans le monde de RoR2), pour cacher le survivant
        // filmé derrière les murs et les rochers de Skyrim (voir SurvivorStreamer).
        public void ForEachOccluder(Action<Mesh, Matrix4x4> visit)
        {
            foreach (Piece piece in _pieces.Values)
                if (piece.Object != null && piece.Mesh != null) visit(piece.Mesh, piece.Object.transform.localToWorldMatrix);
        }

        // Appelé à chaque image. offset : monde RoR2 = monde Skyrim (repères RoR2) + offset.
        public void Update(Vector3 offset, bool offsetChanged, bool visible, Func<Material> material)
        {
            if (!Open()) return;
            if ((uint)Marshal.ReadInt32(_view, 0) != BridgeInfo.DecorMagic) return;

            uint heartbeat = (uint)Marshal.ReadInt32(_view, 16);
            if (heartbeat != _lastHeartbeat)
            {
                _lastHeartbeat = heartbeat;
                _lastAlive = Time.time;
            }

            if (visible != _visible || (visible && _material == null))
            {
                _visible = visible;
                if (visible && _material == null) _material = material();
                foreach (Piece piece in _pieces.Values) SetRenderer(piece);
            }

            // Changement de niveau dans RoR2 : nos objets ont été détruits avec la scène. On repart de zéro.
            foreach (Piece piece in _pieces.Values)
            {
                if (piece.Object == null)
                {
                    _pieces.Clear();
                    _pending.Clear();
                    _order.Clear();
                    _lastSequence = 0;
                    _logged = false;
                    _log.LogInfo("Decor : nouvelle scene RoR2, decor reconstruit");
                }
                break;
            }

            ReadTable();

            if (offsetChanged)
                foreach (Piece piece in _pieces.Values) piece.Object.transform.position = piece.Center + offset;

            // Construction des nouveaux objets, petit à petit.
            int budget = TrianglesPerFrame;
            while (budget > 0 && _order.Count > 0)
            {
                ulong id = _order.Dequeue();
                if (!_pending.TryGetValue(id, out Pending pending)) continue;  // retiré entre-temps
                _pending.Remove(id);
                Piece piece = Build(pending.Triangles);
                if (piece == null) continue;
                piece.Object.transform.position = piece.Center + offset;
                piece.Object.SetActive(_shown);
                _pieces[id] = piece;
                budget -= pending.Triangles.Length / 9;
            }
            if (!_logged && _pieces.Count > 0 && _pending.Count == 0)
            {
                _logged = true;
                _log.LogInfo($"Decor : {_pieces.Count} objets de Skyrim construits dans RoR2");
            }
        }

        public void SetShown(bool shown)
        {
            if (_shown == shown) return;
            _shown = shown;
            foreach (Piece piece in _pieces.Values)
                if (piece.Object != null) piece.Object.SetActive(shown);
            if (shown) _log.LogInfo("Decor : decor de Skyrim en place dans RoR2");
        }

        private bool Open()
        {
            if (_view != IntPtr.Zero) return true;
            if (_openTried) return false;
            _openTried = true;
            IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.DecorMapSize, BridgeInfo.DecorMapName);
            if (mapping != IntPtr.Zero) _view = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.DecorMapSize);
            if (_view == IntPtr.Zero) _log.LogWarning("Decor : pont impossible a ouvrir");
            return _view != IntPtr.Zero;
        }

        // Lit la liste des objets (numéro pair, inchangé pendant la lecture) : nouveaux objets à construire,
        // objets disparus à détruire.
        private void ReadTable()
        {
            uint sequence = (uint)Marshal.ReadInt32(_view, 4);
            if (sequence == _lastSequence || (sequence & 1) != 0) return;
            int count = Marshal.ReadInt32(_view, 8);
            if (count < 0 || count > BridgeInfo.DecorMaxObjects) return;

            var entries = new byte[count * BridgeInfo.DecorEntrySize];
            if (count > 0) Marshal.Copy(IntPtr.Add(_view, BridgeInfo.DecorHeaderSize), entries, 0, entries.Length);

            var present = new HashSet<ulong>();
            var fresh = new List<Pending>();
            for (int i = 0; i < count; i++)
            {
                int at = i * BridgeInfo.DecorEntrySize;
                ulong id = BitConverter.ToUInt64(entries, at);
                int first = BitConverter.ToInt32(entries, at + 8);
                int triangles = BitConverter.ToInt32(entries, at + 12);
                present.Add(id);
                if (_pieces.ContainsKey(id) || _pending.ContainsKey(id) || triangles <= 0 || first < 0) continue;
                long offset = BridgeInfo.DecorTrianglesOffset + (long)first * 36;
                if (offset + (long)triangles * 36 > BridgeInfo.DecorMapSize) continue;
                var data = new float[triangles * 9];
                Marshal.Copy(new IntPtr(_view.ToInt64() + offset), data, 0, data.Length);
                fresh.Add(new Pending { Id = id, Triangles = data });
            }
            if ((uint)Marshal.ReadInt32(_view, 4) != sequence) return;  // réécrite pendant la lecture : on attend la suivante
            _lastSequence = sequence;

            foreach (Pending pending in fresh)
            {
                _pending[pending.Id] = pending;
                _order.Enqueue(pending.Id);
            }

            var gone = new List<ulong>();
            foreach (ulong id in _pieces.Keys)
                if (!present.Contains(id)) gone.Add(id);
            foreach (ulong id in gone)
            {
                UnityEngine.Object.Destroy(_pieces[id].Object);
                UnityEngine.Object.Destroy(_pieces[id].Mesh);
                _pieces.Remove(id);
            }
            var dropped = new List<ulong>();
            foreach (ulong id in _pending.Keys)
                if (!present.Contains(id)) dropped.Add(id);
            foreach (ulong id in dropped) _pending.Remove(id);

            _updates++;
            if (_updates <= 3 || _updates % 100 == 0)
                _log.LogInfo($"Decor : liste {_updates} recue ({count} objets ; {fresh.Count} nouveaux, {gone.Count} retires)");
        }

        // Un objet : ses triangles, des deux côtés (on ne traverse jamais une surface, ni par dessous).
        private Piece Build(float[] data)
        {
            int triangles = data.Length / 9;
            if (triangles == 0) return null;
            var center = new Vector3(data[0], data[2], data[1]);
            var vertices = new Vector3[triangles * 3];
            var indices = new int[triangles * 6];
            var colors = new Color[vertices.Length];
            for (int t = 0; t < triangles; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int s = t * 9 + k * 3;
                    vertices[t * 3 + k] = new Vector3(data[s], data[s + 2], data[s + 1]) - center;
                    colors[t * 3 + k] = DecorColor;
                }
                int v = t * 3;
                indices[t * 6 + 0] = v; indices[t * 6 + 1] = v + 1; indices[t * 6 + 2] = v + 2;
                indices[t * 6 + 3] = v; indices[t * 6 + 4] = v + 2; indices[t * 6 + 5] = v + 1;
            }

            var mesh = new Mesh { name = "SkyrimRoR2_Decor" };
            if (vertices.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = indices;
            mesh.RecalculateBounds();

            var piece = new Piece { Mesh = mesh, Center = center };
            piece.Object = new GameObject("SkyrimRoR2_Decor");
            piece.Object.layer = LayerIndex.world.intVal;  // le calque du décor : collisions, tirs, grappins, mines
            piece.Object.AddComponent<MeshFilter>().sharedMesh = mesh;
            try
            {
                piece.Object.AddComponent<MeshCollider>().sharedMesh = mesh;
            }
            catch (Exception e)
            {
                _log.LogWarning("Decor : objet impossible a rendre solide (" + e.Message + ")");
            }
            MeshRenderer renderer = piece.Object.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            SetRenderer(piece);
            return piece;
        }

        private void SetRenderer(Piece piece)
        {
            MeshRenderer renderer = piece.Object != null ? piece.Object.GetComponent<MeshRenderer>() : null;
            if (renderer == null) return;
            renderer.sharedMaterial = _material;
            renderer.enabled = _visible && _material != null;
        }
    }
}
