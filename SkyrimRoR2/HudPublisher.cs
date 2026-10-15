using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // Publie dans le pont (zone 7) tout ce qu'affiche le HUD de RoR2 :
    // vie / bouclier / barrière, niveau, expérience, or, les 4 compétences, l'équipement et les objets.
    // Skyrim s'en sert pour dessiner le HUD par-dessus son image.
    internal sealed class HudPublisher
    {
        private readonly ManualLogSource _log;
        private readonly IconExporter _icons;
        private uint _sequence;
        private bool _errorLogged;

        // Ces fonctions ont changé de nom selon les versions de RoR2 : on les cherche une fois.
        private MethodInfo _getItemCount;
        private bool _itemCountSearched;

        public HudPublisher(ManualLogSource log)
        {
            _log = log;
            _icons = new IconExporter(log);
        }

        public void Update(BridgeWriter bridge, CharacterBody body, CharacterMaster master)
        {
            IntPtr hud = bridge.ZonePointer(BridgeInfo.HudOffset);
            if (hud == IntPtr.Zero) return;

            try
            {
                Write(hud, body, master);
            }
            catch (Exception e)
            {
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    _log.LogError("HUD : erreur pendant la lecture des infos RoR2 : " + e);
                }
            }
            try { WriteAllies(body, master); } catch (Exception) { }
            _icons.Process();
        }

        // ---------- 0.73 : la liste des alliés (à gauche du HUD, comme dans RoR2) ----------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        private IntPtr _allyView;
        private bool _allyOpenTried;
        private uint _allySequence;
        private float _nextAllies;
        private readonly byte[] _allyEntry = new byte[BridgeInfo.AllyHudEntrySize];

        private void WriteAllies(CharacterBody body, CharacterMaster master)
        {
            if (Time.unscaledTime < _nextAllies) return;
            _nextAllies = Time.unscaledTime + 0.2f;
            if (_allyView == IntPtr.Zero)
            {
                if (_allyOpenTried) return;
                _allyOpenTried = true;
                IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.AllyHudMapSize, BridgeInfo.AllyHudMapName);
                if (mapping != IntPtr.Zero) _allyView = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.AllyHudMapSize);
                if (_allyView == IntPtr.Zero) { _log.LogWarning("HUD : pont des allies impossible a ouvrir"); return; }
            }
            // Les alliés de ce survivant (drones, tourelles, invocations), regroupés par sorte, les plus nombreux d'abord.
            var groups = new List<(string key, string icon, float health, float shield, int count)>();
            if (body != null && master != null)
                foreach (CharacterMaster minion in CharacterMaster.readOnlyInstancesList)
                {
                    if (minion == null || minion == master || minion.minionOwnership == null || minion.minionOwnership.ownerMaster != master) continue;
                    CharacterBody ally = minion.GetBody();
                    if (ally == null || ally.healthComponent == null || !ally.healthComponent.alive) continue;
                    HealthComponent h = ally.healthComponent;
                    float full = Mathf.Max(1f, h.fullHealth + h.fullShield);
                    string key = ally.name.Replace("(Clone)", "");
                    int at = groups.FindIndex(g => g.key == key);
                    if (at >= 0)
                    {
                        var g = groups[at];
                        groups[at] = (g.key, g.icon, Mathf.Min(g.health, h.health / full), Mathf.Min(g.shield, h.shield / full), g.count + 1);
                    }
                    else groups.Add((key, _icons.RequestTexture(ally.portraitIcon), h.health / full, h.shield / full, 1));
                }
            int n = Mathf.Min(groups.Count, BridgeInfo.AllyHudMax);
            for (int i = 0; i < n; i++)
            {
                Array.Clear(_allyEntry, 0, _allyEntry.Length);
                byte[] text = System.Text.Encoding.ASCII.GetBytes(groups[i].icon ?? "");
                Array.Copy(text, _allyEntry, Math.Min(text.Length, BridgeInfo.IconNameSize - 1));
                BitConverter.GetBytes(Mathf.Clamp01(groups[i].health)).CopyTo(_allyEntry, 48);
                BitConverter.GetBytes(Mathf.Clamp01(groups[i].shield)).CopyTo(_allyEntry, 52);
                BitConverter.GetBytes(groups[i].count).CopyTo(_allyEntry, 56);
                Marshal.Copy(_allyEntry, 0, IntPtr.Add(_allyView, BridgeInfo.AllyHudHeaderSize + i * BridgeInfo.AllyHudEntrySize), _allyEntry.Length);
            }
            Marshal.WriteInt32(_allyView, 8, n);
            Marshal.WriteInt32(_allyView, 4, unchecked((int)++_allySequence));
            Marshal.WriteInt32(_allyView, 0, unchecked((int)BridgeInfo.AllyHudMagic));
        }

        private void Write(IntPtr hud, CharacterBody body, CharacterMaster master)
        {
            // Pas de survivant : HUD vide (niveau 0 = rien à afficher).
            if (body == null || master == null)
            {
                Marshal.WriteInt32(IntPtr.Add(hud, BridgeInfo.HudSpecialOffset), 0, 0);  // pas de jauge
                Marshal.WriteInt32(hud, 32, 0);
                Marshal.WriteInt32(hud, 44, 0);
                Finish(hud);
                return;
            }

            HealthComponent health = body.healthComponent;
            if (health != null)
            {
                WriteFloat(hud, 8, health.health);
                WriteFloat(hud, 12, health.shield);
                WriteFloat(hud, 16, health.barrier);
                WriteFloat(hud, 20, health.fullHealth);
                WriteFloat(hud, 24, health.fullShield);
                WriteFloat(hud, 28, health.fullBarrier);
            }

            Marshal.WriteInt32(hud, 32, Mathf.Max(1, Mathf.RoundToInt(body.level)));
            WriteFloat(hud, 36, LevelSync.Active ? -1f : ExperienceFraction());  // 0.66 : barre cachée si le niveau suit Skyrim
            Marshal.WriteInt32(hud, 40, unchecked((int)master.money));

            // Les 4 compétences.
            SkillLocator skills = body.skillLocator;
            WriteSkill(hud, 0, skills != null ? skills.primary : null);
            WriteSkill(hud, 1, skills != null ? skills.secondary : null);
            WriteSkill(hud, 2, skills != null ? skills.utility : null);
            WriteSkill(hud, 3, skills != null ? skills.special : null);

            // L'équipement (case 5) et les objets.
            Inventory inventory = master.inventory;
            WriteEquipment(hud, inventory);
            WriteItems(hud, inventory);

            // Le portrait et le nom du survivant.
            WriteText(IntPtr.Add(hud, BridgeInfo.HudPortraitOffset), _icons.RequestTexture(body.portraitIcon));
            WriteUtf8(IntPtr.Add(hud, BridgeInfo.HudNameOffset), SkyrimLanguage.Text(body.baseNameToken));

            // La jauge propre au survivant (recharge du Railgunner, corruption du Démon du vide, Chercheuse).
            WriteSpecial(IntPtr.Add(hud, BridgeInfo.HudSpecialOffset), body);

            Finish(hud);
        }

        private void Finish(IntPtr hud)
        {
            Marshal.WriteInt32(hud, 4, unchecked((int)++_sequence));
            Marshal.WriteInt32(hud, 0, unchecked((int)BridgeInfo.HudMagicValue));
        }

        private static float ExperienceFraction()
        {
            TeamManager team = TeamManager.instance;
            if (team == null) return 0f;
            ulong xp = team.GetTeamExperience(TeamIndex.Player);
            ulong current = team.GetTeamCurrentLevelExperience(TeamIndex.Player);
            ulong next = team.GetTeamNextLevelExperience(TeamIndex.Player);
            if (next <= current) return 0f;
            return Mathf.Clamp01((float)(xp - Math.Min(xp, current)) / (next - current));
        }

        private void WriteSkill(IntPtr hud, int index, GenericSkill skill)
        {
            IntPtr slot = IntPtr.Add(hud, BridgeInfo.HudHeaderSize + index * BridgeInfo.HudSlotSize);
            if (skill == null || skill.skillDef == null)
            {
                WriteSlot(slot, "", 0f, 0f, 0, 0);
                return;
            }
            string icon = _icons.Request(skill.skillDef.icon);
            WriteSlot(slot, icon, skill.cooldownRemaining, RechargeInterval(skill), skill.stock, skill.maxStock);
        }

        // Durée d'une recharge, objets compris (le nom a changé selon les versions de RoR2).
        private static float RechargeInterval(GenericSkill skill)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = typeof(GenericSkill);
            foreach (string name in new[] { "finalRechargeInterval", "CalculateFinalRechargeInterval", "GetFinalRechargeInterval" })
            {
                try
                {
                    PropertyInfo property = type.GetProperty(name, flags);
                    if (property != null && property.GetValue(skill) is float p) return p;
                    FieldInfo field = type.GetField(name, flags);
                    if (field != null && field.GetValue(skill) is float f) return f;
                    MethodInfo method = type.GetMethod(name, flags, null, Type.EmptyTypes, null);
                    if (method != null && method.Invoke(skill, null) is float m) return m;
                }
                catch { }
            }
            return skill.skillDef != null ? skill.skillDef.baseRechargeInterval : 0f;
        }

        private void WriteEquipment(IntPtr hud, Inventory inventory)
        {
            IntPtr slot = IntPtr.Add(hud, BridgeInfo.HudHeaderSize + 4 * BridgeInfo.HudSlotSize);
            EquipmentDef def = inventory != null ? EquipmentCatalog.GetEquipmentDef(inventory.currentEquipmentIndex) : null;
            if (def == null)
            {
                WriteSlot(slot, "", 0f, 0f, 0, 0);
                return;
            }

            EquipmentState state = inventory.currentEquipmentState;
            float remaining = Mathf.Max(0f, state.chargeFinishTime.timeUntil);
            if (float.IsInfinity(remaining) || float.IsNaN(remaining)) remaining = 0f;
            int charges = state.charges;
            WriteSlot(slot, IconKey(def, def.pickupIconSprite), remaining, def.cooldown, charges, Mathf.Max(1, charges));
        }

        private void WriteItems(IntPtr hud, Inventory inventory)
        {
            int written = 0;
            var list = new System.Text.StringBuilder();
            if (inventory != null)
            {
                EquipmentDef equipment = EquipmentCatalog.GetEquipmentDef(inventory.currentEquipmentIndex);
                if (equipment != null)
                {
                    list.Append(IconKey(equipment, equipment.pickupIconSprite)).Append('\t')
                        .Append(Clean(SkyrimLanguage.Text(equipment.nameToken))).Append("\t0\t")
                        .Append(Clean(SkyrimLanguage.Text(equipment.pickupToken))).Append('\n');
                }
                List<ItemIndex> order = inventory.itemAcquisitionOrder;
                for (int i = 0; order != null && i < order.Count; i++)
                {
                    ItemDef def = ItemCatalog.GetItemDef(order[i]);
                    if (def == null || def.hidden) continue;
                    int count = ItemCount(inventory, order[i]);
                    if (count <= 0) continue;
                    // Les versions récentes de RoR2 ne gardent plus l'icône directement : elle est chargée à part.
                    string icon = IconKey(def, def.pickupIconSprite);
                    list.Append(icon).Append('\t').Append(Clean(SkyrimLanguage.Text(def.nameToken))).Append('\t').Append(count).Append('\t')
                        .Append(Clean(SkyrimLanguage.Text(def.pickupToken))).Append('\n');
                    if (written >= BridgeInfo.HudMaxItems) continue;

                    IntPtr entry = IntPtr.Add(hud, BridgeInfo.HudHeaderSize + BridgeInfo.HudSlots * BridgeInfo.HudSlotSize + written * BridgeInfo.HudItemSize);
                    WriteText(entry, icon);
                    Marshal.WriteInt32(entry, BridgeInfo.IconNameSize, count);
                    written++;
                }
            }
            Marshal.WriteInt32(hud, 44, written);
            WriteItemList(list.ToString());
        }

        // La liste des objets (nom, nombre, description) pour l'écran des objets de Skyrim : objets.txt.
        private string _lastList;
        private void WriteItemList(string text)
        {
            if (text == _lastList) return;
            _lastList = text;
            try
            {
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimRoR2");
                System.IO.Directory.CreateDirectory(folder);
                string path = System.IO.Path.Combine(folder, "objets.txt");
                System.IO.File.WriteAllText(path + ".tmp", text, new System.Text.UTF8Encoding(false));
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(path + ".tmp", path);
            }
            catch (Exception e)
            {
                _log.LogWarning("HUD : liste des objets impossible a ecrire (" + e.Message + ")");
            }
        }

        // Sans balises de couleur (<style=...>) ni retours à la ligne ni tabulations.
        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "");
            return text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        // L'icône d'un objet : directe (anciennes versions), sinon chargée depuis sa référence d'asset
        // (en image "Sprite", ou à défaut en texture entière).
        private readonly Dictionary<UnityEngine.Object, string> _iconKeys = new Dictionary<UnityEngine.Object, string>();
        private int _iconLogs;

        private string IconKey(UnityEngine.Object def, Sprite direct)
        {
            if (direct != null) return _icons.Request(direct);
            if (def == null) return "";
            if (_iconKeys.TryGetValue(def, out string known)) return known;
            string key = "";
            string how = "aucune reference";
            try
            {
                // On fouille l'objet, puis sa fiche de ramassage (PickupDef) : une image déjà là, ou une
                // référence d'asset d'icône à charger.
                object pickup = null;
                if (def is ItemDef item) pickup = PickupCatalog.GetPickupDef(PickupCatalog.FindPickupIndex(item.itemIndex));
                else if (def is EquipmentDef equipment) pickup = PickupCatalog.GetPickupDef(PickupCatalog.FindPickupIndex(equipment.equipmentIndex));
                foreach (object source in new[] { (object)def, pickup })
                {
                    if (source == null || key.Length > 0) continue;
                    foreach (var (name, value) in IconMembers(source))
                    {
                        if (value is Sprite sprite && sprite != null) { key = _icons.Request(sprite); how = name; break; }
                        if (value is Texture texture && texture != null) { key = _icons.RequestTexture(texture); how = name; break; }
                        object runtimeKey = value?.GetType().GetProperty("RuntimeKey")?.GetValue(value);
                        if (runtimeKey == null || runtimeKey.ToString().Length == 0) continue;
                        if (LoadAsset(runtimeKey, typeof(Sprite)) is Sprite loaded && loaded != null) { key = _icons.Request(loaded); how = name + " (sprite)"; break; }
                        if (LoadAsset(runtimeKey, typeof(Texture2D)) is Texture2D tex && tex != null) { key = _icons.RequestTexture(tex); how = name + " (texture)"; break; }
                        how = name + " : chargement rate";
                    }
                }
                if (key.Length == 0 && how == "aucune reference" && _iconLogs < 2)
                {
                    var names = new List<string>();
                    foreach (var (name, value) in IconMembers(def, true)) names.Add(name + (value == null ? "=null" : ":" + value.GetType().Name));
                    _log.LogInfo("HUD : champs de " + def.GetType().Name + " : " + string.Join(", ", names));
                }
            }
            catch (Exception e)
            {
                how = "erreur : " + e.Message;
            }
            if (_iconLogs++ < 8) _log.LogInfo($"HUD : icone de {def.name} -> {(key.Length > 0 ? key : "aucune")} ({how})");
            _iconKeys[def] = key;
            return key;
        }

        // Les champs et propriétés d'un objet (et de ses parents) qui parlent d'icône.
        private static IEnumerable<(string, object)> IconMembers(object source, bool all = false)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type type = source.GetType(); type != null && type != typeof(object) && type != typeof(UnityEngine.Object); type = type.BaseType)
            {
                foreach (FieldInfo field in type.GetFields(flags))
                {
                    if (!all && field.Name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    object value;
                    try { value = field.GetValue(source); } catch (Exception) { continue; }
                    yield return (field.Name, value);
                }
                foreach (PropertyInfo property in type.GetProperties(flags))
                {
                    if (property.GetIndexParameters().Length > 0) continue;
                    if (!all && property.Name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    object value;
                    try { value = property.GetValue(source); } catch (Exception) { continue; }
                    yield return (property.Name, value);
                }
            }
        }

        private static readonly Dictionary<Type, MethodInfo> _loaders = new Dictionary<Type, MethodInfo>();
        private static MethodInfo _loadGeneric;
        private static bool _loadSearched;

        // Addressables.LoadAssetAsync<T>(clé).WaitForCompletion(), sans dépendre de la bibliothèque.
        private static object LoadAsset(object key, Type type)
        {
            if (!_loadSearched)
            {
                _loadSearched = true;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type addressables = assembly.GetType("UnityEngine.AddressableAssets.Addressables");
                    if (addressables == null) continue;
                    foreach (MethodInfo method in addressables.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (method.Name != "LoadAssetAsync" || !method.IsGenericMethodDefinition) continue;
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(object)) continue;
                        _loadGeneric = method;
                        break;
                    }
                    break;
                }
            }
            if (_loadGeneric == null) return null;
            if (!_loaders.TryGetValue(type, out MethodInfo loader)) _loaders[type] = loader = _loadGeneric.MakeGenericMethod(type);
            try
            {
                object handle = loader.Invoke(null, new[] { key });
                return handle?.GetType().GetMethod("WaitForCompletion", Type.EmptyTypes)?.Invoke(handle, null);
            }
            catch (Exception)
            {
                return null;  // mauvais type pour cette clé
            }
        }

        // Nombre d'exemplaires d'un objet (le nom de la fonction dépend de la version de RoR2).
        private int ItemCount(Inventory inventory, ItemIndex item)
        {
            if (!_itemCountSearched)
            {
                _itemCountSearched = true;
                foreach (string name in new[] { "GetItemCount", "GetItemCountEffective", "GetItemCountPermanent" })
                {
                    _getItemCount = typeof(Inventory).GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(ItemIndex) }, null);
                    if (_getItemCount != null) break;
                }
                if (_getItemCount == null) _log.LogWarning("HUD : impossible de compter les objets (fonction introuvable)");
            }
            if (_getItemCount == null) return 1;
            object result = _getItemCount.Invoke(inventory, new object[] { item });
            return result is int n ? n : 1;
        }

        private static void WriteSlot(IntPtr slot, string icon, float cooldown, float total, int stock, int maxStock)
        {
            WriteText(slot, icon);
            WriteFloat(slot, BridgeInfo.IconNameSize, cooldown);
            WriteFloat(slot, BridgeInfo.IconNameSize + 4, total);
            Marshal.WriteInt32(slot, BridgeInfo.IconNameSize + 8, stock);
            Marshal.WriteInt32(slot, BridgeInfo.IconNameSize + 12, maxStock);
        }

        // Écrit un texte court (48 octets maximum, zéro de fin compris).
        private static void WriteText(IntPtr where, string text)
        {
            text = text ?? "";
            int length = Math.Min(text.Length, BridgeInfo.IconNameSize - 1);
            for (int i = 0; i < length; i++) Marshal.WriteByte(where, i, (byte)text[i]);
            Marshal.WriteByte(where, length, 0);
        }

        // Écrit un texte avec accents (UTF-8), coupé proprement à 47 octets.
        private static void WriteUtf8(IntPtr where, string text)
        {
            // On enlève les balises de mise en forme de RoR2 (<color=...>, <style>...) et les caractères
            // spéciaux que la police de Skyrim ne sait pas afficher (ils donnaient des "?").
            text = System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]*>", "");
            text = text.Replace("?", "").Replace("\u0153", "oe").Replace("\u0152", "Oe");  // "Démon du v??ide", "œ"
            var clean = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
                if ((c >= 0x20 && c < 0x7F) || (c >= 0xA0 && c <= 0x17F)) clean.Append(c);
            text = clean.ToString().Trim();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            int length = Math.Min(bytes.Length, BridgeInfo.IconNameSize - 1);
            while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80) length--;  // pas de lettre coupée en deux
            for (int i = 0; i < length; i++) Marshal.WriteByte(where, i, bytes[i]);
            Marshal.WriteByte(where, length, 0);
        }

        // ---------- Jauges propres à certains survivants ----------
        //
        // RoR2 dessine ces jauges dans son propre HUD, à partir de l'état de la compétence ou d'un composant du
        // survivant. On lit ces mêmes valeurs (par leur nom : elles changent parfois d'une version à l'autre) :
        //   Démon du vide : sa corruption (0 à 100), violette, rouge une fois corrompu ;
        //   Railgunner : la recharge active, avec la fenêtre où appuyer pour un rechargement rapide ;
        //   Chercheuse : ses charges de tranquillité, et la progression de la Méditation.
        // Au premier passage, le journal liste les valeurs trouvées (pour vérifier).
        private readonly HashSet<string> _specialLogged = new HashSet<string>();
        private int _tranquilityBuff = -2;  // numéro du bonus "tranquillité" (-2 : pas encore cherché, -1 : introuvable)

        private void WriteSpecial(IntPtr at, CharacterBody body)
        {
            int kind = 0, charges = 0, maxCharges = 0;
            float value = 0f, windowStart = 0f, windowEnd = 0f;
            uint color = 0xFFFFFFFFu;
            string label = "";
            string survivor = body.name.Replace("(Clone)", "");
            try
            {
                if (survivor.StartsWith("VoidSurvivor", StringComparison.Ordinal))
                {
                    Component controller = FindComponent(body.gameObject, "VoidSurvivorController");
                    if (controller != null)
                    {
                        float corruption = ReadFloat(controller, "corruption", "_corruption", "currentCorruption");
                        float max = ReadFloat(controller, "maxCorruption", "corruptionMax");
                        if (max <= 0f) max = 100f;
                        bool corrupted = ReadBool(controller, "isCorrupted", "_isCorrupted", "corrupted");
                        kind = 1;
                        value = Mathf.Clamp01(corruption / max);
                        color = corrupted ? 0xE0303AFFu : 0x9B5CF0FFu;
                        label = corrupted ? "CORROMPU" : $"Corruption {{}} %|{Mathf.RoundToInt(corruption)}";  // 0.74 : traduit par Skyrim
                        LogOnce("VoidSurvivor", controller);
                    }
                }
                else if (survivor.StartsWith("Railgunner", StringComparison.Ordinal))
                {
                    // 0.73 : la machine de rechargement est presque toujours dans l'état "Waiting" (rien à faire) ; le
                    // mini-jeu n'existe que pendant "Reloading". Avant, on prenait le premier état venu ("Waiting") :
                    // la barre restait vide.
                    object state = FindState(body, "Reload.Reloading") ?? FindState(body, "Reloading");
                    if (state != null)
                    {
                        float age = ReadFloat(state, "fixedAge", "age");
                        float duration = ReadFloat(state, "duration", "reloadDuration", "baseDuration");
                        kind = 2;
                        value = duration > 0f ? Mathf.Clamp01(age / duration) : 0f;
                        // La fenêtre du rechargement rapide : en fraction de la durée (…Percent), sinon en secondes.
                        float startPercent = ReadFloat(state, "boostWindowStartPercent", "boostWindowStartFraction");
                        float endPercent = ReadFloat(state, "boostWindowEndPercent", "boostWindowEndFraction");
                        if (endPercent > 0f)
                        {
                            windowStart = startPercent > 1f ? startPercent / 100f : startPercent;
                            windowEnd = endPercent > 1f ? endPercent / 100f : endPercent;
                        }
                        else if (duration > 0f)
                        {
                            float start = ReadFloat(state, "boostWindowDelay", "boostWindowStart", "windowStart", "boostWindowStartTime");
                            float length = ReadFloat(state, "boostWindowDuration", "windowDuration", "boostWindowLength");
                            float end = ReadFloat(state, "boostWindowEnd", "windowEnd", "boostWindowEndTime");
                            if (end <= 0f) end = start + length;
                            windowStart = start / duration;
                            windowEnd = end / duration;
                        }
                        color = 0x4FC3F7FFu;
                        label = "Recharge : clic au bon moment";
                        LogOnce("RailgunnerReloading", state);
                    }
                }
                else if (survivor.StartsWith("Seeker", StringComparison.Ordinal))
                {
                    object meditation = FindState(body, "Meditat");
                    if (meditation != null)
                    {
                        float age = ReadFloat(meditation, "fixedAge", "age");
                        float duration = ReadFloat(meditation, "duration", "maxDuration", "baseDuration");
                        kind = 4;
                        value = duration > 0f ? Mathf.Clamp01(1f - age / duration) : 0f;  // le temps qui reste
                        color = 0x7FE0C0FFu;
                        label = "Meditation";
                        LogOnce("SeekerMeditation", meditation);
                        // 0.73 : le mini-jeu (la suite de directions à taper) : kind 5, les directions dans le texte
                        // ("H" haut, "B" bas, "G" gauche, "D" droite), l'étape en cours dans charges.
                        Component controller = FindComponent(body.gameObject, "SeekerController");
                        if (MeditationSequence(meditation, controller, out string sequence, out int step))
                        {
                            kind = 5;
                            label = sequence;
                            charges = step;
                            maxCharges = sequence.Length;
                        }
                    }
                    else
                    {
                        int buff = TranquilityBuff();
                        if (buff >= 0)
                        {
                            kind = 3;
                            charges = body.GetBuffCount((BuffIndex)buff);
                            maxCharges = 7;
                            color = 0x7FE0C0FFu;
                            label = "Tranquillite";
                        }
                    }
                }
            }
            catch (Exception) { kind = 0; }

            Marshal.WriteInt32(at, 0, kind);
            WriteFloat(at, 4, value);
            WriteFloat(at, 8, Mathf.Clamp01(windowStart));
            WriteFloat(at, 12, Mathf.Clamp01(windowEnd));
            Marshal.WriteInt32(at, 16, charges);
            Marshal.WriteInt32(at, 20, maxCharges);
            Marshal.WriteInt32(at, 24, unchecked((int)color));
            WriteUtf8(IntPtr.Add(at, 28), label);
        }

        // La suite de directions de la Méditation : dans l'état ou dans le SeekerController, un tableau dont le nom
        // parle de "séquence" ou d'"entrée" ; l'étape en cours dans un nombre dont le nom parle d'"étape".
        // Les noms exacts dépendent de la version de RoR2 : tout est noté une fois dans le journal.
        private bool MeditationSequence(object state, Component controller, out string sequence, out int step)
        {
            sequence = "";
            step = 0;
            Array values = null;
            string arrayName = "";
            int stepValue = -1;
            foreach (object target in new[] { state, (object)controller })
            {
                if (target == null) continue;
                LogAllOnce("Meditation:" + target.GetType().Name, target);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                for (Type type = target.GetType(); type != null && type != typeof(object); type = type.BaseType)
                {
                    foreach (FieldInfo field in type.GetFields(flags | BindingFlags.DeclaredOnly))
                    {
                        string n = field.Name.ToLowerInvariant();
                        object v;
                        try { v = field.GetValue(target); } catch (Exception) { continue; }
                        if (values == null && v is Array array && array.Length >= 2 && array.Length <= 16 &&
                            (n.Contains("seq") || n.Contains("input") || n.Contains("direction") || n.Contains("combo")))
                        {
                            values = array;
                            arrayName = n;
                        }
                        else if (stepValue < 0 && (n.Contains("step") || n.Contains("currentindex") || n.Contains("progress")) &&
                                 (v is int || v is sbyte || v is byte || v is short))
                            stepValue = Convert.ToInt32(v);
                    }
                }
            }
            if (values == null) return false;
            var text = new System.Text.StringBuilder();
            int start = 0;
            if (arrayName.Contains("step") && values.Length > 1)  // "stepAndSequence" : la case 0 est l'étape
            {
                stepValue = Convert.ToInt32(values.GetValue(0));
                start = 1;
            }
            for (int i = start; i < values.Length; i++)
            {
                object v = values.GetValue(i);
                text.Append(DirectionLetter(v));
            }
            sequence = text.ToString().Trim('?');
            step = Mathf.Clamp(stepValue, 0, sequence.Length);
            if (_sequenceLogged.Add(sequence))
                _log.LogInfo($"HUD : Meditation, suite {arrayName} = [{string.Join(",", values.Cast<object>())}] -> {sequence}, etape {stepValue}");
            return sequence.Length > 0;
        }

        private readonly HashSet<string> _sequenceLogged = new HashSet<string>();

        // Une direction -> une lettre (H haut, B bas, G gauche, D droite). Une énumération : par son nom.
        // Un nombre : 0 haut, 1 bas, 2 gauche, 3 droite (à confirmer avec le journal).
        private static char DirectionLetter(object v)
        {
            if (v == null) return '?';
            if (v.GetType().IsEnum)
            {
                string n = v.ToString().ToLowerInvariant();
                if (n.Contains("up") || n.Contains("forward") || n.Contains("north")) return 'H';
                if (n.Contains("down") || n.Contains("back") || n.Contains("south")) return 'B';
                if (n.Contains("left") || n.Contains("west")) return 'G';
                if (n.Contains("right") || n.Contains("east")) return 'D';
            }
            int k;
            try { k = Convert.ToInt32(v); } catch (Exception) { return '?'; }
            switch (k)
            {
                case 0: return 'H';
                case 1: return 'B';
                case 2: return 'G';
                case 3: return 'D';
                default: return '?';
            }
        }

        // Tous les champs (tous types), une fois, pour comprendre une nouvelle version de RoR2.
        private void LogAllOnce(string what, object target)
        {
            if (!_specialLogged.Add(what)) return;
            var parts = new List<string>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type type = target.GetType(); type != null && type != typeof(object) && parts.Count < 60; type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    try
                    {
                        object v = field.GetValue(target);
                        string shown = v is Array a ? "[" + string.Join(",", a.Cast<object>().Take(16)) + "]" : v?.ToString() ?? "null";
                        if (shown.Length > 60) shown = shown.Substring(0, 60);
                        parts.Add(field.Name + "=" + shown);
                    }
                    catch (Exception) { }
                    if (parts.Count >= 60) break;
                }
            _log.LogInfo($"HUD : {what} ({target.GetType().FullName}) : {string.Join(", ", parts)}");
        }

        private int TranquilityBuff()
        {
            if (_tranquilityBuff != -2) return _tranquilityBuff;
            _tranquilityBuff = -1;
            try
            {
                for (int i = 0; i < BuffCatalog.buffCount; i++)
                {
                    BuffDef def = BuffCatalog.GetBuffDef((BuffIndex)i);
                    string name = def != null ? def.name ?? "" : "";
                    if (name.IndexOf("Tranquil", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Chakra", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _tranquilityBuff = i;
                        _log.LogInfo("HUD : bonus de tranquillite de la Chercheuse = " + name);
                        break;
                    }
                }
            }
            catch (Exception) { }
            return _tranquilityBuff;
        }

        private static Component FindComponent(GameObject holder, string typeName)
        {
            foreach (Component c in holder.GetComponents<Component>())
                if (c != null && c.GetType().Name == typeName) return c;
            return null;
        }

        // L'état en cours d'une machine à états du survivant dont le nom de type contient ce mot.
        private static object FindState(CharacterBody body, string word)
        {
            foreach (EntityStateMachine machine in body.GetComponents<EntityStateMachine>())
            {
                object state = machine != null ? machine.state : null;
                if (state != null && state.GetType().FullName.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return state;
            }
            return null;
        }

        private static float ReadFloat(object target, params string[] names)
        {
            object value = ReadMember(target, names);
            if (value is float f) return f;
            if (value is int i) return i;
            if (value is double d) return (float)d;
            return 0f;
        }

        private static bool ReadBool(object target, params string[] names) => ReadMember(target, names) is bool b && b;

        private static object ReadMember(object target, string[] names)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (string name in names)
            {
                for (Type type = target.GetType(); type != null; type = type.BaseType)
                {
                    FieldInfo field = type.GetField(name, flags | BindingFlags.DeclaredOnly);
                    if (field != null) return field.GetValue(field.IsStatic ? null : target);
                    PropertyInfo property = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                    if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(target);
                }
            }
            return null;
        }

        // Une fois par sorte : les valeurs numériques trouvées (pour ajuster les noms si RoR2 les change).
        private void LogOnce(string what, object target)
        {
            if (!_specialLogged.Add(what)) return;
            var parts = new List<string>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type type = target.GetType(); type != null && parts.Count < 40; type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType != typeof(float) && field.FieldType != typeof(bool) && field.FieldType != typeof(int)) continue;
                    try { parts.Add(field.Name + "=" + field.GetValue(field.IsStatic ? null : target)); } catch (Exception) { }
                    if (parts.Count >= 40) break;
                }
            _log.LogInfo($"HUD : jauge {what} ({target.GetType().FullName}) : {string.Join(", ", parts)}");
        }

        private static void WriteFloat(IntPtr where, int offset, float value)
        {
            Marshal.WriteInt32(where, offset, BitConverter.SingleToInt32Bits(value));
        }
    }
}
