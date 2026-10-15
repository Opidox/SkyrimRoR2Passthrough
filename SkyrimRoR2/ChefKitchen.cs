using System;
using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using RoR2;
using UnityEngine;

namespace SkyrimRoR2
{
    // 0.71 : la marmite de Skyrim devient le CHEF errant de RoR2 (DLC Alloyed Collective).
    //
    // On combine deux objets PORTÉS (ou l'équipement) en un nouveau, avec les recettes du CHEF errant.
    //  - Toutes les secondes, on écrit dans recettes.txt les recettes possibles avec ce que porte le survivant
    //    ("numéro<TAB>texte du bouton"). Skyrim les montre en quittant une marmite.
    //  - Skyrim demande une recette (rang 11, nombre = numéro de la recette) : on retire les deux ingrédients et
    //    on donne le plat. Skyrim recopie ces changements dans son inventaire, comme un objet ramassé.
    //
    // Les recettes sont écrites avec les noms ANGLAIS des objets (ceux du jeu en anglais) : on retrouve chaque
    // objet par son nom anglais (ou un nom interne connu). Une recette dont un objet est introuvable (pas le DLC,
    // nom différent) est simplement ignorée, et notée une fois dans le journal.
    internal sealed class ChefKitchen
    {
        private struct Key
        {
            public bool Equipment;
            public int Index;
            public override string ToString() => Equipment ? "E" + Index : "I" + Index;
        }

        private sealed class Recipe
        {
            public int Id;
            public string A, B, Result;
            public int Count;
            public Key KeyA, KeyB, KeyResult;
            public bool Resolved;
        }

