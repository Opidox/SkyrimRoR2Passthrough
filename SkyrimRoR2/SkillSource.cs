using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using RoR2;
using UnityEngine;

namespace SkyrimRoR2
{
    // 0.63 : l'expérience des OBJETS et de l'ÉQUIPEMENT dépend de ce qui a frappé.
    //
    // Les coups des compétences (M1, M2, Maj, R) suivent [ExperienceSurvivants] dans SkyrimRoR2.ini. Les autres coups
    // du survivant (missiles de l'ATG, brûlures, saignements, Accumulateur Preon...) reçoivent ici un "code de
    // compétence" Skyrim, envoyé avec le dégât (octet 2 de DamageEvent.Reserved). Utiliser un équipement donne
    // aussi un peu d'expérience, même s'il ne blesse personne (Fruit étranger -> Guérison...).
    //
    // Codes (les mêmes dans skse/src/Experience.cpp) :
    //   0 inconnu (comme M1), 1 Archerie, 2 Une main, 3 Deux mains, 4 Destruction feu, 5 Destruction gel,
    //   6 Destruction foudre, 7 Altération, 8 Conjuration, 9 Illusion, 10 Guérison, 11 Alchimie, 12 Discrétion,
    //   13 Parade, 14 Enchantement, 15 Éloquence.
    internal sealed class SkillSource
    {
        public const int Unknown = 0, Archery = 1, OneHanded = 2, TwoHanded = 3, Fire = 4, Frost = 5, Shock = 6,
            Alteration = 7, Conjuration = 8, Illusion = 9, Restoration = 10, Alchemy = 11, Sneak = 12, Block = 13,
            Enchanting = 14, Speech = 15;

