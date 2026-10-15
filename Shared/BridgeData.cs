using System.Runtime.InteropServices;

namespace SkyrimRoR2.Shared
{
    // Ce fichier décrit le "pont" : une petite zone de mémoire que RoR2 et Skyrim
    // lisent et écrivent tous les deux, comme un tableau blanc partagé.
    // Il est utilisé par le mod RoR2 et par la visionneuse.
    // Le plugin Skyrim (C++) en a une copie identique : Shared/bridge_protocol.h

    public static class BridgeInfo
    {
        // Nom du tableau blanc dans Windows. Les deux côtés doivent utiliser le même.
        public const string MapName = "Local\\SkyrimRoR2_Bridge";

        // Taille réservée (4 Ko, largement assez pour l'instant).
        public const int MapSize = 8192;

        // Version du format. On l'augmente à chaque changement de structure.
        public const uint VersionValue = 18;  // 18 : 0.73.4 (128 pièces par modèle) ; 16 : 0.70 (coop : lieu, degats distants, pont coop) ; 17 : 0.73 (liste des alliés, 4096 sprites)

        // Zone 1, au début du pont : écrite par RoR2, lue par Skyrim.
        public const uint MagicValue = 0x42325253;      // "SR2B"

        // Zone 2, à 512 octets du début : écrite par Skyrim, lue par RoR2.
        public const int InputOffset = 512;
        public const uint InputMagicValue = 0x4E494B53; // "SKIN"

        // Zone 3, à 1024 octets : les ennemis proches, écrits par Skyrim, lus par RoR2.
        public const int ActorOffset = 1024;
        public const uint ActorMagicValue = 0x52544341;  // "ACTR"
        public const int MaxActors = 32;
        public const int ActorHeaderSize = 16;           // magic, sequence, count, reserved
        public const int ActorEntrySize = 44;
        public const uint ActorDead = 1;                 // drapeau : vient de mourir dans Skyrim
        public const uint ActorPassive = 2;              // drapeau : pas hostile (animal, passant) : les alliés l'ignorent
        public const int ActorEliteShift = 4;            // élite : (Flags >> 4) & 3 : 0 aucun, 1 feu, 2 glace, 3 foudre
        public const uint ActorEliteMask = 3;

        // Zone 4, à 2560 octets : les dégâts, écrits par RoR2, lus par Skyrim (file circulaire).
        public const int DamageOffset = 2560;
        public const uint DamageMagicValue = 0x474D4144; // "DAMG"
        public const int DamageSlots = 64;
        public const int DamageHeaderSize = 16;          // magic, writeCount, reserved x2
        public const int DamageEventSize = 16;
        public const uint DamageCrit = 1;                // drapeau : coup critique
        public const uint DamageRemote = 2;              // 0.70 (coop) : coup d'un coéquipier (ni expérience, ni atouts)

        // Zone 5, à 3600 octets : les coups reçus par le joueur, écrits par Skyrim, lus par RoR2.
        public const int HitOffset = 3600;
        public const uint HitMagicValue = 0x53544948;    // "HITS"
        public const int HitSlots = 16;
        public const int HitHeaderSize = 16;             // magic, writeCount, reserved x2
        public const int HitEventSize = 16;
        public const uint HitFraction = 1;               // drapeau : Amount = part des PV max (0.1 = 10 %)

        // Zone 6, à 3872 octets : les ordres de Skyrim à RoR2 (lancer une partie, quel survivant...).
        public const int CommandOffset = 3872;
        public const uint CommandMagicValue = 0x444D4F43; // "COMD"

        // Zone 7, à 4096 octets : le HUD de RoR2, écrit par RoR2, lu par Skyrim.
        public const int HudOffset = 4096;
        public const uint HudMagicValue = 0x44554853;     // "SHUD"
        public const int HudSlots = 5;                    // 4 compétences + l'équipement
        public const int HudMaxItems = 64;
        public const int IconNameSize = 48;
        public const int HudHeaderSize = 48;
        public const int HudSlotSize = 64;
        public const int HudItemSize = 52;
        public const int HudPortraitOffset = 48 + 5 * 64 + 64 * 52;  // dans la zone 7 : icône du survivant
        public const int HudNameOffset = HudPortraitOffset + 48;     // dans la zone 7 : nom du survivant
        public const int HudSpecialOffset = HudNameOffset + 48;      // dans la zone 7 : jauge propre au survivant (76 octets)