        // Ingrédient A + ingrédient B = plat (nombre). Les deux ingrédients peuvent être dans n'importe quel ordre.
        private static readonly (string a, string b, string result, int count)[] Table =
        {
            // Repas
            ("Bison Steak", "Bison Steak", "Seared Steak", 1), ("Bison Steak", "Mocha", "Seared Steak", 1),
            ("Bison Steak", "Cautious Slug", "Seared Steak", 1), ("Bison Steak", "Eclipse Lite", "Seared Steak", 1),
            ("Bison Steak", "Bustling Fungus", "Seared Steak", 1), ("Bison Steak", "Power Elixir", "Seared Steak", 1),
            ("Bison Steak", "Elusive Antlers", "Seared Steak", 1), ("Bison Steak", "Energy Drink", "Seared Steak", 1),
            ("Bison Steak", "Monster Tooth", "Seared Steak", 1),
            ("White Scrap", "Bison Steak", "Quick Fix", 1), ("White Scrap", "Mocha", "Quick Fix", 1),
            ("White Scrap", "Cautious Slug", "Quick Fix", 1), ("White Scrap", "Eclipse Lite", "Quick Fix", 1),
            ("White Scrap", "Bustling Fungus", "Quick Fix", 1), ("White Scrap", "Power Elixir", "Quick Fix", 1),
            ("White Scrap", "Elusive Antlers", "Quick Fix", 1), ("White Scrap", "Energy Drink", "Quick Fix", 1),
            ("White Scrap", "Monster Tooth", "Quick Fix", 1),
            ("Lepton Daisy", "Lepton Daisy", "Hearty Stew", 1), ("Lepton Daisy", "Squid Polyp", "Hearty Stew", 1),
            ("Lepton Daisy", "Leeching Seed", "Hearty Stew", 1), ("Lepton Daisy", "Noxious Thorn", "Hearty Stew", 1),
            ("Molten Perforator", "Charged Perforator", "Sauteed Worms", 1),
            ("57 Leaf Clover", "57 Leaf Clover", "Ultimate Meal", 1), ("57 Leaf Clover", "Alien Head", "Ultimate Meal", 1),
            ("57 Leaf Clover", "Growth Nectar", "Ultimate Meal", 1), ("57 Leaf Clover", "Wake of Vultures", "Ultimate Meal", 1),
            ("57 Leaf Clover", "Rejuvenation Rack", "Ultimate Meal", 1), ("57 Leaf Clover", "Symbiotic Scorpion", "Ultimate Meal", 1),
            ("57 Leaf Clover", "Brainstalks", "Ultimate Meal", 1), ("57 Leaf Clover", "Bottled Chaos", "Ultimate Meal", 1),
            ("57 Leaf Clover", "Interstellar Desk Plant", "Ultimate Meal", 1),
            // Objets blancs
            ("White Scrap", "Infusion", "Bison Steak", 2), ("White Scrap", "Predatory Instincts", "Lens-Maker's Glasses", 2),
            ("White Scrap", "Harvester's Scythe", "Lens-Maker's Glasses", 2), ("White Scrap", "Laser Scope", "Lens-Maker's Glasses", 4),
            ("White Scrap", "Hardlight Afterburner", "Paul's Goat Hoof", 4), ("White Scrap", "Kinetic Dampener", "Personal Shield Generator", 2),
            ("White Scrap", "Transcendence", "Personal Shield Generator", 4), ("White Scrap", "Interstellar Desk Plant", "Bustling Fungus", 4),
            ("White Scrap", "Ben's Raincoat", "Topaz Brooch", 4),
            ("Empty Bottle", "Energy Drink", "Power Elixir", 1), ("Empty Bottle", "Eclipse Lite", "Power Elixir", 1),
            ("Empty Bottle", "Bottled Chaos", "Power Elixir", 4),
            ("White Scrap", "Green Scrap", "White Scrap", 2), ("White Scrap", "Yellow Scrap", "White Scrap", 4),
            ("White Scrap", "Red Scrap", "White Scrap", 4),
            // Objets verts
            ("Bundle of Fireworks", "Sticky Bomb", "AtG Missile", 1), ("Pocket ICBM", "Green Scrap", "AtG Missile", 2),
            ("Crowbar", "Armor-Piercing Rounds", "Old Guillotine", 1), ("Wake of Vultures", "Green Scrap", "Old Guillotine", 2),
            ("Gasoline", "Sticky Bomb", "Will-o'-the-wisp", 1), ("Runald's Band", "Gasoline", "Kjaro's Band", 1),
            ("Kjaro's Band", "Stun Grenade", "Runald's Band", 1), ("Bundle of Fireworks", "Rusted Key", "Shipping Request Form", 1),
            ("Shipping Request Form", "Ghor's Tome", "Sale Star", 1), ("Personal Shield Generator", "Rose Buckler", "Kinetic Dampener", 1),
            ("Cautious Slug", "Energy Drink", "Red Whip", 1), ("Gasoline", "Gasoline", "Ignition Tank", 1),
            ("Tri-Tip Dagger", "Bolstering Lantern", "Noxious Thorn", 1), ("Monster Tooth", "Lens-Maker's Glasses", "Harvester's Scythe", 1),
            ("Monster Tooth", "Hiker's Boots", "Harvester's Scythe", 1), ("Wax Quail", "Luminous Shot", "Faraday Spur", 1),
            ("Warbanner", "Sticky Bomb", "Box of Dynamite", 1), ("Green Scrap", "Green Scrap", "Regenerating Scrap", 1),
            ("Red Scrap", "Green Scrap", "Green Scrap", 2), ("Red Scrap", "Yellow Scrap", "Green Scrap", 2),
            // Objets rouges
            ("Death Mark", "Infusion", "Alien Head", 1), ("Death Mark", "Seed of Life", "Alien Head", 1),
            ("Harvester's Scythe", "Predatory Instincts", "Laser Scope", 1), ("Primordial Cube", "Ukulele", "Sentient Meat Hook", 1),
            ("Noxious Thorn", "Death Mark", "Growth Nectar", 1), ("The Back-up", "Box of Dynamite", "Spare Drone Parts", 1),
            ("Hopoo Feather", "Wax Quail", "H3AD-5T v2", 1), ("Hopoo Feather", "Faraday Spur", "H3AD-5T v2", 1),
            ("Harvester's Scythe", "Trophy Hunter's Tricorn (Consumed)", "Rejuvenation Rack", 1), ("Bandolier", "Old Guillotine", "Brainstalks", 1),
            ("AtG Missile", "Disposable Missile Launcher", "Pocket ICBM", 1), ("AtG Missile", "Luminous Shot", "Runic Lens", 1),
            ("Sale Star", "Regenerating Scrap", "Sonorous Whispers", 1), ("Ukulele", "Razorwire", "Unstable Tesla Coil", 1),
            ("Hopoo Feather", "Ignition Tank", "Hardlight Afterburner", 1), ("Ukulele", "Shuriken", "Electric Boomerang", 1),
            // Équipements
            ("Fuel Cell", "Ben's Raincoat", "Blast Shower", 1), ("Gasoline", "Paul's Goat Hoof", "Volcanic Egg", 1),
            ("Shuriken", "Tri-Tip Dagger", "Sawmerang", 1), ("Fuel Cell", "Bottled Chaos", "Recycler", 1),
            ("Fuel Cell", "AtG Missile", "Disposable Missile Launcher", 1), ("Gasoline", "Fuel Cell", "Molotov (6-Pack)", 1),
            ("Sale Star", "Shipping Request Form", "Executive Card", 1),
            ("Trophy Hunter's Tricorn (Consumed)", "Armor-Piercing Rounds", "Trophy Hunter's Tricorn", 1),
            // Objets de boss
            ("Old War Stealth Kit", "Runald's Band", "Genesis Loop", 1), ("Infusion", "Leeching Seed", "Titanic Knurl", 1),
            ("Infusion", "Squid Polyp", "Queen's Gland", 1), ("Razorwire", "Leeching Seed", "Planula", 1),
            ("AtG Missile", "Kjaro's Band", "Molten Perforator", 1), ("AtG Missile", "Royal Capacitor", "Charged Perforator", 1),
            ("Titanic Knurl", "Aurelionite's Blessing", "Halcyon Seed", 1), ("Death Mark", "Lepton Daisy", "Mired Urn", 1),
            ("Will-o'-the-wisp", "Rose Buckler", "Little Disciple", 1), ("Will-o'-the-wisp", "Noxious Thorn", "Shatterspleen", 1),
            ("Sentry Key", "Box of Dynamite", "Empathy Cores", 1), ("Squid Polyp", "Old Guillotine", "Defense Nucleus", 1),
            // Objets d'élite
            ("Wake of Vultures", "Growth Nectar", "Aurelionite's Blessing", 1), ("Wake of Vultures", "Interstellar Desk Plant", "His Reassurance", 1),
            ("Wake of Vultures", "Beads of Fealty", "His Spiteful Boon", 1), ("Wake of Vultures", "Sentry Key", "Of One Mind", 1),
        };

