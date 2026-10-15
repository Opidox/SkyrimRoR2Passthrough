using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using RoR2;
using RoR2.Skills;
using SkyrimRoR2.Shared;
using UnityEngine;

namespace SkyrimRoR2
{
    // Les variantes de compétences choisies dans Skyrim (menu Magie > Pouvoirs).
    //
    // 1. À chaque partie, on écrit la liste des variantes de chaque survivant pour Skyrim :
    //    %LOCALAPPDATA%\SkyrimRoR2\variantes.txt, une ligne "survivant|compétence|variante|nom".
    //    (compétence : 0 = principale, 1 = secondaire, 2 = utilitaire, 3 = spéciale, 4 = skin,
    //     5 à 8 = cases en plus : balises du Capitaine, drones de l'Opérateur... ;
    //     variante -1 = le titre de cette case en plus)
    // 2. Skyrim envoie les variantes voulues pour son survivant en cours. On les pose directement
    //    sur le survivant, sans passer par le loadout du profil RoR2 : c'est Skyrim qui décide
    //    de ce qui est débloqué, pas le profil.
    // 3. À chaque nouveau corps (changement de survivant, nouvelle étape...), on les repose.
    internal class LoadoutApplier
    {
        private readonly ManualLogSource _log;
        private Run _listWrittenFor;
        private CharacterBody _appliedBody;
        private uint _appliedSequence;
        private bool _hasApplied;
        private MethodInfo _setBaseSkill;
        private bool _searched;
        private string _skinRequest;  // dernier "survivant|skin" pour lequel on a fait réapparaître le corps

        public LoadoutApplier(ManualLogSource log)
        {
            _log = log;
        }

        // PV en plus pour le survivant, gagnés dans Skyrim (niveaux en Santé, enchantements, potions).
        // On les ajoute à sa vie DE BASE (baseMaxHealth) : RoR2 les compte alors à chaque recalcul de ses
        // statistiques, quel que soit le chemin (objets, niveaux, buffs...). On retient ce qu'on a ajouté
        // à quel corps, pour ne l'ajouter qu'une fois (un nouveau corps repart de la vie de base du survivant).
        private CharacterBody _bonusBody;
        private float _bonusApplied;

        internal static bool IsLocalSurvivor(CharacterBody body)
        {
            return body != null && body == LocalUserManager.GetFirstLocalUser()?.cachedBody;
        }

        public void Update(BridgeWriter bridge, CharacterBody body)
        {
            WriteVariantList();
            if (body == null || bridge == null || !bridge.ReadCommand(out SkyrimCommand command)) return;

            // Vie partagée.
            float bonus = float.IsNaN(command.BonusHealth) ? 0f : Math.Max(0f, command.BonusHealth);
            if (body != _bonusBody)
            {
                _bonusBody = body;
                _bonusApplied = 0f;  // nouveau corps : vie de base d'origine
            }
            if (Math.Abs(bonus - _bonusApplied) > 0.5f)
            {
                body.baseMaxHealth += bonus - _bonusApplied;
                _bonusApplied = bonus;
                body.MarkAllStatsDirty();
                _log.LogInfo($"Vie de Skyrim : +{bonus:F0} PV pour le survivant (vie de base {body.baseMaxHealth:F0})");
            }
            if (command.Variants == null || command.Variants.Length < 4) return;

            // Le skin (voir ApplySkin).
            if (!string.IsNullOrEmpty(command.VariantSurvivor) && IsSurvivor(body, command.VariantSurvivor))
                ApplySkin(body, command.VariantSurvivor, command.Skin);

            // Déjà fait pour ce corps et cet ordre.
            if (_hasApplied && body == _appliedBody && command.VariantSequence == _appliedSequence) return;

            // Les variantes ne concernent que le survivant que Skyrim croit jouer.
            if (string.IsNullOrEmpty(command.VariantSurvivor) || !IsSurvivor(body, command.VariantSurvivor)) return;

            _appliedBody = body;
            _appliedSequence = command.VariantSequence;
            _hasApplied = true;

            SkillLocator locator = body.skillLocator;
            if (locator == null) return;
            GenericSkill[] slots = { locator.primary, locator.secondary, locator.utility, locator.special };
            for (int slot = 0; slot < 4; slot++)
            {
                int wanted = command.Variants[slot];
                GenericSkill skill = slots[slot];
                if (wanted < 0 || skill == null || skill.skillFamily == null) continue;
                SkillFamily.Variant[] variants = skill.skillFamily.variants;
                if (variants == null || wanted >= variants.Length || variants[wanted].skillDef == null) continue;
                SkillDef def = variants[wanted].skillDef;
                if (skill.baseSkill == def) continue;
                if (SetBaseSkill(skill, def))
                {
                    _log.LogInfo($"Variante de Skyrim : {command.VariantSurvivor} competence {slot + 1} -> {Language.GetString(def.skillNameToken)}");
                    // Le MUL-T ne change le modèle de son arme qu'à l'apparition du corps : on inscrit l'arme dans le
                    // loadout du joueur et on le fait réapparaître au même endroit (une fois par choix).
                    if (slot == 0 && body.name.StartsWith("Toolbot", StringComparison.Ordinal))
                        RespawnForWeapon(body, skill, (uint)wanted, command.VariantSurvivor);
                }
            }

            // Les cases en plus (balises du Capitaine, drones de départ de l'Opérateur...).
            if (command.ExtraVariants == null) return;
            List<GenericSkill> extras = Extras(body.gameObject);
            GenericSkill[] all = body.GetComponents<GenericSkill>();
            for (int k = 0; k < extras.Count && k < command.ExtraVariants.Length; k++)
            {
                int wanted = command.ExtraVariants[k];
                GenericSkill skill = extras[k];
                SkillFamily.Variant[] variants = skill.skillFamily.variants;
                if (wanted < 0 || variants == null || wanted >= variants.Length || variants[wanted].skillDef == null) continue;
                // Dans le loadout du joueur : utilisé à chaque nouveau corps et au début de chaque étape
                // (c'est là que l'Opérateur reçoit ses drones).
                SetLoadoutVariant(body, Array.IndexOf(all, skill), (uint)wanted);
                SkillDef def = variants[wanted].skillDef;
                if (skill.baseSkill == def) continue;
                if (SetBaseSkill(skill, def))
                    _log.LogInfo($"Variante de Skyrim : {command.VariantSurvivor} case {SlotTitle(skill, k)} -> {Language.GetString(def.skillNameToken)}");
            }
        }

