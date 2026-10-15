using System.IO.MemoryMappedFiles;
using System.Text;
using SkyrimRoR2.Shared;

// La visionneuse affiche en direct les deux zones du pont :
// ce que RoR2 écrit (le survivant) et ce que Skyrim écrit (les commandes).
// Pratique pour comprendre ce qui se passe quand quelque chose ne marche pas.

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "SkyrimRoR2 - Visionneuse du pont";

MemoryMappedFile map = null;
Console.WriteLine("En attente de RoR2 ou de Skyrim...");

// 1. Attendre qu'un des deux jeux ait créé le pont.
while (map == null)
{
    try
    {
        map = MemoryMappedFile.OpenExisting(BridgeInfo.MapName, MemoryMappedFileRights.Read);
    }
    catch (FileNotFoundException)
    {
        Thread.Sleep(1000);
    }
}

using (map)
using (var view = map.CreateViewAccessor(0, BridgeInfo.MapSize, MemoryMappedFileAccess.Read))
{
    Console.Clear();
    Console.CursorVisible = false;

    uint lastRoR2 = 0, lastSkyrim = 0;
    DateTime ror2Seen = DateTime.MinValue, skyrimSeen = DateTime.MinValue;

    // 2. Lire et afficher en boucle (ferme la fenêtre pour arrêter).
    while (true)
    {
        view.Read(0, out BridgeData data);
        view.Read(BridgeInfo.InputOffset, out SkyrimInput input);

        if (data.Sequence != lastRoR2) { lastRoR2 = data.Sequence; ror2Seen = DateTime.Now; }
        if (input.Sequence != lastSkyrim) { lastSkyrim = input.Sequence; skyrimSeen = DateTime.Now; }
        bool ror2Alive = (DateTime.Now - ror2Seen).TotalSeconds < 2;
        bool skyrimAlive = (DateTime.Now - skyrimSeen).TotalSeconds < 2;

        var lines = new List<string>
        {
            "=== Pont SkyrimRoR2 ===",
            "",
            "--- RoR2 -> Skyrim ---",
            ror2Alive ? $"RoR2         : connecté (format v{data.Version})" : "RoR2         : absent",
            data.InRun == 1 ? "Survivant    : en jeu" : "Survivant    : aucun (menu ou chargement)",
            data.InRun == 1 ? $"Position     : X {data.PosX,9:F2}   Y {data.PosY,9:F2}   Z {data.PosZ,9:F2}" : "",
            data.InRun == 1 ? $"PV           : {data.Health:F0} / {data.MaxHealth:F0}" : "",
            data.InRun == 1 ? $"Vitesse      : {Math.Sqrt(data.VelX * data.VelX + data.VelZ * data.VelZ),5:F1} m/s au sol   ({data.VelY,5:F1} m/s vertical)" : "",
            "",
            "--- Skyrim -> RoR2 ---",
            skyrimAlive && input.Magic == BridgeInfo.InputMagicValue ? "Skyrim       : connecté" : "Skyrim       : absent",
            input.Active == 1 ? "Commandes    : ACTIVES (Skyrim au premier plan)" : "Commandes    : en pause (Skyrim pas au premier plan, ou menu)",
            $"Déplacement  : avant {input.MoveForward,4:F0}   droite {input.MoveRight,4:F0}",
            $"Caméra       : direction {input.Yaw * 57.2958f,7:F1}°   inclinaison {input.Pitch * 57.2958f,6:F1}°",
            $"Touches      : {Key("Saut", input.Jump)} {Key("Sprint", input.Sprint)} {Key("C1", input.Skill1)} {Key("C2", input.Skill2)} {Key("C3", input.Skill3)} {Key("C4", input.Skill4)}",
            "",
            "--- Combat ---",
            $"Ennemis Skyrim proches : {ActorCount(view)}",
            $"Dégâts envoyés à Skyrim : {DamageCount(view)}",
            $"Coups reçus dans Skyrim : {HitCount(view)}",
            data.SurvivorDead == 1 ? "Survivant    : MORT (le joueur Skyrim meurt aussi)" : "",
            "",
            "--- Lancement ---",
            CommandLine(view),
            "",
            "--- HUD ---",
            HudLine(view),
        };

        Console.SetCursorPosition(0, 0);
        foreach (string line in lines)
            Console.WriteLine(line.PadRight(78));

        Thread.Sleep(100);
    }
}

// Les ordres de Skyrim (zone 6) : partie automatique et survivant choisi.
static string CommandLine(MemoryMappedViewAccessor view)
{
    if (view.ReadUInt32(BridgeInfo.CommandOffset) != BridgeInfo.CommandMagicValue) return "Ordres       : aucun";
    int autoStart = view.ReadInt32(BridgeInfo.CommandOffset + 12);
    uint request = view.ReadUInt32(BridgeInfo.CommandOffset + 8);
    var bytes = new byte[32];
    view.ReadArray(BridgeInfo.CommandOffset + 16, bytes, 0, 32);
    string survivor = System.Text.Encoding.ASCII.GetString(bytes).Split('\0')[0];
    return $"Partie auto  : {(autoStart == 1 ? "oui" : "non")}   survivant : {survivor}   chargements Skyrim : {request}";
}

// Le HUD écrit par RoR2 (zone 7) : niveau, or, nombre d'objets.
static string HudLine(MemoryMappedViewAccessor view)
{
    if (view.ReadUInt32(BridgeInfo.HudOffset) != BridgeInfo.HudMagicValue) return "HUD          : rien";
    int level = view.ReadInt32(BridgeInfo.HudOffset + 32);
    uint money = view.ReadUInt32(BridgeInfo.HudOffset + 40);
    int items = view.ReadInt32(BridgeInfo.HudOffset + 44);
    return level <= 0 ? "HUD          : pas de survivant" : $"HUD          : niveau {level}   or {money}   objets {items}";
}

// Nombre d'ennemis dans la liste écrite par Skyrim (zone 3).
static int ActorCount(MemoryMappedViewAccessor view)
{
    if (view.ReadUInt32(BridgeInfo.ActorOffset) != BridgeInfo.ActorMagicValue) return 0;
    return Math.Min(view.ReadInt32(BridgeInfo.ActorOffset + 8), BridgeInfo.MaxActors);
}

// Nombre total de dégâts écrits par RoR2 (zone 4).
static uint DamageCount(MemoryMappedViewAccessor view)
{
    if (view.ReadUInt32(BridgeInfo.DamageOffset) != BridgeInfo.DamageMagicValue) return 0;
    return view.ReadUInt32(BridgeInfo.DamageOffset + 4);
}

// Nombre total de coups reçus par le joueur, écrits par Skyrim (zone 5).
static uint HitCount(MemoryMappedViewAccessor view)
{
    if (view.ReadUInt32(BridgeInfo.HitOffset) != BridgeInfo.HitMagicValue) return 0;
    return view.ReadUInt32(BridgeInfo.HitOffset + 4);
}

// Affiche [Saut] si la touche est enfoncée, sinon des points.
static string Key(string name, int pressed) =>
    pressed != 0 ? $"[{name}]" : new string('.', name.Length + 2);
