using BepInEx.Logging;
using RoR2;
using UnityEngine;

namespace SkyrimRoR2
{
    // Les monstres "normaux" de la partie RoR2 (Lémuriens, Beetles, Wisps...) apparaissent dans
    // la carte RoR2 et attaquent le survivant, alors qu'on ne les voit pas depuis Skyrim.
    // Maintenant que les ennemis viennent de Skyrim, on les empêche d'exister :
    //  - les "directeurs" de RoR2 (qui font apparaître les monstres) sont coupés ;
    //  - les monstres déjà là sont retirés, sans or ni expérience.
    // Les doubles des ennemis Skyrim ne sont jamais touchés. Les coffres et objets restent.
    internal sealed class MonsterBlocker
    {
        private const float CheckIntervalSeconds = 0.5f;

        // Les équipes ennemies du survivant dans RoR2.
        private static readonly TeamIndex[] EnemyTeams = { TeamIndex.Monster, TeamIndex.Void, TeamIndex.Lunar };

        private readonly ManualLogSource _log;
        private float _nextCheck;
        private int _removed;

        public MonsterBlocker(ManualLogSource log)
        {
            _log = log;
        }

        public void Update(GhostManager ghosts)
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + CheckIntervalSeconds;
            if (Run.instance == null) return;

            // 1. Plus de nouveaux monstres.
            foreach (CombatDirector director in Object.FindObjectsOfType<CombatDirector>())
            {
                if (director.enabled) director.enabled = false;
            }

            // 2. On retire ceux qui sont déjà là (sauf nos doubles).
            foreach (TeamIndex team in EnemyTeams)
            {
                var members = TeamComponent.GetTeamMembers(team);
                for (int i = members.Count - 1; i >= 0; i--)
                {
                    CharacterBody body = members[i] != null ? members[i].body : null;
                    if (body == null || ghosts.IsGhost(body)) continue;

                    CharacterMaster master = body.master;
                    if (master != null) Object.Destroy(master.gameObject);
                    Object.Destroy(body.gameObject);

                    _removed++;
                    if (_removed <= 5 || _removed % 50 == 0)
                        _log.LogInfo($"Monstre RoR2 retire : {body.name} (total {_removed})");
                }
            }
        }
    }
}