        private string _weaponRequest = "";

        private void RespawnForWeapon(CharacterBody body, GenericSkill skill, uint variant, string survivor)
        {
            CharacterMaster master = body.master;
            if (master == null) return;
            string request = survivor + "|" + variant + "|" + body.GetInstanceID();
            if (request == _weaponRequest) return;
            _weaponRequest = request;
            int skillSlot = Array.IndexOf(body.GetComponents<GenericSkill>(), skill);
            // 0.70 (coop, invité) : c'est l'hôte qui change le loadout et fait réapparaître le survivant.
            if (Coop.Instance != null && Coop.Instance.ClientActive) { Coop.Instance.SendLoadout(1, skillSlot, variant); return; }
            SetLoadoutVariant(body, skillSlot, variant);
            _log.LogInfo($"Arme du {survivor} changee : reapparition pour mettre son modele a jour");
            master.Respawn(body.footPosition, body.transform.rotation);
        }

        // 0.70 (coop, hôte) : le skin (kind 0) ou l'arme (kind 1 : case, variante) d'un invité, puis réapparition.
        internal void ApplyLoadoutFor(CharacterBody body, int kind, int skillSlot, uint value)
        {
            CharacterMaster master = body != null ? body.master : null;
            if (master == null || master.loadout == null) return;
            try
            {
                if (kind == 0) master.loadout.bodyLoadoutManager.SetSkinIndex(body.bodyIndex, value);
                else SetLoadoutVariant(body, skillSlot, value);
                master.Respawn(body.footPosition, body.transform.rotation);
                _log.LogInfo($"Coop : {(kind == 0 ? "skin" : "arme")} d'un invite change ({value}), reapparition");
            }
            catch (Exception e) { _log.LogWarning("Coop : loadout d'un invite impossible a changer (" + (e.InnerException?.Message ?? e.Message) + ")"); }
        }

        // Les cases de compétences en plus des quatre habituelles (au plus 4), dans l'ordre de RoR2.
        internal static List<GenericSkill> Extras(GameObject bodyObject)
        {
            var list = new List<GenericSkill>();
            SkillLocator locator = bodyObject != null ? bodyObject.GetComponent<SkillLocator>() : null;
            if (locator == null) return list;
            foreach (GenericSkill skill in bodyObject.GetComponents<GenericSkill>())
            {
                if (list.Count >= 4) break;
                if (skill == null || skill.skillFamily == null || skill.skillFamily.variants == null) continue;
                if (skill == locator.primary || skill == locator.secondary || skill == locator.utility || skill == locator.special) continue;
                if (ReadBool(skill, "hideInLoadout")) continue;
                list.Add(skill);
            }
            return list;
        }

        private static bool ReadBool(object target, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object value = target.GetType().GetField(name, flags)?.GetValue(target) ?? target.GetType().GetProperty(name, flags)?.GetValue(target);
            return value is bool b && b;
        }

