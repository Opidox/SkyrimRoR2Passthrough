using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace SkyrimRoR2
{
    // Convertit le modèle 3D d'un allié de RoR2 (drone, tourelle...) en modèle Skyrim : un fichier .nif
    // (format des modèles de Skyrim Special Edition) et ses textures .dds, dans
    // %LOCALAPPDATA%\SkyrimRoR2\modeles. Skyrim les recopie dans Data\meshes et Data\textures\SkyrimRoR2,
    // puis affiche le vrai modèle dans le monde, éclairé et caché par le décor comme n'importe quel objet.
    //
    // Le modèle est figé dans la pose où il est au moment de la conversion (pas encore d'animation).
    // Repères : Unity X droite, Y haut, Z avant ; Skyrim X est, Y nord, Z haut ; 1 m = 70 unités Skyrim.
    internal static class ModelExporter
    {
        private const float UnitsPerMeter = 70f;
        private const int MaxVertices = 60000;   // un morceau de modèle Skyrim : 65535 sommets et triangles au plus

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimRoR2", "modeles");

        private sealed class Part
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int> Triangles = new List<int>();
            public string Texture;
            public string Glow;   // texture des parties lumineuses (null : aucune)
            public bool Effect;   // matière d'effet de RoR2 : shader d'effet de Skyrim (transparent, sans éclairage)
            public bool Additive; // mélange additif (lueurs), sinon transparence normale
            public float Alpha = 1f;  // opacité de base (sprites : plusieurs niveaux)
        }

        public const int MaxBones = 128;  // pièces animées au plus par modèle (0.73.4 : 64 -> 128)
        private const float GiantSize = 50f;  // mètres : au-delà, un morceau est une zone d'effet, pas le modèle

        // Version de la conversion : à monter quand la conversion change, pour que les anciens fichiers soient refaits.
        public const int FormatVersion = 6;  // 3 : matières d'effet (shader d'effet de Skyrim) ; 4 : textures sans _MainTex (glace) ; 5 : lumière minimum (0.66) ; 6 : couleurs des effets et des particules 3D (0.73)
        private const int SpriteVersion = 4; // 0.73.1 : on garde les sprites déjà convertis (les refaire tous les rendait invisibles un lancement entier)

        // 0.66 : lumière minimum des modèles (0 à 1). Skyrim n'éclaire pas par ses lampes les objets ajoutés par le
        // mod (autels, imprimantes, drones, alliés) : dans un donjon sombre, ils sortaient presque noirs. Chaque
        // matière brille donc un peu de sa propre texture, comme sous une faible lumière.
        public static float MinLight = 0.3f;

        // À côté de chaque modèle converti, un petit fichier "<nom>.os.txt" : version, hauteur, puis le chemin
        // de chaque os. Il permet de réutiliser un modèle déjà converti (lancement suivant) sans le refaire :
        // le fichier ne change plus, et Skyrim, qui ne relit pas un fichier modifié pendant la partie, le voit bien.
        private static void WriteCacheInfo(string name, List<string> bones, float height)
        {
            try
            {
                var lines = new List<string> { VersionFor(name) + "|" + height.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                lines.AddRange(bones);
                File.WriteAllLines(Path.Combine(Folder, name + ".os.txt"), lines);
            }
            catch (Exception) { }
        }

        // 0.73.4 : quelques modèles à refaire une fois, sans tout reconvertir (le reste resterait invisible un lancement).
        private static readonly string[] Redo = { "IcePillar", "IceWall" };
        private static int VersionFor(string name)
        {
            foreach (string key in Redo)
                if (name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return FormatVersion * 100 + 2;  // 0.73.5 : glace refaite
            return FormatVersion;
        }

        // Un modèle déjà converti avec cette version de la conversion ? (bones reçoit ses os.)
        public static bool TryLoadCached(string name, List<string> bones, out float height)
        {
            height = 0f;
            bones.Clear();
            try
            {
                string info = Path.Combine(Folder, name + ".os.txt");
                if (!File.Exists(info) || !File.Exists(Path.Combine(Folder, name + ".nif"))) return false;
                string[] lines = File.ReadAllLines(info);
                if (lines.Length < 2) return false;
                string[] head = lines[0].Split('|');
                if (head.Length < 2 || head[0] != VersionFor(name).ToString()) return false;
                float.TryParse(head[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out height);
                for (int i = 1; i < lines.Length; i++) bones.Add(lines[i]);
                return true;
            }
            catch (Exception)
            {
                bones.Clear();
                return false;
            }
        }

        // Convertit le modèle (racine "root"). Chaque morceau est rattaché à l'os (ou l'objet) qui le fait bouger :
        // le modèle Skyrim est un assemblage de pièces rigides, une par os. "bones" reçoit, dans l'ordre des
        // pièces, le chemin de chaque os depuis la racine ("" = la racine elle-même), pour les animer ensuite.
        // Hauteur (m) du dernier modèle converti : du plus bas au plus haut de ses sommets, dans sa pose actuelle.
        public static float LastHeight => _high > _low ? _high - _low : 0f;
        private static float _low, _high;

        // L'objet d'un tir porte parfois lui-même une partie de son apparence (le bouclier-bulle de l'Ingénieur est
        // sur le projectile, pas sur son "fantôme") : ses formes en matière d'effet, sans les zones de collision.
        public static bool HasEffectMesh(Transform root)
        {
            if (root == null) return false;
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material material = renderer.sharedMaterial;
                if (material != null && IsEffectMaterial(material) && !IsDistortion(material) &&
                    renderer.GetComponent<MeshFilter>() != null && renderer.GetComponent<MeshFilter>().sharedMesh != null) return true;
            }
            return false;
        }

        // flat : tout dans la pièce de la racine (objets fixes posés dans Skyrim : personne n'y place les pièces animées,
        // elles restaient toutes empilées au centre -> capsule lunaire "en morceaux").
        public static bool Export(Transform root, string name, ManualLogSource log, List<string> bones, bool includeHidden = false, bool effectsOnly = false, bool flat = false)
        {
            if (ExportPass(root, name, log, bones, includeHidden, effectsOnly, flat, false)) return true;
            // 0.73 : rien de "visible" selon le filtre habituel (grappin de la Chargeuse : une matière sans texture
            // ni couleur) : deuxième essai, en gris, sans les zones de collision ni les indicateurs.
            if (effectsOnly || flat) return false;
            if (!ExportPass(root, name, log, bones, includeHidden, false, false, true)) return false;
            log.LogInfo($"Modeles : {name} converti avec ses matieres sans texture");
            return true;
        }

        private static bool ExportPass(Transform root, string name, ManualLogSource log, List<string> bones, bool includeHidden, bool effectsOnly, bool flat, bool relaxed)
        {
            _low = float.MaxValue;
            _high = float.MinValue;
            Directory.CreateDirectory(Folder);
            var groups = new List<List<Part>>();
            var groupOf = new Dictionary<Transform, int>();
            var textures = new Dictionary<Texture, string>();
            var glows = new Dictionary<Material, string>();
            var effectTextures = new Dictionary<Material, string>();
            var renderers = new List<Renderer>();
            root.GetComponentsInChildren(false, renderers);
            bones.Clear();
            int index = 0;

            int Group(Transform bone)
            {
                if (bone == null) bone = root;
                if (groupOf.TryGetValue(bone, out int g)) return g;
                if (groups.Count >= MaxBones) return groupOf.TryGetValue(root, out int r) ? r : 0;
                g = groups.Count;
                groups.Add(new List<Part>());
                groupOf[bone] = g;
                bones.Add(PathOf(root, bone));
                return g;
            }
            Group(root);  // la pièce 0 : la racine

            foreach (Renderer renderer in renderers)
            {
                // Particules en forme d'objets 3D (cristaux du mur de glace de l'Artificière...) : acceptées aussi.
                bool particles = renderer is ParticleSystemRenderer psr && psr.renderMode == ParticleSystemRenderMode.Mesh;
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer || particles)) continue;
                if (!includeHidden && !renderer.enabled) continue;
                // 0.73.4 : les objets RoR2 affichés sur un allié (broche, anneaux... sur la tourelle mobile) ajoutaient des
                // pièces : au-delà de 64, les pattes n'étaient plus animées. On ne les recopie plus.
                if (UnderItemDisplay(renderer.transform, root)) continue;
                if (effectsOnly && (particles || !IsEffectMaterial(renderer.sharedMaterial))) continue;
                if (!effectsOnly && !(relaxed ? VisibleRelaxed(renderer) : Visible(renderer, includeHidden))) continue;
                // Un morceau immense (zone d'effet, indicateur de plusieurs dizaines de mètres...) n'est pas le
                // modèle lui-même : il faussait la taille (Fils illégitime "mesuré" à 85 m) et s'affichait en géant.
                if (renderer.bounds.size.magnitude > GiantSize)
                {
                    log.LogInfo($"Modeles : {name} : morceau geant ecarte ({renderer.name}, {renderer.bounds.size.magnitude:F0} m)");
                    continue;
                }
                Matrix4x4 particleSpace = Matrix4x4.identity;
                Mesh mesh = particles ? ParticleMesh((ParticleSystemRenderer)renderer, out particleSpace) : ReadableMesh(renderer);
                if (mesh == null) continue;
                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                Vector2[] uvs = mesh.uv;
                Matrix4x4 toWorld = particles
                    ? particleSpace
                    : renderer is SkinnedMeshRenderer
                    ? Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one)  // déjà à l'échelle
                    : renderer.transform.localToWorldMatrix;
                Transform[] owner = renderer is SkinnedMeshRenderer skinned ? BoneOfEachVertex(skinned, vertices, toWorld) : null;
                Transform rigid = renderer.transform;
                Material[] materials = renderer.sharedMaterials;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    int[] triangles = mesh.GetTriangles(sub);
                    if (triangles.Length == 0) continue;
                    Material material = sub < materials.Length ? materials[sub] : null;
                    if (IsDistortion(material)) continue;  // déformation de l'air : rien à dessiner dans Skyrim
                    bool effect = IsEffectMaterial(material);
                    // 0.73 : la couleur des particules "en objets 3D" (cristaux du mur de glace, glaçon du CHEF...) vient de la
                    // particule elle-même, pas de la matière. Et ces objets, comme les matières spéciales (glace, neige,
                    // parallaxe), ne sont pas éclairés par Skyrim : ils sortaient noirs. Ils brillent de leur texture.
                    Color particleTint = particles ? ParticleColor((ParticleSystemRenderer)renderer) : Color.white;
                    bool special = !effect && material != null && material.shader != null &&
                                   material.shader.name.IndexOf("Deferred/Standard", StringComparison.OrdinalIgnoreCase) < 0;
                    string texture = effect ? EffectTextureFor(material, name, ref index, effectTextures, log, particleTint)
                                            : TextureFor(material, name, ref index, textures, log, particleTint, particles);
                    string glow = effect ? null : (particles || special) ? texture : GlowFor(material, name, ref index, glows, log);
                    // 0.73 : une matière blanche qui brille d'une couleur (cœur de la Nano-bombe...) : dans RoR2 on voit la
                    // lueur ; dans Skyrim on voyait le blanc (sphère blanche). La lueur sert aussi de couleur.
                    if (!effect && glow != null && glow != texture && PlainWhite(material)) texture = glow;
                    bool additive = effect && IsAdditive(material);
                    var current = new Dictionary<int, Part>();             // morceau en cours, par pièce
                    var maps = new Dictionary<Part, Dictionary<int, int>>();
                    for (int t = 0; t + 2 < triangles.Length; t += 3)
                    {
                        // La pièce du triangle : l'os de la majorité de ses sommets.
                        Transform bone = rigid;
                        if (owner != null)
                        {
                            Transform a = owner[triangles[t]], b = owner[triangles[t + 1]], c = owner[triangles[t + 2]];
                            bone = b == c ? b : a;
                        }
                        if (bone == null || flat) bone = root;
                        int g = Group(bone);
                        Transform frame = groupOf.ContainsKey(bone) ? bone : root;  // trop d'os : pièce de la racine
                        if (!current.TryGetValue(g, out Part part) || part.Vertices.Count > MaxVertices - 3 || part.Triangles.Count / 3 >= MaxVertices)
                        {
                            part = new Part { Texture = texture, Glow = glow, Effect = effect, Additive = additive };
                            groups[g].Add(part);
                            current[g] = part;
                            maps[part] = new Dictionary<int, int>();
                        }
                        Dictionary<int, int> map = maps[part];
                        Quaternion inverse = Quaternion.Inverse(frame.rotation);
                        for (int k = 0; k < 3; k++)
                        {
                            int v = triangles[t + k];
                            if (!map.TryGetValue(v, out int local))
                            {
                                local = part.Vertices.Count;
                                map[v] = local;
                                Vector3 world = toWorld.MultiplyPoint3x4(vertices[v]);
                                if (world.y < _low) _low = world.y;
                                if (world.y > _high) _high = world.y;
                                part.Vertices.Add(inverse * (world - frame.position));
                                Vector3 normal = normals.Length > v ? toWorld.MultiplyVector(normals[v]).normalized : Vector3.up;
                                part.Normals.Add(inverse * normal);
                                part.Uvs.Add(uvs.Length > v ? uvs[v] : Vector2.zero);
                            }
                            part.Triangles.Add(local);
                        }
                    }
                }
                if (mesh != null && (renderer is SkinnedMeshRenderer || particles || mesh != renderer.GetComponent<MeshFilter>()?.sharedMesh)) UnityEngine.Object.Destroy(mesh);
            }
            int partCount = 0, total = 0;
            foreach (List<Part> group in groups)
                foreach (Part p in group) { partCount++; total += p.Triangles.Count / 3; }
            if (partCount == 0) return false;
            WriteFlatNormalMap();
            SaveFile(Path.Combine(Folder, name + ".nif"), WriteNif(groups));
            WriteCacheInfo(name, bones, LastHeight);
            log.LogInfo($"Modeles : {name} converti pour Skyrim ({groups.Count} pieces animees, {partCount} morceaux, {total} triangles, {textures.Count} textures)");
            return true;
        }

        // Ce que RoR2 dessine vraiment : pas les formes cachées, d'ombre seulement, ni les matières sans texture
        // ni lueur (souvent des zones de collision ou d'effet, qui sortiraient en blanc).
        private static bool Visible(Renderer renderer, bool includeHidden = false)
        {
            if (!includeHidden && (renderer.forceRenderingOff || renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)) return false;
            Material material = renderer.sharedMaterial;
            if (material == null || material.shader == null) return false;
            string shader = material.shader.name;
            if (shader.Contains("Hidden") || shader.Contains("Debug") || shader.Contains("Wireframe")) return false;
            bool textured = material.HasProperty("_MainTex") && material.GetTexture("_MainTex") != null;
            bool glows = material.HasProperty("_EmColor") && material.GetColor("_EmColor").maxColorComponent > 0.02f;
            bool tinted = material.HasProperty("_Color") && material.GetColor("_Color") != Color.white;
            return textured || glows || tinted;
        }

        private static bool UnderItemDisplay(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent)
                if (t.name.StartsWith("Display", StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool PlainWhite(Material material)
        {
            if (material == null) return true;
            Texture main = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
            bool plainTexture = main == null || main.name.IndexOf("white", StringComparison.OrdinalIgnoreCase) >= 0;
            Color color = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            return plainTexture && color.r > 0.8f && color.g > 0.8f && color.b > 0.8f;
        }

        private static bool VisibleRelaxed(Renderer renderer)
        {
            if (renderer.forceRenderingOff || renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) return false;
            string n = renderer.name.ToLowerInvariant();
            if (n.Contains("collision") || n.Contains("hitbox") || n.Contains("indicator") || n.Contains("trigger") || n.Contains("radius")) return false;
            Material material = renderer.sharedMaterial;
            if (material == null || material.shader == null) return false;
            string shader = material.shader.name;
            return !(shader.Contains("Hidden") || shader.Contains("Debug") || shader.Contains("Wireframe") || shader.Contains("Intersection"));
        }

        private static string PathOf(Transform root, Transform bone)
        {
            var names = new List<string>();
            for (Transform t = bone; t != null && t != root; t = t.parent) names.Insert(0, t.name);
            return string.Join("/", names);
        }

        // Quel os fait bouger chaque sommet ? On décale chaque os un instant et on regarde quels sommets suivent
        // (ça marche même quand le modèle est protégé). Un sommet appartient à l'os le plus "profond" qui l'entraîne.
        private static Transform[] BoneOfEachVertex(SkinnedMeshRenderer skinned, Vector3[] reference, Matrix4x4 toWorld)
        {
            var owner = new Transform[reference.Length];
            var depthOf = new int[reference.Length];
            Transform[] bones = skinned.bones;
            if (bones == null || bones.Length == 0) return owner;
            var baked = new Mesh();
            const float push = 0.2f;
            try
            {
                foreach (Transform bone in bones)
                {
                    if (bone == null) continue;
                    int depth = 0;
                    for (Transform t = bone; t != null; t = t.parent) depth++;
                    Vector3 saved = bone.position;
                    bone.position = saved + Vector3.up * push;
                    skinned.BakeMesh(baked);
                    bone.position = saved;
                    Vector3[] moved = baked.vertices;
                    for (int v = 0; v < reference.Length && v < moved.Length; v++)
                    {
                        float shift = (toWorld.MultiplyPoint3x4(moved[v]) - toWorld.MultiplyPoint3x4(reference[v])).magnitude;
                        if (shift > push * 0.5f && depth > depthOf[v])
                        {
                            depthOf[v] = depth;
                            owner[v] = bone;
                        }
                    }
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(baked);
            }
            return owner;
        }

        // Les particules d'un système "en objets 3D", figées dans leur position actuelle.
        // Unity les donne déjà tournées comme dans le monde, mais autour de la position de l'objet
        // (pas de rotation à rajouter : sinon le mur de glace sortait couché). Par sécurité, on vérifie
        // en comparant le centre obtenu à celui que Unity affiche, parmi les trois façons possibles.
        private static Mesh ParticleMesh(ParticleSystemRenderer renderer, out Matrix4x4 space)
        {
            space = Matrix4x4.Translate(renderer.transform.position);
            try
            {
                var baked = new Mesh();
                renderer.BakeMesh(baked, true);
                if (baked.vertexCount == 0) { UnityEngine.Object.Destroy(baked); return null; }
                Vector3 shown = renderer.bounds.center;
                Vector3 center = baked.bounds.center;
                Matrix4x4[] candidates = { space, Matrix4x4.identity, renderer.transform.localToWorldMatrix };
                float best = (candidates[0].MultiplyPoint3x4(center) - shown).sqrMagnitude;
                for (int i = 1; i < candidates.Length; i++)
                {
                    float error = (candidates[i].MultiplyPoint3x4(center) - shown).sqrMagnitude;
                    if (error < best * 0.5f - 0.01f) { best = error; space = candidates[i]; }  // nettement mieux seulement
                }
                return baked;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Une copie lisible du maillage, dans sa pose actuelle.
        private static Mesh ReadableMesh(Renderer renderer)
        {
            try
            {
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    if (skinned.sharedMesh == null) return null;
                    var baked = new Mesh();
                    skinned.BakeMesh(baked);
                    return baked;
                }
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh shared = filter != null ? filter.sharedMesh : null;
                if (shared == null) return null;
                if (shared.isReadable) return shared;
                // Maillage protégé : on le fait "cuire" par un faux personnage, ce qui en donne une copie lisible.
                var holder = new GameObject("SkyrimRoR2_Export");
                try
                {
                    var temp = holder.AddComponent<SkinnedMeshRenderer>();
                    temp.sharedMesh = shared;
                    var baked = new Mesh();
                    temp.BakeMesh(baked);
                    return baked.vertexCount > 0 ? baked : null;
                }
                finally
                {
                    UnityEngine.Object.Destroy(holder);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------- Matières d'effet de RoR2 ----------
        //
        // Les effets de RoR2 (shaders "Hopoo Games/FX/Cloud Remap"...) colorent une texture en gris avec une
        // palette ("_RemapTex") : l'intensité de chaque point de la texture choisit sa couleur et sa transparence
        // dans la palette, le tout multiplié par une teinte et un "boost". On refait ce calcul ici, une fois pour
        // toutes, dans une texture en couleurs, que Skyrim affiche avec son shader d'effet (sans éclairage,
        // transparent, en lueur additive si RoR2 l'est aussi). Le rendu est celui de RoR2, figé (sans défilement).

        public static bool IsEffectMaterial(Material material)
        {
            if (material == null || material.shader == null) return false;
            string shader = material.shader.name;
            return shader.IndexOf("/FX/", StringComparison.OrdinalIgnoreCase) >= 0 || shader.IndexOf("Particles", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   shader.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 || shader.IndexOf("Cloud", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   material.HasProperty("_RemapTex");
        }

        public static bool IsDistortion(Material material)
        {
            return material != null && material.shader != null && material.shader.name.IndexOf("Distortion", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsAdditive(Material material)
        {
            if (material == null) return true;
            string shader = material.shader != null ? material.shader.name : "";
            if (shader.IndexOf("Opaque", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (shader.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // UnityEngine.Rendering.BlendMode : 1 = One (destination gardée telle quelle : on ajoute de la lumière)
            if (material.HasProperty("_DstBlend")) return Mathf.RoundToInt(material.GetFloat("_DstBlend")) == 1;
            return true;
        }

        private static string EffectTextureFor(Material material, string name, ref int index, Dictionary<Material, string> done, ManualLogSource log, Color tint)
        {
            if (material != null && done.TryGetValue(material, out string known)) return known;
            string file = $"{name}_{index++}";
            try
            {
                Texture2D composed = ComposeEffectTexture(material, tint, 256);
                SaveFile(Path.Combine(Folder, file + ".dds"), WriteDds(composed));
                UnityEngine.Object.Destroy(composed);
            }
            catch (Exception e)
            {
                log.LogWarning($"Modeles : texture d'effet de {name} impossible a convertir ({e.Message})");
            }
            if (material != null) done[material] = file;
            return file;
        }

        // La texture d'effet telle que RoR2 l'affiche : couleurs de la palette, teinte, boost, transparence.
        public static Texture2D ComposeEffectTexture(Material material, Color tint, int maxSize)
        {
            Texture main = material != null && material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
            Texture remap = material != null && material.HasProperty("_RemapTex") ? material.GetTexture("_RemapTex") : null;
            Color materialTint = Color.white;
            if (material != null && material.HasProperty("_TintColor")) materialTint = material.GetColor("_TintColor");
            else if (material != null && material.HasProperty("_Color")) materialTint = material.GetColor("_Color");
            float boost = material != null && material.HasProperty("_Boost") ? Mathf.Clamp(material.GetFloat("_Boost"), 0.1f, 8f) : 1f;
            float alphaBoost = material != null && material.HasProperty("_AlphaBoost") ? Mathf.Clamp(material.GetFloat("_AlphaBoost"), 0.1f, 8f) : 1f;

            Texture2D source = main != null ? ReadTexture(main, Color.white, maxSize) : Blob(64);
            Texture2D ramp = remap != null ? ReadTexture(remap, Color.white, 256) : null;
            Color[] pixels = source.GetPixels();
            // L'intensité d'un point : sa transparence si la texture en a une, sinon sa luminosité.
            bool alphaUseful = false;
            for (int i = 0; i < pixels.Length && !alphaUseful; i += 7) alphaUseful = pixels[i].a < 0.97f;
            Color multiply = new Color(materialTint.r * tint.r, materialTint.g * tint.g, materialTint.b * tint.b, materialTint.a * tint.a);
            for (int i = 0; i < pixels.Length; i++)
            {
                Color p = pixels[i];
                float value = alphaUseful ? p.a : p.grayscale;
                Color c;
                if (ramp != null)
                {
                    Color r = ramp.GetPixelBilinear(Mathf.Clamp01(value), 0.5f);
                    c = new Color(r.r * boost, r.g * boost, r.b * boost, r.a * Mathf.Clamp01(value * alphaBoost));
                }
                else c = new Color(p.r * boost, p.g * boost, p.b * boost, Mathf.Clamp01(value * alphaBoost));
                c = new Color(c.r * multiply.r, c.g * multiply.g, c.b * multiply.b, c.a * multiply.a);
                pixels[i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
            string shaderName = material != null && material.shader != null ? material.shader.name : "";
            // 0.73 : les matières "Opaque" de RoR2 (Acolyte, Chercheuse...) ne sont pas transparentes : une découpe nette.
            bool opaque = shaderName.IndexOf("Opaque", StringComparison.OrdinalIgnoreCase) >= 0;
            // Bulles, sphères de zone, boucliers ("Intersection", "Fresnel", "Parallax") : RoR2 n'en dessine que les
            // bords ou des reflets. Pleines, elles sortaient en boule blanche ou violette : on les garde légères.
            bool shell = shaderName.IndexOf("Intersection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         shaderName.IndexOf("Fresnel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         shaderName.IndexOf("Parallax", StringComparison.OrdinalIgnoreCase) >= 0;
            // Trop sombre (couleur venue d'ailleurs que la texture, palette lue au mauvais endroit...) : sortait noir.
            // On garde les formes et la transparence, avec la couleur la plus vive de la palette ou de la teinte.
            double sum = 0, weight = 0;
            for (int i = 0; i < pixels.Length; i += 3)
            {
                Color p = pixels[i];
                sum += p.a * Mathf.Max(p.r, Mathf.Max(p.g, p.b));
                weight += p.a;
            }
            Color bright = BrightestOf(ramp, multiply, out float intended);
            // (Une vraie fumée noire a une palette sombre : elle reste noire.)
            bool dark = weight > 1 && sum / weight < 0.08 && intended >= 0.3f;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                if (dark)
                {
                    float level = Mathf.Max(0.35f, c.a);
                    c = new Color(bright.r * level, bright.g * level, bright.b * level, c.a);
                }
                if (opaque) c.a = c.a > 0.1f ? 1f : 0f;
                if (shell) c.a = Mathf.Min(c.a, 0.35f);
                pixels[i] = c;
            }
            source.SetPixels(pixels);
            source.Apply();
            if (ramp != null) UnityEngine.Object.Destroy(ramp);
            return source;
        }

        // La couleur la plus vive : celle de la palette (s'il y en a une), sinon la teinte ; blanc bleuté par défaut.
        private static Color BrightestOf(Texture2D ramp, Color tint, out float intended)
        {
            Color best = Color.black;
            float bestValue = 0f;
            intended = 0f;
            if (ramp != null)
                for (int x = 0; x < ramp.width; x += Mathf.Max(1, ramp.width / 64))
                {
                    Color c = ramp.GetPixel(x, ramp.height / 2);
                    float v = Mathf.Max(c.r, Mathf.Max(c.g, c.b)) * Mathf.Max(0.3f, c.a);
                    if (v > bestValue) { bestValue = v; best = c; }
                }
            if (bestValue < 0.1f)
            {
                best = tint;
                bestValue = Mathf.Max(tint.r, Mathf.Max(tint.g, tint.b));
            }
            intended = bestValue;
            if (bestValue < 0.1f) return new Color(0.75f, 0.85f, 1f, 1f);
            float k = 1f / bestValue;  // ramenée à pleine luminosité
            return new Color(Mathf.Clamp01(best.r * k), Mathf.Clamp01(best.g * k), Mathf.Clamp01(best.b * k), 1f);
        }

        // Une tache ronde, douce sur les bords (particules sans texture).
        private static Texture2D Blob(int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    pixels[y * size + x] = new Color(1f, 1f, 1f, a * a);
                }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        // ---------- Sprites (particules, traînées, rayons) ----------
        //
        // Un sprite = un carré de 1 m (70 unités Skyrim) dans le plan X-Z de Skyrim, avec la texture d'effet
        // recomposée d'une matière de RoR2. Skyrim en pose une copie par particule (ou par morceau de traînée),
        // tournée vers la caméra et à la bonne taille. Quatre niveaux d'opacité (fichiers _0 à _3) servent aux
        // particules qui s'effacent. Fichiers : <clé>_<niveau>_A.nif et une seule texture <clé>_0_A_0.dds.
        public static readonly float[] SpriteAlpha = { 1f, 0.62f, 0.36f, 0.16f };

        public static string SpriteKey(Material material, Color tint)
        {
            string materialName = material != null ? material.name : "";
            string text = (material != null ? materialName + "|" + (material.shader != null ? material.shader.name : "") : "blob") +
                          "|" + Mathf.RoundToInt(tint.r * 15) + Mathf.RoundToInt(tint.g * 15) + Mathf.RoundToInt(tint.b * 15) + "|" + SpriteVersion;
            uint hash = 2166136261u;
            foreach (char c in text) { hash ^= c; hash *= 16777619u; }
            return "S_" + hash.ToString("x8");
        }

        public static string SpriteFile(string key, int level) => key + "_" + level;  // nom du modèle (sans "_A")

        public static bool SpriteExported(string key, string suffix)
        {
            return File.Exists(Path.Combine(Folder, SpriteFile(key, 0) + suffix + ".nif")) && File.Exists(Path.Combine(Folder, SpriteFile(key, 0) + suffix + "_0.dds"));
        }

        public static DateTime SpriteTime(string key, string suffix)
        {
            try { return File.GetLastWriteTime(Path.Combine(Folder, SpriteFile(key, 0) + suffix + ".nif")); }
            catch (Exception) { return DateTime.MaxValue; }
        }

        public static bool ExportSprite(string key, string suffix, Material material, Color tint, ManualLogSource log)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string texture = SpriteFile(key, 0) + suffix + "_0";
                Texture2D composed = ComposeEffectTexture(material, tint, 128);
                SaveFile(Path.Combine(Folder, texture + ".dds"), WriteDds(composed));
                UnityEngine.Object.Destroy(composed);
                bool additive = IsAdditive(material);
                for (int level = 0; level < SpriteAlpha.Length; level++)
                {
                    var part = new Part { Texture = texture, Effect = true, Additive = additive, Alpha = SpriteAlpha[level] };
                    Vector3[] corners = { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
                    Vector2[] uvs = { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
                    for (int i = 0; i < 4; i++)
                    {
                        part.Vertices.Add(corners[i]);
                        part.Normals.Add(Vector3.forward);
                        part.Uvs.Add(uvs[i]);
                    }
                    part.Triangles.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
                    SaveFile(Path.Combine(Folder, SpriteFile(key, level) + suffix + ".nif"), WriteNif(new List<List<Part>> { new List<Part> { part } }));
                }
                return true;
            }
            catch (Exception e)
            {
                log.LogWarning($"Sprites : {key} impossible a convertir ({e.Message})");
                return false;
            }
        }

        // ---------- Textures ----------

        // Sans "_MainTex" (matières de RoR2 à texture "verte" ou "de neige", comme la glace de l'Artificière) :
        // la première autre texture de couleur. On note une fois dans le journal ce que la matière contient.
        private static readonly HashSet<Material> _described = new HashSet<Material>();
        private static Texture OtherColorTexture(Material material, string name, ManualLogSource log)
        {
            Texture found = null;
            var listed = new List<string>();
            try
            {
                foreach (string property in material.GetTexturePropertyNames())
                {
                    Texture texture = material.GetTexture(property);
                    listed.Add(property + (texture != null ? "=" + texture.name : ""));
                    if (found != null || texture == null) continue;
                    string p = property.ToLowerInvariant();
                    if (p.Contains("normal") || p.Contains("bump") || p.Contains("remap") || p.Contains("noise") || p.Contains("mask") ||
                        p.Contains("cloud") || p.Contains("fresnel") || p.Contains("ramp") || p.Contains("height") || p.Contains("emtex")) continue;
                    found = texture;
                }
            }
            catch (Exception) { }
            if (_described.Add(material))
                log.LogInfo($"Modeles : {name} : matiere {material.name} ({material.shader?.name}) sans _MainTex ; textures [{string.Join(", ", listed)}] -> {(found != null ? found.name : "aucune")}");
            return found;
        }

        private static string TextureFor(Material material, string name, ref int index, Dictionary<Texture, string> done, ManualLogSource log, Color particleTint, bool brighten = false)
        {
            Texture source = null;
            Color tint = Color.white;
            if (material != null)
            {
                if (material.HasProperty("_MainTex")) source = material.GetTexture("_MainTex");
                if (material.HasProperty("_Color")) tint = material.GetColor("_Color");
                if (source == null) source = OtherColorTexture(material, name, log);
            }
            tint = new Color(tint.r * particleTint.r, tint.g * particleTint.g, tint.b * particleTint.b, tint.a);
            // Une teinte presque noire donnerait un objet noir : on garde au moins un peu de la texture.
            if (Mathf.Max(tint.r, Mathf.Max(tint.g, tint.b)) < 0.15f) tint = new Color(0.6f, 0.75f, 0.9f, tint.a);
            if (source != null && particleTint == Color.white && done.TryGetValue(source, out string known)) return known;
            string file = $"{name}_{index++}";
            try
            {
                Texture2D readable = ReadTexture(source, tint);
                // 0.73.4 : une texture presque noire (glace de l'Artificière : sa couleur vient d'un effet de RoR2 qu'on ne
                // peut pas recopier) : on garde ses motifs, sur la couleur de la particule ou un bleu glacé.
                Color[] px = readable.GetPixels();
                double lum = 0;
                for (int i = 0; i < px.Length; i += 5) lum += Mathf.Max(px[i].r, Mathf.Max(px[i].g, px[i].b));
                double average = px.Length > 0 ? lum / (px.Length / 5.0 + 1) : 1;
                // 0.73.5 : particules en objets 3D (piliers du mur de glace) trop sombres : RoR2 les éclaire de reflets qu'on
                // ne recopie pas. On remonte leur luminosité (motifs gardés) jusqu'à un niveau moyen d'environ 0,6.
                if (brighten && average >= 0.06 && average < 0.35)
                {
                    float gain = (float)(0.6 / average);
                    for (int i = 0; i < px.Length; i++)
                        px[i] = new Color(Mathf.Clamp01(px[i].r * gain), Mathf.Clamp01(px[i].g * gain), Mathf.Clamp01(px[i].b * gain), px[i].a);
                    readable.SetPixels(px);
                    readable.Apply();
                }
                if (px.Length > 0 && average < 0.06)
                {
                    Color basis = particleTint != Color.white && Mathf.Max(particleTint.r, Mathf.Max(particleTint.g, particleTint.b)) > 0.2f
                        ? particleTint : new Color(0.62f, 0.82f, 1f, 1f);
                    for (int i = 0; i < px.Length; i++)
                    {
                        float k = 0.75f + 0.25f * Mathf.Clamp01(px[i].grayscale * 8f);
                        px[i] = new Color(basis.r * k, basis.g * k, basis.b * k, px[i].a);
                    }
                    readable.SetPixels(px);
                    readable.Apply();
                }
                SaveFile(Path.Combine(Folder, file + ".dds"), WriteDds(readable));
                UnityEngine.Object.Destroy(readable);
            }
            catch (Exception e)
            {
                log.LogWarning($"Modeles : texture de {name} impossible a convertir ({e.Message})");
            }
            if (source != null && particleTint == Color.white) done[source] = file;
            return file;
        }

        // Couleur de départ des particules (moyenne si elle varie).
        private static Color ParticleColor(ParticleSystemRenderer renderer)
        {
            try
            {
                ParticleSystem ps = renderer.GetComponent<ParticleSystem>();
                if (ps == null) return Color.white;
                ParticleSystem.MinMaxGradient g = ps.main.startColor;
                Color c;
                switch (g.mode)
                {
                    case ParticleSystemGradientMode.Color: c = g.color; break;
                    case ParticleSystemGradientMode.TwoColors: c = Color.Lerp(g.colorMin, g.colorMax, 0.5f); break;
                    case ParticleSystemGradientMode.Gradient: c = g.gradient != null ? g.gradient.Evaluate(0.5f) : Color.white; break;
                    case ParticleSystemGradientMode.TwoGradients: c = g.gradientMax != null ? g.gradientMax.Evaluate(0.5f) : Color.white; break;
                    default: c = Color.white; break;
                }
                return new Color(c.r, c.g, c.b, 1f);
            }
            catch (Exception) { return Color.white; }
        }

        // Les parties lumineuses d'une matière RoR2 (texture d'émission × couleur d'émission), ou null.
        private static string GlowFor(Material material, string name, ref int index, Dictionary<Material, string> done, ManualLogSource log)
        {
            if (material == null) return null;
            if (done.TryGetValue(material, out string known)) return known;
            Color color = material.HasProperty("_EmColor") ? material.GetColor("_EmColor") : Color.black;
            if (material.HasProperty("_EmPower")) color *= Mathf.Max(0f, material.GetFloat("_EmPower"));
            Texture source = material.HasProperty("_EmTex") ? material.GetTexture("_EmTex") : null;
            string file = null;
            if (Mathf.Max(color.r, Mathf.Max(color.g, color.b)) > 0.02f)
            {
                file = $"{name}_{index++}";
                try
                {
                    Color clamped = new Color(Mathf.Clamp01(color.r), Mathf.Clamp01(color.g), Mathf.Clamp01(color.b), 1f);
                    Texture2D readable = ReadTexture(source, clamped);
                    SaveFile(Path.Combine(Folder, file + ".dds"), WriteDds(readable));
                    UnityEngine.Object.Destroy(readable);
                }
                catch (Exception e)
                {
                    log.LogWarning($"Modeles : lueur de {name} impossible a convertir ({e.Message})");
                    file = null;
                }
            }
            done[material] = file;
            return file;
        }

        // Un "trait" lumineux (balle traçante) : une fine barre de 1,5 m qui brille, pointe vers l'avant (+Z).
        public static void ExportStreak(string name, Color color, ManualLogSource log)
        {
            Directory.CreateDirectory(Folder);
            Texture2D solid = ReadTexture(null, color);
            SaveFile(Path.Combine(Folder, name + "_0.dds"), WriteDds(solid));
            UnityEngine.Object.Destroy(solid);
            var part = new Part { Texture = name + "_0", Glow = name + "_0" };
            const float w = 0.025f, length = 1.5f;
            Vector3[] corners =
            {
                new Vector3(-w, -w, -length), new Vector3(w, -w, -length), new Vector3(w, w, -length), new Vector3(-w, w, -length),
                new Vector3(-w, -w, 0f), new Vector3(w, -w, 0f), new Vector3(w, w, 0f), new Vector3(-w, w, 0f)
            };
            int[][] faces = { new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 }, new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 } };
            foreach (int[] face in faces)
            {
                Vector3 normal = Vector3.Cross(corners[face[1]] - corners[face[0]], corners[face[2]] - corners[face[0]]).normalized;
                int start = part.Vertices.Count;
                foreach (int c in face)
                {
                    part.Vertices.Add(corners[c]);
                    part.Normals.Add(normal);
                    part.Uvs.Add(new Vector2(0.5f, 0.5f));
                }
                part.Triangles.Add(start); part.Triangles.Add(start + 1); part.Triangles.Add(start + 2);
                part.Triangles.Add(start); part.Triangles.Add(start + 2); part.Triangles.Add(start + 3);
            }
            WriteFlatNormalMap();
            SaveFile(Path.Combine(Folder, name + ".nif"), WriteNif(new List<List<Part>> { new List<Part> { part } }));
            log.LogInfo($"Modeles : trait lumineux {name} cree pour Skyrim");
        }

        // Recopie une texture (même protégée ou compressée) dans une image lisible, teinte comprise.
        private static Texture2D ReadTexture(Texture source, Color tint, int maxSize = 1024)
        {
            int width = source != null ? Mathf.Clamp(source.width, 4, 1024) : 4;
            int height = source != null ? Mathf.Clamp(source.height, 4, 1024) : 4;
            while (width > maxSize || height > maxSize)
            {
                width = Mathf.Max(4, width / 2);
                height = Mathf.Max(4, height / 2);
            }
            var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
            if (source == null)
            {
                var pixels = new Color[width * height];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = tint;
                result.SetPixels(pixels);
                result.Apply();
                return result;
            }
            RenderTexture previous = RenderTexture.active;
            RenderTexture temp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, temp);
            RenderTexture.active = temp;
            result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            result.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temp);
            if (tint != Color.white)
            {
                Color[] pixels = result.GetPixels();
                for (int i = 0; i < pixels.Length; i++) pixels[i] *= tint;
                result.SetPixels(pixels);
                result.Apply();
            }
            return result;
        }

        private static void WriteFlatNormalMap()
        {
            string path = Path.Combine(Folder, "plat_n.dds");
            if (File.Exists(path)) return;
            var flat = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var pixels = new Color32[16];
            for (int i = 0; i < 16; i++) pixels[i] = new Color32(128, 128, 255, 255);
            flat.SetPixels32(pixels);
            flat.Apply();
            SaveFile(path, WriteDds(flat));
            UnityEngine.Object.Destroy(flat);
        }

        // DDS non compressé (BGRA 8 bits), avec ses réductions (mipmaps) pour ne pas scintiller de loin.
        // Écrit à côté puis renomme : Skyrim (qui recopie ces fichiers) ne voit jamais un fichier à moitié écrit.
        internal static void SaveFile(string path, byte[] bytes)
        {
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        private static byte[] WriteDds(Texture2D texture)
        {
            int width = texture.width, height = texture.height;
            Color32[] top = texture.GetPixels32();
            var levels = new List<Color32[]> { FlipRows(top, width, height) };
            var sizes = new List<Vector2Int> { new Vector2Int(width, height) };
            while (width > 1 || height > 1)
            {
                int w = Mathf.Max(1, width / 2), h = Mathf.Max(1, height / 2);
                Color32[] previous = levels[levels.Count - 1];
                var next = new Color32[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int r = 0, g = 0, b = 0, a = 0, n = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = Mathf.Min(x * 2 + dx, width - 1), sy = Mathf.Min(y * 2 + dy, height - 1);
                                Color32 c = previous[sy * width + sx];
                                r += c.r; g += c.g; b += c.b; a += c.a; n++;
                            }
                        next[y * w + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                    }
                levels.Add(next);
                sizes.Add(new Vector2Int(w, h));
                width = w;
                height = h;
            }

            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                w.Write(0x20534444u);                 // "DDS "
                w.Write(124u);                        // taille de l'en-tête
                w.Write(0x1u | 0x2u | 0x4u | 0x8u | 0x1000u | 0x20000u);  // caps, height, width, pitch, pixelformat, mipmapcount
                w.Write((uint)sizes[0].y);
                w.Write((uint)sizes[0].x);
                w.Write((uint)(sizes[0].x * 4));      // pitch
                w.Write(0u);                          // profondeur
                w.Write((uint)levels.Count);          // nombre de mipmaps
                for (int i = 0; i < 11; i++) w.Write(0u);
                w.Write(32u);                         // pixel format
                w.Write(0x41u);                       // RGB + alpha
                w.Write(0u);
                w.Write(32u);
                w.Write(0x00FF0000u);
                w.Write(0x0000FF00u);
                w.Write(0x000000FFu);
                w.Write(0xFF000000u);
                w.Write(0x1000u | 0x8u | 0x400000u);  // texture, complexe, mipmaps
                w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u);
                foreach (Color32[] level in levels)
                    foreach (Color32 c in level)
                    {
                        w.Write(c.b); w.Write(c.g); w.Write(c.r); w.Write(c.a);
                    }
                return stream.ToArray();
            }
        }

        // Unity range les lignes du bas vers le haut ; les DDS du haut vers le bas.
        private static Color32[] FlipRows(Color32[] pixels, int width, int height)
        {
            var flipped = new Color32[pixels.Length];
            for (int y = 0; y < height; y++) Array.Copy(pixels, y * width, flipped, (height - 1 - y) * width, width);
            return flipped;
        }

        // ---------- Le fichier .nif (Skyrim Special Edition) ----------
        //
        // Blocs : 0 = NiNode (racine) ; puis pour chaque pièce : un NiNode, et pour chacun de ses morceaux :
        // BSTriShape, BSLightingShaderProperty, BSShaderTextureSet. Les pièces sont les enfants de la racine, dans l'ordre.

        private static byte[] WriteNif(List<List<Part>> groups)
        {
            var strings = new List<string> { "SkyrimRoR2" };
            var blocks = new List<byte[]>();
            var types = new List<ushort>();   // 0 NiNode, 1 BSTriShape, 2 BSLightingShaderProperty, 3 BSShaderTextureSet

            // Numéros des blocs, calculés d'avance.
            var nodeIndex = new int[groups.Count];
            var shapeIndex = new List<int>[groups.Count];
            int next = 1;
            for (int g = 0; g < groups.Count; g++)
            {
                nodeIndex[g] = next++;
                shapeIndex[g] = new List<int>();
                foreach (Part _ in groups[g]) { shapeIndex[g].Add(next); next += 3; }
            }

            blocks.Add(Block(w =>
            {
                WriteObjectNet(w, 0);
                WriteAvObject(w);
                w.Write((uint)groups.Count);
                foreach (int node in nodeIndex) w.Write(node);
                w.Write(0u);
            }));
            types.Add(0);

            for (int g = 0; g < groups.Count; g++)
            {
                strings.Add("Os" + g);
                int nameIndex = strings.Count - 1;
                List<int> children = shapeIndex[g];
                blocks.Add(Block(w =>
                {
                    WriteObjectNet(w, nameIndex);
                    WriteAvObject(w);
                    w.Write((uint)children.Count);
                    foreach (int child in children) w.Write(child);
                    w.Write(0u);
                }));
                types.Add(0);
                for (int i = 0; i < groups[g].Count; i++)
                {
                    Part part = groups[g][i];
                    int shape = shapeIndex[g][i];
                    strings.Add("Morceau" + g + "_" + i);
                    if (part.Effect)
                    {
                        // Shape -> shader d'effet (shape + 1) et transparence (shape + 2).
                        blocks.Add(Shape(part, strings.Count - 1, shape + 1, shape + 2));
                        types.Add(1);
                        blocks.Add(EffectShader(part));
                        types.Add(4);
                        blocks.Add(Block(w =>
                        {
                            WriteObjectNet(w, -1);
                            // NiAlphaProperty : mélange activé ; source = SRC_ALPHA (6), destination = ONE (0)
                            // pour une lueur additive, INV_SRC_ALPHA (7) pour une transparence normale. Pas de test.
                            ushort flags = (ushort)(1 | (6 << 1) | ((part.Additive ? 0 : 7) << 5));
                            w.Write(flags);
                            w.Write((byte)0);  // seuil
                        }));
                        types.Add(5);
                        continue;
                    }
                    blocks.Add(Shape(part, strings.Count - 1, shape + 1, -1));
                    types.Add(1);
                    blocks.Add(Block(w =>
                    {
                        // 0.66 : sans lueur à elle, la matière brille faiblement de sa propre texture (lumière minimum).
                        bool ownGlow = part.Glow != null;
                        float minLight = Mathf.Clamp01(MinLight);
                        bool glows = ownGlow || minLight > 0.001f;
                        w.Write(glows ? 2u : 0u); // type de shader : lumineux (carte de lueur) ou par défaut
                        WriteObjectNet(w, -1);
                        // drapeaux 1 : test de profondeur, ombres portées et reçues (+ émet sa propre lumière)
                        w.Write(0x80000300u | (glows ? 1u << 22 : 0u));
                        // drapeaux 2 : écriture de profondeur, deux faces (+ carte de lueur)
                        w.Write(0x00000011u | (glows ? 1u << 6 : 0u));
                        w.Write(0f); w.Write(0f); // décalage des UV
                        w.Write(1f); w.Write(1f); // échelle des UV
                        w.Write(shape + 2);       // jeu de textures
                        if (ownGlow) { w.Write(1f); w.Write(1f); w.Write(1f); w.Write(2f); }  // émission : blanche, forte
                        else if (glows) { w.Write(1f); w.Write(1f); w.Write(1f); w.Write(minLight); }  // lumière minimum
                        else { w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); }
                        w.Write(3u);              // textures répétées
                        w.Write(1f);              // opacité
                        w.Write(0f);              // réfraction
                        w.Write(30f);             // brillance
                        w.Write(1f); w.Write(1f); w.Write(1f);  // couleur des reflets
                        w.Write(0.3f);            // force des reflets
                        w.Write(0.3f);            // éclairage par l'arrière
                        w.Write(2f);
                    }));
                    types.Add(2);
                    blocks.Add(Block(w =>
                    {
                        w.Write(9);
                        SizedString(w, "textures\\SkyrimRoR2\\" + part.Texture + ".dds");
                        SizedString(w, "textures\\SkyrimRoR2\\plat_n.dds");
                        string glowMap = part.Glow ?? (Mathf.Clamp01(MinLight) > 0.001f ? part.Texture : null);
                        SizedString(w, glowMap != null ? "textures\\SkyrimRoR2\\" + glowMap + ".dds" : "");
                        for (int k = 3; k < 9; k++) SizedString(w, "");
                    }));
                    types.Add(3);
                }
            }

            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
                w.Write(0x14020007u);
                w.Write((byte)1);         // petit-boutiste
                w.Write(12u);             // version utilisateur
                w.Write((uint)blocks.Count);
                w.Write(100u);            // Skyrim Special Edition
                ExportString(w, "SkyrimRoR2");
                ExportString(w, "");
                ExportString(w, "");
                string[] typeNames = { "NiNode", "BSTriShape", "BSLightingShaderProperty", "BSShaderTextureSet", "BSEffectShaderProperty", "NiAlphaProperty" };
                w.Write((ushort)typeNames.Length);
                foreach (string type in typeNames) SizedString(w, type);
                foreach (ushort type in types) w.Write(type);
                foreach (byte[] block in blocks) w.Write((uint)block.Length);
                w.Write((uint)strings.Count);
                int longest = 0;
                foreach (string text in strings) longest = Math.Max(longest, text.Length);
                w.Write((uint)longest);
                foreach (string text in strings) SizedString(w, text);
                w.Write(0u);              // groupes
                foreach (byte[] block in blocks) w.Write(block);
                w.Write(1u);              // une racine : le bloc 0
                w.Write(0);
                return stream.ToArray();
            }
        }

        // BSEffectShaderProperty de Skyrim Special Edition (format vérifié dans nif.xml de niftools, BSVER 100).
        private static byte[] EffectShader(Part part)
        {
            return Block(w =>
            {
                WriteObjectNet(w, -1);
                // drapeaux 1 : test de profondeur (bit 31) ; drapeaux 2 : deux faces (bit 4), pas d'écriture de profondeur
                w.Write(0x80000000u);
                w.Write(0x00000010u);
                w.Write(0f); w.Write(0f); // décalage des UV
                w.Write(1f); w.Write(1f); // échelle des UV
                SizedString(w, "textures\\SkyrimRoR2\\" + part.Texture + ".dds");
                w.Write((byte)3);         // textures répétées
                w.Write((byte)255);       // influence de l'éclairage
                w.Write((byte)0);         // LOD de la carte d'environnement
                w.Write((byte)0);
                w.Write(1f); w.Write(1f); // angles d'atténuation (inutilisés)
                w.Write(1f); w.Write(1f);
                w.Write(1f); w.Write(1f); w.Write(1f); w.Write(Mathf.Clamp01(part.Alpha));  // couleur de base (et opacité)
                w.Write(1f);              // force de la couleur
                w.Write(100f);            // douceur des bords (effets "doux")
                SizedString(w, "");       // pas de palette : la couleur est déjà dans la texture
            });
        }

        private static byte[] Shape(Part part, int nameIndex, int shaderIndex, int alphaIndex)
        {
            return Block(w =>
            {
                WriteObjectNet(w, nameIndex);
                WriteAvObject(w);

                // Sommets en unités Skyrim.
                int count = part.Vertices.Count;
                var positions = new Vector3[count];
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                for (int i = 0; i < count; i++)
                {
                    Vector3 v = part.Vertices[i];
                    positions[i] = new Vector3(v.x, v.z, v.y) * UnitsPerMeter;
                    min = Vector3.Min(min, positions[i]);
                    max = Vector3.Max(max, positions[i]);
                }
                Vector3 center = (min + max) * 0.5f;
                float radius = 0f;
                foreach (Vector3 p in positions) radius = Mathf.Max(radius, (p - center).magnitude);
                w.Write(center.x); w.Write(center.y); w.Write(center.z); w.Write(radius);
                w.Write(-1);              // peau (pas d'os)
                w.Write(shaderIndex);     // shader
                w.Write(alphaIndex);      // transparence (-1 : aucune)

                // Description des sommets : position + UV + normale + tangente (28 octets).
                const ulong flags = 0x1 | 0x2 | 0x8 | 0x10 | 0x400;
                ulong desc = 7UL | (4UL << 8) | (5UL << 16) | (6UL << 20) | (flags << 44);
                w.Write(desc);
                int triangles = part.Triangles.Count / 3;
                w.Write((ushort)triangles);
                w.Write((ushort)count);
                w.Write((uint)(count * 28 + triangles * 6));
                for (int i = 0; i < count; i++)
                {
                    Vector3 p = positions[i];
                    Vector3 n = part.Normals[i];
                    Vector3 normal = new Vector3(n.x, n.z, n.y).normalized;
                    Vector3 tangent = Vector3.Cross(normal, Mathf.Abs(normal.z) < 0.9f ? Vector3.forward : Vector3.right).normalized;
                    Vector3 bitangent = Vector3.Cross(normal, tangent);
                    Vector2 uv = part.Uvs[i];
                    w.Write(p.x); w.Write(p.y); w.Write(p.z);
                    w.Write(bitangent.x);
                    w.Write(Half(uv.x)); w.Write(Half(1f - uv.y));
                    w.Write(Byte(normal.x)); w.Write(Byte(normal.y)); w.Write(Byte(normal.z)); w.Write(Byte(bitangent.y));
                    w.Write(Byte(tangent.x)); w.Write(Byte(tangent.y)); w.Write(Byte(tangent.z)); w.Write(Byte(bitangent.z));
                }
                // Unity et Skyrim n'ont pas la même "main" : on inverse l'ordre des sommets de chaque triangle.
                for (int t = 0; t < triangles; t++)
                {
                    w.Write((ushort)part.Triangles[t * 3]);
                    w.Write((ushort)part.Triangles[t * 3 + 2]);
                    w.Write((ushort)part.Triangles[t * 3 + 1]);
                }
                w.Write(0u);              // données de particules
            });
        }

        private static void WriteObjectNet(BinaryWriter w, int nameIndex)
        {
            w.Write(nameIndex);           // nom (dans la table des noms)
            w.Write(0u);                  // données en plus
            w.Write(-1);                  // contrôleur
        }

        private static void WriteAvObject(BinaryWriter w)
        {
            w.Write(14u);                 // drapeaux
            w.Write(0f); w.Write(0f); w.Write(0f);              // position
            w.Write(1f); w.Write(0f); w.Write(0f);              // rotation (identité)
            w.Write(0f); w.Write(1f); w.Write(0f);
            w.Write(0f); w.Write(0f); w.Write(1f);
            w.Write(1f);                  // échelle
            w.Write(-1);                  // collision
        }

        private static byte[] Block(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                write(w);
                return stream.ToArray();
            }
        }

        private static void SizedString(BinaryWriter w, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            w.Write((uint)bytes.Length);
            w.Write(bytes);
        }

        private static void ExportString(BinaryWriter w, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            w.Write((byte)(bytes.Length + 1));
            w.Write(bytes);
            w.Write((byte)0);
        }

        private static byte Byte(float value) => (byte)Mathf.Clamp(Mathf.RoundToInt((value + 1f) * 127.5f), 0, 255);

        private static ushort Half(float value) => Mathf.FloatToHalf(value);
    }
}
