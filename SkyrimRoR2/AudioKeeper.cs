using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace SkyrimRoR2
{
    // 0.66 : les sons de RoR2 (tirs, explosions, objets, montée de niveau) s'entendent pendant qu'on joue dans Skyrim.
    //
    // RoR2 joue déjà ses sons au bon endroit autour du survivant (les ennemis, les tirs et les effets sont à la même
    // place que dans Skyrim). Mais son moteur de son (Wwise) se met en veille dès que sa fenêtre n'est plus active,
    // c'est-à-dire tout le temps, puisqu'on joue dans Skyrim. On l'empêche de s'endormir, et on règle le volume :
    // les effets à VolumeSons, la musique de RoR2 coupée par défaut (celle de Skyrim reste). Les réglages de son
    // de RoR2 lui-même ne sont pas modifiés (RoR2 sans le mod garde les siens).
    internal sealed class AudioKeeper
    {
        private static bool _keepAwake;
        private static bool _muteMonsters;

        private readonly ManualLogSource _log;
        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<float> _master;
        private readonly ConfigEntry<float> _effects;
        private readonly ConfigEntry<float> _music;
        private readonly ConfigEntry<bool> _monsterSounds;
        private MethodInfo _setRtpc;     // AkSoundEngine.SetRTPCValue(string, float)
        private MethodInfo _wakeUp;      // AkSoundEngine.WakeupFromSuspend()
        private bool _looked;
        private float _next;
        private bool _logged;

        public AudioKeeper(ManualLogSource log, ConfigFile config)
        {
            _log = log;
            _enabled = config.Bind("7 - Sons", "SonsRoR2", true,
                "Plays RoR2 sounds (shots, explosions, items...) while you play in Skyrim.");
            _master = config.Bind("7 - Sons", "VolumeGeneral", 100f, "Master volume of RoR2 while playing in Skyrim (0 to 100).");
            _effects = config.Bind("7 - Sons", "VolumeSons", 80f, "Volume of RoR2 sound effects (0 to 100).");
            _music = config.Bind("7 - Sons", "VolumeMusique", 0f, "Volume of RoR2 music (0 to 100). 0: only Skyrim's music.");
            _monsterSounds = config.Bind("7 - Sons", "SonsDesDoubles", false,
                "Sounds of the RoR2 doubles of Skyrim enemies (spawn, cries, death). Off by default: you hear the Skyrim enemy.");
        }

        // Empêche Wwise de se mettre en veille quand la fenêtre de RoR2 perd la main.
        public void Install(Harmony harmony)
        {
            try
            {
                Type controller = FindType("AkSoundEngineController");
                if (controller == null) { _log.LogWarning("Sons : moteur de son de RoR2 introuvable"); return; }
                MethodInfo focus = AccessTools.Method(controller, "OnApplicationFocus");
                MethodInfo pause = AccessTools.Method(controller, "OnApplicationPause");
                if (focus != null) harmony.Patch(focus, prefix: new HarmonyMethod(typeof(AudioKeeper), nameof(FocusPrefix)));
                if (pause != null) harmony.Patch(pause, prefix: new HarmonyMethod(typeof(AudioKeeper), nameof(PausePrefix)));
                _log.LogInfo($"Crochet installe : sons de RoR2 sans veille ({(focus != null ? "focus" : "-")}, {(pause != null ? "pause" : "-")})");

                // Les sons joués par les doubles des ennemis (apparition, cris, mort) : coupés.
                int muted = 0;
                var mute = new HarmonyMethod(typeof(AudioKeeper), nameof(SoundPrefix));
                foreach (MethodInfo method in typeof(Util).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "PlaySound" && method.Name != "PlayAttackSpeedSound" && method.Name != "PlayScaledSound") continue;
                    if (method.ReturnType != typeof(uint) || !method.GetParameters().Any(p => p.ParameterType == typeof(GameObject))) continue;
                    try { harmony.Patch(method, prefix: mute); muted++; }
                    catch (Exception e) { _log.LogWarning("Sons : " + method.Name + " impossible a filtrer (" + e.Message + ")"); }
                }
                _log.LogInfo($"Crochet installe : sons des doubles d'ennemis coupes ({muted} fonctions de son)");
            }
            catch (Exception e)
            {
                _log.LogWarning("Sons : crochet impossible (" + e.Message + ")");
            }
        }

        private static void FocusPrefix(ref bool __0)
        {
            if (_keepAwake) __0 = true;   // "la fenêtre a toujours la main"
        }

        // Un son joué par un ennemi de RoR2 (tous des doubles d'ennemis Skyrim) : on ne le joue pas.
        private static bool SoundPrefix(object[] __args, ref uint __result)
        {
            if (!_muteMonsters || __args == null) return true;
            foreach (object arg in __args)
            {
                if (!(arg is GameObject source) || source == null) continue;
                CharacterBody body = source.GetComponentInParent<CharacterBody>();
                if (body != null && body.teamComponent != null && body.teamComponent.teamIndex == TeamIndex.Monster)
                {
                    __result = 0;
                    return false;
                }
            }
            return true;
        }

        private static void PausePrefix(ref bool __0)
        {
            if (_keepAwake) __0 = false;  // "pas en pause"
        }

        public void Update(bool connected)
        {
            _keepAwake = _enabled.Value && connected;
            _muteMonsters = connected && !_monsterSounds.Value;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 2f;
            if (!_looked)
            {
                _looked = true;
                Type engine = FindType("AkSoundEngine");
                if (engine != null)
                {
                    _setRtpc = engine.GetMethod("SetRTPCValue", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(float) }, null);
                    _wakeUp = engine.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "WakeupFromSuspend" && m.GetParameters().Length == 0);
                }
                if (_setRtpc == null) _log.LogWarning("Sons : reglage du volume de RoR2 introuvable");
            }
            if (!connected) return;
            try
            {
                if (_enabled.Value && !Application.isFocused) _wakeUp?.Invoke(null, null);
                if (_setRtpc != null)
                {
                    float master = _enabled.Value ? Mathf.Clamp(_master.Value, 0f, 100f) : 0f;
                    _setRtpc.Invoke(null, new object[] { "Volume_Master", master });
                    _setRtpc.Invoke(null, new object[] { "Volume_SFX", Mathf.Clamp(_effects.Value, 0f, 100f) });
                    _setRtpc.Invoke(null, new object[] { "Volume_MSX", Mathf.Clamp(_music.Value, 0f, 100f) });
                }
                if (!_logged)
                {
                    _logged = true;
                    _log.LogInfo($"Sons : sons de RoR2 {(_enabled.Value ? "actifs" : "coupes")} (general {_master.Value:F0}, sons {_effects.Value:F0}, musique {_music.Value:F0})");
                }
            }
            catch (Exception e)
            {
                if (!_logged) { _logged = true; _log.LogWarning("Sons : " + e.Message); }
            }
        }

        private static Type FindType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(name, false); } catch (Exception) { }
                if (type != null) return type;
            }
            return null;
        }
    }
}