        // Le titre d'une case en plus, comme dans l'écran de loadout de RoR2 ("Balise 1"...), sinon son nom interne.
        private static string SlotTitle(GenericSkill skill, int index)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (string field in new[] { "loadoutTitleToken", "titleToken" })
            {
                object token = skill.GetType().GetField(field, flags)?.GetValue(skill);
                if (token is string t && t.Length > 0)
                {
                    string text = CleanName(Language.GetString(t));
                    if (text.Length > 0 && text != t) return text;
                }
            }
            // Pas de titre traduit : on devine d'après la case (son nom interne, "FireSpear", ne veut rien dire en jeu).
            SkillFamily family = skill.skillFamily;
            SkillLocator locator = skill.GetComponent<SkillLocator>();
            SkillFamily primary = locator != null && locator.primary != null ? locator.primary.skillFamily : null;
            if (family != null && primary != null && family.variants != null && primary.variants != null && family.variants.Length > 0 &&
                primary.variants.Length > 0 && family.variants[0].skillDef == primary.variants[0].skillDef)
                return "M1 secondaire";  // MUL-T : l'arme de la deuxième main (mode Puissance)
            string name = skill.skillName ?? "";
            if (name.Length == 0 || name.IndexOf("passive", StringComparison.OrdinalIgnoreCase) >= 0) return "Passif";
            return "Case " + (index + 5);
        }

        private MethodInfo _setSkillVariant;
        private bool _variantSearched;

