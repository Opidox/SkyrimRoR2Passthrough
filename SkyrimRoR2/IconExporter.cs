using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using UnityEngine;

namespace SkyrimRoR2
{
    // Exporte les icônes de RoR2 (compétences, objets, équipement) en images PNG,
    // pour que Skyrim puisse les afficher dans son HUD.
    // Dossier : %LOCALAPPDATA%\SkyrimRoR2\icones\
    // Chaque icône n'est exportée qu'une fois (on garde les fichiers d'une partie à l'autre).
    internal sealed class IconExporter
    {
        private const int ExportsPerFrame = 2;  // pas trop d'un coup, pour ne pas faire ramer RoR2

        private readonly ManualLogSource _log;
        private readonly string _folder;
        private readonly HashSet<string> _done = new HashSet<string>();
        private readonly Queue<KeyValuePair<string, Sprite>> _queue = new Queue<KeyValuePair<string, Sprite>>();
        private readonly HashSet<string> _queued = new HashSet<string>();
        private int _exported;

        public IconExporter(ManualLogSource log)
        {
            _log = log;
            _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimRoR2", "icones");
            try
            {
                Directory.CreateDirectory(_folder);
                _log.LogInfo("Icones du HUD : " + _folder);
            }
            catch (Exception e)
            {
                _log.LogError("Impossible de creer le dossier des icones : " + e.Message);
            }
        }

        // Donne le nom de l'icône (celui que Skyrim cherchera) et prévoit son export si besoin.
        public string Request(Sprite sprite)
        {
            if (sprite == null) return "";
            string key = KeyOf(sprite);
            if (key.Length == 0 || _done.Contains(key) || _queued.Contains(key)) return key;

            if (File.Exists(PathOf(key)))
            {
                _done.Add(key);  // déjà exportée lors d'une partie précédente
                return key;
            }
            _queue.Enqueue(new KeyValuePair<string, Sprite>(key, sprite));
            _queued.Add(key);
            return key;
        }

        // Pareil pour une image entière (le portrait d'un survivant).
        public string RequestTexture(Texture texture)
        {
            if (texture == null) return "";
            string key = Clean("portrait_" + texture.name);
            if (key.Length == 0 || _done.Contains(key) || _queuedTextures.ContainsKey(key)) return key;
            if (File.Exists(PathOf(key)))
            {
                _done.Add(key);
                return key;
            }
            _queuedTextures[key] = texture;
            return key;
        }

        private readonly Dictionary<string, Texture> _queuedTextures = new Dictionary<string, Texture>();

        // À appeler à chaque image : exporte quelques icônes en attente.
        public void Process()
        {
            if (_queuedTextures.Count > 0)
            {
                var first = new List<KeyValuePair<string, Texture>>(_queuedTextures)[0];
                _queuedTextures.Remove(first.Key);
                _done.Add(first.Key);
                try
                {
                    if (first.Value != null)
                    {
                        ExportRegion(first.Value, new Rect(0, 0, first.Value.width, first.Value.height), PathOf(first.Key));
                        _log.LogInfo("Portrait exporte pour Skyrim : " + first.Key);
                    }
                }
                catch (Exception e)
                {
                    _log.LogWarning("Portrait impossible a exporter (" + first.Key + ") : " + e.Message);
                }
            }

            for (int i = 0; i < ExportsPerFrame && _queue.Count > 0; i++)
            {
                KeyValuePair<string, Sprite> next = _queue.Dequeue();
                _queued.Remove(next.Key);
                _done.Add(next.Key);  // même en cas d'échec : on ne réessaie pas en boucle
                if (next.Value == null) continue;
                try
                {
                    Export(next.Value, PathOf(next.Key));
                    _exported++;
                    if (_exported <= 5 || _exported % 25 == 0)
                        _log.LogInfo($"Icone exportee pour Skyrim : {next.Key} (total {_exported})");
                }
                catch (Exception e)
                {
                    _log.LogWarning("Icone impossible a exporter (" + next.Key + ") : " + e.Message);
                }
            }
        }

        private string PathOf(string key) => Path.Combine(_folder, key + ".png");

        // Nom de fichier sûr, assez court pour le pont (47 caractères au plus).
        private static string KeyOf(Sprite sprite) => Clean(sprite.name);

        private static string Clean(string name)
        {
            name = name ?? "";
            var chars = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    chars.Append(c);
                if (chars.Length >= BridgeInfoSizes.MaxIconName) break;
            }
            return chars.ToString();
        }

        // Recopie l'icône (souvent rangée dans une grande image commune) dans une petite image lisible, puis en PNG.
        private static void Export(Sprite sprite, string path)
        {
            Rect rect;
            try { rect = sprite.textureRect; } catch { rect = sprite.rect; }
            ExportRegion(sprite.texture, rect, path);
        }

        private static void ExportRegion(Texture source, Rect rect, string path)
        {
            RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            RenderTexture previous = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;

                copy = new Texture2D(Mathf.Max(1, (int)rect.width), Mathf.Max(1, (int)rect.height), TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(rect.x, rect.y, copy.width, copy.height), 0, 0);
                copy.Apply();

                byte[] png = copy.EncodeToPNG();

                // Écrit à côté puis renomme : Skyrim ne lit jamais un fichier à moitié écrit.
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, png);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
        }
    }

    internal static class BridgeInfoSizes
    {
        public const int MaxIconName = 47;  // + le zéro de fin = 48 octets dans le pont
    }
}
