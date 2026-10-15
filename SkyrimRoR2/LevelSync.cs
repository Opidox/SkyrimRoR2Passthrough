using System;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // 0.66 : le niveau RoR2 suit le niveau du personnage Skyrim.
    //
    // Skyrim envoie son niveau (SkyrimCommand.SkyrimLevel) ; le niveau RoR2 de l'équipe vaut ce niveau divisé par
    // NiveauxSkyrimParNiveau (2 par défaut, arrondi vers le bas, au moins 1). Les ennemis tués ne donnent plus
    // d'expérience RoR2 (crochet sur TeamManager.GiveTeamExperience), et le HUD de Skyrim cache la barre d'expérience.
    // En montant d'un palier, RoR2 joue son effet de montée de niveau, comme d'habitude.
    internal sealed class LevelSync
    {
        public static bool Active { get; private set; }   // le niveau suit Skyrim en ce moment
        private static bool _blockKills;

        private readonly ManualLogSource _log;
        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<int> _ratio;
        private readonly MethodInfo _setLevel;           // TeamManager.SetTeamLevel(TeamIndex, uint)
        private readonly MethodInfo _setExperience;      // TeamManager.SetTeamExperience(TeamIndex, ulong) (secours)
        private readonly MethodInfo _experienceFor;      // TeamManager.GetExperienceForLevel(uint) (secours)
        private uint _lastTarget;
        private bool _warned;

        public LevelSync(ManualLogSource log, ConfigFile config)
        {
            _log = log;
            _enabled = ConfigMove.Bind(config, "Jeu", "2 - Jeu", "NiveauSuitSkyrim", true,
                "Le niveau RoR2 du survivant suit le niveau de ton personnage Skyrim (les ennemis ne donnent plus d'experience RoR2, " +
                "et la barre d'experience RoR2 est cachee).");
            _ratio = ConfigMove.Bind(config, "Jeu", "2 - Jeu", "NiveauxSkyrimParNiveau", 2,
                "Nombre de niveaux Skyrim pour un niveau RoR2 (2 : niveau Skyrim 30 = niveau RoR2 15).");
            _setLevel = AccessTools.Method(typeof(TeamManager), "SetTeamLevel", new[] { typeof(TeamIndex), typeof(uint) });
            _setExperience = AccessTools.Method(typeof(TeamManager), "SetTeamExperience", new[] { typeof(TeamIndex), typeof(ulong) });
            _experienceFor = AccessTools.Method(typeof(TeamManager), "GetExperienceForLevel", new[] { typeof(uint) });
            if (_setLevel == null && (_setExperience == null || _experienceFor == null))
                _log.LogWarning("Niveau : impossible de regler le niveau RoR2 (methodes de TeamManager introuvables)");
        }

        public void Update(BridgeWriter bridge, CharacterMaster master)
        {
            Active = false;
            _blockKills = false;
            if (!_enabled.Value || bridge == null || Run.instance == null || TeamManager.instance == null) return;
            if (!bridge.ReadCommand(out SkyrimCommand command) || command.SkyrimLevel <= 0) return;
            Active = true;
            _blockKills = true;
            // 0.70 (coop) : le niveau est celui de toute l'équipe, réglé par l'hôte (moyenne des niveaux Skyrim des joueurs).
            if (!UnityEngine.Networking.NetworkServer.active) return;
            int skyrimLevel = command.SkyrimLevel;
            int team = Coop.Instance != null ? Coop.Instance.TeamSkyrimLevel(skyrimLevel) : -1;
            if (team > 0) skyrimLevel = team;

            int ratio = Mathf.Max(1, _ratio.Value);
            uint target = (uint)Mathf.Max(1, skyrimLevel / ratio);
            uint current = TeamManager.instance.GetTeamLevel(TeamIndex.Player);
            if (current == target) { _lastTarget = target; return; }
            try
            {
                if (_setLevel != null) _setLevel.Invoke(TeamManager.instance, new object[] { TeamIndex.Player, target });
                else if (_setExperience != null && _experienceFor != null)
                {
                    object xp = _experienceFor.Invoke(null, new object[] { target });
                    _setExperience.Invoke(TeamManager.instance, new object[] { TeamIndex.Player, xp });
                }
                else return;
                // En descendant (sauvegarde plus ancienne), RoR2 ne recalcule pas tout seul les PV et les dégâts.
                CharacterBody body = master != null ? master.GetBody() : null;
                if (body != null) body.RecalculateStats();
                if (target != _lastTarget)
                    _log.LogInfo($"Niveau : niveau Skyrim {skyrimLevel} -> niveau RoR2 {target} (avant : {current})");
                _lastTarget = target;
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; _log.LogWarning("Niveau : reglage impossible (" + e.Message + ")"); }
            }
        }

        // Les ennemis tués (orbes d'expérience) ne font plus monter le niveau RoR2 de l'équipe du joueur.
        [HarmonyPatch(typeof(TeamManager), "GiveTeamExperience")]
        internal static class GiveExperiencePatch
        {
            private static bool Prefix(object[] __args)
            {
                return !(_blockKills && __args != null && __args.Length > 0 && __args[0] is TeamIndex team && team == TeamIndex.Player);
            }
        }
    }
}
