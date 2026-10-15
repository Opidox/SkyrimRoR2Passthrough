using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using RoR2;
using RoR2.Projectile;
using UnityEngine;

namespace SkyrimRoR2
{
    // La liste des projectiles, effets et balles traçantes présents, tenue à jour au fil de l'eau.
    // (Les chercher dans toute la scène plusieurs fois par seconde faisait tomber RoR2 à quelques images par seconde.)
    // Un crochet sur leur apparition ("Start" / "OnEnable") les ajoute ; ceux détruits ou rangés sont ignorés.
    internal static class Tracked
    {
        public static readonly HashSet<ProjectileController> Projectiles = new HashSet<ProjectileController>();
        public static readonly HashSet<EffectComponent> Effects = new HashSet<EffectComponent>();
        public static readonly HashSet<Tracer> Tracers = new HashSet<Tracer>();
        private static float _nextCleanup;

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            int hooks = 0;
            hooks += Hook(harmony, typeof(ProjectileController), nameof(OnProjectile));
            hooks += Hook(harmony, typeof(EffectComponent), nameof(OnEffect));
            hooks += Hook(harmony, typeof(Tracer), nameof(OnTracer));
            log.LogInfo($"Suivi des tirs et effets : {hooks} crochets installes");
        }

        private static int Hook(Harmony harmony, Type type, string handler)
        {
            int count = 0;
            var postfix = new HarmonyMethod(typeof(Tracked).GetMethod(handler, BindingFlags.Static | BindingFlags.NonPublic));
            foreach (string name in new[] { "Start", "OnEnable", "Awake" })
            {
                MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (method == null) continue;
                try { harmony.Patch(method, postfix: postfix); count++; }
                catch (Exception) { }
            }
            return count;
        }

        private static void OnProjectile(ProjectileController __instance) => Projectiles.Add(__instance);
        // Les effets qui viennent d'apparaître (pour les rejouer dans Skyrim).
        public static readonly List<EffectComponent> NewEffects = new List<EffectComponent>();
        private static readonly Dictionary<int, float> _lastStart = new Dictionary<int, float>();

        private static void OnEffect(EffectComponent __instance)
        {
            Effects.Add(__instance);
            int id = __instance.GetInstanceID();
            if (_lastStart.TryGetValue(id, out float last) && Time.unscaledTime - last < 0.2f) return;  // Start + OnEnable
            _lastStart[id] = Time.unscaledTime;
            if (NewEffects.Count < 256) NewEffects.Add(__instance);
        }
        private static void OnTracer(Tracer __instance) => Tracers.Add(__instance);

        // Retire de temps en temps ceux qui n'existent plus.
        public static void Cleanup()
        {
            if (Time.unscaledTime < _nextCleanup) return;
            _nextCleanup = Time.unscaledTime + 1f;
            Projectiles.RemoveWhere(p => p == null);
            Effects.RemoveWhere(e => e == null);
            Tracers.RemoveWhere(t => t == null);
        }

        public static bool Alive(Component component) => component != null && component.gameObject.activeInHierarchy;
    }
}