        // Zone 8 : les objets RoR2 gagnés dans Skyrim (écrits par Skyrim). Rangs : 0 blanc, 1 vert, 2 rouge,
        // 3 jaune, 4 vide, 5 lunaire, 6 équipement.
        public const int LootOffset = 7968;
        public const uint LootMagic = 0x544F4F4C;  // "LOOT"
        public const int LootSlots = 8;
        public const int LootHeaderSize = 16;       // magic, writeCount, réservé x2
        public const int LootEventSize = 8;         // rang, nombre

        // Zone 9 : le dernier objet donné (écrit par RoR2) : magic, séquence, rang, réservé, nom (64 octets UTF-8).
        public const int PickupOffset = 8048;
        public const uint PickupMagic = 0x4B434950;  // "PICK"
        public const int PickupNameSize = 64;

        // Troisième pont : le relief de Skyrim autour du joueur (voir TerrainMirror.cs).
        // En-tête de 64 octets : magic, sequence (impair pendant l'écriture), side, spacing,
        // originX, originY, playerX, playerY, playerZ, playerSequence, tileCells ; puis side x side hauteurs (float).
        // Coordonnées du monde de Skyrim, en mètres : X = est, Y = nord, Z = hauteur.
        public const string ReliefMapName = "Local\\SkyrimRoR2_Relief";
        public const uint ReliefMagic = 0x464C4552;  // "RELF"
        public const int ReliefMaxSide = 129;
        public const int ReliefHeaderSize = 64;
        public const int ReliefMapSize = ReliefHeaderSize + ReliefMaxSide * ReliefMaxSide * 4;

        // Cinquième pont : les alliés à afficher dans Skyrim avec leur vrai modèle (voir AllyMirror.cs).
        public const string AllyMapName = "Local\\SkyrimRoR2_Allies";
        public const uint AllyMagic = 0x594C4C41;  // "ALLY"
        public const int AllyMax = 128;
        public const int AllyHeaderSize = 16;
        public const int AllyEntrySize = 112;
        public const int AllyBones = 128;  // 0.73.4 : 64 -> 128 (tourelle mobile)
        public const int AllyPoseSize = AllyBones * 48;
        public const int AllyPoseOffset = AllyHeaderSize + AllyMax * AllyEntrySize;
        public const int AllyMapSize = AllyPoseOffset + AllyMax * AllyPoseSize;

        // Neuvième pont (0.70) : la coop (voir Coop.cs). RoR2 écrit de 0 à 1023, Skyrim de 1024 à 2047.
        public const string CoopMapName = "Local\\SkyrimRoR2_Coop";
        public const int CoopMapSize = 2048;
        public const uint CoopMagic = 0x504F4F43;     // "COOP"
        public const uint CoopSkyMagic = 0x50434B53;  // "SKCP"
        public const int CoopRing = 64;
        public const int CoopTextSize = 256;
        // Partie RoR2 (décalages en octets)
        public const int CoopActive = 8, CoopPlayers = 12, CoopDifficulty = 16, CoopKeepDifficulty = 20, CoopDowned = 24,
            CoopRevive = 28, CoopReviveLabel = 32, CoopTextSequence = 96, CoopText = 100, CoopChargeChest = 356,
            CoopChargeProgress = 360, CoopOpenedWrite = 364, CoopOpened = 368, CoopFinishedWrite = 624, CoopFinished = 628;
        // Partie Skyrim (à partir de CoopSkyrimOffset)
        public const int CoopSkyrimOffset = 1024;
        public const int CoopSkyOpenedWrite = 8, CoopSkyOpened = 12, CoopSkyFinishedWrite = 268, CoopSkyFinished = 272,
            CoopSkyChargeChest = 336, CoopSkyChargeProgress = 340;
        // Les coéquipiers filmés (même format que SurvivorViewMapName).
        public static readonly string[] TeammateViewMapNames =
            { "Local\\SkyrimRoR2_Coequipier1", "Local\\SkyrimRoR2_Coequipier2", "Local\\SkyrimRoR2_Coequipier3" };

