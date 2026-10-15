using System;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoR2;
using UnityEngine;

namespace SkyrimRoR2
{
    // Le survivant reste collé au sol sur les petits reliefs de Skyrim (cailloux, pavés, racines, bords de dalles).
    //
    // Le moteur de déplacement de RoR2 est fait pour des sols lisses : en arrivant vite sur une petite bosse,
    // le survivant monte le long de sa pente, puis "décroche" au sommet et part en l'air avec sa vitesse vers
    // le haut, comme sur un tremplin. Skyrim recopie ce vol : le joueur était projeté en l'air.
    //
    // Deux protections :
    //  1. Le moteur de RoR2 ne décroche plus au sommet d'une bosse (il garde le contact avec le sol tant que
    //     le sol est juste en dessous). Un vrai bord (falaise, toit) fait toujours tomber.
    //  2. En secours : si le survivant quitte le sol tout seul (sans saut, sans compétence, sans être projeté),
    //     avec un petit élan vers le haut et le sol juste sous ses pieds, son élan vers le haut est annulé.
    // Les sauts, les compétences de mobilité et les projections par les ennemis ne sont pas touchés.
    internal sealed class GroundSnap
    {
        private const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const float WatchSeconds = 0.3f;  // on ne regarde que juste après avoir quitté le sol

        private readonly ManualLogSource _log;
        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<float> _maxGap;
        private readonly ConfigEntry<float> _maxRise;

        // Réglages du moteur de RoR2 modifiés (et leurs valeurs d'origine, pour les remettre).
        private object _tunedMotor;
        private float _originalAngle = -1f;
        private float _originalLedgeSpeed = -1f;
        private FieldInfo _angleField;
        private FieldInfo _ledgeSpeedField;
        private bool _tuneLogged;

        private bool _wasGrounded;
        private int _groundJumpCount;
        private float _leftGroundAt = -1f;
        private bool _watching;
        private int _snaps;

        public GroundSnap(ManualLogSource log, ConfigFile config)
        {
            _log = log;
            _enabled = config.Bind("2 - Jeu", "ColleAuSol", true,
                "Le survivant reste colle au sol sur les petits reliefs de Skyrim (cailloux, paves...) au lieu de decoller. " +
                "Les sauts, les competences et les projections ne sont pas touches.");
            _maxGap = config.Bind("2 - Jeu", "ColleAuSolDistance", 0.5f,
                "Distance (m) jusqu'au sol en dessous de laquelle un survivant qui decolle tout seul est recolle au sol.");
            _maxRise = config.Bind("2 - Jeu", "ColleAuSolElanMax", 6f,
                "Elan vers le haut (m/s) au-dela duquel on considere que c'est voulu (saut, competence) : pas de recollage.");
        }

        // Appelé à chaque pas de physique. active : le relief de Skyrim est recopié et Skyrim pilote le survivant.
        public void Step(CharacterBody body, bool active)
        {
            CharacterMotor motor = body != null ? body.characterMotor : null;
            bool on = _enabled.Value && active && motor != null;
            Tune(on ? motor : null);
            if (!on)
            {
                _watching = false;
                _wasGrounded = false;
                return;
            }

            if (motor.isGrounded)
            {
                _wasGrounded = true;
                _groundJumpCount = motor.jumpCount;
                _watching = false;
                return;
            }
            if (_wasGrounded)
            {
                // Il vient de quitter le sol : on surveille un court instant.
                _wasGrounded = false;
                _leftGroundAt = Time.fixedTime;
                _watching = true;
            }
            if (!_watching) return;
            if (Time.fixedTime - _leftGroundAt > WatchSeconds) { _watching = false; return; }

            // Décollage voulu : saut (bouton ou compteur de sauts), véhicule, élan fort, ruée de compétence.
            bool jumped = motor.jumpCount > _groundJumpCount || (body.inputBank != null && body.inputBank.jump.down);
            if (jumped || AutoRun.InAnyVehicle(body)) { _watching = false; return; }
            Vector3 velocity = motor.velocity;
            if (velocity.y > _maxRise.Value) { _watching = false; return; }
            float flat = new Vector2(velocity.x, velocity.z).magnitude;
            if (flat > body.moveSpeed * 1.5f + 1f) { _watching = false; return; }
            if (velocity.y <= 0.05f) return;  // il redescend déjà (bord de marche) : la gravité s'en charge

            // Le sol est-il juste sous ses pieds ?
            float gap = Mathf.Clamp(_maxGap.Value, 0.05f, 2f);
            if (!Physics.Raycast(body.footPosition + Vector3.up * 0.2f, Vector3.down, out RaycastHit hit, 0.2f + gap,
                    LayerIndex.world.mask, QueryTriggerInteraction.Ignore))
                return;

            motor.velocity = new Vector3(velocity.x, -2f, velocity.z);  // l'élan vers le haut est annulé : il retombe aussitôt
            _snaps++;
            if (_snaps <= 10 || _snaps % 100 == 0)
                _log.LogInfo($"Sol : survivant recolle au sol (elan de {velocity.y:F1} m/s vers le haut annule, sol a {Mathf.Max(0f, hit.distance - 0.2f):F2} m ; {_snaps} fois)");
        }