        // Noms (simplifiés) -> nom interne connu, quand le nom anglais ne suffit pas.
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            { "whitescrap", "ScrapWhite" }, { "greenscrap", "ScrapGreen" }, { "redscrap", "ScrapRed" }, { "yellowscrap", "ScrapYellow" },
            { "regeneratingscrap", "RegeneratingScrap" }, { "atgmissile", "Missile" }, { "monstertooth", "Tooth" },
            { "predatoryinstincts", "AttackSpeedOnCrit" }, { "57leafclover", "Clover" }, { "fuelcell", "EquipmentMagazine" },
            { "trophyhunterstricornconsumed", "BossHunterConsumed" }, { "trophyhunterstricorn", "BossHunter" },
            { "disposablemissilelauncher", "CommandMissile" }, { "royalcapacitor", "Lightning" }, { "primordialcube", "Blackhole" },
            { "molotov6pack", "Molotov" }, { "volcanicegg", "FireBallDash" }, { "sawmerang", "Saw" }, { "recycler", "Recycle" },
            { "executivecard", "MultiShopCard" }, { "blastshower", "Cleanse" }, { "bisonsteak", "FlatHealth" }, { "infusion", "Infusion" },
            { "lensmakersglasses", "CritGlasses" }, { "paulsgoathoof", "Hoof" }, { "personalshieldgenerator", "PersonalShield" },
            { "bustlingfungus", "Mushroom" }, { "topazbrooch", "BarrierOnKill" }, { "powerelixir", "HealingPotion" },
            { "bundleoffireworks", "Firework" }, { "stickybomb", "StickyBomb" }, { "pocketicbm", "MoreMissile" },
            { "crowbar", "Crowbar" }, { "armorpiercingrounds", "BossDamageBonus" }, { "oldguillotine", "ExecuteLowHealthElite" },
            { "wakeofvultures", "HeadHunter" }, { "gasoline", "IgniteOnKill" }, { "willothewisp", "ExplodeOnDeath" },
            { "runaldsband", "IceRing" }, { "kjarosband", "FireRing" }, { "stungrenade", "StunChanceOnHit" }, { "rustedkey", "TreasureCache" },
            { "ghorstome", "BonusGoldPackOnKill" }, { "rosebuckler", "SprintArmor" }, { "cautiousslug", "HealWhileSafe" },
            { "energydrink", "SprintBonus" }, { "redwhip", "SprintOutOfCombat" }, { "tritipdagger", "BleedOnHit" },
            { "harvestersscythe", "HealOnCrit" }, { "waxquail", "JumpBoost" }, { "warbanner", "WardOnLevel" },
            { "deathmark", "DeathMark" }, { "alienhead", "AlienHead" }, { "seedoflife", "HealAndRevive" }, { "laserscope", "CritDamage" },
            { "ukulele", "ChainLightning" }, { "sentientmeathook", "BounceNearby" }, { "growthnectar", "BoostAllStats" },
            { "theback-up", "DroneBackup" }, { "thebackup", "DroneBackup" }, { "hopoofeather", "Feather" }, { "h3ad5tv2", "FallBoots" },
            { "rejuvenationrack", "IncreaseHealing" }, { "bandolier", "Bandolier" }, { "brainstalks", "KillEliteFrenzy" },
            { "razorwire", "Thorns" }, { "unstableteslacoil", "ShockNearby" }, { "hardlightafterburner", "UtilitySkillMagazine" },
            { "shuriken", "PrimarySkillShuriken" }, { "bensraincoat", "ImmuneToDebuff" }, { "bottledchaos", "RandomEquipmentTrigger" },
            { "interstellardeskplant", "Plant" }, { "transcendence", "ShieldOnly" },
            { "leptondaisy", "TPHealingNova" }, { "squidpolyp", "Squid" }, { "leechingseed", "Seed" }, { "noxiousthorn", "TriggerEnemyDebuffs" },
            { "symbioticscorpion", "PermanentDebuffOnHit" }, { "mocha", "AttackSpeedAndMoveSpeed" }, { "elusiveantlers", "SpeedBoostPickup" },
            { "oldwarstealthkit", "Phasing" }, { "genesisloop", "NovaOnLowHealth" }, { "titanicknurl", "Knurl" },
            { "queensgland", "BeetleGland" }, { "planula", "ParentEgg" }, { "moltenperforator", "FireballsOnHit" },
            { "chargedperforator", "LightningStrikeOnHit" }, { "halcyonseed", "TitanGoldDuringTP" },             { "miredurn", "SiphonOnLowHealth" }, { "littledisciple", "SprintWisp" }, { "shatterspleen", "BleedOnHitAndExplode" },
            { "defensenucleus", "MinorConstructOnKill" }, { "empathycores", "RoboBallBuddy" },
            { "beadsoffealty", "LunarTrinket" }, { "spaceshipdroneparts", "DroneWeapons" }, { "sparedroneparts", "DroneWeapons" },
            { "luminousshot", "IncreasePrimaryDamage" }, { "runiclens", "MeteorAttackOnHighDamage" }, { "electricboomerang", "StunAndPierce" },
            { "salestar", "LowerPricedChests" },         };