        // Huitième pont (0.66) : les objets de RoR2 à rendre solides dans Skyrim (cube de détritus...), voir SolidMirror.cs.
        public const string SolidMapName = "Local\\SkyrimRoR2_Solids";
        public const uint SolidMagic = 0x444C4F53;  // "SOLD"
        public const int SolidMax = 16;
        public const int SolidHeaderSize = 16;
        public const int SolidEntrySize = 32;
        public const int SolidMapSize = SolidHeaderSize + SolidMax * SolidEntrySize;

        // Neuvième pont (0.73) : la liste des alliés à gauche du HUD (voir HudPublisher.WriteAllies).
        public const string AllyHudMapName = "Local\\SkyrimRoR2_AllyHud";
        public const uint AllyHudMagic = 0x44484C41;  // "ALHD"
        public const int AllyHudMax = 12;
        public const int AllyHudHeaderSize = 16;
        public const int AllyHudEntrySize = 64;
        public const int AllyHudMapSize = AllyHudHeaderSize + AllyHudMax * AllyHudEntrySize;

        // Septième pont : les "sprites" (particules, traînées, rayons de RoR2) à dessiner dans Skyrim (SpriteMirror.cs).
        //   en-tête (16 octets) : magic, numéro de séquence, nombre de sprites, nombre de noms ;
        //   puis SpriteNames noms de 32 octets (clé de la texture d'effet, ex. "S_1a2b3c4d") ;
        //   puis SpriteMax entrées de 48 octets : numéro du nom (int), forme (octet : 0 face à la caméra,
        //   1 étiré le long d'un axe, 2 à plat, 3 debout), niveau d'opacité (octet 0 à 3), porté par le survivant (octet), 1 libre,
        //   centre (monde Skyrim, m : 3 floats), largeur, hauteur (m), rotation (radians), axe (3 floats, Skyrim), libre.
        public const string SpriteMapName = "Local\\SkyrimRoR2_Sprites";
        public const uint SpriteMagic = 0x54525053;  // "SPRT"
        public const int SpriteNames = 4096;  // 0.73 : 1024 -> 4096
        public const int SpriteNameSize = 32;
        public const int SpriteMax = 3072;
        public const int SpriteEntrySize = 48;
        public const int SpriteNamesOffset = 16;
        public const int SpriteEntriesOffset = SpriteNamesOffset + SpriteNames * SpriteNameSize;
        public const int SpriteMapSize = SpriteEntriesOffset + SpriteMax * SpriteEntrySize;

        // Sixième pont : les effets à rejouer avec des effets de Skyrim (voir EffectMirror.cs).
        public const string EffectMapName = "Local\\SkyrimRoR2_Effets";
        public const uint EffectMagic = 0x58464645;  // "EFFX"
        public const int EffectRing = 128;
        public const int EffectMapSize = 16 + EffectRing * 24;

        // Septième pont : les ennemis Skyrim déplacés par RoR2 (voir GhostManager.cs).
        public const string PushMapName = "Local\\SkyrimRoR2_Pousse";
        public const uint PushMagic = 0x48535550;  // "PUSH"
        public const int PushMax = 32;
        public const int PushMapSize = 16 + PushMax * 16;

        // Quatrième pont : le décor de Skyrim (vraies formes de collision), voir DecorMirror.cs.
        public const string DecorMapName = "Local\\SkyrimRoR2_Decor";
        public const uint DecorMagic = 0x524F4344;  // "DCOR"
        public const int DecorHeaderSize = 64;
        public const int DecorMaxObjects = 8192;
        public const int DecorEntrySize = 16;
        public const int DecorTrianglesOffset = DecorHeaderSize + DecorMaxObjects * DecorEntrySize;
        public const int DecorMapSize = 48 * 1024 * 1024;

