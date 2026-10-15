using BepInEx.Logging;
using RoR2;
using UnityEngine;

namespace SkyrimRoR2
{
    // Neutralise la carte de RoR2 : c'est Skyrim qui fournit le monde.
    //
    //  1. Les murs et le sol de la carte RoR2 deviennent intangibles (on les voit encore,
    //     mais plus rien ne bloque) : le survivant ne se coince plus dans un décor invisible
    //     depuis Skyrim, et les doubles ne sont plus enfermés dans les rochers.
    //  2. Un petit plateau invisible (6 m) suit le survivant sous ses pieds : il marche toujours
    //     dessus. Petit et sous ses pieds, il ne gêne presque pas les tirs vers un ennemi en contrebas.
    //     Sa hauteur suit le sol de Skyrim : Skyrim envoie la distance entre les pieds du joueur et le sol,
    //     et le plateau se place à la même distance sous le survivant. Le joueur saute d'une falaise dans
    //     Skyrim : le plateau descend, le survivant tombe (animations de chute) et atterrit en même temps.
    //     Filet de sécurité : si le survivant passe quand même dessous, il est remis dessus.
    //  3. Les zones "hors limites" de RoR2 sont coupées, sinon le jeu ramènerait le survivant
    //     sur la carte dès qu'il s'en éloigne.
    //
    // Les coffres, le téléporteur et les autres objets interactifs ne sont pas touchés.
    internal sealed class MapNeutralizer
    {
        private const float ScanIntervalSeconds = 2f;   // nouveaux morceaux de décor (portes, ponts...)
        private const float FloorSize = 6f;             // plateau de 6 m sous les pieds du survivant
        private const float FloorThickness = 1f;
        private const float SafetyNetDepth = 3f;        // en dessous, on remet le survivant sur le plateau
        private const float GroundedGap = 0.4f;         // joueur Skyrim à moins de 40 cm du sol : il est au sol
        private const float FallGap = 1.0f;             // plus d'1 m de vide...
        private const float FallDelay = 0.15f;          // ...pendant 0,15 s : c'est une vraie chute
        private bool _falling;                          // le plateau suit le sol de Skyrim vers le bas
        private float _gapSince = -1f;                  // depuis quand il y a plus d'1 m de vide

        private readonly ManualLogSource _log;
        private GameObject _floor;
        private float _nextScan;
        private int _collidersDisabled;
        private int _safetyNetUses;

        public MapNeutralizer(ManualLogSource log)
        {
            _log = log;
        }