        private readonly ManualLogSource _log;
        private readonly List<Recipe> _recipes = new List<Recipe>();
        private bool _resolved;
        private float _nextWrite;
        private string _lastWritten;
        private Dictionary<string, Key> _byName;

        public ChefKitchen(ManualLogSource log)
        {
            _log = log;
            for (int i = 0; i < Table.Length; i++)
            {
                var (a, b, result, count) = Table[i];
                _recipes.Add(new Recipe { Id = i + 1, A = a, B = b, Result = result, Count = count });
            }
        }

        private static string Simple(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in (text ?? "").ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static MethodInfo _englishLookup;
        private static bool _englishSearched;

        // Le nom anglais d'un objet (quelle que soit la langue du jeu).
        private static string English(string token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            if (!_englishSearched)
            {
                _englishSearched = true;
                _englishLookup = typeof(Language).GetMethod("GetString", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
            }
            try
            {
                if (_englishLookup != null)
                {
                    string text = _englishLookup.Invoke(null, new object[] { token, "en" }) as string;
                    if (!string.IsNullOrEmpty(text) && text != token) return text;
                }
            }
            catch (Exception) { }
            return Language.GetString(token);
        }

        private void Resolve()
        {
            if (_resolved || ItemCatalog.itemCount == 0) return;
            _resolved = true;
            _byName = new Dictionary<string, Key>();
            var byInternal = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ItemCatalog.itemCount; i++)
            {
                ItemDef def = ItemCatalog.GetItemDef((ItemIndex)i);
                if (def == null) continue;
                var key = new Key { Equipment = false, Index = i };
                byInternal[def.name] = key;
                string name = Simple(English(def.nameToken));
                if (name.Length > 0 && !_byName.ContainsKey(name)) _byName[name] = key;
            }
            for (int i = 0; i < EquipmentCatalog.equipmentCount; i++)
            {
                EquipmentDef def = EquipmentCatalog.GetEquipmentDef((EquipmentIndex)i);
                if (def == null) continue;
                var key = new Key { Equipment = true, Index = i };
                if (!byInternal.ContainsKey(def.name)) byInternal[def.name] = key;
                string name = Simple(English(def.nameToken));
                if (name.Length > 0 && !_byName.ContainsKey(name)) _byName[name] = key;
            }
            bool Find(string wanted, out Key key)
            {
                string simple = Simple(wanted);
                if (_byName.TryGetValue(simple, out key)) return true;
                if (Aliases.TryGetValue(simple, out string internalName) && byInternal.TryGetValue(internalName, out key)) return true;
                // Un nom qui commence par l'autre ("Faraday Spur" / "Faraday Spurt", "AtG Missile Mk. 1"...).
                var close = _byName.Where(p => p.Key.Length >= 5 && simple.Length >= 5 && (p.Key.StartsWith(simple) || simple.StartsWith(p.Key))).ToList();
                if (close.Count == 1) { key = close[0].Value; return true; }
                return false;
            }
            int ok = 0;
            var missing = new HashSet<string>();
            foreach (Recipe recipe in _recipes)
            {
                bool a = Find(recipe.A, out recipe.KeyA), b = Find(recipe.B, out recipe.KeyB), r = Find(recipe.Result, out recipe.KeyResult);
                recipe.Resolved = a && b && r && !(recipe.KeyA.Equipment && recipe.KeyB.Equipment);
                if (recipe.Resolved) ok++;
                if (!a) missing.Add(recipe.A);
                if (!b) missing.Add(recipe.B);
                if (!r) missing.Add(recipe.Result);
            }
            _log.LogInfo($"CHEF errant : {ok} recettes sur {_recipes.Count}" + (missing.Count > 0 ? " ; introuvables : " + string.Join(", ", missing) : ""));
        }

        private static int Has(Inventory inventory, Key key)
        {
            if (key.Equipment) return inventory.currentEquipmentIndex == (EquipmentIndex)key.Index ? 1 : 0;
            return InventorySync.StaticCount(inventory, (ItemIndex)key.Index);
        }

        private bool CanCook(Inventory inventory, Recipe recipe)
        {
            if (!recipe.Resolved) return false;
            if (recipe.KeyA.ToString() == recipe.KeyB.ToString()) return Has(inventory, recipe.KeyA) >= 2;
            return Has(inventory, recipe.KeyA) >= 1 && Has(inventory, recipe.KeyB) >= 1;
        }

        private static string Display(Key key)
        {
            if (key.Equipment)
            {
                EquipmentDef def = EquipmentCatalog.GetEquipmentDef((EquipmentIndex)key.Index);
                return def != null ? Clean(Language.GetString(def.nameToken)) : "?";
            }
            ItemDef item = ItemCatalog.GetItemDef((ItemIndex)key.Index);
            return item != null ? Clean(Language.GetString(item.nameToken)) : "?";
        }

        private static string Clean(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]*>", "").Replace('\t', ' ').Replace('\n', ' ').Trim();

        // Toutes les secondes : les recettes possibles, pour Skyrim (recettes.txt).
        public void Update(Inventory inventory, string folder)
        {
            if (inventory == null || Time.unscaledTime < _nextWrite) return;
            _nextWrite = Time.unscaledTime + 1f;
            Resolve();
            if (!_resolved) return;
            var text = new StringBuilder();
            var seen = new HashSet<string>();
            foreach (Recipe recipe in _recipes)
            {
                if (!CanCook(inventory, recipe)) continue;
                string label = $"{Display(recipe.KeyA)} + {Display(recipe.KeyB)} = {Display(recipe.KeyResult)}" + (recipe.Count > 1 ? " x" + recipe.Count : "");
                if (!seen.Add(label)) continue;
                if (label.Length > 120) label = label.Substring(0, 120);
                text.Append(recipe.Id).Append('\t').Append(label).Append('\n');
            }
            string result = text.ToString();
            if (result == _lastWritten) return;
            _lastWritten = result;
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "recettes.txt");
                File.WriteAllText(path + ".tmp", result, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e) { _log.LogWarning("CHEF errant : recettes.txt impossible a ecrire (" + e.Message + ")"); }
        }

