using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace SkyrimRoR2
{
    // Rangement des réglages dans r2modman : r2modman montre les sections par ordre alphabétique, elles sont
    // donc numérotées ("1 - Lancement", "2 - Jeu"...). Un réglage qui change de section garde la valeur que
    // le joueur avait choisie dans l'ancienne : BepInEx garde les lignes qu'il ne reconnaît plus ("orphelines"),
    // on y reprend l'ancienne valeur, puis on l'enlève du fichier.
    internal static class ConfigMove
    {
        private static PropertyInfo _orphans;

        public static ConfigEntry<T> Bind<T>(ConfigFile config, string oldSection, string section, string key, T defaultValue, string description)
        {
            ConfigEntry<T> entry = config.Bind(section, key, defaultValue, description);
            try
            {
                if (_orphans == null)
                    _orphans = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_orphans?.GetValue(config) is Dictionary<ConfigDefinition, string> orphans)
                {
                    var old = new ConfigDefinition(oldSection, key);
                    if (orphans.TryGetValue(old, out string value))
                    {
                        orphans.Remove(old);
                        entry.SetSerializedValue(value);  // (enregistre le fichier, sans l'ancienne ligne)
                        config.Save();
                    }
                }
            }
            catch (Exception) { }
            return entry;
        }
    }
}
