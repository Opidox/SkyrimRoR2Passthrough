using System;
using System.Linq;
using System.Reflection;
using RoR2.ExpansionManagement;
using BepInEx.Logging;
using RoR2;
using SkyrimRoR2.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SkyrimRoR2
{
    // Partie automatique : quand Skyrim le demande (zone 6 du pont), RoR2 passe les menus
    // tout seul et lance une partie solo avec le survivant choisi dans SkyrimRoR2.ini.
    //
    //   menu principal  ->  "Solo"  ->  choix du survivant  ->  "Prêt"  ->  partie
    //
    // Si le survivant est mort et que tu recharges ta sauvegarde Skyrim, RoR2 quitte la partie
    // terminée et en relance une neuve.
    // Lancé seul (sans Skyrim), RoR2 reste normal : rien ne se passe.
    internal sealed class AutoRun
    {
        private const float SkyrimTimeoutSeconds = 3f;  // sans nouvelles de Skyrim : on ne fait rien
        private const float TitleSettleSeconds = 1f;    // laisse le menu principal finir de s'afficher
        private const float RetrySeconds = 12f;         // si une étape n'a rien donné, on réessaie

        private readonly ManualLogSource _log;
        private uint _lastSequence;
        private float _lastSkyrimTime = -100f;
        private uint _servedRequest;
        private bool _servedAny;

        private string _scene = "";
        private float _sceneSince;
        private float _nextAction;
        private int _lobbyStep;
        private bool _introSkipAsked;
        private float _nextIntroTry;
        private int _introStep;
        private float _inPodSince = -1f;

        // Vrai si Skyrim pilote RoR2 en ce moment (il envoie ses ordres).
        public bool SkyrimConnected => Time.unscaledTime - _lastSkyrimTime <= SkyrimTimeoutSeconds;

        private float _nextPauseCheck;
        private bool _cursorLogged;
        private bool _rewiredBlocked;
        private bool _rewiredWarned;
        private int _pauseFixes;

        // La touche Échap de Skyrim arrivait aussi dans RoR2 (qui lisait le clavier même en arrière-plan)
        // et ouvrait son menu pause.
        //  1. Tant que Skyrim pilote, RoR2 ignore le clavier quand sa fenêtre n'est pas active
        //     (réglage de Rewired, la bibliothèque de commandes de RoR2). Les commandes de Skyrim,
        //     elles, passent toujours : elles ne passent pas par le clavier de RoR2.
        //  2. Filet de sécurité : si RoR2 est quand même en pause, on enlève la pause.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        // Vrai si c'est vraiment la fenêtre de RoR2 qui est au premier plan
        // (Unity se trompe parfois quand sa fenêtre est cachée).
        private static readonly uint OwnPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        private static bool RoR2InForeground()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;
            GetWindowThreadProcessId(window, out uint pid);
            return pid == OwnPid;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private bool _everConnected;
        private bool _quitting;

        // Skyrim fermé : RoR2 ne doit pas rester caché en fond (il garderait la souris).
        // Après 10 secondes sans nouvelles de Skyrim, RoR2 se ferme tout seul
        // (réglage QuitterAvecSkyrim), sinon sa fenêtre réapparaît.
        public void WatchSkyrimGone(bool quitWithSkyrim)
        {
            if (SkyrimConnected) { _everConnected = true; return; }
            if (!_everConnected || _quitting) return;
            if (Time.unscaledTime - _lastSkyrimTime < 10f) return;

            _quitting = true;
            if (quitWithSkyrim)
            {
                _log.LogInfo("Skyrim est ferme : RoR2 se ferme aussi");
                Application.Quit();
                return;
            }

            _log.LogInfo("Skyrim est ferme : la fenetre RoR2 reapparait");
            EnumWindows((window, param) =>
            {
                GetWindowThreadProcessId(window, out uint pid);
                if (pid != OwnPid) return true;
                if (!IsWindowVisible(window)) ShowWindow(window, 5);  // SW_SHOW
                // Envoyée hors de l'écran par Skyrim : on la ramène.
                if (GetWindowRect(window, out Rect rect) && (rect.Left < -10000 || rect.Right - rect.Left < 300))
                    SetWindowPos(window, IntPtr.Zero, 100, 100, 960, 540, 0x0004 | 0x0010);  // NOZORDER | NOACTIVATE
                return true;
            }, IntPtr.Zero);
            _everConnected = false;
            _quitting = false;
        }

        public void PreventPause()
        {
            // RoR2 caché ne doit pas garder la souris pour lui (sinon elle reste bloquée, par exemple loin du 2e écran).
            if (SkyrimConnected && !RoR2InForeground() && Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
                if (!_cursorLogged) { _cursorLogged = true; _log.LogInfo("Souris liberee par RoR2 (Skyrim pilote)"); }
            }

            SetRewiredBackgroundInput(!SkyrimConnected);

            if (!SkyrimConnected || RoR2InForeground()) return;
            float now = Time.unscaledTime;
            if (now < _nextPauseCheck) return;
            _nextPauseCheck = now + 0.1f;

            bool paused = Time.timeScale == 0f;
            bool closed = false;
            foreach (RoR2.UI.PauseScreenController pause in UnityEngine.Object.FindObjectsOfType<RoR2.UI.PauseScreenController>())
            {
                if (pause == null) continue;
                UnityEngine.Object.Destroy(pause.gameObject);
                closed = true;
            }
            if (paused) Time.timeScale = 1f;

            if (closed || paused)
            {
                _pauseFixes++;
                if (_pauseFixes <= 10)
                    _log.LogInfo($"Pause de RoR2 enlevee (menu ferme : {closed}, temps arrete : {paused})");
            }
        }

        // RoR2 lit son clavier et sa souris même quand sa fenêtre est en arrière-plan : Échap, R, Espace...
        // tapés dans Skyrim arrivaient AUSSI dans RoR2 et se mélangeaient avec les commandes du pont
        // (menu pause, compétence bloquée, saut raté). Tant que Skyrim pilote et que la fenêtre RoR2
        // n'est pas active, on débranche le clavier et la souris de RoR2 (dans Rewired, sa bibliothèque
        // de commandes). Les commandes de Skyrim passent par le pont, pas par le clavier : elles marchent toujours.
        private float _nextRewiredTry;
        private bool _rewiredOk;

        private void SetRewiredBackgroundInput(bool allowed)
        {
            bool block = !allowed && !RoR2InForeground();
            if (_rewiredOk && block == _rewiredBlocked) return;

            float now = Time.unscaledTime;
            if (!_rewiredOk && now < _nextRewiredTry) return;
            _nextRewiredTry = now + 2f;  // Rewired démarre après notre mod : on réessaie tant qu'il n'est pas prêt

            try
            {
                Type reInput = Type.GetType("Rewired.ReInput, Rewired_Core");
                if (reInput == null) { WarnRewired("Rewired introuvable"); return; }

                PropertyInfo ready = reInput.GetProperty("isReady", BindingFlags.Static | BindingFlags.Public);
                if (ready != null && ready.GetValue(null) is bool isReady && !isReady) return;  // pas encore démarré

                object controllers = reInput.GetProperty("controllers", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                int changed = 0;
                foreach (string name in new[] { "Keyboard", "Mouse" })
                {
                    object device = controllers?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(controllers);
                    PropertyInfo enabled = device?.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
                    if (enabled == null || !enabled.CanWrite) continue;
                    enabled.SetValue(device, !block);
                    changed++;
                }
                if (changed == 0) { WarnRewired("clavier/souris de Rewired introuvables"); return; }

                _rewiredOk = true;
                _rewiredBlocked = block;
                _log.LogInfo(block ? "Clavier et souris de RoR2 debranches (Skyrim pilote, fenetre RoR2 en arriere-plan)"
                                   : "Clavier et souris de RoR2 rebranches");
            }
            catch (Exception e)
            {
                WarnRewired((e.InnerException ?? e).Message);
            }
        }

        private void WarnRewired(string message)
        {
            if (_rewiredWarned) return;
            _rewiredWarned = true;
            _log.LogWarning("Rewired : " + message);
        }

        // Vrai si le survivant est assis dans la capsule du début de partie.
        // Attention : d'autres compétences mettent aussi le survivant dans un "véhicule" (Voyage et Sursis de la
        // Chercheuse...). Avant, on les prenait pour la capsule : la Chercheuse était éjectée au bout d'une
        // demi-seconde (écran flou, comme mise à terre) et ses déplacements n'étaient plus recopiés dans Skyrim.
        // On ne garde donc que les vraies capsules (leur nom contient "Pod", ou "Crate" pour le MUL-T).
        public static bool InVehicle(CharacterBody body) => PodSeat(body) != null;

        // Assis dans n'importe quel véhicule (capsule, ou Voyage et Sursis de la Chercheuse...).
        public static bool InAnyVehicle(CharacterBody body)
        {
            if (body == null) return false;
            object seat = GetMember(body, "currentVehicle");
            return seat != null && !seat.Equals(null);
        }

        private static object PodSeat(CharacterBody body)
        {
            if (body == null) return null;
            object seat = GetMember(body, "currentVehicle");
            if (seat == null || seat.Equals(null) || !(seat is Component component) || component == null) return null;
            // (le siège peut être un enfant de la capsule : on regarde aussi le nom de l'objet racine)
            string name = component.gameObject.name + "|" + component.transform.root.gameObject.name;
            return name.IndexOf("Pod", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Crate", StringComparison.OrdinalIgnoreCase) >= 0 ? seat : null;
        }

        // 0.70 (coop, hôte) : fait sortir ce survivant (un invité) de sa capsule. Vrai si c'est fait.
        public static bool Eject(CharacterBody body)
        {
            object seat = PodSeat(body);
            if (seat == null) return false;
            try
            {
                MethodInfo eject = seat.GetType().GetMethod("EjectPassenger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(GameObject) }, null);
                if (eject != null) { eject.Invoke(seat, new object[] { body.gameObject }); return true; }
                eject = seat.GetType().GetMethod("EjectPassenger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (eject != null) { eject.Invoke(seat, null); return true; }
            }
            catch (Exception) { }
            return false;
        }

        // Au début de la partie, le survivant arrive dans une capsule dont on sort avec "Utiliser".
        // Skyrim n'envoie pas cette touche : on le fait sortir tout seul de la capsule.
        public void LeavePod(CharacterBody body)
        {
            if (body == null || !SkyrimConnected) { _inPodSince = -1f; return; }

            object seat = PodSeat(body);
            if (seat == null) { _inPodSince = -1f; return; }

            float now = Time.unscaledTime;
            if (_inPodSince < 0f) _inPodSince = now;
            if (now - _inPodSince < 0.5f) return;  // une demi-seconde, le temps que la capsule s'installe

            if (TryInvoke(seat, "EjectPassenger", body.gameObject) || TryInvokeNoArgument(seat, "EjectPassenger"))
            {
                _log.LogInfo("Survivant sorti de sa capsule");
                _inPodSince = now + 2f;  // pas de nouvel essai tout de suite
            }
        }

        private static object GetMember(object target, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo property = target.GetType().GetProperty(name, flags);
            if (property != null) return property.GetValue(target);
            FieldInfo field = target.GetType().GetField(name, flags);
            return field != null ? field.GetValue(target) : null;
        }

        // Réglage r2modman "Lancement > Survivant" (vide = celui du .ini de Skyrim).
        public BepInEx.Configuration.ConfigEntry<string> SurvivorOverride;

        // Change de survivant tout de suite, en pleine partie (objets et niveau gardés).
        public float LastChangeTime = -100f;  // moment du dernier changement de survivant

        public void ChangeSurvivorNow(string wanted)
        {
            LastChangeTime = Time.unscaledTime;
            if (Run.instance == null || string.IsNullOrWhiteSpace(wanted)) return;
            SurvivorDef survivor = FindSurvivor(wanted);
            LocalUser user = LocalUserManager.GetFirstLocalUser();
            if (survivor != null && user?.userProfile != null) TryInvoke(user.userProfile, "SetSurvivorPreference", survivor);
            // 0.70 (coop, invité) : c'est l'hôte qui change notre survivant.
            Coop coop = Coop.Instance;
            if (coop != null && coop.ClientActive)
            {
                if (survivor != null) coop.SendSurvivor(survivor.cachedName);
                return;
            }
            ChangeSurvivorFor(user?.cachedMaster, wanted);
        }

        // Change le survivant de ce joueur (le nôtre, ou celui d'un invité pour l'hôte), en pleine partie.
        public void ChangeSurvivorFor(CharacterMaster master, string wanted)
        {
            LastChangeTime = Time.unscaledTime;
            if (Run.instance == null || string.IsNullOrWhiteSpace(wanted)) return;
            SurvivorDef survivor = FindSurvivor(wanted);
            if (survivor == null || survivor.bodyPrefab == null || master == null) return;
            CharacterBody current = master.GetBody();
            if (current != null && current.name.StartsWith(survivor.bodyPrefab.name)) return;  // c'est déjà lui

            // Les tourelles, mines, balises, drones de départ... de l'ancien survivant partent avec lui.
            if (current != null) RemoveSkillSummons(master, current);

            if (TryInvoke(master, "TransformBody", survivor.bodyPrefab.name))
            {
                _log.LogInfo("Survivant change en pleine partie : " + Describe(survivor));
                return;
            }
            // Autre façon : on change le corps du maître et on le fait réapparaître au même endroit.
            CharacterBody body = master.GetBody();
            if (body == null) return;
            master.bodyPrefab = survivor.bodyPrefab;
            master.Respawn(body.footPosition, body.transform.rotation);
            _log.LogInfo("Survivant change (reapparition) : " + Describe(survivor));
        }

        // Ce que l'ancien survivant a posé ou appelé avec SES compétences : tourelles et mines de l'Ingénieur,
        // balises du Capitaine, pylônes de la Chargeuse, drones de départ de l'Opérateur...
        // On les reconnaît à leur nom, qui commence comme celui du survivant ("Engi..." pour "EngiBody").
        // Les drones achetés et les alliés donnés par des objets restent.
        private void RemoveSkillSummons(CharacterMaster master, CharacterBody current)
        {
            string prefix = current.name.Replace("(Clone)", "").Trim();
            if (prefix.EndsWith("Body")) prefix = prefix.Substring(0, prefix.Length - 4);
            if (prefix.Length < 3) return;
            var keys = new System.Collections.Generic.List<string> { prefix };
            if (prefix.IndexOf("Operator", StringComparison.OrdinalIgnoreCase) >= 0 || prefix.IndexOf("DroneTech", StringComparison.OrdinalIgnoreCase) >= 0)
                keys.AddRange(new[] { "Operator", "DroneTech", "Mk2", "MkII" });
            // REX : la plante de Prolifération ne porte pas son nom.
            if (prefix.IndexOf("Treebot", StringComparison.OrdinalIgnoreCase) >= 0)
                keys.AddRange(new[] { "Proliferation", "TreebotFlower" });
            // 0.73 : les drones de l'Opérateur s'appellent "DTGunnerDrone...", "DTHealingDrone"... (gardés par erreur avant).
            bool operatorDrones = keys.Contains("DroneTech");
            bool Matches(string name) => name != null && (keys.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                                          (operatorDrones && name.StartsWith("DT", StringComparison.Ordinal)));
            InventorySync.RemoveSurvivorItems(master.inventory, _log);

            int removed = 0;
            var kept = new System.Collections.Generic.List<string>();
            try
            {
                // La liste des objets posés n'est pas publique dans cette version de RoR2 : on la lit par son type.
                foreach ((Deployable deployable, string slot) in FindDeployables(master))
                {
                    if (deployable == null) continue;
                    if (!Matches(slot) && !Matches(deployable.gameObject.name))
                    {
                        kept.Add(slot + "/" + deployable.gameObject.name);
                        continue;
                    }
                    CharacterMaster minion = deployable.GetComponent<CharacterMaster>();
                    if (minion != null) minion.TrueKill();
                    else UnityEngine.Object.Destroy(deployable.gameObject);
                    removed++;
                }
                foreach (CharacterMaster minion in CharacterMaster.readOnlyInstancesList.ToList())
                {
                    if (minion == null || minion == master || minion.minionOwnership == null || minion.minionOwnership.ownerMaster != master) continue;
                    CharacterBody body = minion.GetBody();
                    if (!Matches(minion.name) && !(body != null && Matches(body.name)))
                    {
                        kept.Add(minion.name.Replace("(Clone)", ""));
                        continue;
                    }
                    minion.TrueKill();
                    removed++;
                }
            }
            catch (Exception e)
            {
                _log.LogWarning("Changement de survivant : rangement des invocations impossible (" + e.Message + ")");
            }
            _log.LogInfo($"Changement de survivant : {removed} invocation(s) de {prefix} retiree(s)" +
                         (kept.Count > 0 ? " ; gardes : " + string.Join(", ", kept.Distinct()) : ""));
        }

        // Les objets posés par ce maître (liste "deployable + case"), quel que soit le nom du champ.
        private static System.Collections.Generic.List<(Deployable, string)> FindDeployables(CharacterMaster master)
        {
            var result = new System.Collections.Generic.List<(Deployable, string)>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (FieldInfo field in typeof(CharacterMaster).GetFields(flags))
            {
                Type type = field.FieldType;
                if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(System.Collections.Generic.List<>)) continue;
                Type item = type.GetGenericArguments()[0];
                FieldInfo deployableField = item.GetField("deployable", flags);
                FieldInfo slotField = item.GetField("slot", flags);
                if (deployableField == null || slotField == null) continue;
                if (!(field.GetValue(master) is System.Collections.IList list)) continue;
                foreach (object entry in list)
                    if (entry != null) result.Add((deployableField.GetValue(entry) as Deployable, slotField.GetValue(entry)?.ToString() ?? ""));
                break;
            }
            return result;
        }

        // Touches "survivant suivant / précédent" de Skyrim (F8 / F7) : on passe au survivant d'à côté
        // dans la liste de RoR2. Le choix est aussi enregistré dans r2modman (réglage Lancement > Survivant).
        private bool _keysStarted;
        private uint _lastNext, _lastPrevious;

        private void HandleSurvivorKeys(SkyrimCommand command)
        {
            if (!_keysStarted)
            {
                _keysStarted = true;
                _lastNext = command.NextSurvivor;
                _lastPrevious = command.PreviousSurvivor;
                return;
            }
            int step = 0;
            if (command.NextSurvivor != _lastNext) { step += (int)(command.NextSurvivor - _lastNext); _lastNext = command.NextSurvivor; }
            if (command.PreviousSurvivor != _lastPrevious) { step -= (int)(command.PreviousSurvivor - _lastPrevious); _lastPrevious = command.PreviousSurvivor; }
            if (step == 0 || Run.instance == null) return;

            // Seulement les survivants vraiment jouables ici : visibles, débloqués, et dont le DLC est actif.
            var list = SurvivorCatalog.allSurvivorDefs
                .Where(d => d != null && !d.hidden && d.bodyPrefab != null && IsPlayable(d))
                .OrderBy(d => d.desiredSortPosition)
                .ToList();
            if (list.Count == 0) return;

            CharacterBody current = LocalUserManager.GetFirstLocalUser()?.cachedBody;
            int index = current != null ? list.FindIndex(d => current.name.StartsWith(d.bodyPrefab.name)) : -1;
            int next = ((index < 0 ? 0 : index) + step) % list.Count;
            if (next < 0) next += list.Count;

            SurvivorDef chosen = list[next];
            _log.LogInfo("Touche de Skyrim : survivant " + chosen.cachedName);
            // Enregistré dans r2modman ; le changement de réglage déclenche lui-même le changement de survivant.
            if (SurvivorOverride != null && SurvivorOverride.Value != chosen.cachedName) SurvivorOverride.Value = chosen.cachedName;
            else ChangeSurvivorNow(chosen.cachedName);
        }

        // Survivant choisi à un autel de Skyrim (ou F8/F7 parmi les survivants débloqués dans cette sauvegarde).
        private uint _lastSelect;

        private void HandleSurvivorSelect(SkyrimCommand command)
        {
            if (command.SelectSequence == _lastSelect) return;
            _lastSelect = command.SelectSequence;
            string wanted = command.SelectSurvivor;
            if (string.IsNullOrWhiteSpace(wanted)) return;
            _log.LogInfo("Autel de Skyrim : survivant " + wanted);
            // Enregistré dans r2modman (la partie automatique le prendra aussi) ; le changement de réglage
            // déclenche lui-même le changement de survivant en pleine partie.
            if (SurvivorOverride != null && SurvivorOverride.Value != wanted) SurvivorOverride.Value = wanted;
            else ChangeSurvivorNow(wanted);
        }

        // La liste des survivants jouables, avec leur nom affiché, pour l'écran de choix des autels de Skyrim :
        // %LOCALAPPDATA%\SkyrimRoR2\survivants.txt, une ligne "nom interne|nom affiché" par survivant.
        private Run _listWrittenFor;

        private void WriteSurvivorList()
        {
            if (Run.instance == null || Run.instance == _listWrittenFor) return;
            _listWrittenFor = Run.instance;
            try
            {
                var lines = SurvivorCatalog.allSurvivorDefs
                    .Where(d => d != null && !d.hidden && d.bodyPrefab != null && IsPlayable(d))
                    .OrderBy(d => d.desiredSortPosition)
                    .Select(d => d.cachedName + "|" + CleanName(Language.GetString(d.displayNameToken)))
                    .ToList();
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimRoR2");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "survivants.txt"), lines, new System.Text.UTF8Encoding(false));
                _log.LogInfo("Liste des survivants pour les autels de Skyrim : " + lines.Count + " survivants");
            }
            catch (Exception e)
            {
                _log.LogWarning("Liste des survivants impossible a ecrire : " + e.Message);
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

        private MethodInfo _isUnlocked;
        private bool _unlockSearched;

        private bool IsPlayable(SurvivorDef def)
        {
            try
            {
                if (!_unlockSearched)
                {
                    _unlockSearched = true;
                    _isUnlocked = typeof(SurvivorCatalog).GetMethod("SurvivorIsUnlockedOnThisClient",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(SurvivorIndex) }, null);
                }
                if (_isUnlocked != null && _isUnlocked.Invoke(null, new object[] { def.survivorIndex }) is bool unlocked && !unlocked)
                    return false;

                // Survivant d'un DLC : le DLC doit être actif dans cette partie.
                ExpansionRequirementComponent requirement = def.bodyPrefab.GetComponent<ExpansionRequirementComponent>();
                if (requirement != null && requirement.requiredExpansion != null && Run.instance != null &&
                    !Run.instance.IsExpansionEnabled(requirement.requiredExpansion))
                    return false;
            }
            catch { }
            return true;
        }

        public AutoRun(ManualLogSource log)
        {
            _log = log;
        }

        public void Update(BridgeWriter bridge, bool survivorDead)
        {
            float now = Time.unscaledTime;

            string scene = SceneManager.GetActiveScene().name ?? "";
            if (scene != _scene)
            {
                _log.LogInfo($"[{Time.realtimeSinceStartup:F1} s] Scene : {scene}");
                _scene = scene;
                _sceneSince = now;
                _nextAction = Mathf.Min(_nextAction, now + 0.3f);  // nouvel écran : on agit tout de suite
                _lobbyStep = 0;
            }

            if (!bridge.ReadCommand(out SkyrimCommand command)) return;
            if (command.Sequence != _lastSequence)
            {
                _lastSequence = command.Sequence;
                _lastSkyrimTime = now;
            }
            HandleSurvivorKeys(command);
            HandleSurvivorSelect(command);
            WriteSurvivorList();
            if (now - _lastSkyrimTime > SkyrimTimeoutSeconds || command.AutoStart == 0) return;
            if (now < _nextAction) return;

            // 1. Une partie est en cours.
            if (Run.instance != null)
            {
                if (!survivorDead)
                {
                    MarkServed(command.RequestId);  // la partie en cours répond à la demande
                }
                else if (Coop.Instance != null && Coop.Instance.Enabled)
                {
                    MarkServed(command.RequestId);  // 0.70 (coop) : on ne quitte jamais la partie (la coop gère la mort)
                }
                else if (!_servedAny || command.RequestId != _servedRequest)
                {
                    // Survivant mort + nouvelle sauvegarde chargée dans Skyrim : on repart à zéro.
                    _log.LogInfo("Survivant mort et sauvegarde Skyrim rechargee : on quitte la partie pour en relancer une");
                    Submit("disconnect");
                    MarkServed(command.RequestId);
                    _nextAction = now + 5f;
                }
                return;
            }

            // 0. La cinématique d'introduction : on la passe une fois qu'elle est bien chargée,
            //    comme si on appuyait sur une touche (la sauter AVANT le chargement casse le jeu,
            //    c'est ce que faisait intro_skip).
            if (IsIntroScene(scene))
            {
                // Le bouton "passer" n'existe qu'une fois le jeu chargé à 100 % :
                // on attend que l'écran de chargement (le pourcentage) disparaisse, puis on passe.
                if (now >= _nextIntroTry && now - _sceneSince >= 2f)
                {
                    _nextIntroTry = now + 1f;
                    if (!_introSkipAsked)
                    {
                        _introSkipAsked = true;
                        _introStep = 0;
                        DescribeIntro();
                    }
                    if (!StillLoading())
                    {
                        TrySkipIntro(_introStep++);
                        _nextIntroTry = now + 3f;  // laisse le temps à la scène de changer avant l'essai suivant
                    }
                }
                return;
            }
            _introSkipAsked = false;

            // Le survivant choisi dans r2modman passe avant celui du .ini de Skyrim.
            string wanted = SurvivorOverride != null && !string.IsNullOrWhiteSpace(SurvivorOverride.Value) ? SurvivorOverride.Value : command.Survivor;
            SurvivorDef survivor = FindSurvivor(wanted);

            // 2. Écran de choix du survivant.
            Coop coop = Coop.Instance;
            if (PreGameController.instance != null)
            {
                if (_lobbyStep == 0)
                {
                    ChooseSurvivorInLobby(survivor);
                    _lobbyStep = 1;
                    _lobbySince = now;
                    _nextAction = now + 0.5f;  // laisse le choix arriver avant de lancer
                }
                else if (coop != null && coop.WantsJoin)
                {
                    // 0.70 (coop, invité) : c'est l'hôte qui lance la partie ; on garde juste notre choix à jour.
                    if (now - _lobbySince > 3f) { ChooseSurvivorInLobby(survivor); _lobbySince = now; }
                    _nextAction = now + 1f;
                }
                else if (coop != null && coop.WantsHost && NetworkUser.readOnlyInstancesList.Count < coop.WaitPlayers && now - _lobbySince < coop.WaitSeconds)
                {
                    // 0.70 (coop, hôte) : on attend les invités.
                    if (now >= _nextWaitLog)
                    {
                        _nextWaitLog = now + 10f;
                        _log.LogInfo($"Coop : en attente des joueurs ({NetworkUser.readOnlyInstancesList.Count}/{coop.WaitPlayers}, " +
                                     $"encore {coop.WaitSeconds - (now - _lobbySince):F0} s au plus)");
                    }
                    _nextAction = now + 1f;
                }
                else
                {
                    _log.LogInfo($"[{Time.realtimeSinceStartup:F1} s] Partie automatique : lancement de la partie");
                    if (!TryInvokeNoArgument(PreGameController.instance, "StartRun"))
                        Submit("pregame_start_run");
                    _lobbyStep = 0;
                    _nextAction = now + RetrySeconds;
                }
                return;
            }

            // 3. Menu principal : on clique sur "Solo".
            if (scene == "title" && now - _sceneSince >= TitleSettleSeconds)
            {
                LocalUser user = LocalUserManager.GetFirstLocalUser();
                if (user == null || user.userProfile == null) return;  // profil pas encore chargé

                if (survivor != null) TryInvoke(user.userProfile, "SetSurvivorPreference", survivor);
                if (coop != null && coop.WantsJoin)
                {
                    // 0.70 (coop, invité) : on rejoint l'hôte par Steam.
                    DescribeNetworkCommands();
                    if (coop.HostId.Length == 0)
                    {
                        if (now >= _nextWaitLog) { _nextWaitLog = now + 30f; _log.LogWarning("Coop : mode Invite, mais IdentifiantHote est vide (reglages r2modman, section 8 - Coop)"); }
                        _nextAction = now + 5f;
                        return;
                    }
                    _log.LogInfo($"[{Time.realtimeSinceStartup:F1} s] Coop : connexion a l'hote " + coop.HostId);
                    Submit("connect_steamworks_p2p " + coop.HostId);
                    _nextAction = now + 20f;
                    return;
                }
                bool host = coop != null && coop.WantsHost;
                _log.LogInfo($"[{Time.realtimeSinceStartup:F1} s] Partie automatique : menu principal -> {(host ? "partie coop ouverte" : "Solo")} (survivant " + Describe(survivor) + ")");
                Submit(host ? "gamemode ClassicRun; host 1" : "gamemode ClassicRun; host 0");  // directement, sans le fondu au noir du bouton

                _nextAction = now + RetrySeconds;
            }
        }

        private float _lobbySince;
        private float _nextWaitLog;
        private bool _commandsDescribed;

        // Une fois : les commandes réseau de la console de RoR2 (pour vérifier dans le journal celle qui rejoint un ami).
        private void DescribeNetworkCommands()
        {
            if (_commandsDescribed) return;
            _commandsDescribed = true;
            try
            {
                foreach (FieldInfo field in typeof(RoR2.Console).GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    object value = field.IsStatic ? field.GetValue(null) : (RoR2.Console.instance != null ? field.GetValue(RoR2.Console.instance) : null);
                    if (!(value is System.Collections.IDictionary dictionary)) continue;
                    var names = dictionary.Keys.OfType<string>().Where(k => k.Contains("connect") || k.Contains("lobby") || k.Contains("join") || k.Contains("host")).ToList();
                    if (names.Count > 0) _log.LogInfo("Coop : commandes reseau de RoR2 : " + string.Join(", ", names));
                }
            }
            catch (Exception e) { _log.LogInfo("Coop : commandes de RoR2 illisibles (" + e.Message + ")"); }
        }

        private static bool IsIntroScene(string scene)
        {
            string name = scene.ToLowerInvariant();
            return name.Contains("intro") || name.Contains("cutscene");
        }

        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Vrai tant que l'écran de chargement (avec son pourcentage) est affiché.
        private static bool StillLoading()
        {
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (behaviour != null && behaviour.GetType().Name == "LoadingScreenCanvas" && behaviour.isActiveAndEnabled)
                    return true;
            }
            return false;
        }

        private static MonoBehaviour FindBehaviour(string typeName)
        {
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (behaviour != null && behaviour.GetType().Name == typeName) return behaviour;
            }
            return null;
        }

        // Note dans le journal comment est faite la cinématique (pour viser juste si besoin).
        private void DescribeIntro()
        {
            MonoBehaviour intro = FindBehaviour("IntroCutsceneController");
            if (intro == null) { _log.LogWarning("Cinematique : IntroCutsceneController introuvable"); return; }
            Type type = intro.GetType();
            foreach (FieldInfo field in type.GetFields(AnyInstance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                object value = null;
                try { value = field.IsStatic ? field.GetValue(null) : field.GetValue(intro); } catch { }
                _log.LogInfo("Cinematique - champ : " + field.FieldType.Name + " " + field.Name + " = " + value);
            }
            foreach (MethodInfo method in type.GetMethods(AnyInstance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                _log.LogInfo("Cinematique - methode : " + method.Name + " (" + method.GetParameters().Length + " parametres)");
        }

        // Essais successifs pour terminer la cinématique, du plus "propre" au plus direct.
        private void TrySkipIntro(int step)
        {
            MonoBehaviour intro = FindBehaviour("IntroCutsceneController");
            string[] words = { "skip", "finish", "end", "complete", "exit", "title" };

            if (step == 0 && intro != null)
            {
                foreach (MethodInfo method in intro.GetType().GetMethods(AnyInstance | BindingFlags.DeclaredOnly))
                {
                    if (method.GetParameters().Length != 0 || method.ReturnType != typeof(void)) continue;
                    string name = method.Name.ToLowerInvariant();
                    if (!words.Any(w => name.Contains(w))) continue;
                    if (Call(intro, method)) return;
                }
                step = 1;  // rien trouvé : essai suivant tout de suite
            }

            if (step == 1 && intro != null)
            {
                // L'événement "fin de cinématique" posé sur le même objet.
                MonoBehaviour delayed = intro.GetComponent("DelayedEvent") as MonoBehaviour;
                if (delayed != null)
                {
                    foreach (FieldInfo field in delayed.GetType().GetFields(AnyInstance))
                    {
                        if (field.GetValue(delayed) is UnityEngine.Events.UnityEventBase unityEvent)
                        {
                            MethodInfo invoke = unityEvent.GetType().GetMethod("Invoke", Type.EmptyTypes);
                            if (invoke != null && Call(unityEvent, invoke)) return;
                        }
                    }
                }
                step = 2;
            }

            if (step == 2)
            {
                _log.LogInfo("Cinematique : passage direct au menu principal");
                Submit("set_scene title");
                return;
            }

            if (step == 5) _log.LogWarning("Cinematique : impossible de la passer, elle ira jusqu'au bout");
        }

        private bool Call(object target, MethodInfo method)
        {
            try
            {
                method.Invoke(target, null);
                _log.LogInfo("Cinematique passee : " + target.GetType().Name + "." + method.Name);
                return true;
            }
            catch (Exception e)
            {
                _log.LogWarning("Cinematique : " + target.GetType().Name + "." + method.Name + " a echoue : " + (e.InnerException ?? e).Message);
                return false;
            }
        }

        private void MarkServed(uint requestId)
        {
            _servedRequest = requestId;
            _servedAny = true;
        }

        private void ChooseSurvivorInLobby(SurvivorDef survivor)
        {
            if (survivor == null || survivor.bodyPrefab == null) return;

            BodyIndex bodyIndex = BodyCatalog.FindBodyIndex(survivor.bodyPrefab);
            NetworkUser user = NetworkUser.readOnlyLocalPlayersList.FirstOrDefault();
            if (user == null) return;

            LocalUser local = user.localUser;
            if (local != null && local.userProfile != null) TryInvoke(local.userProfile, "SetSurvivorPreference", survivor);

            // Le choix "officiel" du survivant dans le salon (même chose qu'un clic sur son portrait).
            bool sent = TryInvoke(user, "CallCmdSetBodyPreference", bodyIndex) || TryInvoke(user, "CmdSetBodyPreference", bodyIndex);
            _log.LogInfo($"[{Time.realtimeSinceStartup:F1} s] Partie automatique : survivant choisi = " + Describe(survivor) + (sent ? "" : " (par le profil seulement)"));
        }

        // Retrouve un survivant par son nom : interne ("Commando", "Huntress", "Mage"...),
        // nom du corps ("HuntressBody") ou nom affiché dans la langue du jeu ("Chasseresse").
        private SurvivorDef FindSurvivor(string wanted)
        {
            string key = Simplify(wanted);
            if (key.Length == 0) return null;

            foreach (SurvivorDef def in SurvivorCatalog.allSurvivorDefs)
            {
                if (def == null) continue;
                if (Simplify(def.cachedName) == key) return def;
                if (def.bodyPrefab != null && (Simplify(def.bodyPrefab.name) == key || Simplify(def.bodyPrefab.name) == key + "body")) return def;
                if (!string.IsNullOrEmpty(def.displayNameToken) && Simplify(Language.GetString(def.displayNameToken)) == key) return def;
            }

            if (!_warnedUnknown && SurvivorCatalog.allSurvivorDefs.Any())
            {
                _warnedUnknown = true;
                _log.LogWarning("Survivant inconnu : \"" + wanted + "\". Noms possibles : " +
                    string.Join(", ", SurvivorCatalog.allSurvivorDefs.Where(d => d != null).Select(d => d.cachedName)));
            }
            return null;
        }

        private bool _warnedUnknown;

        private static string Simplify(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        private static string Describe(SurvivorDef def) => def != null ? def.cachedName : "(celui du profil)";

        private void Submit(string commandLine)
        {
            if (RoR2.Console.instance == null) return;
            RoR2.Console.instance.SubmitCmd((NetworkUser)null, commandLine, false);
        }

        private bool TryInvokeNoArgument(object target, string method)
        {
            try
            {
                MethodInfo info = target.GetType().GetMethod(method,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (info == null) return false;
                info.Invoke(target, null);
                return true;
            }
            catch (Exception e)
            {
                _log.LogWarning("Appel impossible (" + method + ") : " + (e.InnerException ?? e).Message);
                return false;
            }
        }

        // Appelle une méthode par son nom (elle change parfois entre les versions de RoR2).
        private bool TryInvoke(object target, string method, object argument)
        {
            try
            {
                MethodInfo info = target.GetType().GetMethod(method,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { argument.GetType() }, null);
                if (info == null) return false;
                info.Invoke(target, new[] { argument });
                return true;
            }
            catch (Exception e)
            {
                _log.LogWarning("Appel impossible (" + method + ") : " + (e.InnerException ?? e).Message);
                return false;
            }
        }
    }
}
