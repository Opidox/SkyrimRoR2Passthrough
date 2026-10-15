using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using UnityEngine;

namespace SkyrimRoR2
{
    // Taille d'écran du RoR2 normal. Le mod lance RoR2 en petite fenêtre ; RoR2 retient cette taille dans ses
    // réglages, et le RoR2 normal (sans le mod) se rouvrait ensuite en vignette. RoR2 ne range pas ces réglages
    // dans le registre de Windows, mais dans son propre fichier de réglages (config.cfg).
    //
    // Au lancement, si les réglages d'écran sont "normaux" (pas ceux du mod), on note le fichier et ces lignes
    // dans %LOCALAPPDATA%\SkyrimRoR2\ecran_ror2.txt. Quand RoR2 se ferme, une petite tâche lancée par Skyrim
    // (voir Launcher.cpp) remet ces lignes dans le fichier.
    internal static class ScreenKeeper
    {
        private static readonly string[] Names = { "resolution", "fullscreen", "window_mode", "screen_mode", "vsync" };

        public static void Save(ManualLogSource log)
        {
            try
            {
                var candidates = new List<string>
                {
                    Path.Combine(Application.dataPath, "Config", "config.cfg"),
                    Path.Combine(Application.persistentDataPath, "Config", "config.cfg"),
                    Path.Combine(Application.persistentDataPath, "config.cfg"),
                };
                foreach (string file in candidates)
                {
                    if (!File.Exists(file)) continue;
                    var kept = new List<string>();
                    bool small = false;
                    foreach (string raw in File.ReadAllLines(file))
                    {
                        string line = raw.Trim();
                        foreach (string name in Names)
                        {
                            if (!line.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)) continue;
                            kept.Add(line);
                            if (name == "resolution" && LooksSmall(line)) small = true;
                        }
                    }
                    log.LogInfo($"Ecran de RoR2 : reglages trouves dans {file} : {(kept.Count > 0 ? string.Join(" ; ", kept) : "aucun")}");
                    if (kept.Count == 0) continue;
                    if (small)
                    {
                        log.LogInfo("Ecran de RoR2 : reglages actuels trop petits (ceux du mod), on garde ceux notes avant");
                        return;
                    }
                    var output = new List<string> { file };
                    output.AddRange(kept);
                    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimRoR2");
                    Directory.CreateDirectory(folder);
                    File.WriteAllLines(Path.Combine(folder, "ecran_ror2.txt"), output);
                    return;
                }
                log.LogInfo("Ecran de RoR2 : fichier de reglages introuvable (" + string.Join(", ", candidates) + ")");
            }
            catch (Exception e)
            {
                log.LogWarning("Ecran de RoR2 : reglages illisibles (" + e.Message + ")");
            }
        }

        // "resolution 960x540" : plus petit que 800 de large = la petite fenêtre du mod.
        private static bool LooksSmall(string line)
        {
            string value = line.Substring(line.IndexOf(' ') + 1).Trim().Trim('"');
            int x = value.IndexOf('x');
            return x > 0 && int.TryParse(value.Substring(0, x), out int width) && width < 800;
        }
    }
}