        // Second pont : l'image des mains du survivant (voir ViewmodelStreamer.cs).
        public const string ViewMapName = "Local\\SkyrimRoR2_Mains";
        public const string WorldViewMapName = "Local\\SkyrimRoR2_Monde";  // même format : vue du monde depuis la caméra de Skyrim
        public const uint ViewMagic = 0x444F4D56;  // "VMOD"
        public const int ViewMaxWidth = 1280;
        public const int ViewMaxHeight = 720;
        public const int ViewHeaderSize = 64;
        public const int ViewFrameSize = ViewMaxWidth * ViewMaxHeight * 4;
        public const int ViewMapSize = ViewHeaderSize + 2 * ViewFrameSize;
        // 0.65 : le survivant filmé pour la vue à la 3e personne (seulement le rectangle où il se trouve).
        public const string SurvivorViewMapName = "Local\\SkyrimRoR2_Survivant";
        public const int SurvivorMaxWidth = 1920;
        public const int SurvivorMaxHeight = 1080;
        public const int SurvivorFrameSize = SurvivorMaxWidth * SurvivorMaxHeight * 4;
        public const int SurvivorMapSize = ViewHeaderSize + 2 * SurvivorFrameSize;
    }

    // Zone 6 : les ordres de Skyrim.
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
    public struct SkyrimCommand
    {
        public uint Magic;        // toujours CommandMagicValue
        public uint Sequence;     // augmente à chaque envoi : preuve que Skyrim tourne
        public uint RequestId;    // augmente à chaque chargement de partie Skyrim
        public int AutoStart;     // 1 : RoR2 doit lancer une partie tout seul

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Survivor;   // survivant voulu ("Commando", "Huntress"...)

        public uint NextSurvivor;      // augmente à chaque appui sur "survivant suivant"
        public uint PreviousSurvivor;  // augmente à chaque appui sur "survivant précédent"

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string SelectSurvivor;  // survivant choisi à un autel de Skyrim
        public uint SelectSequence;    // augmente à chaque nouveau choix

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string VariantSurvivor; // survivant concerné par les variantes ci-dessous

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public int[] Variants;         // variante de chaque compétence (0 = la première, -1 = ne pas toucher)
        public uint VariantSequence;   // augmente à chaque changement de variante
        public int Skin;               // skin du survivant (0 = celui de base, -1 = ne pas toucher)
        public float BonusHealth;      // PV en plus pour le survivant (vie gagnée dans Skyrim au-delà du départ)
        public float GroundGap;        // distance pieds -> sol du joueur Skyrim, en mètres (-1 : inconnue)
        public float VerticalSpeed;    // vitesse verticale du joueur Skyrim, en m/s (+ = vers le haut)
        public float CeilingHeight;    // plafond au-dessus des pieds du joueur Skyrim, en mètres (-1 : aucun)
        public float CameraX;          // caméra de Skyrim (monde de Skyrim, mètres)
        public float CameraY;
        public float CameraZ;
        public float FrustumLeft;      // champ de vision : bords de l'image à 1 m devant la caméra (0 : inconnu)
        public float FrustumRight;
        public float FrustumTop;
        public float FrustumBottom;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public int[] ExtraVariants;    // variante des cases en plus (balises du Capitaine, drones de l'Opérateur...)
        public float Difficulty;       // difficulté (0 au départ ; +1 par donjon terminé, un peu chaque jour)
        public uint InventorySequence; // augmente à chaque demande de Skyrim : objets de inventaire_charge.txt
        public uint ReviveSequence;    // augmente quand le joueur se réveille dans Skyrim après la mort du survivant
        public int SkyrimLevel;        // 0.66 : niveau du personnage Skyrim (0 : inconnu) ; le niveau RoR2 le suit
    }

    // Zone 3 : un ennemi Skyrim proche.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ActorEntry
    {
        public uint FormId;       // identifiant Skyrim de l'acteur
        public uint Flags;        // ActorDead...

        public float RelX;        // position par rapport au joueur, en mètres,
        public float RelY;        // déjà dans les axes de RoR2 (X est, Y hauteur, Z nord)
        public float RelZ;

        public float Height;      // taille de l'acteur, en mètres
        public float Health;      // PV actuels dans Skyrim
        public float MaxHealth;   // PV maximum dans Skyrim

        public float Width;       // boîte de collision de Skyrim (m) : de gauche à droite
        public float Length;      // de l'arrière à l'avant
        public float Yaw;         // orientation (radians, 0 = nord, sens des aiguilles d'une montre)
    }