        public void Update(CharacterBody survivor, float skyrimGroundGap, bool terrainActive)
        {
            if (Run.instance == null || survivor == null) return;

            // Le relief de Skyrim est recopié (TerrainMirror) : le plateau plat est rangé, il gênerait.
            if (_floor != null && _floor.activeSelf == terrainActive)
            {
                _floor.SetActive(!terrainActive);
                _falling = false;
                _gapSince = -1f;
                if (!terrainActive)
                {
                    // Retour du plateau (relief coupé ou trop vieux) : sous les pieds du survivant.
                    Vector3 f = survivor.footPosition;
                    _floor.transform.position = new Vector3(f.x, f.y - FloorThickness * 0.5f, f.z);
                }
                _log.LogInfo(terrainActive ? "Relief de Skyrim actif : plateau plat range" : "Relief de Skyrim absent : plateau plat remis");
            }

            // Le sol invisible : créé à chaque nouvelle étape, sous les pieds du survivant.
            if (_floor == null)
            {
                CreateFloor(survivor.footPosition);
                _nextScan = 0f;  // nouvelle étape : on neutralise la carte tout de suite
            }
            else if (!terrainActive)
            {
                // Il suit le survivant à l'horizontale...
                Vector3 p = _floor.transform.position;
                float feet = survivor.footPosition.y;
                float top = p.y + FloorThickness * 0.5f;

                // ...et, en hauteur, le sol de Skyrim (jamais au-dessus des pieds du survivant).
                // Seulement pour une vraie chute (plus d'1 m de vide pendant 0,15 s) : sinon, en descendant
                // une pente ou des marches, le survivant passait sans arrêt de "au sol" à "en l'air", et ses
                // mains changeaient d'animation à chaque image (elles semblaient vibrer dans Skyrim).
                if (skyrimGroundGap >= 0f)
                {
                    if (skyrimGroundGap > FallGap)
                    {
                        if (_gapSince < 0f) _gapSince = Time.time;
                        if (Time.time - _gapSince >= FallDelay) _falling = true;
                    }
                    else
                    {
                        _gapSince = -1f;
                    }
                    if (_falling && skyrimGroundGap < GroundedGap)
                    {
                        _falling = false;  // le joueur Skyrim a atterri : le survivant aussi, tout de suite
                        top = feet;
                    }

                    // Hors chute, le plateau garde sa hauteur (comme avant) : sauts et pentes ne changent rien.
                    if (_falling)
                    {
                        float wanted = Mathf.Min(feet - skyrimGroundGap, feet);
                        if (Mathf.Abs(wanted - top) > 0.05f) top = wanted;
                    }
                }
                else
                {
                    _falling = false;
                    _gapSince = -1f;
                }
                _floor.transform.position = new Vector3(survivor.footPosition.x, top - FloorThickness * 0.5f, survivor.footPosition.z);
                p = _floor.transform.position;

                // Filet de sécurité : passé sous le plateau, le survivant est remis dessus.
                float floorTop = p.y + FloorThickness * 0.5f;
                if (survivor.footPosition.y < floorTop - SafetyNetDepth)
                {
                    Vector3 back = new Vector3(survivor.footPosition.x, floorTop + 0.1f, survivor.footPosition.z);
                    TeleportHelper.TeleportBody(survivor, back);
                    _safetyNetUses++;
                    if (_safetyNetUses <= 5 || _safetyNetUses % 20 == 0)
                        _log.LogWarning($"Filet de securite : survivant remis sur le plateau ({_safetyNetUses} fois)");
                }
            }

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + ScanIntervalSeconds;
                DisableMapCollision();
            }
        }

        private void CreateFloor(Vector3 feet)
        {
            _floor = new GameObject("SkyrimRoR2_Sol");
            // Calque normal du décor : le moteur de déplacement du survivant le respecte à coup sûr.
            _floor.layer = LayerIndex.world.intVal;
            _floor.transform.position = new Vector3(feet.x, feet.y - FloorThickness * 0.5f, feet.z);

            BoxCollider box = _floor.AddComponent<BoxCollider>();
            box.size = new Vector3(FloorSize, FloorThickness, FloorSize);

            _log.LogInfo($"Plateau invisible cree sous le survivant, a la hauteur {feet.y:F1}");
        }

        private void DisableMapCollision()
        {
            int worldLayer = LayerIndex.world.intVal;
            int count = 0;

            foreach (Collider collider in Object.FindObjectsOfType<Collider>())
            {
                if (!collider.enabled || collider.isTrigger) continue;
                if (collider.gameObject.layer != worldLayer) continue;
                if (_floor != null && collider.gameObject == _floor) continue;
                if (collider.gameObject.name.StartsWith("SkyrimRoR2_")) continue;  // nos surfaces (relief de Skyrim)
                collider.enabled = false;
                count++;
            }

            foreach (MapZone zone in Object.FindObjectsOfType<MapZone>())
            {
                if (zone.enabled) zone.enabled = false;
                Collider zoneCollider = zone.GetComponent<Collider>();
                if (zoneCollider != null && zoneCollider.enabled) zoneCollider.enabled = false;
            }

            if (count > 0)
            {
                _collidersDisabled += count;
                _log.LogInfo($"Carte RoR2 : {count} morceaux de decor rendus intangibles (total {_collidersDisabled})");
            }
        }
    }
}