        private static void Take(Inventory inventory, Key key, int count)
        {
            if (key.Equipment) EquipmentStates.Swap(inventory, EquipmentIndex.None);
            else InventorySync.StaticRemove(inventory, (ItemIndex)key.Index, count);
        }

        // Cuisiner la recette n° id (demandée par Skyrim). Rend le texte de l'annonce (null : impossible).
        public string Cook(Inventory inventory, int id, out string equipmentGained)
        {
            equipmentGained = null;
            Resolve();
            Recipe recipe = _recipes.FirstOrDefault(r => r.Id == id);
            if (inventory == null || recipe == null || !CanCook(inventory, recipe))
            {
                _log.LogWarning("CHEF errant : recette " + id + " impossible (ingredients plus portes ?)");
                return null;
            }
            if (recipe.KeyA.ToString() == recipe.KeyB.ToString()) Take(inventory, recipe.KeyA, 2);
            else { Take(inventory, recipe.KeyA, 1); Take(inventory, recipe.KeyB, 1); }
            if (recipe.KeyResult.Equipment)
            {
                // 0.72 : l'équipement cuisiné va dans le sac de Skyrim (amulette), comme un équipement trouvé.
                equipmentGained = EquipmentCatalog.GetEquipmentDef((EquipmentIndex)recipe.KeyResult.Index)?.name;
            }
            else InventorySync.StaticGive(inventory, (ItemIndex)recipe.KeyResult.Index, recipe.Count);
            _nextWrite = 0f;  // les recettes possibles ont changé
            string name = Display(recipe.KeyResult) + (recipe.Count > 1 ? " x" + recipe.Count : "");
            _log.LogInfo($"CHEF errant : {recipe.A} + {recipe.B} -> {name}");
            return name;
        }
    }
}