    // Zone 5 : un coup reçu par le joueur dans Skyrim.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct HitEvent
    {
        public uint AttackerFormId; // qui a frappé (0 si inconnu : chute, piège...)
        public float Amount;        // PV perdus (ou part des PV max si HitFraction)
        public uint Flags;          // HitFraction...
        public uint Reserved;     // octet 0 : case de l'attaque + 1 (0 inconnue) ; octet 1 : sa variante
    }

    // Zone 4 : un dégât infligé à un ennemi Skyrim.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DamageEvent
    {
        public uint FormId;       // acteur Skyrim touché
        public float Amount;      // dégâts RoR2
        public uint Flags;        // DamageCrit...
        public uint Reserved;
    }

    // Zone 1 : ce que RoR2 écrit (l'état du survivant).
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
    public struct BridgeData
    {
        public uint Magic;      // toujours MagicValue
        public uint Version;    // toujours VersionValue
        public uint Sequence;   // augmente de 1 à chaque image : preuve que RoR2 tourne

        public int InRun;       // 1 si un survivant est en jeu, 0 sinon (menu, chargement)

        public float PosX;      // position du survivant
        public float PosY;      // (Y = la hauteur dans Unity)
        public float PosZ;

        public float Health;    // PV actuels
        public float MaxHealth; // PV maximum

        public float VelX;      // vitesse du survivant, en mètres par seconde
        public float VelY;      // (X = est, Y = hauteur, Z = nord)
        public float VelZ;

        public int SurvivorDead; // 1 : le survivant est mort pour de bon dans RoR2

        public int SurvivorAirborne; // 1 : le survivant ne touche pas le sol dans RoR2

        public float BlinkTotalX; // total cumulé des petites téléportations (blink...), en mètres
        public float BlinkTotalZ; // Skyrim applique la différence depuis sa dernière lecture
        public int Scope;         // lunette du Railgunner : 0 aucune, 1 visée légère, 2 visée lourde

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Survivor;   // survivant joué ("Loader"...), pour l'expérience dans Skyrim

        public int Cloaked;       // 1 : le survivant est invisible dans RoR2 (fumigène du Bandit...)
    }

    // Zone 2 : ce que Skyrim écrit (les commandes du joueur).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct SkyrimInput
    {
        public uint Magic;        // toujours InputMagicValue
        public uint Sequence;     // augmente à chaque envoi : preuve que Skyrim tourne

        public int Active;        // 1 si Skyrim est au premier plan, en jeu, hors menu

        public float MoveForward; // +1 avancer (Z), -1 reculer (S)
        public float MoveRight;   // +1 droite (D), -1 gauche (Q/A)

        public float Yaw;         // direction de la caméra Skyrim (radians, 0 = nord)
        public float Pitch;       // inclinaison (radians, positif = regarde vers le bas)

        public int Jump;          // Espace
        public int Sprint;        // Ctrl gauche
        public int Skill1;        // clic gauche   : compétence principale
        public int Skill2;        // clic droit    : compétence secondaire
        public int Skill3;        // Maj gauche    : utilitaire
        public int Skill4;        // R             : spéciale
        public int Equipment;     // équipement RoR2 (touche du .ini, G par défaut)

        // 0.65 : la vue à la 3e personne filmée par RoR2 (voir SurvivorStreamer.cs).
        public int ThirdPerson;   // 1 : Skyrim est à la 3e personne, on filme le survivant
        public float CameraYaw;   // direction de la caméra de Skyrim (radians, 0 = nord)
        public float CameraPitch; // (positif = vers le bas)
        public float AmbientR, AmbientG, AmbientB;  // lumière ambiante de Skyrim (1 = blanc)
        public float SunR, SunG, SunB;              // lumière directionnelle (soleil, lune, cellule)
        public float SunX, SunY, SunZ;              // direction VERS cette lumière (Skyrim : x est, y nord, z haut)
        public int Interior;      // 1 : intérieur
        public uint PlaceId;      // 0.70 (coop) : le lieu du joueur (cellule si intérieur, monde si extérieur)
    }
}
