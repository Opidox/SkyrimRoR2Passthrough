using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using RoR2;
using RoR2.CharacterAI;
using RoR2.Navigation;
using UnityEngine;

namespace SkyrimRoR2
{
    // Les chemins des alliés (tourelles mobiles de l'Ingénieur, drones, alliés achetés...) dans le décor de Skyrim.
    //
    // Les IA de RoR2 se déplacent avec une "carte des chemins" : des points où l'on peut se tenir, reliés quand
    // on peut aller de l'un à l'autre. Celle du niveau RoR2 ne correspond plus à rien (on joue dans Skyrim).
    // On en refait donc une à partir du décor de Skyrim recopié (DecorMirror) : une grille de points posés sur
    // les sols (plusieurs étages possibles : ponts, toits, sous les arches), reliés quand le passage est libre.
    // Une seconde carte, quelques mètres plus haut, sert aux drones volants. Elles sont refaites quand le joueur
    // s'éloigne, ou de temps en temps (le décor autour change).
    internal sealed class NavMirror
    {
        private const float Spacing = 3f;             // mètres entre deux points (dehors)
        private const float IndoorSpacing = 1.5f;     // en intérieur (donjons) : couloirs et portes étroits
        private const float Radius = 60f;             // autour du survivant (dehors)
        private const float IndoorRadius = 40f;       // en intérieur
        private const float StepHeight = 2.2f;        // marche franchissable entre deux points voisins
        private const float AirHeight = 6f;           // hauteur des points des drones (sans plafond)
        private const float RebuildDistance = 20f;    // refaite si le survivant s'est éloigné de ça...
        private const float IndoorRebuildDistance = 12f;
        private const float RebuildEvery = 15f;       // ...ou au plus tard toutes les 15 s
        private const float MinInterval = 3f;
        private const int MaxNodes = 6000;

        // Alliés coincés : loin du survivant et sans se rapprocher pendant un moment, ils sont ramenés près de lui.
        private const float LeashDistance = 30f;
        private const float LeashSeconds = 6f;
        private const float LeashProgress = 3f;       // mètres de rapprochement qui comptent comme "il avance"

        private sealed class Follower
        {
            public float BestDistance;
            public float Since;
        }
        private readonly Dictionary<CharacterBody, Follower> _followers = new Dictionary<CharacterBody, Follower>();
        private readonly List<CharacterBody> _gone = new List<CharacterBody>();
        private float _lastLeashCheck;
        private bool _indoor;

        private readonly ManualLogSource _log;
        private SceneInfo _scene;
        private Vector3 _lastCenter;
        private float _lastBuild = -100f;
        private bool _built;
        private int _builds;
        private bool _described;

        public NavMirror(ManualLogSource log)
        {
            _log = log;
        }

        public void Update(CharacterBody survivor, bool decorActive)
        {
            if (!decorActive || survivor == null || SceneInfo.instance == null) return;
            try { Leash(survivor); }
            catch (Exception e) { _log.LogWarning("Allies coinces : " + e.Message); }
            if (SceneInfo.instance != _scene)
            {
                _scene = SceneInfo.instance;
                _built = false;
            }
            Vector3 center = survivor.footPosition;
            float since = Time.time - _lastBuild;
            if (since < MinInterval) return;
            float rebuild = _indoor ? IndoorRebuildDistance : RebuildDistance;
            if (_built && since < RebuildEvery && (center - _lastCenter).sqrMagnitude < rebuild * rebuild) return;
            _lastBuild = Time.time;
            _lastCenter = center;
            try
            {
                Build(center);
                _built = true;
            }
            catch (Exception e)
            {
                _log.LogWarning("Chemins : impossible de refaire la carte des chemins (" + e.GetType().Name + " : " + e.Message + ")");
            }
        }

        private static bool Ours(Collider collider) => collider != null && collider.gameObject.name.StartsWith("SkyrimRoR2_");

