using System;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // Les effets de RoR2 (explosions, flammes, éclairs, gel, soins...) rejoués avec les effets de Skyrim.
    //
    // Chaque nouvel effet de RoR2 est rangé dans une "famille" d'après son nom (une explosion, de la foudre...),
    // puis envoyé à Skyrim avec sa position et sa taille. Skyrim joue au même endroit un de ses propres
    // effets de la même famille : il reste dans le style de Skyrim, éclairé et caché par le décor.
    internal sealed class EffectMirror
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        // Familles (mêmes numéros côté Skyrim, voir Effects.cpp).
        public const int Explosion = 1, Lightning = 2, Frost = 3, Fire = 4, Heal = 5, Poison = 6, Magic = 7, Impact = 8;

        private readonly ManualLogSource _log;
        private readonly ConfigEntry<bool> _enabled;
        private IntPtr _view;
        private bool _openTried;
        private uint _written;
        private int _sent;
        private readonly System.Collections.Generic.HashSet<string> _seen = new System.Collections.Generic.HashSet<string>();

        public EffectMirror(ManualLogSource log, ConfigFile config)
        {
            _log = log;
            _enabled = ConfigMove.Bind(config, "Monde", "4 - Effets", "EffetsSkyrim", true,
                "Fallback: replays with a Skyrim effect the RoR2 effects (explosions, flames, lightning, frost, healing...) that are not yet copied with their real particles (EffetsRoR2).");
        }

        public bool Enabled => _enabled.Value;

        // Branché par le plugin : l'effet est-il déjà dessiné avec ses vraies particules (SpriteMirror) ?
        // Si oui, on ne le remplace plus par un effet de Skyrim (celui-ci ne sert plus que de secours).
        public Func<GameObject, bool> Covered;

        public void Update(CharacterBody survivor, TerrainMirror terrain, bool connected)
        {
            if (!_enabled.Value || !connected || survivor == null || terrain == null || !Open())
            {
                Tracked.NewEffects.Clear();
                return;
            }
            foreach (EffectComponent effect in Tracked.NewEffects)
            {
                if (effect == null) continue;
                int family = Family(effect.gameObject.name);
                if (_seen.Add(effect.gameObject.name) && _seen.Count <= 300)
                    _log.LogInfo($"Effet vu : {effect.gameObject.name.Replace("(Clone)", "")} -> famille {family}");
                if (family == 0) continue;
                if (Covered != null && Covered(effect.gameObject)) continue;
                Vector3 position = effect.transform.position;
                if (GhostManager.NearRecentSpawn(position)) continue;  // apparition d'un double : rien à montrer
                if ((position - survivor.corePosition).sqrMagnitude > 150f * 150f) continue;
                if (!terrain.TryMapToSkyrim(position, out Vector3 skyrim)) continue;
                float size = Mathf.Clamp(effect.transform.lossyScale.x, 0.3f, 4f);
                Send(family, skyrim, size);
            }
            Tracked.NewEffects.Clear();
        }

        // La famille d'un effet, d'après son nom. 0 : on ne le rejoue pas (éclairs de tir, traînées, interface...).
        public static int Family(string name)
        {
            string n = name.ToLowerInvariant();
            // "ImpactRailgun" contient aussi les lettres de "trail" (impac-T-RAIL-gun) : ce n'est pas une traînée.
            bool trail = n.Replace("railgun", "").Contains("trail");
            if (n.Contains("tracer") || n.Contains("muzzle") || trail || n.Contains("indicator")) return 0;
            if (n.Contains("heal") || n.Contains("medkit") || n.Contains("regen") || n.Contains("mend")) return Heal;
            if (n.Contains("lightning") || n.Contains("electric") || n.Contains("shock") || n.Contains("thunder") || n.Contains("zap") || n.Contains("taser")) return Lightning;
            if (n.Contains("ice") || n.Contains("frost") || n.Contains("freeze") || n.Contains("cold") || n.Contains("snow")) return Frost;
            if (n.Contains("poison") || n.Contains("acid") || n.Contains("croco") || n.Contains("blight") || n.Contains("toxic")) return Poison;
            if (n.Contains("void") || n.Contains("lunar") || n.Contains("nullifier") || n.Contains("magic")) return Magic;
            if (n.Contains("fire") || n.Contains("flame") || n.Contains("burn") || n.Contains("magma") || n.Contains("lava") || n.Contains("napalm")) return Fire;
            if (n.Contains("explosion") || n.Contains("explode") || n.Contains("blast") || n.Contains("bomb") || n.Contains("missile") || n.Contains("grenade")) return Explosion;
            if (n.Contains("impact") || n.Contains("hit") || n.Contains("spark")) return Impact;
            return 0;
        }

        private void Send(int family, Vector3 skyrim, float size)
        {
            int slot = (int)(_written % (uint)BridgeInfo.EffectRing);
            IntPtr at = IntPtr.Add(_view, 16 + slot * 24);
            Marshal.WriteInt32(at, 0, family);
            Marshal.WriteInt32(at, 4, BitConverter.ToInt32(BitConverter.GetBytes(skyrim.x), 0));
            Marshal.WriteInt32(at, 8, BitConverter.ToInt32(BitConverter.GetBytes(skyrim.y), 0));
            Marshal.WriteInt32(at, 12, BitConverter.ToInt32(BitConverter.GetBytes(skyrim.z), 0));
            Marshal.WriteInt32(at, 16, BitConverter.ToInt32(BitConverter.GetBytes(size), 0));
            Marshal.WriteInt32(at, 20, unchecked((int)(_written + 1)));
            _written++;
            Marshal.WriteInt32(_view, 4, unchecked((int)_written));
            Marshal.WriteInt32(_view, 0, unchecked((int)BridgeInfo.EffectMagic));
            _sent++;
            if (_sent <= 3 || _sent % 200 == 0) _log.LogInfo($"Effets : {_sent} effets envoyes a Skyrim");
        }

        private bool Open()
        {
            if (_view != IntPtr.Zero) return true;
            if (_openTried) return false;
            _openTried = true;
            IntPtr mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)BridgeInfo.EffectMapSize, BridgeInfo.EffectMapName);
            if (mapping != IntPtr.Zero) _view = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)BridgeInfo.EffectMapSize);
            if (_view == IntPtr.Zero) _log.LogWarning("Effets : pont impossible a ouvrir");
            else
            {
                // On repart du compteur déjà écrit (RoR2 relancé pendant que Skyrim tourne).
                _written = (uint)Marshal.ReadInt32(_view, 4);
            }
            return _view != IntPtr.Zero;
        }
    }
}