        private void SetLoadoutVariant(CharacterBody body, int skillSlot, uint variant)
        {
            CharacterMaster master = body.master;
            if (master == null || master.loadout == null || skillSlot < 0) return;
            if (!_variantSearched)
            {
                _variantSearched = true;
                _setSkillVariant = master.loadout.bodyLoadoutManager.GetType().GetMethod("SetSkillVariant",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(BodyIndex), typeof(int), typeof(uint) }, null);
                if (_setSkillVariant == null) _log.LogWarning("Variantes : SetSkillVariant introuvable (les drones de depart ne suivront pas)");
            }
            if (_setSkillVariant == null) return;
            try { _setSkillVariant.Invoke(master.loadout.bodyLoadoutManager, new object[] { body.bodyIndex, skillSlot, variant }); }
            catch (Exception e) { _log.LogWarning("Variantes : loadout impossible a changer : " + (e.InnerException?.Message ?? e.Message)); }
        }

        // Le skin : on ne touche PAS au modèle déjà affiché (depuis RoR2 1.5, les skins s'appliquent en
        // différé, et le changer en direct pouvait faire planter le jeu). On l'inscrit dans le loadout du
        // joueur : RoR2 l'applique lui-même à la création du corps. Si le corps actuel n'a pas le bon skin,
        // on le fait réapparaître au même endroit (une seule fois par choix), comme un changement de survivant.
        private void ApplySkin(CharacterBody body, string survivor, int skin)
        {
            if (skin < 0) return;
            CharacterMaster master = body.master;
            if (master == null || master.loadout == null) return;
            BodyIndex index = body.bodyIndex;
            SkinDef[] skins = SkinCatalog.GetBodySkinDefs(index);
            uint wanted = skins != null && skin < skins.Length ? (uint)skin : 0u;
            try
            {
                if (master.loadout.bodyLoadoutManager.GetSkinIndex(index) != wanted)
                    master.loadout.bodyLoadoutManager.SetSkinIndex(index, wanted);
                if (BodySkin(body) == wanted) return;

                string request = survivor + "|" + wanted;
                if (request == _skinRequest) return;  // déjà demandé : pas de boucle de réapparitions
                _skinRequest = request;
                // 0.70 (coop, invité) : c'est l'hôte qui change le skin et fait réapparaître le survivant.
                if (Coop.Instance != null && Coop.Instance.ClientActive) { Coop.Instance.SendLoadout(0, 0, wanted); return; }
                _log.LogInfo($"Skin de Skyrim : {survivor} -> {(skins != null && wanted < skins.Length ? Language.GetString(skins[wanted].nameToken) : wanted.ToString())} (reapparition)");
                master.Respawn(body.footPosition, body.transform.rotation);
            }
            catch (Exception e)
            {
                _log.LogWarning("Skin : changement impossible : " + (e.InnerException?.Message ?? e.Message));
                _skinRequest = survivor + "|" + wanted;
            }
        }

        // Le numéro de skin du corps (champ ou propriété selon la version de RoR2). 0 si introuvable.
        internal static uint BodySkin(CharacterBody body)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object value = typeof(CharacterBody).GetProperty("skinIndex", flags)?.GetValue(body)
                           ?? typeof(CharacterBody).GetField("skinIndex", flags)?.GetValue(body);
            return value is uint number ? number : 0u;
        }

        // Change la compétence de base d'une case (comme le ferait le loadout au début de la partie).
        private bool SetBaseSkill(GenericSkill skill, SkillDef def)
        {
            if (!_searched)
            {
                _searched = true;
                _setBaseSkill = typeof(GenericSkill).GetMethod("SetBaseSkill",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(SkillDef) }, null);
                if (_setBaseSkill == null) _log.LogError("Variantes : GenericSkill.SetBaseSkill introuvable dans cette version de RoR2");
            }
            if (_setBaseSkill == null) return false;
            try
            {
                _setBaseSkill.Invoke(skill, new object[] { def });
                return true;
            }
            catch (Exception e)
            {
                _log.LogWarning("Variantes : changement impossible : " + (e.InnerException?.Message ?? e.Message));
                return false;
            }
        }

        private static bool IsSurvivor(CharacterBody body, string survivor)
        {
            foreach (SurvivorDef def in SurvivorCatalog.allSurvivorDefs)
                if (def != null && def.cachedName == survivor && def.bodyPrefab != null)
                    return body.name.StartsWith(def.bodyPrefab.name);
            return false;
        }

        private void WriteVariantList()
        {
            if (Run.instance == null || Run.instance == _listWrittenFor) return;
            _listWrittenFor = Run.instance;
            try
            {
                var lines = new List<string>();
                foreach (SurvivorDef def in SurvivorCatalog.allSurvivorDefs.Where(d => d != null && !d.hidden && d.bodyPrefab != null))
                {
                    SkillLocator locator = def.bodyPrefab.GetComponent<SkillLocator>();
                    if (locator == null) continue;
                    GenericSkill[] slots = { locator.primary, locator.secondary, locator.utility, locator.special };
                    for (int slot = 0; slot < 4; slot++)
                    {
                        SkillFamily family = slots[slot] != null ? slots[slot].skillFamily : null;
                        if (family == null || family.variants == null) continue;
                        for (int i = 0; i < family.variants.Length; i++)
                        {
                            SkillDef skill = family.variants[i].skillDef;
                            string name = skill != null ? CleanName(Language.GetString(skill.skillNameToken)) : "";
                            if (name.Length == 0) name = "Variante " + (i + 1);
                            lines.Add($"{def.cachedName}|{slot}|{i}|{name}");
                        }
                    }

                    // Les cases en plus (balises du Capitaine, drones de l'Opérateur...) : 5 à 8.
                    List<GenericSkill> extras = Extras(def.bodyPrefab);
                    for (int k = 0; k < extras.Count; k++)
                    {
                        SkillFamily family = extras[k].skillFamily;
                        lines.Add($"{def.cachedName}|{5 + k}|-1|{SlotTitle(extras[k], k)}");
                        for (int i = 0; i < family.variants.Length; i++)
                        {
                            SkillDef skill = family.variants[i].skillDef;
                            string name = skill != null ? CleanName(Language.GetString(skill.skillNameToken)) : "";
                            if (name.Length == 0) name = "Variante " + (i + 1);
                            lines.Add($"{def.cachedName}|{5 + k}|{i}|{name}");
                        }
                    }

                    // Les skins du survivant (comptés comme une 5e "compétence").
                    SkinDef[] skins = SkinCatalog.GetBodySkinDefs(BodyCatalog.FindBodyIndex(def.bodyPrefab));
                    for (int i = 0; skins != null && i < skins.Length; i++)
                    {
                        string name = skins[i] != null ? CleanName(Language.GetString(skins[i].nameToken)) : "";
                        if (name.Length == 0) name = "Skin " + (i + 1);
                        lines.Add($"{def.cachedName}|4|{i}|{name}");
                    }
                }
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimRoR2");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "variantes.txt"), lines, new System.Text.UTF8Encoding(false));
                _log.LogInfo("Liste des variantes de competences pour Skyrim : " + lines.Count + " variantes");
            }
            catch (Exception e)
            {
                _log.LogWarning("Liste des variantes impossible a ecrire : " + e.Message);
            }
        }

        // Sans balises de couleur ni caractères que la police de Skyrim n'a pas.
        private static string CleanName(string text)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]*>", "");
            // Lettres que la police de Skyrim n'a pas : "écœurant" -> "écoeurant".
            text = text.Replace("\u0153", "oe").Replace("\u0152", "Oe").Replace("\u00E6", "ae").Replace("\u00C6", "Ae");
            // Le Démon du vide a des noms "corrompus" pleins de "?" dans la traduction française ("N??oyad?e") :
            // dans Skyrim, on les écrit proprement ("Noyade").
            text = text.Replace("?", "");
            var clean = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
                // (0xAD : tiret invisible de coupure, il s'affichait en "??" dans "Démon du vide")
                if ((c >= 0x20 && c < 0x7F && c != '|') || (c >= 0xA0 && c <= 0x17F && c != 0xAD)) clean.Append(c);
            return clean.ToString().Trim();
        }
    }
}