        // Équipements (nom interne de RoR2) -> compétence.
        private static readonly Dictionary<string, int> Equipment = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "CommandMissile", Archery },      // Lance-missiles jetable
            { "Goldgat", Archery },             // Le Financeur
            { "BossHunter", Archery },          // Tricorne du chasseur de trophées
            { "BossHunterConsumed", Archery },
            { "BFG", Shock },                   // Accumulateur Preon
            { "Lightning", Shock },             // Condensateur royal
            { "Meteor", Fire },                 // Météorite luisante
            { "Molotov", Fire },                // Molotov (6 paquets)
            { "FireBallDash", Fire },           // Oeuf volcanique
            { "BurnNearby", Fire },             // Teinture de Helfire
            { "Saw", OneHanded },               // Scierang
            { "Blackhole", Alteration },        // Cube primordial
            { "CrippleWard", Alteration },      // Effigie du chagrin
            { "Jetpack", Alteration },          // Ailes de Milky Chrysalis
            { "Gateway", Alteration },          // Vase excentrique
            { "Recycle", Alteration },          // Recycleur
            { "Scanner", Alteration },          // Scanner radar
            { "LunarPortalOnUse", Alteration },
            { "DroneBackup", Conjuration },     // Renfort
            { "GummyClone", Conjuration },      // Goobo Jr.
            { "DeathProjectile", Conjuration }, // Pardonne-moi, s'il te plaît
            { "TeamWarCry", Illusion },         // Opus de Gorag
            { "CritOnUse", Illusion },          // HUD oculaire
            { "Fruit", Restoration },           // Fruit étranger
            { "PassiveHealing", Restoration },  // Esprit noueux
            { "Cleanse", Restoration },         // Douche explosive
            { "LifestealOnHit", Restoration },  // Sangsue supermassive
            { "HealAndRevive", Restoration },   // Graine de vie
            { "HealAndReviveConsumed", Restoration },
            { "Tonic", Alchemy },               // Tonique spinel
            { "QuestVolatileBattery", Fire },   // Batterie instable
            { "MultiShopCard", Speech },        // Carte exécutive
            { "Recycler", Alteration },
        };

        // Morceaux du nom de ce qui a frappé (projectile, zone...) -> compétence. Le premier qui correspond gagne.
        private static readonly (string part, int skill)[] Inflictors =
        {
            // Équipements tirés (quand RoR2 ne dit pas que c'est l'équipement)
            ("BeamSphere", Shock), ("Molotov", Fire), ("FireballVehicle", Fire), ("Sawmerang", OneHanded),
            ("GravSphere", Alteration), ("Meteor", Fire), ("DeathProjectile", Conjuration),
            // Objets
            ("MissileProjectile", Archery),     // Disque ATG
            ("Shuriken", Archery),              // Shuriken
            ("Firework", Archery),              // Feux d'artifice
            ("StickyBomb", Fire),               // Bombe collante
            ("FireTornado", Fire), ("FireMeatball", Fire), ("Fireball", Fire), ("LunarSun", Fire),
            ("IceRing", Frost), ("Frost", Frost),
            ("Lightning", Shock), ("Tesla", Shock), ("Chain", Shock),
            ("ElementalRingVoid", Alteration), ("Void", Alteration),
            ("Dagger", OneHanded),              // Dague cérémonielle
            ("Razor", OneHanded),               // Fil de rasoir
            ("Spore", Alchemy), ("Poison", Alchemy),
            ("Bear", Block),
        };

        private readonly ManualLogSource _log;
        private readonly BridgeWriter _bridge;
        private readonly Func<CharacterBody> _survivor;
        private FieldInfo _dotField;
        private bool _dotSearched;
        private int _logged;

        public SkillSource(ManualLogSource log, BridgeWriter bridge, Func<CharacterBody> survivor)
        {
            _log = log;
            _bridge = bridge;
            _survivor = survivor;
            // Utilisation d'un équipement : un peu d'expérience, même sans dégâts. L'événement de RoR2 est branché
            // par son nom (il change d'une version à l'autre) : s'il manque, seuls les dégâts comptent.
            try
            {
                EventInfo ev = typeof(EquipmentSlot).GetEvent("onServerEquipmentActivated", BindingFlags.Public | BindingFlags.Static);
                MethodInfo handler = typeof(SkillSource).GetMethod(nameof(OnEquipmentActivated), BindingFlags.NonPublic | BindingFlags.Instance);
                if (ev != null && handler != null)
                {
                    ev.AddEventHandler(null, Delegate.CreateDelegate(ev.EventHandlerType, this, handler));
                    _log.LogInfo("Experience : utilisation de l'equipement suivie");
                }
                else _log.LogInfo("Experience : utilisation de l'equipement introuvable (seuls ses degats comptent)");
            }
            catch (Exception e) { _log.LogWarning("Experience : equipement non suivi (" + e.Message + ")"); }
        }

        // Expérience donnée par une utilisation d'équipement (en "PV de coup" de Skyrim, comme pour les dégâts).
        private const float UseExperience = 12f;

        private void OnEquipmentActivated(EquipmentSlot slot, EquipmentIndex index)
        {
            try
            {
                CharacterBody survivor = _survivor();
                if (slot == null || survivor == null || slot.characterBody != survivor) return;
                int skill = SkillOfEquipment(index);
                if (skill == Unknown) return;
                _bridge?.WriteDamage(0u, UseExperience, false, 5, 0, skill);  // formId 0 : de l'expérience seulement
                if (++_logged <= 10) _log.LogInfo($"Experience : equipement {EquipmentName(index)} utilise -> competence {skill}");
            }
            catch (Exception) { }
        }

        private static string EquipmentName(EquipmentIndex index)
        {
            EquipmentDef def = EquipmentCatalog.GetEquipmentDef(index);
            return def != null ? def.name ?? "" : "";
        }

        private static int SkillOfEquipment(EquipmentIndex index)
        {
            string name = EquipmentName(index);
            if (name.Length == 0) return Unknown;
            if (Equipment.TryGetValue(name, out int skill)) return skill;
            if (name.StartsWith("Elite", StringComparison.Ordinal)) return Unknown;  // aspects d'élite : rien à utiliser
            return Alteration;
        }

        // La compétence d'un coup du survivant qui ne vient pas d'une compétence (source : DamageSource de RoR2).
        public int Classify(DamageInfo damageInfo, CharacterBody survivor, string source)
        {
            try
            {
                if (source.Contains("Equipment") && survivor != null && survivor.equipmentSlot != null)
                {
                    int skill = SkillOfEquipment(survivor.equipmentSlot.equipmentIndex);
                    if (skill != Unknown) return skill;
                }
                // Dégâts sur la durée : brûlure, saignement, poison...
                string dot = DotName(damageInfo);
                if (dot.Length > 0)
                {
                    if (dot.Contains("Burn") || dot.Contains("Helfire") || dot.Contains("Fire")) return Fire;
                    if (dot.Contains("Bleed")) return OneHanded;
                    if (dot.Contains("Blight") || dot.Contains("Poison")) return Alchemy;
                    if (dot.Contains("Fracture")) return Alteration;
                }
                GameObject inflictor = damageInfo.inflictor;
                if (inflictor != null && (survivor == null || inflictor != survivor.gameObject))
                {
                    string name = inflictor.name;
                    foreach (var (part, skill) in Inflictors)
                        if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return skill;
                }
                string type = damageInfo.damageType.ToString();
                if (type.Contains("IgniteOnHit")) return Fire;
                if (type.Contains("Shock")) return Shock;
                if (type.Contains("Freeze") || type.Contains("SlowOnHit")) return Frost;
                if (type.Contains("BleedOnHit")) return OneHanded;
                if (type.Contains("PoisonOnHit") || type.Contains("BlightOnHit")) return Alchemy;
            }
            catch (Exception) { }
            return Unknown;
        }

        private string DotName(DamageInfo damageInfo)
        {
            if (!_dotSearched)
            {
                _dotSearched = true;
                _dotField = typeof(DamageInfo).GetField("dotIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            object value = _dotField?.GetValue(damageInfo);
            if (value == null) return "";
            string text = value.ToString();
            return text == "None" || text == "-1" ? "" : text;
        }
    }
}