        private void Build(Vector3 center)
        {
            int mask = LayerIndex.world.mask;
            const QueryTriggerInteraction ignore = QueryTriggerInteraction.Ignore;

            // 0. Intérieur (donjon) ? Un plafond au-dessus du survivant et autour de lui.
            bool wasIndoor = _indoor;
            _indoor = IsIndoor(center, mask);
            if (_indoor != wasIndoor) _log.LogInfo("Chemins : " + (_indoor ? "interieur (grille fine, drones sous le plafond)" : "exterieur"));
            float spacing = _indoor ? IndoorSpacing : Spacing;
            float radius = _indoor ? IndoorRadius : Radius;

            // 1. Les points : sur chaque colonne de la grille, chaque sol (étages compris) avec de la place au-dessus.
            var ground = new List<Vector3>();
            var golemOk = new List<bool>();
            var columns = new Dictionary<long, List<int>>();
            var columnOf = new List<long>();
            int reach = Mathf.CeilToInt(radius / spacing);
            int cx = Mathf.RoundToInt(center.x / spacing), cz = Mathf.RoundToInt(center.z / spacing);
            for (int ix = -reach; ix <= reach && ground.Count < MaxNodes; ix++)
                for (int iz = -reach; iz <= reach && ground.Count < MaxNodes; iz++)
                {
                    if (ix * ix + iz * iz > reach * reach) continue;
                    int gx = cx + ix, gz = cz + iz;
                    float x = gx * spacing, z = gz * spacing;
                    RaycastHit[] hits = Physics.RaycastAll(new Vector3(x, center.y + 60f, z), Vector3.down, 120f, mask, ignore);
                    Array.Sort(hits, (a, b) => b.point.y.CompareTo(a.point.y));
                    long key = Key(gx, gz);
                    foreach (RaycastHit hit in hits)
                    {
                        if (!Ours(hit.collider) || hit.normal.y < 0.6f) continue;  // pas un sol praticable (mur, pente raide)
                        Vector3 p = hit.point;
                        if (columns.TryGetValue(key, out List<int> same) && same.Exists(i => Mathf.Abs(ground[i].y - p.y) < 1.5f)) continue;
                        if (Physics.CheckCapsule(p + Vector3.up * 0.9f, p + Vector3.up * 1.7f, 0.4f, mask, ignore)) continue;  // pas la place
                        if (same == null) columns[key] = same = new List<int>();
                        same.Add(ground.Count);
                        ground.Add(p);
                        columnOf.Add(key);
                        golemOk.Add(!Physics.CheckCapsule(p + Vector3.up * 2.0f, p + Vector3.up * 4.0f, 1.4f, mask, ignore));
                    }
                }

            // 2. Les liaisons au sol : voisins de grille, marche raisonnable, rien entre les deux, du sol au milieu.
            var groundLinks = new List<int>[ground.Count];
            for (int i = 0; i < ground.Count; i++)
            {
                groundLinks[i] = new List<int>();
                Unpack(columnOf[i], out int gx, out int gz);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        if (!columns.TryGetValue(Key(gx + dx, gz + dz), out List<int> others)) continue;
                        foreach (int j in others)
                        {
                            Vector3 a = ground[i], b = ground[j];
                            if (Mathf.Abs(a.y - b.y) > StepHeight) continue;
                            if (Physics.Linecast(a + Vector3.up * 1.0f, b + Vector3.up * 1.0f, mask, ignore)) continue;
                            if (Physics.Linecast(a + Vector3.up * 0.5f, b + Vector3.up * 0.5f, mask, ignore) &&
                                Mathf.Abs(a.y - b.y) < 0.4f) continue;  // obstacle bas (muret, caisse) sur du plat
                            Vector3 middle = (a + b) * 0.5f;
                            if (!Physics.Raycast(middle + Vector3.up * 1.5f, Vector3.down, out RaycastHit under, Mathf.Abs(a.y - b.y) + 3f, mask, ignore)) continue;
                            if (under.point.y < Mathf.Min(a.y, b.y) - 1.0f) continue;  // un trou au milieu (on tomberait d'un étage)
                            Vector3 floor = new Vector3(middle.x, Mathf.Max(under.point.y, Mathf.Min(a.y, b.y)), middle.z);
                            if (Physics.CheckCapsule(floor + Vector3.up * 0.9f, floor + Vector3.up * 1.5f, 0.3f, mask, ignore)) continue;  // passage trop bas ou trop étroit
                            groundLinks[i].Add(j);
                        }
                    }
            }

            // 3. Les points des drones : au-dessus des points au sol, là où il y a de la place.
            var air = new List<Vector3>();
            var airColumns = new Dictionary<long, List<int>>();
            var airColumnOf = new List<long>();
            for (int i = 0; i < ground.Count; i++)
            {
                // Sous un plafond (donjon, grotte, maison) : à mi-hauteur entre le sol et le plafond.
                float height = AirHeight;
                if (Physics.Raycast(ground[i] + Vector3.up * 0.5f, Vector3.up, out RaycastHit roof, AirHeight + 1.5f, mask, ignore))
                {
                    float room = roof.point.y - ground[i].y;
                    if (room < 2.2f) continue;  // trop bas pour un drone
                    height = Mathf.Clamp(room * 0.55f, 1.2f, room - 1.0f);
                }
                Vector3 p = ground[i] + Vector3.up * height;
                if (Physics.CheckSphere(p, _indoor ? 0.6f : 1.0f, mask, ignore)) continue;
                long key = columnOf[i];
                if (airColumns.TryGetValue(key, out List<int> same) && same.Exists(k => Mathf.Abs(air[k].y - p.y) < 2f)) continue;
                if (same == null) airColumns[key] = same = new List<int>();
                same.Add(air.Count);
                air.Add(p);
                airColumnOf.Add(key);
            }
            var airLinks = new List<int>[air.Count];
            for (int i = 0; i < air.Count; i++)
            {
                airLinks[i] = new List<int>();
                Unpack(airColumnOf[i], out int gx, out int gz);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        if (!airColumns.TryGetValue(Key(gx + dx, gz + dz), out List<int> others)) continue;
                        foreach (int j in others)
                            if (Mathf.Abs(air[i].y - air[j].y) < 4f && !Physics.Linecast(air[i], air[j], mask, ignore)) airLinks[i].Add(j);
                    }
            }