        // Le moteur de RoR2 (KinematicCharacterMotor) : on l'empêche de décrocher au sommet des bosses.
        // Lu par réflexion : les noms changent selon les versions, et un réglage absent est simplement ignoré.
        private void Tune(CharacterMotor motor)
        {
            object kinematic = null;
            if (motor != null)
            {
                try { kinematic = GetMember(motor, "Motor"); }
                catch (Exception) { kinematic = null; }
            }
            if (ReferenceEquals(kinematic, _tunedMotor)) return;

            // Ancien moteur (autre survivant, réglage coupé) : on remet ses valeurs.
            if (_tunedMotor != null && !(_tunedMotor is UnityEngine.Object o && o == null))
            {
                try
                {
                    if (_angleField != null && _originalAngle >= 0f) _angleField.SetValue(_tunedMotor, _originalAngle);
                    if (_ledgeSpeedField != null && _originalLedgeSpeed >= 0f) _ledgeSpeedField.SetValue(_tunedMotor, _originalLedgeSpeed);
                }
                catch (Exception) { }
            }
            _tunedMotor = kinematic;
            _originalAngle = -1f;
            _originalLedgeSpeed = -1f;
            if (kinematic == null) return;

            try
            {
                Type type = kinematic.GetType();
                _angleField = type.GetField("MaxStableDenivelationAngle", AnyMember);
                _ledgeSpeedField = type.GetField("MaxVelocityForLedgeSnap", AnyMember);
                if (_angleField != null && _angleField.FieldType == typeof(float))
                {
                    _originalAngle = (float)_angleField.GetValue(kinematic);
                    if (_originalAngle < 180f) _angleField.SetValue(kinematic, 180f);
                }
                else _angleField = null;
                if (_ledgeSpeedField != null && _ledgeSpeedField.FieldType == typeof(float))
                {
                    _originalLedgeSpeed = (float)_ledgeSpeedField.GetValue(kinematic);
                    if (_originalLedgeSpeed < 40f) _ledgeSpeedField.SetValue(kinematic, 40f);
                }
                else _ledgeSpeedField = null;
                if (!_tuneLogged)
                {
                    _tuneLogged = true;
                    _log.LogInfo("Sol : moteur du survivant regle pour rester au sol sur les bosses (" +
                                 (_angleField != null ? $"angle {_originalAngle:F0} -> 180" : "angle introuvable") + ", " +
                                 (_ledgeSpeedField != null ? $"bords {_originalLedgeSpeed:F0} -> 40 m/s" : "bords introuvable") + ")");
                }
            }
            catch (Exception e)
            {
                if (!_tuneLogged) { _tuneLogged = true; _log.LogWarning("Sol : reglage du moteur impossible (" + e.Message + "), seul le secours s'applique"); }
            }
        }

        private static object GetMember(object target, string name)
        {
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(name, AnyMember | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
                PropertyInfo property = type.GetProperty(name, AnyMember | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(target, null);
                type = type.BaseType;
            }
            return null;
        }
    }
}
