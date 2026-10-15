using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using RoR2;

namespace SkyrimRoR2
{
    // 0.74 : les noms de RoR2 (objets, survivants, compétences, skins) dans la langue de SKYRIM.
    //
    // Le plugin de Skyrim écrit sa langue dans %LOCALAPPDATA%\SkyrimRoR2\langue.txt ("french", "german"...) au
    // démarrage. On prend la langue de RoR2 qui lui correspond, sans changer la langue choisie dans RoR2 lui-même.
    // Si RoR2 n'a pas cette langue, on garde celle de RoR2.
    internal static class SkyrimLanguage
    {
        private static ManualLogSource _log;
        private static string _skyrim;          // "french"
        private static string _ror2;            // "fr" (null : langue de RoR2)
        private static DateTime _fileTime;
        private static float _nextCheck;
        private static MethodInfo _getString;   // Language.GetString(token, langue)
        private static bool _searched;
        private static readonly HashSet<string> _loaded = new HashSet<string>();

        private static readonly Dictionary<string, string> Prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "english", "en" }, { "french", "fr" }, { "german", "de" }, { "italian", "it" }, { "spanish", "es" },
            { "polish", "pl" }, { "russian", "ru" }, { "japanese", "ja" }, { "chinese", "zh" }, { "korean", "ko" },
            { "portuguese", "pt" }, { "turkish", "tr" }, { "czech", "cs" },
        };

        public static void Init(ManualLogSource log) { _log = log; }

        private static string FilePath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimRoR2", "langue.txt");

        // La langue de RoR2 à utiliser (relu au plus toutes les 5 s, si le fichier a changé).
        private static string Current()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextCheck) return _ror2;
            _nextCheck = now + 5f;
            try
            {
                string path = FilePath();
                if (!File.Exists(path)) return _ror2;
                DateTime time = File.GetLastWriteTimeUtc(path);
                if (time == _fileTime) return _ror2;
                _fileTime = time;
                string skyrim = File.ReadAllText(path).Trim().ToLowerInvariant();
                if (skyrim == _skyrim) return _ror2;
                _skyrim = skyrim;
                _ror2 = Choose(skyrim);
                _log?.LogInfo($"Langue : Skyrim en {skyrim} -> textes de RoR2 en {_ror2 ?? "(langue de RoR2)"}");
            }
            catch (Exception e) { _log?.LogWarning("Langue : langue.txt illisible : " + e.Message); }
            return _ror2;
        }

        private static IEnumerable<string> Available()
        {
            var names = new List<string>();
            try
            {
                Type type = typeof(Language);
                object all = type.GetMethod("GetAllLanguages", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);
                if (all == null)
                {
                    FieldInfo field = type.GetField("languagesByName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (field?.GetValue(null) is IDictionary dict) all = dict.Values;
                }
                if (all is IEnumerable list)
                    foreach (object language in list)
                    {
                        if (language == null) continue;
                        Type t = language.GetType();
                        string name = t.GetField("name")?.GetValue(language) as string ?? t.GetProperty("name")?.GetValue(language) as string;
                        if (!string.IsNullOrEmpty(name)) names.Add(name);
                    }
            }
            catch (Exception) { }
            return names;
        }

        private static string Choose(string skyrim)
        {
            if (!Prefixes.TryGetValue(skyrim, out string prefix)) prefix = skyrim.Length >= 2 ? skyrim.Substring(0, 2) : skyrim;
            List<string> names = Available().ToList();
            _log?.LogInfo("Langue : langues de RoR2 : " + (names.Count > 0 ? string.Join(", ", names) : "(liste introuvable)"));
            var matches = names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return null;
            if (prefix == "zh")  // le chinois de Skyrim est le chinois traditionnel
            {
                string traditional = matches.FirstOrDefault(n => n.IndexOf("tw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                                 n.IndexOf("hant", StringComparison.OrdinalIgnoreCase) >= 0);
                if (traditional != null) return traditional;
            }
            return matches[0];
        }

        // Charge les textes d'une langue de RoR2 qui n'est pas celle en cours (RoR2 ne charge que la sienne).
        private static void EnsureLoaded(string name)
        {
            if (!_loaded.Add(name)) return;
            try
            {
                Type type = typeof(Language);
                MethodInfo find = type.GetMethod("FindLanguageByName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                object language = find?.Invoke(null, new object[] { name });
                if (language == null) return;
                PropertyInfo loaded = type.GetProperty("stringsLoaded", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (loaded != null && loaded.GetValue(language) is bool yes && yes) return;
                MethodInfo load = type.GetMethod("LoadStrings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                load?.Invoke(language, null);
                _log?.LogInfo($"Langue : textes de RoR2 en {name} charges{(load == null ? " (LoadStrings introuvable)" : "")}");
            }
            catch (Exception e) { _log?.LogWarning($"Langue : textes {name} non charges : {e.Message}"); }
        }

        // Le texte d'un jeton de RoR2 ("ITEM_SYRINGE_NAME") dans la langue de Skyrim.
        public static string Text(string token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            string language = Current();
            if (language != null)
            {
                if (!_searched)
                {
                    _searched = true;
                    _getString = typeof(Language).GetMethod("GetString", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
                }
                if (_getString != null)
                {
                    try
                    {
                        EnsureLoaded(language);
                        string text = _getString.Invoke(null, new object[] { token, language }) as string;
                        if (!string.IsNullOrEmpty(text) && text != token) return text;
                    }
                    catch (Exception) { }
                }
            }
            return Language.GetString(token);
        }
    }
}
