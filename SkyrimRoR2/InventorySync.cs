using System;
using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // Les objets du survivant vivent dans l'inventaire de Skyrim (0.61) :
    //  - RoR2 écrit en continu ses objets VISIBLES dans %LOCALAPPDATA%\SkyrimRoR2\inventaire.txt, avec en tête
    //    "applique:N" (la dernière demande de Skyrim appliquée). Skyrim y lit ce que RoR2 a changé tout seul
    //    (objet ramassé, objet du vide qui en mange un autre, objet consommé...) et le recopie dans son inventaire.
    //  - Skyrim écrit dans inventaire_charge.txt ce qu'il veut (objets portés par le joueur) et augmente
    //    InventorySequence. Deux façons :
    //      "mode:remplacer" : on remplace tout (chargement d'une sauvegarde) ;
    //      "mode:ajuster"   : lignes "base:" (ce que Skyrim croit que RoR2 a) et "item:" (ce qu'il veut) : on applique
    //                         seulement la différence. Ce que RoR2 a changé entre-temps est ainsi gardé.
    //    La première demande d'une partie RoR2 est toujours appliquée en entier (RoR2 relancé).
    //  - objets_infos.txt : le catalogue des objets (nom interne, rang, nom affiché, description, drapeaux),
    //    pour que Skyrim nomme ses objets et sache lesquels on peut imprimer, fondre ou acheter.
    // Les objets cachés de RoR2 (compteurs internes, aides de difficulté) ne sont jamais touchés.
    // Et la mort : la partie RoR2 ne se termine plus (pas d'écran de fin). Le survivant reste à terre jusqu'à ce que
    // Skyrim le relève (ReviveSequence : réveil sans rechargement) ou recharge une sauvegarde (RequestId).
    internal sealed class InventorySync
    {
        private readonly ManualLogSource _log;
        private uint _appliedSequence;
        private bool _sequenceKnown;
        private string _lastWritten;
        private float _nextWrite;
        private MethodInfo _give, _remove, _count, _setEquipment;
        private bool _lookedUp;
        private Run _run;                 // la partie en cours (une nouvelle partie : tout est réappliqué)
        private bool _appliedInRun;       // une demande a déjà été appliquée dans cette partie
        private string _catalogWritten;   // dernier catalogue écrit

        // Mort du survivant gardée jusqu'à ce que Skyrim le relève.
        public static bool SurvivorDown;
        private uint _downRequest;
        private uint _downRevive;
        private bool _reviveKnown;
        private uint _reviveSeen;

        public InventorySync(ManualLogSource log)
        {
            _log = log;
        }

        internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimRoR2");

        // Appelée par le crochet de fin de partie : vrai = on empêche la fin de partie.
        public static bool BlockGameOver()
        {
            AutoRun autoRun = SkyrimRoR2Plugin.Instance != null ? SkyrimRoR2Plugin.Instance.AutoRunForSync : null;
            if (autoRun == null || !autoRun.SkyrimConnected) return false;
            if (!SurvivorDown) SkyrimRoR2Plugin.Instance.Logger_.LogInfo("Survivant mort : pas de fin de partie RoR2, Skyrim prend le relais");
            SurvivorDown = true;
            return true;
        }

        public void Update(BridgeWriter bridge, CharacterMaster master)
        {
            if (!bridge.ReadCommand(out SkyrimCommand command)) return;

            // Le compteur de réveils de Skyrim (connu dès le départ : seul un NOUVEAU réveil relève le survivant).
            if (!_reviveKnown) { _reviveKnown = true; _reviveSeen = command.ReviveSequence; }

            // 0.70 (coop) : la mort et le retour sont gérés par la coop (voir Coop.cs).
            Coop coop = Coop.Instance;
            bool coopActive = coop != null && coop.Active;
            if (coopActive && SurvivorDown) { SurvivorDown = false; _downRequest = 0; }

            // Mort : on attend que Skyrim nous relève (réveil sans rechargement) ou recharge une sauvegarde.
            if (SurvivorDown)
            {
                if (_downRequest == 0) { _downRequest = command.RequestId == 0 ? 1u : command.RequestId; _downRevive = command.ReviveSequence; }
                else if ((command.RequestId != _downRequest || command.ReviveSequence != _downRevive) && master != null && Run.instance != null)
                {
                    bool revived = command.ReviveSequence != _downRevive;
                    SurvivorDown = false;
                    _downRequest = 0;
                    _reviveSeen = command.ReviveSequence;
                    Vector3 at = master.deathFootPosition;
                    master.Respawn(at, Quaternion.identity);
                    _log.LogInfo(revived ? "Reveil dans Skyrim : le survivant revient (sans rechargement)" : "Sauvegarde Skyrim rechargee : le survivant revient");
                }
            }

            if (master == null || Run.instance == null || master.inventory == null) return;
            Inventory inventory = master.inventory;
            LookUp();

            // Nouvelle partie RoR2 (relancée) : la demande en cours est réappliquée, en entier.
            if (Run.instance != _run)
            {
                _run = Run.instance;
                _appliedInRun = false;
                _appliedSequence = 0;
                _catalogWritten = null;
            }
            WriteCatalog();

            if (!_sequenceKnown)
            {
                _sequenceKnown = true;
                _appliedSequence = 0;  // la première demande de cette session sera appliquée
            }
            // 0.70 (coop, invité) : c'est l'hôte qui change nos objets. On lui envoie la demande, et on ne répond à Skyrim
            // ("applique:N") qu'une fois l'hôte passé ET ses changements arrivés chez nous (sinon Skyrim croirait que
            // RoR2 a retiré des objets, et les retirerait de son inventaire).
            if (coop != null && coop.ClientActive)
            {
                if (command.InventorySequence != _pendingSequence && command.InventorySequence != _appliedSequence && command.InventorySequence != 0)
                {
                    _pendingSequence = command.InventorySequence;
                    string path = Path.Combine(Folder, "inventaire_charge.txt");
                    string request = File.Exists(path) ? File.ReadAllText(path) : "";
                    bool replace = !_appliedInRun || !request.Contains("mode:ajuster");
                    coop.SendInventory(_pendingSequence, request, replace);
                    _appliedInRun = true;
                    _log.LogInfo($"Objets de Skyrim : demande {_pendingSequence} envoyee a l'hote ({(replace ? "tout remplacer" : "ajustement")})");
                }
                if (_pendingSequence != 0 && coop.InventoryDone == _pendingSequence && Time.unscaledTime - coop.InventoryDoneTime > 0.5f)
                {
                    _appliedSequence = _pendingSequence;
                    _pendingSequence = 0;
                    _nextWrite = 0f;
                }
            }
            else if (command.InventorySequence != _appliedSequence && command.InventorySequence != 0)
            {
                _appliedSequence = command.InventorySequence;
                try { Apply(inventory); }
                catch (Exception e) { _log.LogWarning("Objets de Skyrim : impossible de les reprendre (" + e.Message + ")"); }
                _nextWrite = 0f;  // Skyrim attend notre réponse : on l'écrit tout de suite
            }

            // Nos objets, pour Skyrim.
            if (Time.unscaledTime < _nextWrite) return;
            _nextWrite = Time.unscaledTime + 0.5f;
            string text = Describe(inventory);
            if (text == _lastWritten) return;
            _lastWritten = text;
            WriteFile("inventaire.txt", text);
        }

        private void WriteFile(string name, string text)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string path = Path.Combine(Folder, name);
                File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e)
            {
                _log.LogWarning("Objets : " + name + " impossible a ecrire (" + e.Message + ")");
            }
        }

        // Les objets que Skyrim voit (pas les compteurs cachés de RoR2).
        private static bool Visible(ItemDef def) => def != null && !def.hidden && !SurvivorItems.Contains(def.name);

        // 0.73 : les objets donnés par un survivant lui-même (microbots du Capitaine) : pas dans l'inventaire de
        // Skyrim (ni jetables, ni rangeables), et retirés quand on change de survivant (RoR2 les redonne au retour).
        internal static readonly HashSet<string> SurvivorItems = new HashSet<string> { "CaptainDefenseMatrix" };

        internal static void RemoveSurvivorItems(Inventory inventory, ManualLogSource log)
        {
            if (inventory == null) return;
            StaticLookUp();
            foreach (string name in SurvivorItems)
            {
                ItemIndex index = ItemCatalog.FindItemIndex(name);
                if (index == ItemIndex.None) continue;
                int count = StaticCount(inventory, index);
                if (count <= 0 || s_remove == null) continue;
                try
                {
                    s_remove.Invoke(inventory, new object[] { index, count });
                    log.LogInfo($"Changement de survivant : {name} x{count} retire (objet propre au survivant)");
                }
                catch (Exception e) { log.LogWarning("Changement de survivant : " + name + " impossible a retirer (" + e.Message + ")"); }
            }
        }

        // "applique:N", puis "item:NomInterne=nombre" par ligne, puis "equipement:NomInterne".
        private string Describe(Inventory inventory)
        {
            var text = new StringBuilder();
            text.Append("applique:").Append(_appliedSequence).Append('\n');
            List<ItemIndex> order = inventory.itemAcquisitionOrder;
            for (int i = 0; order != null && i < order.Count; i++)
            {
                ItemDef def = ItemCatalog.GetItemDef(order[i]);
                int count = Count(inventory, order[i]);
                if (!Visible(def) || count <= 0) continue;
                text.Append("item:").Append(def.name).Append('=').Append(count).Append('\n');
            }
            EquipmentDef equipment = EquipmentCatalog.GetEquipmentDef(inventory.currentEquipmentIndex);
            // 0.72 : toujours la ligne (vide = aucun équipement) : Skyrim voit aussi un équipement consommé ou retiré.
            text.Append("equipement:").Append(equipment != null ? equipment.name : "").Append('\n');
            return text.ToString();
        }

        private static Dictionary<string, int> ParseCounts(string[] lines, string prefix)
        {
            var counts = new Dictionary<string, int>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (!line.StartsWith(prefix)) continue;
                int equal = line.LastIndexOf('=');
                if (equal < 0 || !int.TryParse(line.Substring(equal + 1), out int count)) continue;
                string name = line.Substring(prefix.Length, equal - prefix.Length);
                if (name.Length > 0) counts[name] = Math.Max(0, count);
            }
            return counts;
        }

        private uint _pendingSequence;  // 0.70 (coop, invité) : demande envoyée à l'hôte, pas encore appliquée

        private void Apply(Inventory inventory)
        {
            string path = Path.Combine(Folder, "inventaire_charge.txt");
            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            bool replace = !_appliedInRun || !text.Contains("mode:ajuster");
            ApplyText(inventory, text, replace, _log, "", _appliedSequence);
            _appliedInRun = true;
        }

        private static MethodInfo s_give, s_remove, s_count, s_setEquipment;
        private static bool s_lookedUp;

        // 0.70 : appliquer une demande de Skyrim à un inventaire (le nôtre, ou celui d'un invité pour l'hôte).
        public static void ApplyText(Inventory inventory, string text, bool replace, ManualLogSource log, string who, uint sequence = 0)
        {
            StaticLookUp();
            MethodInfo _give = s_give, _remove = s_remove;
            string[] lines = (text ?? "").Split('\n');
            Dictionary<string, int> wanted = ParseCounts(lines, "item:");
            Dictionary<string, int> basis = ParseCounts(lines, "base:");

            int given = 0, removed = 0;
            if (replace)
            {
                // Tout est remplacé : les objets visibles absents de la liste sont retirés.
                List<ItemIndex> order = inventory.itemAcquisitionOrder != null ? new List<ItemIndex>(inventory.itemAcquisitionOrder) : new List<ItemIndex>();
                foreach (ItemIndex item in order)
                {
                    ItemDef def = ItemCatalog.GetItemDef(item);
                    if (!Visible(def) || wanted.ContainsKey(def.name)) continue;
                    int count = StaticCount(inventory, item);
                    if (count > 0 && _remove != null) { _remove.Invoke(inventory, new object[] { item, count }); removed += count; }
                }
                foreach (var pair in wanted)
                {
                    ItemIndex index = ItemCatalog.FindItemIndex(pair.Key);
                    if (index == ItemIndex.None || !Visible(ItemCatalog.GetItemDef(index))) continue;
                    int delta = pair.Value - StaticCount(inventory, index);
                    if (delta > 0 && _give != null) { _give.Invoke(inventory, new object[] { index, delta }); given += delta; }
                    else if (delta < 0 && _remove != null) { _remove.Invoke(inventory, new object[] { index, -delta }); removed -= delta; }
                }
            }
            else
            {
                // Seulement ce que Skyrim a changé (voulu - base) : ce que RoR2 a changé entre-temps reste.
                var names = new HashSet<string>(wanted.Keys);
                names.UnionWith(basis.Keys);
                foreach (string name in names)
                {
                    wanted.TryGetValue(name, out int target);
                    basis.TryGetValue(name, out int before);
                    int delta = target - before;
                    if (delta == 0) continue;
                    ItemIndex index = ItemCatalog.FindItemIndex(name);
                    if (index == ItemIndex.None || !Visible(ItemCatalog.GetItemDef(index))) continue;
                    if (delta > 0 && _give != null) { _give.Invoke(inventory, new object[] { index, delta }); given += delta; }
                    else if (delta < 0 && _remove != null)
                    {
                        int take = Math.Min(-delta, StaticCount(inventory, index));
                        if (take > 0) { _remove.Invoke(inventory, new object[] { index, take }); removed += take; }
                    }
                }
            }

            // L'équipement : seulement si Skyrim en parle ("equipement:" seul = aucun).
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (!line.StartsWith("equipement:")) continue;
                string name = line.Substring(11).Trim();
                EquipmentIndex index = name.Length > 0 ? EquipmentCatalog.FindEquipmentIndex(name) : EquipmentIndex.None;
                // 0.72 : l'amulette portée dans Skyrim. Rien à faire si c'est déjà lui (sa recharge reste) ; sinon on garde
                // la recharge de l'ancien et on reprend celle du nouveau (changer d'amulette ne recharge pas l'équipement).
                if (index == inventory.currentEquipmentIndex || (name.Length > 0 && index == EquipmentIndex.None)) continue;
                EquipmentStates.Swap(inventory, index);
            }
            log.LogInfo($"Objets de Skyrim appliques{(string.IsNullOrEmpty(who) ? "" : " pour " + who)} ({(replace ? "tout remplace" : "ajustement")}, demande {sequence}) : +{given} / -{removed}");
        }

        internal static void StaticLookUp()
        {
            if (s_lookedUp) return;
            s_lookedUp = true;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (string name in new[] { "GiveItem", "GiveItemPermanent" })
                if ((s_give = typeof(Inventory).GetMethod(name, flags, null, new[] { typeof(ItemIndex), typeof(int) }, null)) != null) break;
            foreach (string name in new[] { "RemoveItem", "RemoveItemPermanent" })
                if ((s_remove = typeof(Inventory).GetMethod(name, flags, null, new[] { typeof(ItemIndex), typeof(int) }, null)) != null) break;
            foreach (string name in new[] { "GetItemCountPermanent", "GetItemCount", "GetItemCountEffective" })
                if ((s_count = typeof(Inventory).GetMethod(name, flags, null, new[] { typeof(ItemIndex) }, null)) != null) break;
            s_setEquipment = typeof(Inventory).GetMethods(flags).FirstOrDefault(m =>
                m.Name == "SetEquipmentIndex" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(EquipmentIndex));
        }

        internal static int StaticCount(Inventory inventory, ItemIndex item)
        {
            if (s_count == null) return 0;
            object result = s_count.Invoke(inventory, new object[] { item });
            return result is int n ? n : 0;
        }

        // 0.71 : donner / retirer des objets (pour la marmite).
        internal static void StaticGive(Inventory inventory, ItemIndex item, int count)
        {
            StaticLookUp();
            if (count > 0) s_give?.Invoke(inventory, new object[] { item, count });
        }

        internal static void StaticRemove(Inventory inventory, ItemIndex item, int count)
        {
            StaticLookUp();
            if (count > 0) s_remove?.Invoke(inventory, new object[] { item, count });
        }

        internal static void StaticSetEquipment(Inventory inventory, EquipmentIndex index)
        {
            if (s_setEquipment == null) return;
            ParameterInfo[] parameters = s_setEquipment.GetParameters();
            object[] args = new object[parameters.Length];
            args[0] = index;
            for (int i = 1; i < args.Length; i++)
                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : (parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null);
            s_setEquipment.Invoke(inventory, args);
        }

        // 0.72 : les équipements, pour Skyrim (amulettes) : nom interne, nom affiché, description, drapeaux
        //   (l lunaire, b boss, e aspect d'élite).
        private void WriteEquipmentCatalog()
        {
            var text = new StringBuilder();
            int count = 0;
            for (int i = 0; i < EquipmentCatalog.equipmentCount; i++)
            {
                EquipmentDef def = EquipmentCatalog.GetEquipmentDef((EquipmentIndex)i);
                if (def == null || string.IsNullOrEmpty(def.nameToken)) continue;
                string name = Clean(Language.GetString(def.nameToken));
                if (name.Length == 0 || name == def.nameToken) name = def.name;
                string description = Clean(Language.GetString(def.pickupToken));
                string flags = (def.isLunar ? "l" : "") + (def.isBoss ? "b" : "") + (def.name.StartsWith("Elite", StringComparison.Ordinal) ? "e" : "");
                text.Append(def.name).Append('\t').Append(name).Append('\t').Append(description).Append('\t').Append(flags).Append('\n');
                count++;
            }
            WriteFile("equipements_infos.txt", text.ToString());
            _log.LogInfo($"Equipements : catalogue de {count} equipements ecrit pour Skyrim (equipements_infos.txt)");
        }

        // Le catalogue des objets pour Skyrim : nom interne, rang, nom affiché, description, drapeaux.
        //   rang : 0 blanc, 1 vert, 2 rouge, 3 jaune, 4 vide, 5 lunaire, 6 autre (objet consommé...)
        //   drapeaux : "f" ferraille, "t" trouvable (dans la partie, peut sortir d'un coffre ou d'une imprimante)
        private void WriteCatalog()
        {
            if (_catalogWritten != null) return;
            var text = new StringBuilder();
            int count = 0;
            for (int i = 0; i < ItemCatalog.itemCount; i++)
            {
                ItemDef def = ItemCatalog.GetItemDef((ItemIndex)i);
                if (!Visible(def)) continue;
                int tier = TierCode(def);
                string flags = "";
                if (def.ContainsTag(ItemTag.Scrap)) flags += "f";
                bool findable = tier <= 5 && !def.ContainsTag(ItemTag.Scrap) && !def.ContainsTag(ItemTag.WorldUnique) &&
                                (Run.instance == null || Run.instance.IsItemAvailable(def.itemIndex));
                if (findable) flags += "t";
                string name = Clean(Language.GetString(def.nameToken));
                if (name.Length == 0) name = def.name;
                string description = Clean(Language.GetString(def.pickupToken));
                text.Append(def.name).Append('\t').Append(tier).Append('\t').Append(name).Append('\t').Append(description).Append('\t').Append(flags).Append('\n');
                count++;
            }
            _catalogWritten = text.ToString();
            WriteFile("objets_infos.txt", _catalogWritten);
            WriteEquipmentCatalog();
            _log.LogInfo($"Objets : catalogue de {count} objets ecrit pour Skyrim (objets_infos.txt)");
        }

        // 0.71 : 7 sanctifié (Hallowed Concepts), 8 repas (CHEF errant) : rangs nouveaux, reconnus par leur nom.
        internal static int TierCode(ItemDef def)
        {
            int code = TierCode(def.tier);
            if (code != 6) return code;
            string tierName = "";
            try { tierName = ItemTierCatalog.GetItemTierDef(def.tier)?.name ?? def.tier.ToString(); }
            catch (Exception) { tierName = def.tier.ToString(); }
            if (tierName.IndexOf("Hallow", StringComparison.OrdinalIgnoreCase) >= 0) return 7;
            if (tierName.IndexOf("Food", StringComparison.OrdinalIgnoreCase) >= 0 || tierName.IndexOf("Meal", StringComparison.OrdinalIgnoreCase) >= 0) return 8;
            try
            {
                foreach (ItemTag tag in def.tags ?? new ItemTag[0])
                {
                    string t = tag.ToString();
                    if (t.IndexOf("Food", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Meal", StringComparison.OrdinalIgnoreCase) >= 0) return 8;
                }
            }
            catch (Exception) { }
            return 6;
        }

        internal static int TierCode(ItemTier tier)
        {
            switch (tier)
            {
                case ItemTier.Tier1: return 0;
                case ItemTier.Tier2: return 1;
                case ItemTier.Tier3: return 2;
                case ItemTier.Boss: return 3;
                case ItemTier.VoidTier1:
                case ItemTier.VoidTier2:
                case ItemTier.VoidTier3:
                case ItemTier.VoidBoss: return 4;
                case ItemTier.Lunar: return 5;
                default: return 6;
            }
        }

        // Sans balises de couleur, tabulations ni lettres que la police de Skyrim n'a pas.
        private static string Clean(string text)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]*>", "");
            text = text.Replace("œ", "oe").Replace("Œ", "Oe").Replace("æ", "ae").Replace("Æ", "Ae");
            var clean = new StringBuilder(text.Length);
            foreach (char c in text)
                if ((c >= 0x20 && c < 0x7F && c != '|') || (c >= 0xA0 && c <= 0x17F && c != 0xAD)) clean.Append(c);
                else if (c == '\t' || c == '\n' || c == '\r') clean.Append(' ');
            return clean.ToString().Trim();
        }

        private void SetEquipment(Inventory inventory, EquipmentIndex index)
        {
            if (_setEquipment == null) return;
            ParameterInfo[] parameters = _setEquipment.GetParameters();
            object[] args = new object[parameters.Length];
            args[0] = index;
            for (int i = 1; i < args.Length; i++)
                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : (parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null);
            _setEquipment.Invoke(inventory, args);
        }

        private int Count(Inventory inventory, ItemIndex item)
        {
            if (_count == null) return 0;
            object result = _count.Invoke(inventory, new object[] { item });
            return result is int n ? n : 0;
        }

        private void LookUp()
        {
            if (_lookedUp) return;
            _lookedUp = true;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (string name in new[] { "GiveItem", "GiveItemPermanent" })
                if ((_give = typeof(Inventory).GetMethod(name, flags, null, new[] { typeof(ItemIndex), typeof(int) }, null)) != null) break;
            foreach (string name in new[] { "RemoveItem", "RemoveItemPermanent" })
                if ((_remove = typeof(Inventory).GetMethod(name, flags, null, new[] { typeof(ItemIndex), typeof(int) }, null)) != null) break;
            foreach (string name in new[] { "GetItemCountPermanent", "GetItemCount", "GetItemCountEffective" })
                if ((_count = typeof(Inventory).GetMethod(name, flags, null, new[] { typeof(ItemIndex) }, null)) != null) break;
            _setEquipment = typeof(Inventory).GetMethods(flags).FirstOrDefault(m =>
                m.Name == "SetEquipmentIndex" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(EquipmentIndex));
            _log.LogInfo($"Objets de Skyrim : donner {_give?.Name ?? "?"}, retirer {_remove?.Name ?? "?"}, compter {_count?.Name ?? "?"}");
        }
    }
}
