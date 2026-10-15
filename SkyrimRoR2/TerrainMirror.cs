using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // Le relief de Skyrim recopié dans RoR2.
    //
    // Skyrim mesure la hauteur du sol autour du joueur (une grille de points, voir Terrain.cpp côté Skyrim)
    // et l'écrit dans un troisième pont. Ici, on en fait une surface invisible qui épouse le relief, sur le
    // calque du décor : le survivant marche dessus, ses mines s'y posent, son grappin s'y accroche, ses tirs
    // s'y arrêtent. Les murs de Skyrim deviennent des parois verticales.
    //
    // La surface est découpée en tuiles fixes dans le monde : quand une nouvelle grille arrive, seules les
    // tuiles qui ont changé sont reconstruites (reconstruire 200 m de surface d'un coup ferait des à-coups).
    //
    // Calage : le décalage entre le monde de Skyrim et celui de RoR2 reste FIXE (une surface qui bougeait
    // avec le survivant faisait reculer les murs quand le grappin tirait vers eux). On ne le recale que si
    // les deux jeux se sont vraiment écartés, survivant au sol.
    // Repères : Skyrim X = est, Y = nord, Z = hauteur ; RoR2 X = est, Y = hauteur, Z = nord.
    internal sealed class TerrainMirror
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        private const float StaleSeconds = 3f;       // plus de nouvelle grille depuis 3 s : on retire la surface
        private const float DecorDrift = 1.5f;       // avec le vrai décor : écart (m) toléré avant de replacer le survivant
        private const float DecorDriftTime = 0.5f;
        private const float StuckSpeed = 1.5f;       // m/s : en dessous, le survivant est considéré comme bloqué   // ...pendant au moins 0,4 s
        private float _apartSince = -1f;
        private const float HorizontalResync = 4f;   // écart (m) entre survivant et joueur Skyrim avant recalage
        private const float VerticalResync = 2.5f;
        private const float FallGap = 1.0f;          // joueur Skyrim à plus d'1 m du sol...
        private const float FallSpeed = -2f;         // ...qui descend (m/s) : il tombe
        private const float CliffHeight = 1.5f;      // au-delà de 1,5 m d'écart dans une case : marche verticale (mur)

        private sealed class Tile
        {
            public GameObject Object;
            public Mesh Mesh;
            public MeshCollider Collider;
            public MeshRenderer Renderer;
            public float WorldX, WorldY;   // coin sud-ouest (monde Skyrim, m)
            public int Hash;
            public uint SeenIn;            // dernière grille où la tuile existait
        }

        private readonly ManualLogSource _log;
        private readonly Dictionary<long, Tile> _tiles = new Dictionary<long, Tile>();
        private IntPtr _view;
        private bool _openTried;

        private uint _lastSequence;
        private uint _gridCount;
        private float _lastGridTime = -100f;
        private float[] _heights = new float[0];
        private int _rebuilt;

        private Vector3 _offset;                     // monde RoR2 = monde Skyrim (repères RoR2) + _offset
        private bool _offsetValid;
        private bool _offsetChanged;
        private int _resyncs;
        private bool _shown;

        // Pour vérifier le relief à l'oeil dans RoR2 : sols gris clair, murs gris foncé, un peu transparents.
        private static readonly Color FloorColor = new Color(0.8f, 0.8f, 0.8f, 0.45f);
        private static readonly Color WallColor = new Color(0.3f, 0.3f, 0.3f, 0.7f);
        private Material _material;
        private bool _visible;

        public TerrainMirror(ManualLogSource log)
        {
            _log = log;
            _decor = new DecorMirror(log);
        }

        // Vrai quand une surface récente est en place (le petit plateau plat est alors inutile).
        public bool Active => _active;

        // 0.66 : le relief et le décor recopiés de Skyrim, pour cacher le survivant filmé derrière (SurvivorStreamer).
        public void ForEachOccluder(Action<Mesh, Matrix4x4> visit)
        {
            if (!_active) return;
            foreach (Tile tile in _tiles.Values)
                if (tile.Object != null && tile.Mesh != null) visit(tile.Mesh, tile.Object.transform.localToWorldMatrix);
            _decor.ForEachOccluder(visit);
        }
        public bool DecorActive => _decor.Active;

        // Un point du monde de Skyrim (mètres : X est, Y nord, Z hauteur) dans le monde de RoR2.
        public bool TryMapSkyrim(Vector3 skyrim, out Vector3 ror2)
        {
            ror2 = Vector3.zero;
            if (!_active || !_offsetValid) return false;
            ror2 = new Vector3(skyrim.x, skyrim.z, skyrim.y) + _offset;
            return true;
        }

        // Un point du monde de RoR2 dans le monde de Skyrim (mètres : X est, Y nord, Z hauteur).
        public bool TryMapToSkyrim(Vector3 ror2, out Vector3 skyrim)
        {
            skyrim = Vector3.zero;
            if (!_active || !_offsetValid) return false;
            Vector3 d = ror2 - _offset;
            skyrim = new Vector3(d.x, d.z, d.y);
            return true;
        }

        // Les pieds du joueur Skyrim dans le monde de RoR2 (pour viser depuis ses yeux).
        public bool TryGetSkyrimFeet(out Vector3 feet)
        {
            feet = Vector3.zero;
            if (!_active || !_offsetValid || _view == IntPtr.Zero) return false;
            feet = new Vector3(ReadFloat(24), ReadFloat(32), ReadFloat(28)) + _offset;
            return true;
        }
        private bool _active;
        private readonly DecorMirror _decor;

        public void Update(CharacterBody survivor, bool enabled, float skyrimGroundGap, float skyrimVerticalSpeed, bool visible,
            float skyrimCeiling = -1f)
        {
            SetVisible(visible);
            if (!enabled || survivor == null || Run.instance == null || !Open())
            {
                UpdateCeiling(null, 0f, -1f);
                Show(false);
                _decor.SetShown(false);
                _active = false;
                _offsetValid = false;
                return;
            }

            // Où est le joueur dans Skyrim (en repères RoR2 : X = est, Y = hauteur, Z = nord).
            var player = new Vector3(ReadFloat(24), ReadFloat(32), ReadFloat(28));
            Vector3 feet = survivor.footPosition;
            UpdateOffset(survivor, player, feet, skyrimGroundGap, skyrimVerticalSpeed);

            // Le décor en vraies formes (s'il est envoyé), sinon l'ancienne grille de hauteurs.
            ReadGrid();
            _decor.Update(_offset, _offsetChanged, _visible, () => Material());
            bool decorOk = _decor.Active;
            bool gridOk = !decorOk && _tiles.Count > 0 && Time.time - _lastGridTime < StaleSeconds;
            UpdateCeiling(survivor, player.y + _offset.y, gridOk ? skyrimCeiling : -1f);  // les vraies formes ont déjà leurs plafonds

            if (!gridOk && !decorOk)
            {
                Show(false);
                _decor.SetShown(false);
                _active = false;
                _offsetValid = false;
                return;
            }

            if (_offsetChanged || !_shown)
                foreach (Tile tile in _tiles.Values) Place(tile);
            _offsetChanged = false;
            Show(gridOk);
            _decor.SetShown(decorOk);
            _active = true;
            SafetyNet(survivor, player + _offset, decorOk);
        }

        // Filet de sécurité : le survivant ne doit jamais se retrouver SOUS la surface (après un recalage,
        // une tuile refaite pile sous ses pieds...) : il tomberait dans le vide pour toujours.
        // Deux cas : la surface est au-dessus de ses pieds à l'endroit où il est (il est passé dessous),
        // ou il n'y a plus de surface sous lui et il est loin sous le joueur Skyrim. On le remet dessus.
        private float _nextSafetyCheck;
        private float _lostSince = -1f;
        private int _rescues;

        private void SafetyNet(CharacterBody survivor, Vector3 skyrimFeet, bool decor)
        {
            CharacterMotor motor = survivor.characterMotor;
            if (motor == null || motor.isGrounded || Time.time < _nextSafetyCheck) return;
            _nextSafetyCheck = Time.time + 0.2f;

            Vector3 feet = survivor.footPosition;
            Vector3? rescue = null;
            if (decor)
            {
                // Vraies formes : il peut y avoir des arches et des ponts au-dessus de lui, c'est normal.
                // On regarde seulement s'il reste une surface de Skyrim SOUS lui.
                bool below = false;
                foreach (RaycastHit hit in Physics.RaycastAll(feet + Vector3.up * 0.3f, Vector3.down, 300f,
                    LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                    if (hit.collider != null && hit.collider.gameObject.name.StartsWith("SkyrimRoR2_")) { below = true; break; }
                // Il faut que ça dure (1,5 s) : un grappin ou une ruée écartent un instant les deux jeux, c'est normal.
                if (!below || feet.y < skyrimFeet.y - 15f)
                {
                    if (_lostSince < 0f) _lostSince = Time.time;
                    if (Time.time - _lostSince >= 1.5f)
                    {
                        rescue = skyrimFeet + Vector3.up * 0.5f;
                        _lostSince = -1f;
                    }
                }
                else _lostSince = -1f;
            }
            else
            {
                const float above = 60f;
                RaycastHit[] hits = Physics.RaycastAll(feet + Vector3.up * above, Vector3.down, above + 200f,
                    LayerIndex.world.mask, QueryTriggerInteraction.Ignore);
                bool found = false;
                float top = float.NegativeInfinity;
                foreach (RaycastHit hit in hits)
                {
                    if (hit.collider == null || hit.collider.gameObject.name != "SkyrimRoR2_Relief" || hit.normal.y < 0.5f) continue;
                    found = true;
                    if (hit.point.y > top) top = hit.point.y;
                }
                if (found && top > feet.y + 1.0f)
                    rescue = new Vector3(feet.x, top + 0.3f, feet.z);                 // passé sous la surface
                else if (!found && feet.y < skyrimFeet.y - 10f)
                    rescue = skyrimFeet + Vector3.up * 0.5f;                          // plus de surface du tout sous lui
            }
            if (rescue == null) return;
            if (AutoRun.InAnyVehicle(survivor)) return;  // passager d'un véhicule (Voyage de la Chercheuse...) : il bouge avec lui

            SkyrimRoR2Plugin.MarkReplaced();
            TeleportHelper.TeleportBody(survivor, rescue.Value);
            motor.velocity = Vector3.zero;
            _rescues++;
            if (_rescues <= 10 || _rescues % 50 == 0)
                _log.LogWarning($"Relief : survivant passe sous le sol, remis dessus ({_rescues} fois)");
        }

        // 0.70 (coop) : le lieu du joueur dans Skyrim (posé par le plugin à chaque image), et la règle de calage de la
        // partie coop : (lieu, intérieur, calage naturel, pieds) -> calage commun à tous les joueurs de ce lieu
        // (null : pas encore connu, on garde le naturel). Null hors coop.
        public uint PlaceId;
        public bool PlaceInterior;
        public Func<uint, bool, Vector3, Vector3, Vector3?> CoopOffset;
        private uint _offsetPlace;

        private void ReplaceSurvivor(CharacterBody survivor, Vector3 at)
        {
            if (AutoRun.InAnyVehicle(survivor)) return;
            SkyrimRoR2Plugin.MarkReplaced();
            TeleportHelper.TeleportBody(survivor, at + Vector3.up * 0.1f);
            if (survivor.characterMotor != null) survivor.characterMotor.velocity = Vector3.zero;
        }

        private void UpdateOffset(CharacterBody survivor, Vector3 player, Vector3 feet, float skyrimGroundGap, float skyrimVerticalSpeed)
        {
            // Coop : nouveau lieu dans Skyrim, nouveau calage.
            if (CoopOffset != null && _offsetValid && PlaceId != _offsetPlace) _offsetValid = false;
            if (!_offsetValid)
            {
                Vector3 natural = feet - player;
                Vector3 chosen = natural;
                if (CoopOffset != null)
                {
                    Vector3? shared = CoopOffset(PlaceId, PlaceInterior, natural, feet);
                    if (shared.HasValue) chosen = shared.Value;
                }
                _offset = chosen;
                _offsetValid = true;
                _offsetChanged = true;
                _offsetPlace = PlaceId;
                if ((chosen - natural).sqrMagnitude > 0.01f)
                {
                    ReplaceSurvivor(survivor, player + chosen);
                    _log.LogInfo($"Coop : calage du lieu {PlaceId:X8} repris de la partie, survivant replace");
                }
                return;
            }
            if (CoopOffset != null)
            {
                Vector3? shared = CoopOffset(PlaceId, PlaceInterior, _offset, player + _offset);
                if (shared.HasValue && (shared.Value - _offset).sqrMagnitude > 0.0025f)
                {
                    _offset = shared.Value;
                    _offsetChanged = true;
                    ReplaceSurvivor(survivor, player + _offset);
                    _log.LogInfo($"Coop : nouveau calage commun pour le lieu {PlaceId:X8}, survivant replace");
                    return;
                }
            }
            CharacterMotor motor = survivor.characterMotor;
            bool grounded = motor == null || motor.isGrounded;
            Vector3 drift = feet - (player + _offset);

            // Avec le vrai décor, c'est Skyrim qui a raison : le décor ne bouge plus jamais. Si le survivant
            // s'écarte du joueur Skyrim (coincé dans un objet, poussé...), c'est lui qu'on remet à sa place.
            // (En coop, le calage est commun à tous : on ne le décale jamais, c'est le survivant qu'on remet en place.)
            if (_decor.Active || CoopOffset != null)
            {
                // Seulement si le survivant est (presque) immobile : c'est le signe qu'il est coincé.
                // Pendant un grappin, une ruée ou une spéciale, il va plus vite que le joueur Skyrim et
                // le remettre en arrière ferait des retours en arrière (rollback).
                float speed = motor != null ? motor.velocity.magnitude : 0f;
                bool far = new Vector2(drift.x, drift.z).magnitude > DecorDrift || Mathf.Abs(drift.y) > VerticalResync;
                // Ou bien : le joueur Skyrim est déjà posé au sol alors que le survivant vole encore loin de lui
                // (les deux jeux se sont décalés pendant un saut) : on le ramène aussi.
                bool skyrimLanded = skyrimGroundGap >= 0f && skyrimGroundGap < 0.15f && !grounded;
                // Passager d'un véhicule (Voyage, Sursis de la Chercheuse...) : sa propre vitesse vaut zéro, mais il
                // n'est pas coincé ; le remettre en place à chaque instant rendait ses déplacements saccadés.
                bool apart = far && (speed < StuckSpeed || skyrimLanded) && !AutoRun.InAnyVehicle(survivor);
                if (!apart) { _apartSince = -1f; return; }
                if (_apartSince < 0f) _apartSince = Time.time;
                if (Time.time - _apartSince < DecorDriftTime) return;
                _apartSince = -1f;
                SkyrimRoR2Plugin.MarkReplaced();
                TeleportHelper.TeleportBody(survivor, player + _offset + Vector3.up * 0.1f);
                if (motor != null) motor.velocity = Vector3.zero;
                _resyncs++;
                if (_resyncs <= 10 || _resyncs % 50 == 0)
                    _log.LogInfo($"Decor : survivant ecarte de {drift.magnitude:F1} m du joueur Skyrim (coince ?), remis a sa place ({_resyncs} fois)");
                return;
            }

            // À l'horizontale : seulement survivant au sol (pendant un grappin ou une ruée, les deux jeux
            // s'écartent un instant, c'est normal), et seulement pour un vrai écart.
            if (grounded && new Vector2(drift.x, drift.z).magnitude > HorizontalResync)
            {
                _offset.x += drift.x;
                _offset.z += drift.z;
                _offsetChanged = true;
                _resyncs++;
                if (_resyncs <= 5 || _resyncs % 50 == 0)
                    _log.LogInfo($"Relief : survivant et joueur Skyrim ecartes de {new Vector2(drift.x, drift.z).magnitude:F1} m, surface recalee ({_resyncs} fois)");
            }

            // En hauteur : la surface ne bouge plus pendant les sauts et les chutes (c'est le joueur Skyrim qui
            // suit le survivant, et ils atterrissent ensemble sur le même relief). Recalage seulement au sol.
            if (grounded && Mathf.Abs(drift.y) > VerticalResync)
            {
                _offset.y += drift.y;
                _offsetChanged = true;
            }
        }

        // Le plafond de Skyrim : une plaque invisible (calque du décor) au-dessus de la tête du survivant,
        // à la hauteur du plafond mesuré dans Skyrim. Le survivant propulsé vers le haut s'y cogne, comme
        // le joueur Skyrim, au lieu de traverser et de laisser le joueur Skyrim collé au plafond.
        private GameObject _ceiling;
        private bool _ceilingLogged;

        private void UpdateCeiling(CharacterBody survivor, float skyrimFeetY, float ceilingHeight)
        {
            bool wanted = survivor != null && ceilingHeight > 0f;
            float ceilingY = skyrimFeetY + ceilingHeight;
            if (wanted)
            {
                // Seulement si le plafond est bien au-dessus de la tête du survivant (sinon il serait dedans).
                float head = survivor.footPosition.y + Mathf.Max(1f, (survivor.corePosition.y - survivor.footPosition.y) * 2f);
                wanted = ceilingY > head + 0.05f;
            }
            if (!wanted)
            {
                if (_ceiling != null) _ceiling.SetActive(false);
                return;
            }
            if (_ceiling == null)
            {
                _ceiling = new GameObject("SkyrimRoR2_Plafond");
                _ceiling.layer = LayerIndex.world.intVal;
                BoxCollider box = _ceiling.AddComponent<BoxCollider>();
                box.size = new Vector3(6f, 1f, 6f);
                UnityEngine.Object.DontDestroyOnLoad(_ceiling);
            }
            Vector3 at = survivor.footPosition;
            _ceiling.transform.position = new Vector3(at.x, ceilingY + 0.5f, at.z);  // le dessous de la plaque = le plafond
            if (!_ceiling.activeSelf) _ceiling.SetActive(true);
            if (!_ceilingLogged)
            {
                _ceilingLogged = true;
                _log.LogInfo($"Relief : plafond de Skyrim recopie ({ceilingHeight:F1} m au-dessus du joueur)");
            }
        }

        private float ReadFloat(int offset) => Marshal.PtrToStructure<float>(IntPtr.Add(_view, offset));

        private bool Open()
        {
            if (_view != IntPtr.Zero) return true;
            if (_openTried) return false;
            _openTried = true;
            IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.ReliefMapSize, BridgeInfo.ReliefMapName);
            if (mapping != IntPtr.Zero) _view = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.ReliefMapSize);
            if (_view == IntPtr.Zero) _log.LogWarning("Relief : pont impossible a ouvrir");
            return _view != IntPtr.Zero;
        }

        // Lit une nouvelle grille complète (numéro pair, inchangé pendant la copie), puis met les tuiles à jour.
        private void ReadGrid()
        {
            if ((uint)Marshal.ReadInt32(_view, 0) != BridgeInfo.ReliefMagic) return;
            uint sequence = (uint)Marshal.ReadInt32(_view, 4);
            if (sequence == _lastSequence || (sequence & 1) != 0) return;

            int side = Marshal.ReadInt32(_view, 8);
            int tileCells = Marshal.ReadInt32(_view, 40);
            if (side < 2 || side > BridgeInfo.ReliefMaxSide || tileCells < 1 || (side - 1) % tileCells != 0) return;
            float spacing = ReadFloat(12);
            float originX = ReadFloat(16);
            float originY = ReadFloat(20);
            int count = side * side;
            if (_heights.Length != count) _heights = new float[count];
            Marshal.Copy(IntPtr.Add(_view, BridgeInfo.ReliefHeaderSize), _heights, 0, count);
            if ((uint)Marshal.ReadInt32(_view, 4) != sequence) return;  // réécrite pendant la copie : on attend la suivante

            _lastSequence = sequence;
            _lastGridTime = Time.time;
            _gridCount++;

            float tileSize = tileCells * spacing;
            int tilesPerSide = (side - 1) / tileCells;
            int changed = 0;
            for (int ty = 0; ty < tilesPerSide; ty++)
                for (int tx = 0; tx < tilesPerSide; tx++)
                {
                    float worldX = originX + tx * tileSize;
                    float worldY = originY + ty * tileSize;
                    long key = ((long)Mathf.RoundToInt(worldX / tileSize) << 32) ^ (uint)Mathf.RoundToInt(worldY / tileSize);
                    int hash = TileHash(side, tx * tileCells, ty * tileCells, tileCells);
                    if (!_tiles.TryGetValue(key, out Tile tile))
                    {
                        tile = CreateTile(worldX, worldY);
                        _tiles[key] = tile;
                        tile.Hash = hash + 1;  // force la construction
                    }
                    tile.SeenIn = _gridCount;
                    if (tile.Hash != hash)
                    {
                        tile.Hash = hash;
                        BuildTile(tile, side, tx * tileCells, ty * tileCells, tileCells, spacing);
                        Place(tile);
                        changed++;
                    }
                }

            // Les tuiles qui ne sont plus dans la grille (le joueur s'est éloigné) sont détruites.
            var gone = new List<long>();
            foreach (KeyValuePair<long, Tile> pair in _tiles)
                if (pair.Value.SeenIn != _gridCount) gone.Add(pair.Key);
            foreach (long key in gone)
            {
                UnityEngine.Object.Destroy(_tiles[key].Object);
                UnityEngine.Object.Destroy(_tiles[key].Mesh);
                _tiles.Remove(key);
            }

            _rebuilt += changed;
            if (_gridCount == 1 || _gridCount % 100 == 0)
                _log.LogInfo($"Relief : grille {_gridCount} ({side} x {side} points, {_tiles.Count} tuiles, {changed} refaites ; {_rebuilt} en tout)");
        }

        // Empreinte des hauteurs d'une tuile (au centimètre) : si elle ne change pas, la tuile non plus.
        private int TileHash(int side, int column0, int row0, int cells)
        {
            unchecked
            {
                int hash = 17;
                for (int row = row0; row <= row0 + cells; row++)
                    for (int column = column0; column <= column0 + cells; column++)
                        hash = hash * 31 + Mathf.RoundToInt(_heights[row * side + column] * 100f);
                return hash;
            }
        }

        private Tile CreateTile(float worldX, float worldY)
        {
            var tile = new Tile { WorldX = worldX, WorldY = worldY };
            tile.Object = new GameObject("SkyrimRoR2_Relief");
            tile.Object.layer = LayerIndex.world.intVal;  // le calque du décor : collisions, tirs, grappins, mines
            tile.Object.SetActive(_shown);
            tile.Mesh = new Mesh { name = "SkyrimRoR2_Relief" };
            tile.Mesh.MarkDynamic();
            tile.Collider = tile.Object.AddComponent<MeshCollider>();
            tile.Object.AddComponent<MeshFilter>().sharedMesh = tile.Mesh;
            tile.Renderer = tile.Object.AddComponent<MeshRenderer>();
            Material material = null;
            try { material = Material(); } catch (Exception) { }  // l'affichage ne doit jamais empêcher la collision
            tile.Renderer.sharedMaterial = material;
            tile.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tile.Renderer.receiveShadows = false;
            tile.Renderer.enabled = _visible && tile.Renderer.sharedMaterial != null;
            return tile;
        }

        // Un matériau simple pour voir la surface (gris, un peu transparent si possible).
        // Attention : sous RoR2 1.5, Shader.Find lève une erreur pour un nom inconnu (au lieu de rendre null),
        // ce qui empêchait toute la surface d'être créée. Chaque essai est donc protégé, et en dernier
        // recours on prend le matériau par défaut de Unity (celui d'un cube).
        private bool _materialTried;

        private Material Material()
        {
            if (_material != null || _materialTried) return _material;
            _materialTried = true;
            foreach (string name in new[] { "Sprites/Default", "Unlit/Color", "Legacy Shaders/Transparent/Diffuse", "Standard" })
            {
                Shader shader = null;
                try { shader = Shader.Find(name); } catch (Exception) { }
                if (shader == null) continue;
                _material = new Material(shader) { name = "SkyrimRoR2_Relief", color = Color.white, renderQueue = 3000 };
                _log.LogInfo("Relief : affichage avec le shader " + name);
                return _material;
            }
            try
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Material fallback = cube.GetComponent<MeshRenderer>().sharedMaterial;
                if (fallback != null) _material = new Material(fallback) { name = "SkyrimRoR2_Relief", color = new Color(0.6f, 0.6f, 0.6f, 1f) };
                UnityEngine.Object.DestroyImmediate(cube);
            }
            catch (Exception) { }
            if (_material != null) _log.LogInfo("Relief : affichage avec le materiau par defaut de Unity (gris)");
            else _log.LogWarning("Relief : aucun materiau pour afficher la surface (elle marche quand meme, invisible)");
            return _material;
        }

        private void SetVisible(bool visible)
        {
            if (_visible == visible) return;
            _visible = visible;
            foreach (Tile tile in _tiles.Values)
                if (tile.Renderer != null) tile.Renderer.enabled = visible && tile.Renderer.sharedMaterial != null;
            _log.LogInfo(visible ? "Relief : surface affichee (verification)" : "Relief : surface invisible");
        }

        private void Place(Tile tile)
        {
            tile.Object.transform.position = new Vector3(tile.WorldX + _offset.x, _offset.y, tile.WorldY + _offset.z);
        }

        // Pour chaque case : si ses 4 coins sont à peu près au même niveau, une pente douce (2 triangles) ;
        // sinon, 4 petits paliers à la hauteur de chaque coin, séparés par des parois verticales.
        // Un mur de Skyrim devient ainsi un vrai mur, qu'on ne peut pas escalader.
        private void BuildTile(Tile tile, int side, int column0, int row0, int cells, float spacing)
        {
            float half = spacing * 0.5f;
            var vertices = new List<Vector3>(cells * cells * 6);
            var triangles = new List<int>(cells * cells * 12);
            var colors = new List<Color>(cells * cells * 6);
            for (int r = 0; r < cells; r++)
                for (int c = 0; c < cells; c++)
                {
                    int row = row0 + r, column = column0 + c;
                    float x0 = c * spacing, z0 = r * spacing;
                    float ha = _heights[row * side + column];            // sud-ouest
                    float hb = _heights[row * side + column + 1];        // sud-est
                    float hc = _heights[(row + 1) * side + column];      // nord-ouest
                    float hd = _heights[(row + 1) * side + column + 1];  // nord-est
                    float low = Mathf.Min(Mathf.Min(ha, hb), Mathf.Min(hc, hd));
                    float high = Mathf.Max(Mathf.Max(ha, hb), Mathf.Max(hc, hd));

                    if (high - low <= CliffHeight)
                    {
                        AddFloor(vertices, triangles, x0, z0, x0 + spacing, z0 + spacing, ha, hb, hc, hd);
                        while (colors.Count < vertices.Count) colors.Add(FloorColor);
                        continue;
                    }
                    float xm = x0 + half, zm = z0 + half, x1 = x0 + spacing, z1 = z0 + spacing;
                    AddFloor(vertices, triangles, x0, z0, xm, zm, ha, ha, ha, ha);  // palier sud-ouest
                    AddFloor(vertices, triangles, xm, z0, x1, zm, hb, hb, hb, hb);  // sud-est
                    AddFloor(vertices, triangles, x0, zm, xm, z1, hc, hc, hc, hc);  // nord-ouest
                    AddFloor(vertices, triangles, xm, zm, x1, z1, hd, hd, hd, hd);  // nord-est
                    while (colors.Count < vertices.Count) colors.Add(FloorColor);
                    AddWall(vertices, triangles, new Vector3(xm, 0f, z0), new Vector3(xm, 0f, zm), ha, hb);
                    AddWall(vertices, triangles, new Vector3(xm, 0f, zm), new Vector3(xm, 0f, z1), hc, hd);
                    AddWall(vertices, triangles, new Vector3(x0, 0f, zm), new Vector3(xm, 0f, zm), ha, hc);
                    AddWall(vertices, triangles, new Vector3(xm, 0f, zm), new Vector3(x1, 0f, zm), hb, hd);
                    while (colors.Count < vertices.Count) colors.Add(WallColor);
                }

            tile.Mesh.Clear();
            tile.Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            tile.Mesh.SetVertices(vertices);
            tile.Mesh.SetTriangles(triangles, 0);
            tile.Mesh.SetColors(colors);
            tile.Mesh.RecalculateBounds();
            tile.Collider.sharedMesh = null;  // force la mise à jour de la collision
            tile.Collider.sharedMesh = tile.Mesh;
        }

        // Un morceau de sol (rectangle, une hauteur par coin), face vers le haut.
        private static void AddFloor(List<Vector3> vertices, List<int> triangles,
            float x0, float z0, float x1, float z1, float sw, float se, float nw, float ne)
        {
            int i = vertices.Count;
            vertices.Add(new Vector3(x0, sw, z0));
            vertices.Add(new Vector3(x1, se, z0));
            vertices.Add(new Vector3(x0, nw, z1));
            vertices.Add(new Vector3(x1, ne, z1));
            triangles.Add(i); triangles.Add(i + 2); triangles.Add(i + 1);
            triangles.Add(i + 1); triangles.Add(i + 2); triangles.Add(i + 3);
        }

        // Une paroi verticale entre deux paliers (des deux côtés, pour bloquer dans les deux sens).
        private static void AddWall(List<Vector3> vertices, List<int> triangles, Vector3 from, Vector3 to, float h1, float h2)
        {
            float low = Mathf.Min(h1, h2), high = Mathf.Max(h1, h2);
            if (high - low < 0.05f) return;
            int i = vertices.Count;
            vertices.Add(new Vector3(from.x, low, from.z));
            vertices.Add(new Vector3(to.x, low, to.z));
            vertices.Add(new Vector3(from.x, high, from.z));
            vertices.Add(new Vector3(to.x, high, to.z));
            triangles.Add(i); triangles.Add(i + 2); triangles.Add(i + 1);
            triangles.Add(i + 1); triangles.Add(i + 2); triangles.Add(i + 3);
            triangles.Add(i); triangles.Add(i + 1); triangles.Add(i + 2);
            triangles.Add(i + 1); triangles.Add(i + 3); triangles.Add(i + 2);
        }

        private void Show(bool visible)
        {
            if (_shown == visible) return;
            _shown = visible;
            foreach (Tile tile in _tiles.Values)
                if (tile.Object != null) tile.Object.SetActive(visible);
            if (visible) _log.LogInfo("Relief : surface de Skyrim en place dans RoR2");
        }
    }
}