            bool groundSet = Install(MapNodeGroup.GraphType.Ground, ground, groundLinks, golemOk, true);
            bool airSet = Install(MapNodeGroup.GraphType.Air, air, airLinks, null, false);
            ResetAgents();

            _builds++;
            if (_builds <= 3 || _builds % 20 == 0)
            {
                int groundCount = 0, airCount = 0;
                foreach (List<int> list in groundLinks) groundCount += list.Count;
                foreach (List<int> list in airLinks) airCount += list.Count;
                _log.LogInfo($"Chemins : carte refaite{(_indoor ? " en interieur" : "")} ({ground.Count} points au sol, {groundCount} liaisons ; {air.Count} points en l'air, " +
                             $"{airCount} liaisons){(groundSet ? "" : " - carte au sol non remplacee")}{(airSet ? "" : " - carte des drones non remplacee")}");
            }
        }

        // Intérieur : un plafond au-dessus du survivant et de la plupart des points autour de lui.
        private static bool IsIndoor(Vector3 center, int mask)
        {
            int covered = 0;
            Vector3[] probes = { Vector3.zero, new Vector3(4f, 0f, 0f), new Vector3(-4f, 0f, 0f), new Vector3(0f, 0f, 4f), new Vector3(0f, 0f, -4f) };
            foreach (Vector3 offset in probes)
            {
                if (Physics.Raycast(center + offset + Vector3.up * 0.5f, Vector3.up, out RaycastHit hit, 25f, mask, QueryTriggerInteraction.Ignore) && Ours(hit.collider))
                    covered++;
            }
            return covered >= 4;
        }

        // Les alliés qui marchent ou volent (drones, tourelles mobiles...), loin du survivant et qui ne se rapprochent
        // plus : ramenés juste derrière lui. Les tourelles fixes ne bougent jamais.
        private void Leash(CharacterBody survivor)
        {
            if (Time.time - _lastLeashCheck < 1f) return;
            _lastLeashCheck = Time.time;
            CharacterMaster owner = survivor.master;
            if (owner == null) return;

            foreach (CharacterBody body in _followers.Keys) if (body == null) _gone.Add(body);
            foreach (CharacterBody body in _gone) _followers.Remove(body);
            _gone.Clear();

            foreach (CharacterMaster master in CharacterMaster.readOnlyInstancesList)
            {
                if (master == null || master == owner) continue;
                MinionOwnership minion = master.minionOwnership;
                if (minion == null || minion.ownerMaster != owner) continue;
                CharacterBody body = master.GetBody();
                if (body == null || body.healthComponent == null || !body.healthComponent.alive) continue;
                if (body.baseMoveSpeed < 0.5f) continue;  // tourelle fixe

                float distance = Vector3.Distance(body.corePosition, survivor.corePosition);
                if (!_followers.TryGetValue(body, out Follower follower))
                {
                    _followers[body] = follower = new Follower { BestDistance = distance, Since = Time.time };
                }
                if (distance < LeashDistance || distance < follower.BestDistance - LeashProgress)
                {
                    follower.BestDistance = distance;
                    follower.Since = Time.time;
                    continue;
                }
                if (Time.time - follower.Since < LeashSeconds) continue;

                // Juste derrière le survivant, sur le sol (un drone un peu plus haut, sous le plafond).
                Vector3 back = survivor.inputBank != null ? -survivor.inputBank.aimDirection : -survivor.transform.forward;
                back.y = 0f;
                back = back.sqrMagnitude > 0.01f ? back.normalized : Vector3.back;
                Vector3 spot = survivor.footPosition + back * 2.5f;
                if (Physics.Linecast(survivor.corePosition, spot + Vector3.up, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                    spot = survivor.footPosition;  // un mur derrière lui : à ses pieds
                bool flying = body.characterMotor == null;
                if (flying)
                {
                    float lift = 3f;
                    if (Physics.Raycast(spot + Vector3.up * 0.5f, Vector3.up, out RaycastHit roof, 6f, LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                        lift = Mathf.Clamp((roof.point.y - spot.y) * 0.5f, 1f, 3f);
                    spot += Vector3.up * lift;
                }
                TeleportHelper.TeleportBody(body, spot);
                follower.BestDistance = 0f;
                follower.Since = Time.time;
                _log.LogInfo($"Allie coince ramene pres du survivant : {body.name} (il etait a {distance:F0} m)");
                foreach (BaseAI ai in master.GetComponents<BaseAI>())
                {
                    object agent = typeof(BaseAI).GetProperty("broadNavigationAgent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ai)
                                   ?? typeof(BaseAI).GetField("broadNavigationAgent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ai);
                    try { agent?.GetType().GetMethod("InvalidatePath", Type.EmptyTypes)?.Invoke(agent, null); } catch (Exception) { }
                }
            }
        }

        private static long Key(int gx, int gz) => ((long)gx << 32) ^ (uint)gz;

        private static void Unpack(long key, out int gx, out int gz)
        {
            gx = (int)(key >> 32);
            gz = (int)(uint)(key & 0xFFFFFFFF);
        }

        // Remplit la carte des chemins du niveau avec nos points et nos liaisons.
        private bool Install(MapNodeGroup.GraphType type, List<Vector3> points, List<int>[] links, List<bool> golemOk, bool walking)
        {
            NodeGraph graph = SceneInfo.instance.GetNodeGraph(type);
            if (graph == null) return false;
            Describe(graph);

            int count = points.Count;
            var lineOfSight = new SerializableBitArray(count);
            for (int i = 0; i < count; i++) lineOfSight[i] = true;

            var nodes = new NodeGraph.Node[count];
            var allLinks = new List<NodeGraph.Link>();
            for (int i = 0; i < count; i++)
            {
                int start = allLinks.Count;
                foreach (int j in links[i])
                {
                    Vector3 a = points[i], b = points[j];
                    float flat = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                    int hulls = (int)HullMask.Human;
                    if (!walking || (golemOk[i] && golemOk[j])) hulls |= (int)HullMask.Golem;
                    allLinks.Add(new NodeGraph.Link
                    {
                        nodeIndexA = new NodeGraph.NodeIndex(i),
                        nodeIndexB = new NodeGraph.NodeIndex(j),
                        distanceScore = Vector3.Distance(a, b),
                        maxSlope = walking ? Mathf.Atan2(Mathf.Abs(b.y - a.y), Mathf.Max(flat, 0.01f)) * Mathf.Rad2Deg : 0f,
                        minJumpHeight = 0f,
                        hullMask = hulls,
                        jumpHullMask = 0,
                        gateIndex = 0
                    });
                }
                nodes[i] = new NodeGraph.Node
                {
                    position = points[i],
                    linkListIndex = new NodeGraph.LinkListIndex { index = start, size = (uint)(allLinks.Count - start) },
                    forbiddenHulls = HullMask.None,
                    flags = NodeFlags.None,
                    lineOfSightMask = lineOfSight,
                    gateIndex = 0
                };
            }

            FieldInfo nodesField = typeof(NodeGraph).GetField("nodes", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo linksField = typeof(NodeGraph).GetField("links", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (nodesField == null || linksField == null) return false;
            nodesField.SetValue(graph, nodes);
            linksField.SetValue(graph, allLinks.ToArray());

            // Recalcule ce que la carte garde de côté (index de recherche des points proches...).
            foreach (string name in new[] { "OnNodeCountChanged", "RebuildBlockMap" })
            {
                MethodInfo method = typeof(NodeGraph).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, Type.EmptyTypes, null);
                try { method?.Invoke(graph, null); } catch (Exception) { }
            }
            return true;
        }

        // Les IA qui suivaient un chemin de l'ancienne carte en redemandent un.
        private void ResetAgents()
        {
            foreach (CharacterMaster master in CharacterMaster.readOnlyInstancesList)
            {
                if (master == null) continue;
                foreach (BaseAI ai in master.GetComponents<BaseAI>())
                {
                    if (ai == null) continue;
                    object agent = typeof(BaseAI).GetProperty("broadNavigationAgent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ai)
                                   ?? typeof(BaseAI).GetField("broadNavigationAgent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ai);
                    if (agent == null) continue;
                    try { agent.GetType().GetMethod("InvalidatePath", Type.EmptyTypes)?.Invoke(agent, null); } catch (Exception) { }
                }
            }
        }

        // Une fois : ce que contient la carte des chemins de cette version de RoR2 (pour le journal).
        private void Describe(NodeGraph graph)
        {
            if (_described) return;
            _described = true;
            var names = new List<string>();
            foreach (MethodInfo method in typeof(NodeGraph).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (method.GetParameters().Length == 0) names.Add(method.Name);
            _log.LogInfo("Chemins : carte des chemins trouvee (" + graph.name + ") ; fonctions internes : " + string.Join(", ", names));
        }
    }
}
