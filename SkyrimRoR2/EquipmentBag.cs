using System;
using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using System.Reflection;
using System.Text;
using RoR2;

namespace SkyrimRoR2
{
    // 0.72 : les équipements RoR2 sont des amulettes dans l'inventaire de Skyrim (celle qu'on porte est l'équipement actif).
    //
    //  - EquipmentGains : un équipement GAGNÉ (coffre, aspect d'élite, marmite) ne remplace pas l'équipement actif : il est
    //    noté dans equipements_gagnes.txt ("numéro<TAB>nom interne"), et Skyrim met l'amulette dans le sac.
    //  - EquipmentStates : changer d'amulette ne recharge pas un équipement. On garde la recharge de chaque équipement
    //    quand on le quitte, et on la lui rend quand on le reprend.
    internal static class EquipmentGains
    {
        private static readonly List<string> Lines = new List<string>();
        private static long _last;

        public static void Add(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return;
            long sequence = Math.Max(DateTime.UtcNow.Ticks, _last + 1);  // toujours plus grand, même après un redémarrage de RoR2
            _last = sequence;
            Lines.Add(sequence + "\t" + internalName);
            while (Lines.Count > 40) Lines.RemoveAt(0);
            try
            {
                Directory.CreateDirectory(InventorySync.Folder);
                string path = Path.Combine(InventorySync.Folder, "equipements_gagnes.txt");
                File.WriteAllText(path + ".tmp", string.Join("\n", Lines) + "\n", new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception) { }
        }
    }

    internal static class EquipmentStates
    {
        private static readonly Dictionary<EquipmentIndex, object> Saved = new Dictionary<EquipmentIndex, object>();
        private static MethodInfo _setState;
        private static PropertyInfo _currentState;
        private static bool _searched;

        private static void Search()
        {
            if (_searched) return;
            _searched = true;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _currentState = typeof(Inventory).GetProperty("currentEquipmentState", flags);
            _setState = typeof(Inventory).GetMethods(flags).FirstOrDefault(m =>
                m.Name == "SetEquipment" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType.Name == "EquipmentState");
        }

        // Passer à l'équipement "index" (None : aucun), en gardant la recharge de chacun.
        public static void Swap(Inventory inventory, EquipmentIndex index)
        {
            Search();
            try
            {
                if (_currentState != null && inventory.currentEquipmentIndex != EquipmentIndex.None)
                    Saved[inventory.currentEquipmentIndex] = _currentState.GetValue(inventory);
            }
            catch (Exception) { }
            InventorySync.StaticLookUp();
            InventorySync.StaticSetEquipment(inventory, index);
            if (index == EquipmentIndex.None || _setState == null || !Saved.TryGetValue(index, out object state) || state == null) return;
            try
            {
                ParameterInfo[] parameters = _setState.GetParameters();
                object[] args = new object[parameters.Length];
                args[0] = state;
                for (int i = 1; i < args.Length; i++)
                    args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : (parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null);
                _setState.Invoke(inventory, args);
            }
            catch (Exception) { }
        }
    }
}
