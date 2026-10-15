using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // Les objets RoR2 gagnés dans Skyrim (coffres...) : Skyrim demande un rang (blanc, vert, rouge...),
    // on tire un objet au hasard de ce rang parmi ceux disponibles dans la partie, et on le donne au survivant.
    // Le nom de l'objet est renvoyé à Skyrim pour l'annoncer dans le coin de l'écran.
    internal sealed class LootReceiver
    {
        private readonly ManualLogSource _log;
        private uint _read;
        private bool _started;
        private uint _pickupSequence;
        private MethodInfo _giveItem;
        private MethodInfo _setEquipment;
        private bool _lookedUp;

        private readonly ChefKitchen _kitchen;  // 0.71 : la marmite de Skyrim (recettes du CHEF errant)

        public LootReceiver(ManualLogSource log)
        {
            _log = log;
            _kitchen = new ChefKitchen(log);
        }

        // Le nombre envoyé avec un rang : 1 à 10 objets, sauf pour la marmite (numéro de recette) et les aspects.
        private static int CountFor(int tier, int count) => tier == 11 ? count : Math.Max(1, Math.Min(count, 10));

        public void Update(BridgeWriter bridge, CharacterBody survivor)
        {
            ReportGold(bridge, survivor);
            Coop coop = Coop.Instance;
            bool client = coop != null && coop.ClientActive;
            if (!client) CollectStrayPickups(survivor);  // 0.70 : seul l'hôte déplace les objets posés (ils sont à lui)
            // 0.70 (coop, invité) : les objets donnés par l'hôte, annoncés dans notre Skyrim.
            if (client) while (coop.TryTakePickup(out int pickedTier, out string pickedName)) WritePickup(bridge, pickedTier, pickedName);
            // 0.71 : les recettes possibles à la marmite, avec ce que porte le survivant.
            try { _kitchen.Update(survivor != null && survivor.master != null ? survivor.master.inventory : null, InventorySync.Folder); }
            catch (Exception e) { _log.LogWarning("CHEF errant : " + e.Message); }
            IntPtr ring = bridge.ZonePointer(BridgeInfo.LootOffset);
            if (ring == IntPtr.Zero) return;
            if ((uint)Marshal.ReadInt32(ring, 0) != BridgeInfo.LootMagic)
            {
                // File encore vide : la toute première demande de Skyrim comptera (elle était perdue avant).
                _started = true;
                _read = 0;
                return;
            }
            uint written = (uint)Marshal.ReadInt32(ring, 4);
            Thread.MemoryBarrier();
            if (!_started)
            {
                _read = written;  // les demandes d'avant notre arrivée sont ignorées
                _started = true;
                return;
            }
            // Pas de survivant (chargement, changement de survivant) : on attend, les demandes restent dans la file.
            Inventory inventory = survivor != null && survivor.master != null ? survivor.master.inventory : null;
            if (inventory == null || Run.instance == null) return;
            if (written - _read > BridgeInfo.LootSlots) _read = written - (uint)BridgeInfo.LootSlots;

            while (_read != written)
            {
                int slot = (int)(_read % BridgeInfo.LootSlots);
                IntPtr entry = IntPtr.Add(ring, BridgeInfo.LootHeaderSize + slot * BridgeInfo.LootEventSize);
                int tier = Marshal.ReadInt32(entry, 0);
                int count = CountFor(tier, Marshal.ReadInt32(entry, 4));
                _read++;
                // 0.70 (coop, invité) : c'est l'hôte qui donne les objets (à notre survivant).
                if (client) { coop.SendLoot(tier, count); continue; }
                try { Give((t, name) => WritePickup(bridge, t, name), survivor, inventory, tier, count); }
                catch (Exception e) { _log.LogWarning("Objets : impossible de donner un objet de rang " + tier + " (" + e.Message + ")"); }
            }
        }

        // Les objets posés par terre DANS RoR2 (compétences qui fabriquent des objets, comme les R de la Vagabonde,
        // objets lâchés...) sont invisibles dans Skyrim : on les amène au survivant, qui les ramasse aussitôt
        // (et ils arrivent dans l'inventaire de Skyrim).
        private float _nextPickupScan;
        private int _pickupsCollected;

        private void CollectStrayPickups(CharacterBody survivor)
        {
            if (survivor == null || Run.instance == null || Time.unscaledTime < _nextPickupScan) return;
            _nextPickupScan = Time.unscaledTime + 1f;
            try
            {
                foreach (GenericPickupController pickup in UnityEngine.Object.FindObjectsOfType<GenericPickupController>())
                {
                    if (pickup == null || !pickup.isActiveAndEnabled) continue;
                    // 0.70 (coop) : vers le joueur le plus proche (l'objet posé par un invité lui revient).
                    CharacterBody target = survivor;
                    float best = (pickup.transform.position - survivor.corePosition).sqrMagnitude;
                    foreach (PlayerCharacterMasterController player in PlayerCharacterMasterController.instances)
                    {
                        CharacterBody other = player != null && player.master != null ? player.master.GetBody() : null;
                        if (other == null || other == survivor) continue;
                        float distance = (pickup.transform.position - other.corePosition).sqrMagnitude;
                        if (distance < best) { best = distance; target = other; }
                    }
                    if (best > 80f * 80f) continue;
                    pickup.transform.position = target.corePosition;
                    Rigidbody body = pickup.GetComponent<Rigidbody>();
                    if (body != null) { body.position = target.corePosition; body.velocity = Vector3.zero; }
                    _pickupsCollected++;
                    if (_pickupsCollected <= 10 || _pickupsCollected % 50 == 0)
                        _log.LogInfo($"Objets : objet pose dans RoR2 amene au survivant ({_pickupsCollected} en tout)");
                }
            }
            catch (Exception e) { _log.LogWarning("Objets : ramassage automatique impossible (" + e.Message + ")"); }
        }

        // L'or gagné dans RoR2 (ennemis tués...) devient des septims dans Skyrim : on publie le total gagné
        // (zone 9, octet 12), Skyrim ajoute la différence.
        private uint _lastMoney;
        private bool _moneyStarted;
        private uint _goldEarned;

        private void ReportGold(BridgeWriter bridge, CharacterBody survivor)
        {
            CharacterMaster master = survivor != null ? survivor.master : null;
            if (master == null) { _moneyStarted = false; return; }
            uint money = master.money;
            if (_moneyStarted && money > _lastMoney)
            {
                IntPtr note = bridge.ZonePointer(BridgeInfo.PickupOffset);
                if (note == IntPtr.Zero) return;
                if (_goldEarned == 0) _goldEarned = (uint)Marshal.ReadInt32(note, 12);  // suite du total (RoR2 relancé)
                _goldEarned += money - _lastMoney;
                Marshal.WriteInt32(note, 12, unchecked((int)_goldEarned));
                Marshal.WriteInt32(note, 0, unchecked((int)BridgeInfo.PickupMagic));
            }
            _lastMoney = money;
            _moneyStarted = true;
        }

        // 0.70 (coop, hôte) : un objet gagné dans le Skyrim d'un invité, donné à son survivant ; note : l'annonce à lui renvoyer.
        public void GiveFor(CharacterBody survivor, int tier, int count, Action<int, string> note)
        {
            Inventory inventory = survivor != null && survivor.master != null ? survivor.master.inventory : null;
            if (inventory == null || Run.instance == null) { _log.LogWarning("Objets : survivant de l'invite absent, objet de rang " + tier + " perdu"); return; }
            try { Give(note, survivor, inventory, tier, CountFor(tier, count)); }
            catch (Exception e) { _log.LogWarning("Objets : impossible de donner un objet de rang " + tier + " a un invite (" + e.Message + ")"); }
        }

        private void Give(Action<int, string> note, CharacterBody survivor, Inventory inventory, int tier, int count)
        {
            LookUp();
            string name;
            if (tier == 7)
            {
                // Autel de l'Ordre : chaque rang ne garde qu'une sorte d'objet (comme dans RoR2).
                MethodInfo restack = typeof(Inventory).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "ShrineRestackInventory" && m.GetParameters().Length == 1);
                if (restack == null) { _log.LogWarning("Objets : autel de l'Ordre impossible (fonction introuvable)"); return; }
                // Son générateur de hasard (type Xoroshiro128Plus, créé avec une graine).
                object rng = Activator.CreateInstance(restack.GetParameters()[0].ParameterType, (ulong)UnityEngine.Random.Range(1, int.MaxValue));
                restack.Invoke(inventory, new[] { rng });
                _log.LogInfo("Objets : autel de l'Ordre, inventaire melange");
                note(tier, "objets melanges par l'Ordre");
                return;
            }
            if (tier == 8)
            {
                // Autel du Sang : la moitié des PV, sans jamais tuer.
                HealthComponent health = survivor != null ? survivor.healthComponent : null;
                if (health == null) return;
                var damage = new DamageInfo
                {
                    damage = health.fullCombinedHealth * 0.5f,
                    position = survivor.corePosition,
                    procCoefficient = 0f,
                    damageColorIndex = DamageColorIndex.Bleed,
                    damageType = DamageType.NonLethal | DamageType.BypassArmor | DamageType.BypassBlock,
                };
                health.TakeDamage(damage);
                _log.LogInfo("Objets : autel du Sang, " + damage.damage.ToString("F0") + " PV");
                return;
            }
            if (tier == 19)
            {
                // Chargement d'une sauvegarde ou mort : les drones achetés dans Skyrim disparaissent.
                int killed = 0;
                CharacterMaster owner = survivor != null ? survivor.master : null;
                foreach (CharacterMaster drone in _drones.ToList())
                {
                    if (drone == null) continue;
                    // 0.70 (coop) : seulement les drones de ce joueur.
                    if (owner != null && drone.minionOwnership != null && drone.minionOwnership.ownerMaster != null && drone.minionOwnership.ownerMaster != owner) continue;
                    _drones.Remove(drone);
                    CharacterBody body = drone.GetBody();
                    if (body != null && body.healthComponent != null) { try { body.healthComponent.Suicide(null, null); } catch (Exception) { } }
                    UnityEngine.Object.Destroy(drone.gameObject);
                    killed++;
                }
                _drones.RemoveAll(d => d == null);
                if (killed > 0) _log.LogInfo($"Drones : {killed} drone(s) achete(s) retire(s)");
                return;
            }
            if (tier >= 20 && tier < 20 + DroneMasters.Length)
            {
                for (int i = 0; i < count; i++) SummonDrone(survivor, tier - 20);
                return;
            }
            if (tier == 11)
            {
                // 0.71 : la marmite (CHEF errant) : count = numéro de la recette.
                string dish = _kitchen.Cook(inventory, count, out string equipment);
                if (equipment != null) note(EquipmentGained, equipment);  // 0.72 : un équipement va dans le sac
                else if (dish != null) note(tier, dish);
                return;
            }
            if (tier == 9)
            {
                // 0.71 : aspect d'élite lâché par une élite de Skyrim (count : 1 feu, 2 glace, 3 foudre).
                string[] aspects = { null, "EliteFireEquipment", "EliteIceEquipment", "EliteLightningEquipment" };
                EquipmentIndex aspect = count >= 1 && count <= 3 ? EquipmentCatalog.FindEquipmentIndex(aspects[count]) : EquipmentIndex.None;
                if (aspect == EquipmentIndex.None) { _log.LogWarning("Objets : aspect d'elite " + count + " introuvable"); return; }
                _log.LogInfo("Objets : aspect d'elite donne : " + aspects[count]);
                note(EquipmentGained, aspects[count]);  // 0.72 : dans le sac de Skyrim (amulette)
                return;
            }
            if (tier == 6)
            {
                List<EquipmentDef> pool = EquipmentPool();
                if (pool.Count == 0) { _log.LogWarning("Objets : aucun equipement disponible"); return; }
                EquipmentDef def = pool[UnityEngine.Random.Range(0, pool.Count)];
                _log.LogInfo("Objets : equipement donne : " + def.name);
                note(EquipmentGained, def.name);  // 0.72 : dans le sac de Skyrim (amulette), l'équipement actif ne change pas
                return;
            }
            else
            {
                List<ItemDef> pool = ItemPool(tier);
                if (pool.Count == 0 && tier == 10) { _log.LogInfo("Objets : pas d'objet sanctifie dans cette partie (DLC ?) : un rouge a la place"); pool = ItemPool(2); }
                if (pool.Count == 0 || _giveItem == null) { _log.LogWarning("Objets : aucun objet disponible pour le rang " + tier); return; }
                ItemDef def = pool[UnityEngine.Random.Range(0, pool.Count)];
                _giveItem.Invoke(inventory, new object[] { def.itemIndex, count });
                name = Language.GetString(def.nameToken);
                if (count > 1) name += " x" + count;
            }
            _log.LogInfo("Objets : donne " + name + " (rang " + tier + ")");
            note(tier, name);
        }

        private void SetEquipment(Inventory inventory, EquipmentIndex index)
        {
            ParameterInfo[] parameters = _setEquipment.GetParameters();
            object[] args = new object[parameters.Length];
            args[0] = index;
            for (int i = 1; i < args.Length; i++)
                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : (parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null);
            _setEquipment.Invoke(inventory, args);
        }

        // Les drones et tourelles réparés dans Skyrim (rang 20 + numéro), comme les drones cassés de RoR2.
        private static readonly string[] DroneMasters =
        {
            "Drone1Master", "Drone2Master", "DroneMissileMaster", "Turret1Master", "FlameDroneMaster", "EmergencyDroneMaster", "MegaDroneMaster",
        };
        private readonly List<CharacterMaster> _drones = new List<CharacterMaster>();

        private void SummonDrone(CharacterBody survivor, int kind)
        {
            if (survivor == null) return;
            GameObject prefab = MasterCatalog.FindMasterPrefab(DroneMasters[kind]);
            if (prefab == null) { _log.LogWarning("Drones : " + DroneMasters[kind] + " introuvable"); return; }
            Vector3 at = survivor.footPosition + survivor.transform.right * 1.5f + Vector3.up * 1f;
            CharacterMaster master = new MasterSummon
            {
                masterPrefab = prefab,
                position = at,
                rotation = survivor.transform.rotation,
                summonerBodyObject = survivor.gameObject,
                ignoreTeamMemberLimit = true,
                useAmbientLevel = true,
                teamIndexOverride = TeamIndex.Player,
            }.Perform();
            if (master == null) { _log.LogWarning("Drones : " + DroneMasters[kind] + " impossible a faire apparaitre"); return; }
            _drones.RemoveAll(d => d == null);
            _drones.Add(master);
            _log.LogInfo("Drones : " + DroneMasters[kind] + " repare dans Skyrim, il rejoint le survivant");
        }

        private static ItemTier[] Tiers(int tier)
        {
            switch (tier)
            {
                case 0: return new[] { ItemTier.Tier1 };
                case 1: return new[] { ItemTier.Tier2 };
                case 2: return new[] { ItemTier.Tier3 };
                case 3: return new[] { ItemTier.Boss };
                case 4: return new[] { ItemTier.VoidTier1, ItemTier.VoidTier2, ItemTier.VoidTier3, ItemTier.VoidBoss };
                case 5: return new[] { ItemTier.Lunar };
                default: return new[] { ItemTier.Tier1 };
            }
        }

        private static List<ItemDef> ItemPool(int tier)
        {
            ItemTier[] tiers = Tiers(tier);
            var pool = new List<ItemDef>();
            for (int i = 0; i < ItemCatalog.itemCount; i++)
            {
                ItemDef def = ItemCatalog.GetItemDef((ItemIndex)i);
                if (def == null || def.hidden) continue;
                // 0.71 : rang 10 = objets sanctifiés (Hallowed Concepts), reconnus par le nom de leur rang.
                if (tier == 10 ? InventorySync.TierCode(def) != 7 : !tiers.Contains(def.tier)) continue;
                if (def.ContainsTag(ItemTag.WorldUnique)) continue;
                if (Run.instance != null && !Run.instance.IsItemAvailable(def.itemIndex)) continue;
                pool.Add(def);
            }
            return pool;
        }

        private static List<EquipmentDef> EquipmentPool()
        {
            var pool = new List<EquipmentDef>();
            for (int i = 0; i < EquipmentCatalog.equipmentCount; i++)
            {
                EquipmentDef def = EquipmentCatalog.GetEquipmentDef((EquipmentIndex)i);
                if (def == null || !def.canDrop || def.isLunar || def.isBoss) continue;
                if (Run.instance != null && !Run.instance.IsEquipmentAvailable(def.equipmentIndex)) continue;
                pool.Add(def);
            }
            return pool;
        }

        // Les fonctions de l'inventaire ont changé de nom selon les versions de RoR2 : on cherche la bonne.
        private void LookUp()
        {
            if (_lookedUp) return;
            _lookedUp = true;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (string name in new[] { "GiveItem", "GiveItemPermanent" })
            {
                _giveItem = typeof(Inventory).GetMethod(name, flags, null, new[] { typeof(ItemIndex), typeof(int) }, null);
                if (_giveItem != null) break;
            }
            _setEquipment = typeof(Inventory).GetMethods(flags).FirstOrDefault(m =>
                m.Name == "SetEquipmentIndex" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(EquipmentIndex));
            _log.LogInfo("Objets : donner un objet = " + (_giveItem != null ? _giveItem.Name : "introuvable") +
                         ", equipement = " + (_setEquipment != null ? _setEquipment.Name + "(" + _setEquipment.GetParameters().Length + ")" : "introuvable"));
        }

        // 0.72 : "rang" d'une annonce qui veut dire : équipement gagné (nom = son nom interne), à mettre dans le sac de Skyrim.
        internal const int EquipmentGained = 106;

        private void WritePickup(BridgeWriter bridge, int tier, string name)
        {
            if (tier == EquipmentGained)
            {
                EquipmentGains.Add(name);
                EquipmentIndex index = EquipmentCatalog.FindEquipmentIndex(name ?? "");
                EquipmentDef def = index != EquipmentIndex.None ? EquipmentCatalog.GetEquipmentDef(index) : null;
                tier = 6;
                name = (def != null ? Language.GetString(def.nameToken) : name) + " (equipement, dans ton sac)";
            }
            IntPtr note = bridge.ZonePointer(BridgeInfo.PickupOffset);
            if (note == IntPtr.Zero) return;
            byte[] bytes = Encoding.UTF8.GetBytes(name ?? "");
            int length = Math.Min(bytes.Length, BridgeInfo.PickupNameSize - 1);
            // Ne coupe pas un caractère accentué en deux.
            while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80) length--;
            for (int i = 0; i < BridgeInfo.PickupNameSize; i++)
                Marshal.WriteByte(note, 16 + i, i < length ? bytes[i] : (byte)0);
            Marshal.WriteInt32(note, 8, tier);
            Marshal.WriteInt32(note, 0, unchecked((int)BridgeInfo.PickupMagic));
            Thread.MemoryBarrier();
            _pickupSequence = (uint)Marshal.ReadInt32(note, 4) + 1;  // suite du numéro déjà là (RoR2 relancé)
            Marshal.WriteInt32(note, 4, unchecked((int)_pickupSequence));
        }
    }
}
