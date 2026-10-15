#pragma once

// Description du pont pour le cote Skyrim (C++).
// C'est la copie exacte de Shared/BridgeData.cs (cote RoR2, C#) :
// si l'un change, l'autre doit changer pareil, dans le meme ordre.

#include <cstdint>

namespace bridge
{
	inline constexpr const wchar_t* kMapName = L"Local\\SkyrimRoR2_Bridge";
	inline constexpr std::uint32_t  kMapSize = 8192;
	inline constexpr std::uint32_t  kVersion = 18;  // 18 : 0.73.4 (128 pieces par modele) ; 16 : 0.70 (coop : lieu, degats distants, pont coop) ; 17 : 0.73 (liste des allies, 4096 sprites)

	// Zone 1, au debut du pont : ecrite par RoR2, lue par Skyrim.
	inline constexpr std::uint32_t kMagic = 0x42325253;  // "SR2B"

	// Zone 2, a 512 octets du debut : ecrite par Skyrim, lue par RoR2.
	inline constexpr std::uint32_t kInputOffset = 512;
	inline constexpr std::uint32_t kInputMagic = 0x4E494B53;  // "SKIN"

#pragma pack(push, 4)
	// Zone 1 : l'etat du survivant.
	struct BridgeData
	{
		std::uint32_t magic;     // toujours kMagic
		std::uint32_t version;   // toujours kVersion
		std::uint32_t sequence;  // augmente a chaque image de RoR2

		std::int32_t inRun;  // 1 si un survivant est en jeu

		float posX;  // position du survivant (Y = hauteur dans Unity)
		float posY;
		float posZ;

		float health;     // PV actuels
		float maxHealth;  // PV maximum

		float velX;  // vitesse du survivant en m/s (X = est, Y = hauteur, Z = nord)
		float velY;
		float velZ;

		std::int32_t survivorDead;  // 1 : le survivant est mort pour de bon dans RoR2

		std::int32_t survivorAirborne;  // 1 : le survivant ne touche pas le sol dans RoR2

		// Total cumule des petites teleportations (blink de la Huntress...), en metres.
		// Skyrim applique la difference depuis sa derniere lecture.
		float blinkTotalX;  // est
		float blinkTotalZ;  // nord

		std::int32_t scope;  // lunette du Railgunner : 0 aucune, 1 visee legere, 2 visee lourde

		char survivor[32];  // survivant joue dans RoR2 ("Loader"...), pour l'experience

		std::int32_t cloaked;  // 1 : le survivant est invisible dans RoR2 (fumigene du Bandit...)
	};

	// Zone 2 : les commandes du joueur dans Skyrim.
	struct SkyrimInput
	{
		std::uint32_t magic;     // toujours kInputMagic
		std::uint32_t sequence;  // augmente a chaque envoi

		std::int32_t active;  // 1 si Skyrim est au premier plan, en jeu, hors menu

		float moveForward;  // +1 avancer, -1 reculer
		float moveRight;    // +1 droite, -1 gauche

		float yaw;    // direction de la camera (radians, 0 = nord)
		float pitch;  // inclinaison (radians, positif = vers le bas)

		std::int32_t jump;    // Espace
		std::int32_t sprint;  // Ctrl gauche
		std::int32_t skill1;  // clic gauche
		std::int32_t skill2;  // clic droit
		std::int32_t skill3;  // Maj gauche
		std::int32_t skill4;  // R
		std::int32_t equipment;  // equipement RoR2 (touche du .ini, G par defaut)

		// 0.65 : la vue a la 3e personne filmee par RoR2 (voir SkyrimRoR2/SurvivorStreamer.cs).
		std::int32_t thirdPerson;  // 1 : Skyrim est a la 3e personne, RoR2 doit filmer le survivant
		float        cameraYaw;    // direction de la camera de Skyrim (radians, 0 = nord), a la 3e personne
		float        cameraPitch;  // (positif = vers le bas)
		float        ambient[3];   // lumiere ambiante de Skyrim autour du joueur (rouge, vert, bleu ; 1 = blanc)
		float        sun[3];       // lumiere directionnelle (soleil, lune, lumiere de la cellule)
		float        sunDir[3];    // direction VERS cette lumiere (monde Skyrim : x est, y nord, z haut)
		std::int32_t interior;     // 1 : interieur
		std::uint32_t placeId;     // 0.70 (coop) : le lieu du joueur (cellule si interieur, monde si exterieur)
	};
	// Zone 3, a 1024 octets : les ennemis proches, ecrits par Skyrim, lus par RoR2.
	inline constexpr std::uint32_t kActorOffset = 1024;
	inline constexpr std::uint32_t kActorMagic = 0x52544341;  // "ACTR"
	inline constexpr std::uint32_t kMaxActors = 32;
	inline constexpr std::uint32_t kActorDead = 1;     // drapeau : vient de mourir dans Skyrim
	inline constexpr std::uint32_t kActorPassive = 2;  // drapeau : pas hostile (animal, passant) : les allies RoR2 l'ignorent
	// Elite : bits 4 et 5 des drapeaux ((flags >> 4) & 3) : 0 aucun, 1 ardent (feu), 2 glacial, 3 survolte (foudre)
	inline constexpr std::uint32_t kActorEliteShift = 4;
	inline constexpr std::uint32_t kActorEliteMask = 3;

	struct ActorEntry
	{
		std::uint32_t formId;  // identifiant Skyrim de l'acteur
		std::uint32_t flags;   // kActorDead...

		// Position par rapport au joueur, en metres, DEJA dans les axes de RoR2 :
		float relX;  // est
		float relY;  // hauteur
		float relZ;  // nord

		float height;     // taille de l'acteur, en metres
		float health;     // PV actuels dans Skyrim
		float maxHealth;  // PV maximum dans Skyrim

		// La boite de collision de Skyrim (zone qui encaisse les tirs dans RoR2), en metres.
		float width;   // de gauche a droite
		float length;  // de l'arriere a l'avant
		float yaw;     // orientation (radians, 0 = nord, sens des aiguilles d'une montre)
	};

	struct ActorTable
	{
		std::uint32_t magic;     // toujours kActorMagic
		std::uint32_t sequence;  // augmente a chaque mise a jour
		std::uint32_t count;     // nombre d'entrees valides dans actors
		std::uint32_t reserved;
		ActorEntry    actors[kMaxActors];
	};

	// Zone 4, a 2560 octets : les degats, ecrits par RoR2, lus par Skyrim.
	// C'est une "file circulaire" : RoR2 ajoute a la suite, Skyrim lit ce qu'il n'a pas encore vu.
	inline constexpr std::uint32_t kDamageOffset = 2560;
	inline constexpr std::uint32_t kDamageMagic = 0x474D4144;  // "DAMG"
	inline constexpr std::uint32_t kDamageSlots = 64;
	inline constexpr std::uint32_t kDamageCrit = 1;  // drapeau : coup critique
	inline constexpr std::uint32_t kDamageRemote = 2;  // 0.70 (coop) : coup d'un coequipier : ni experience, ni atouts du joueur

	struct DamageEvent
	{
		std::uint32_t formId;  // acteur Skyrim touche
		float         amount;  // degats RoR2
		std::uint32_t flags;   // kDamageCrit...
		std::uint32_t reserved;  // octet 0 : case de l'attaque + 1 (0 inconnue) ; octet 1 : sa variante ; octet 2 : competence d'objet (0.63)
	};

	struct DamageRing
	{
		std::uint32_t magic;       // toujours kDamageMagic
		std::uint32_t writeCount;  // nombre total de degats ecrits depuis le debut
		std::uint32_t reserved1;
		std::uint32_t reserved2;
		DamageEvent   events[kDamageSlots];  // l'evenement n est dans events[n % kDamageSlots]
	};
	// Zone 5, a 3600 octets : les coups recus par le joueur, ecrits par Skyrim, lus par RoR2.
	// Meme principe de file circulaire que la zone 4.
	inline constexpr std::uint32_t kHitOffset = 3600;
	inline constexpr std::uint32_t kHitMagic = 0x53544948;  // "HITS"
	inline constexpr std::uint32_t kHitSlots = 16;
	inline constexpr std::uint32_t kHitFraction = 1;  // drapeau : amount = part des PV max (0.1 = 10 %)

	struct HitEvent
	{
		std::uint32_t attackerFormId;  // qui a frappe (0 si inconnu : chute, piege...)
		float         amount;          // PV perdus (ou part des PV max si kHitFraction)
		std::uint32_t flags;           // kHitFraction...
		std::uint32_t reserved;
	};

	struct HitRing
	{
		std::uint32_t magic;       // toujours kHitMagic
		std::uint32_t writeCount;  // nombre total de coups ecrits depuis le debut
		std::uint32_t reserved1;
		std::uint32_t reserved2;
		HitEvent      events[kHitSlots];
	};

	// Zone 6, a 3872 octets : les ordres de Skyrim a RoR2 (lancer une partie, quel survivant...).
	inline constexpr std::uint32_t kCommandOffset = 3872;
	inline constexpr std::uint32_t kCommandMagic = 0x444D4F43;  // "COMD"

	struct SkyrimCommand
	{
		std::uint32_t magic;      // toujours kCommandMagic
		std::uint32_t sequence;   // augmente a chaque envoi : preuve que Skyrim tourne
		std::uint32_t requestId;  // augmente a chaque chargement de partie Skyrim
		std::int32_t  autoStart;  // 1 : RoR2 doit lancer une partie tout seul
		char          survivor[32];  // nom du survivant voulu ("Commando", "Huntress"...), termine par 0
		std::uint32_t nextSurvivor;      // augmente a chaque appui sur "survivant suivant"
		std::uint32_t previousSurvivor;  // augmente a chaque appui sur "survivant precedent"
		char          selectSurvivor[32];  // survivant choisi a un autel (ou F8/F7 parmi les debloques)
		std::uint32_t selectSequence;      // augmente a chaque nouveau choix
		char          variantSurvivor[32]; // survivant concerne par les variantes ci-dessous
		std::int32_t  variants[4];         // variante de chaque competence (0 = la premiere, -1 = ne pas toucher)
		std::uint32_t variantSequence;     // augmente a chaque changement de variante
		std::int32_t  skin;                // skin du survivant (0 = celui de base, -1 = ne pas toucher)
		float         bonusHealth;         // PV en plus pour le survivant (vie gagnee dans Skyrim au-dela du depart)
		float         groundGap;           // distance pieds -> sol du joueur Skyrim, en metres (-1 : inconnue)
		float         verticalSpeed;       // vitesse verticale du joueur Skyrim, en m/s (+ = vers le haut)
		float         ceilingHeight;       // hauteur du plafond au-dessus des pieds du joueur Skyrim, en metres (-1 : aucun)
		float         cameraX;             // camera de Skyrim (monde de Skyrim, metres)
		float         cameraY;
		float         cameraZ;
		float         frustumLeft;         // champ de vision de la camera (bords de l'image a 1 m devant elle)
		float         frustumRight;        // 0 : inconnu
		float         frustumTop;
		float         frustumBottom;
		std::int32_t  extraVariants[4];    // variante des cases en plus (balises du Capitaine, drones de l'Operateur...)
		float         difficulty;          // difficulte (0 au depart ; +1 par donjon termine, un peu chaque jour)
		std::uint32_t inventorySequence;   // augmente a chaque demande de Skyrim : RoR2 reprend les objets de
		                                   // %LOCALAPPDATA%\SkyrimRoR2\inventaire_charge.txt (objets portes dans Skyrim)
		std::uint32_t reviveSequence;      // augmente quand le joueur se reveille apres la mort du survivant : RoR2 le releve
		std::int32_t  skyrimLevel;         // 0.66 : niveau du personnage Skyrim (0 : inconnu) ; le niveau RoR2 le suit
	};

	// Zone 7, a 4096 octets : le HUD de RoR2 (vie, niveau, or, competences, objets), ecrit par RoR2.
	// Les icones sont des images PNG dans %LOCALAPPDATA%\SkyrimRoR2\icones\<nom>.png
	inline constexpr std::uint32_t kHudOffset = 4096;
	inline constexpr std::uint32_t kHudMagic = 0x44554853;  // "SHUD"
	inline constexpr std::uint32_t kHudSlots = 5;           // 4 competences + l'equipement
	inline constexpr std::uint32_t kHudMaxItems = 64;
	inline constexpr std::uint32_t kIconNameSize = 48;

	struct HudSlot
	{
		char          icon[kIconNameSize];  // nom de l'icone (fichier PNG), vide = case vide
		float         cooldown;             // secondes restantes avant la prochaine charge
		float         cooldownTotal;        // duree totale d'une recharge
		std::int32_t  stock;                // charges disponibles
		std::int32_t  maxStock;             // charges maximum
	};

	struct HudItem
	{
		char          icon[kIconNameSize];  // nom de l'icone de l'objet
		std::int32_t  count;                // nombre d'exemplaires
	};

	// La jauge propre a certains survivants, dessinee sous le viseur.
	//   kind : 0 rien, 1 barre (corruption du Demon du vide), 2 recharge active (barre + fenetre a viser,
	//   Railgunner), 3 charges (tranquillite de la Chercheuse), 4 progression d'une competence (Meditation),
	//   5 (0.73) suite de directions de la Meditation : label = lettres H/B/G/D, charges = etape en cours.
	struct HudSpecial
	{
		std::int32_t  kind;
		float         value;        // 0 a 1 (barre, progression, curseur de la recharge)
		float         windowStart;  // recharge active : debut et fin de la fenetre (0 a 1)
		float         windowEnd;
		std::int32_t  charges;      // charges actuelles
		std::int32_t  maxCharges;
		std::uint32_t color;        // couleur de la barre (0xRRGGBBAA)
		char          label[kIconNameSize];  // texte (UTF-8)
	};

	struct HudData
	{
		std::uint32_t magic;     // toujours kHudMagic
		std::uint32_t sequence;  // augmente a chaque mise a jour

		float health;       // PV
		float shield;       // bouclier
		float barrier;      // barriere (jaune)
		float fullHealth;   // PV max
		float fullShield;   // bouclier max
		float fullBarrier;  // barriere max

		std::int32_t  level;       // niveau
		float         xpFraction;  // progression vers le niveau suivant (0 a 1) ; < 0 : barre cachee (niveau suivi sur Skyrim)
		std::uint32_t money;       // or
		std::int32_t  itemCount;   // nombre d'entrees valides dans items

		HudSlot slots[kHudSlots];  // 0-3 : competences (principale, secondaire, utilitaire, speciale), 4 : equipement
		HudItem items[kHudMaxItems];

		char portrait[kIconNameSize];  // icone du survivant (son portrait de l'ecran de selection)
		char name[kIconNameSize];      // nom du survivant dans la langue du jeu (UTF-8)

		HudSpecial special;  // la jauge propre au survivant (recharge du Railgunner, corruption du Demon du vide...)
	};
	// Zone 8, a 7968 octets : les objets RoR2 gagnes dans Skyrim (coffres...), ecrits par Skyrim, lus par RoR2.
	// File circulaire comme la zone 4. RoR2 tire un objet au hasard du rang demande et le donne au survivant.
	inline constexpr std::uint32_t kLootOffset = 7968;
	inline constexpr std::uint32_t kLootMagic = 0x544F4F4C;  // "LOOT"
	inline constexpr std::uint32_t kLootSlots = 8;
	// Rangs : 0 blanc, 1 vert, 2 rouge, 3 jaune (boss), 4 vide, 5 lunaire, 6 equipement,
	//   7 autel de l'Ordre (melange l'inventaire), 8 autel du Sang (le survivant perd la moitie de ses PV, sans mourir)
	struct LootEvent
	{
		std::int32_t tier;
		std::int32_t count;
	};
	struct LootRing
	{
		std::uint32_t magic;       // toujours kLootMagic
		std::uint32_t writeCount;  // nombre total d'objets demandes depuis le debut
		std::uint32_t reserved1;
		std::uint32_t reserved2;
		LootEvent     events[kLootSlots];
	};

	// Zone 9, a 8048 octets : le dernier objet donne par RoR2 (pour l'annoncer dans Skyrim), ecrit par RoR2.
	inline constexpr std::uint32_t kPickupOffset = 8048;
	inline constexpr std::uint32_t kPickupMagic = 0x4B434950;  // "PICK"
	struct PickupNote
	{
		std::uint32_t magic;     // toujours kPickupMagic
		std::uint32_t sequence;  // augmente a chaque objet donne
		std::int32_t  tier;      // rang de l'objet
		std::int32_t  reserved;
		char          name[64];  // nom de l'objet dans la langue du jeu (UTF-8)
	};

	// Un troisieme pont : le relief de Skyrim autour du joueur, ecrit par Skyrim, lu par RoR2.
	// Une grille carree de hauteurs de sol (coordonnees du monde de Skyrim, en metres :
	// X = est, Y = nord, Z = hauteur). RoR2 en fait une surface invisible sous le survivant.
	// Un quatrieme pont : le decor de Skyrim (vraies formes de collision : sol, maisons, arches, rochers...),
	// ecrit par Skyrim, lu par RoR2 (voir Decor.cpp et DecorMirror.cs). Coordonnees du monde de Skyrim, en metres.
	//   en-tete (64 octets) : magic, sequence (impair pendant l'ecriture), objectCount, triangleCount, heartbeat
	//   puis kDecorMaxObjects entrees de 16 octets : id (64 bits), premier triangle, nombre de triangles
	//   puis les triangles : 9 floats chacun (3 sommets x, y, z)
	inline constexpr const wchar_t* kDecorMapName = L"Local\\SkyrimRoR2_Decor";
	inline constexpr std::uint32_t  kDecorMagic = 0x524F4344;  // "DCOR"
	inline constexpr std::uint32_t  kDecorHeaderSize = 64;
	inline constexpr std::uint32_t  kDecorMaxObjects = 8192;
	inline constexpr std::uint32_t  kDecorEntrySize = 16;
	inline constexpr std::uint32_t  kDecorTrianglesOffset = kDecorHeaderSize + kDecorMaxObjects * kDecorEntrySize;
	inline constexpr std::uint32_t  kDecorMapSize = 48u * 1024u * 1024u;
	inline constexpr std::uint32_t  kDecorMaxTriangles = (kDecorMapSize - kDecorTrianglesOffset) / 36u;

	// Un cinquieme pont : les allies de RoR2 (drones, tourelles...) a afficher dans Skyrim avec leur vrai modele.
	//   en-tete (16 octets) : magic, sequence, count
	//   puis kAllyMax entrees de 112 octets : nom du modele (48), position (monde Skyrim, m : 3 floats),
	//   rotation (matrice 3x3, lignes, repere Skyrim), id, version du modele
	inline constexpr const wchar_t* kAllyMapName = L"Local\\SkyrimRoR2_Allies";
	inline constexpr std::uint32_t  kAllyMagic = 0x594C4C41;  // "ALLY"
	inline constexpr std::uint32_t  kAllyMax = 128;
	inline constexpr std::uint32_t  kAllyHeaderSize = 16;
	inline constexpr std::uint32_t  kAllyEntrySize = 112;
	// Puis, pour chaque allie, la pose de ses pieces animees (une par os) : kAllyBones x 48 octets
	// (position par rapport a la racine du modele, monde Skyrim en metres : 3 floats ; rotation 3x3 : 9 floats).
	// Le nombre de pieces est a l'octet 104 de l'entree.
	inline constexpr std::uint32_t  kAllyBones = 128;  // 0.73.4 : 64 -> 128 (tourelle mobile)
	inline constexpr std::uint32_t  kAllyPoseSize = kAllyBones * 48;
	inline constexpr std::uint32_t  kAllyPoseOffset = kAllyHeaderSize + kAllyMax * kAllyEntrySize;
	inline constexpr std::uint32_t  kAllyMapSize = kAllyPoseOffset + kAllyMax * kAllyPoseSize;

	// Neuvieme pont (0.70) : la coop. RoR2 ecrit la partie haute (0 a 1023), Skyrim la partie basse (1024 a 2047).
	inline constexpr const wchar_t* kCoopMapName = L"Local\\SkyrimRoR2_Coop";
	inline constexpr std::uint32_t  kCoopMapSize = 2048;
	inline constexpr std::uint32_t  kCoopMagic = 0x504F4F43;      // "COOP" (partie RoR2)
	inline constexpr std::uint32_t  kCoopSkyMagic = 0x50434B53;   // "SKCP" (partie Skyrim)
	inline constexpr std::uint32_t  kCoopRing = 64;               // contenants ouverts gardes dans chaque file
	inline constexpr std::uint32_t  kCoopTextSize = 256;

	struct CoopFromRoR2  // a 0
	{
		std::uint32_t magic;           // kCoopMagic
		std::uint32_t sequence;        // augmente a chaque image de RoR2
		std::int32_t  active;          // 1 : partie coop en cours (au moins un coequipier)
		std::int32_t  players;         // joueurs dans la partie
		float         difficulty;      // difficulte commune (celle de l'hote) ; < 0 : pas de difficulte imposee
		std::int32_t  keepDifficulty;  // 1 : a la mort, la difficulte ne repart pas a 1 (des coequipiers sont encore debout)
		std::int32_t  downed;          // 1 : le survivant est a terre, un coequipier peut le relever
		float         revive;          // relevement en cours (0 a 1), < 0 : aucun
		char          reviveLabel[64]; // "Bob te releve", "Tu releves Bob"...
		std::uint32_t textSequence;    // augmente a chaque nouveau message
		char          text[kCoopTextSize];  // message a afficher (UTF-8)
		std::uint32_t chargeChest;     // charge du coffre du boss d'un coequipier, dans ce lieu (0 : aucune)
		float         chargeProgress;  // sa progression (0 a 1)
		std::uint32_t openedWrite;     // contenants ouverts par les coequipiers (file circulaire)
		std::uint32_t opened[kCoopRing];
		std::uint32_t finishedWrite;   // coffres de boss termines par les coequipiers (file circulaire de 16)
		std::uint32_t finished[16];
	};

	struct CoopFromSkyrim  // a 1024
	{
		std::uint32_t magic;           // kCoopSkyMagic
		std::uint32_t sequence;
		std::uint32_t openedWrite;     // contenants ouverts (payes) par ce joueur
		std::uint32_t opened[kCoopRing];
		std::uint32_t finishedWrite;   // coffres de boss termines par ce joueur
		std::uint32_t finished[16];
		std::uint32_t chargeChest;     // sa charge en cours (0 : aucune)
		float         chargeProgress;
	};
	inline constexpr std::uint32_t kCoopSkyrimOffset = 1024;

	// Les coequipiers filmes par RoR2 (0.70) : meme format que kSurvivorViewMapName, un pont par coequipier.
	inline constexpr const wchar_t* kTeammateViewMapNames[3]{ L"Local\\SkyrimRoR2_Coequipier1", L"Local\\SkyrimRoR2_Coequipier2",
		L"Local\\SkyrimRoR2_Coequipier3" };

	// Huitieme pont (0.66) : les objets de RoR2 a rendre solides dans Skyrim (cube de detritus de la Vagabonde).
	//   en-tete (16 octets) : magic, sequence, nombre ; puis kSolidMax entrees de 32 octets : id (int),
	//   centre (monde Skyrim, m : 3 floats), demi-cotes (m : 3 floats), angle autour de la verticale (radians).
	inline constexpr const wchar_t* kSolidMapName = L"Local\\SkyrimRoR2_Solids";
	inline constexpr std::uint32_t  kSolidMagic = 0x444C4F53;  // "SOLD"
	inline constexpr std::uint32_t  kSolidMax = 16;
	inline constexpr std::uint32_t  kSolidHeaderSize = 16;
	inline constexpr std::uint32_t  kSolidEntrySize = 32;
	inline constexpr std::uint32_t  kSolidMapSize = kSolidHeaderSize + kSolidMax * kSolidEntrySize;

	// Neuvieme pont (0.73) : la liste des allies a gauche du HUD (drones, tourelles...), comme dans RoR2.
	//   en-tete (16 octets) : magic, sequence, nombre ; puis kAllyHudMax entrees de 64 octets : icone (48 octets,
	//   nom du fichier PNG), vie (0 a 1), bouclier (0 a 1), nombre d'allies de cette sorte (int), reserve.
	inline constexpr const wchar_t* kAllyHudMapName = L"Local\\SkyrimRoR2_AllyHud";
	inline constexpr std::uint32_t  kAllyHudMagic = 0x44484C41;  // "ALHD"
	inline constexpr std::uint32_t  kAllyHudMax = 12;
	inline constexpr std::uint32_t  kAllyHudHeaderSize = 16;
	inline constexpr std::uint32_t  kAllyHudEntrySize = 64;
	inline constexpr std::uint32_t  kAllyHudMapSize = kAllyHudHeaderSize + kAllyHudMax * kAllyHudEntrySize;

	// Septieme pont : les "sprites" (particules, trainees, rayons de RoR2), voir SpriteMirror.cs et Allies.cpp.
	//   en-tete (16 octets) : magic, sequence, nombre de sprites, nombre de noms ; puis kSpriteNames noms de 32 octets ;
	//   puis kSpriteMax entrees de 48 octets : numero du nom (int), forme (octet), niveau d'opacite (octet), 2 libres,
	//   centre (monde Skyrim, m : 3 floats), largeur, hauteur (m), rotation (radians), axe (3 floats), libre.
	inline constexpr const wchar_t* kSpriteMapName = L"Local\\SkyrimRoR2_Sprites";
	inline constexpr std::uint32_t  kSpriteMagic = 0x54525053;  // "SPRT"
	inline constexpr std::uint32_t  kSpriteNames = 4096;  // 0.73 : 1024 -> 4096 (tous les survivants dans une meme partie)
	inline constexpr std::uint32_t  kSpriteNameSize = 32;
	inline constexpr std::uint32_t  kSpriteMax = 3072;
	inline constexpr std::uint32_t  kSpriteEntrySize = 48;
	inline constexpr std::uint32_t  kSpriteNamesOffset = 16;
	inline constexpr std::uint32_t  kSpriteEntriesOffset = kSpriteNamesOffset + kSpriteNames * kSpriteNameSize;
	inline constexpr std::uint32_t  kSpriteMapSize = kSpriteEntriesOffset + kSpriteMax * kSpriteEntrySize;

	// Un sixieme pont : les effets de RoR2 (explosions, flammes, eclairs...) a rejouer avec des effets de Skyrim.
	//   en-tete (16 octets) : magic, nombre d'effets ecrits depuis le debut ; puis kEffectRing entrees de 24 octets :
	//   categorie, position (monde Skyrim, m : 3 floats), taille, numero
	inline constexpr const wchar_t* kEffectMapName = L"Local\\SkyrimRoR2_Effets";
	inline constexpr std::uint32_t  kEffectMagic = 0x58464645;  // "EFFX"
	inline constexpr std::uint32_t  kEffectRing = 128;
	inline constexpr std::uint32_t  kEffectMapSize = 16 + kEffectRing * 24;

	// Un septieme pont : les ennemis Skyrim que RoR2 deplace (tires par la Chargeuse, portes par un drone,
	// projetes par un coup...). Ecrit par RoR2 a chaque image.
	//   en-tete (16 octets) : magic, nombre d'entrees, numero d'image ; puis kPushMax entrees de 16 octets :
	//   formId de l'ennemi, position voulue par rapport aux pieds du joueur (est, nord, haut, en metres : 3 floats)
	inline constexpr const wchar_t* kPushMapName = L"Local\\SkyrimRoR2_Pousse";
	inline constexpr std::uint32_t  kPushMagic = 0x48535550;  // "PUSH"
	inline constexpr std::uint32_t  kPushMax = 32;
	inline constexpr std::uint32_t  kPushMapSize = 16 + kPushMax * 16;

	inline constexpr const wchar_t* kReliefMapName = L"Local\\SkyrimRoR2_Relief";
	inline constexpr std::uint32_t  kReliefMagic = 0x464C4552;  // "RELF"
	inline constexpr std::uint32_t  kReliefMaxSide = 129;       // 129 x 129 points au plus
	inline constexpr std::uint32_t  kReliefHeaderSize = 64;
	inline constexpr std::uint32_t  kReliefMapSize = kReliefHeaderSize + kReliefMaxSide * kReliefMaxSide * 4;

	struct ReliefHeader
	{
		std::uint32_t magic;     // toujours kReliefMagic
		std::uint32_t sequence;  // augmente a chaque nouvelle grille (impair pendant l'ecriture)
		std::uint32_t side;      // nombre de points par cote
		float         spacing;   // distance entre deux points (m)
		float         originX;   // position (monde Skyrim, m) du premier point : coin sud-ouest
		float         originY;
		float         playerX;   // position actuelle des pieds du joueur (monde Skyrim, m), a chaque image
		float         playerY;
		float         playerZ;
		std::uint32_t playerSequence;  // augmente a chaque mise a jour de la position du joueur
		std::uint32_t tileCells;       // cases par tuile : la grille est decoupee en tuiles fixes dans le monde
		std::uint32_t pad[5];
	};
	// Puis side x side hauteurs (float, m, monde Skyrim), ligne par ligne du sud au nord, d'ouest en est.

	// Un second pont, plus gros, pour l'image des mains du survivant filmees par RoR2.
	// En-tete puis deux images (RoR2 ecrit l'une pendant que Skyrim lit l'autre).
	inline constexpr const wchar_t* kViewMapName = L"Local\\SkyrimRoR2_Mains";
	// Meme format, pour ce que RoR2 voit depuis la camera de Skyrim (allies, tirs, effets) : toute la largeur de l'ecran.
	inline constexpr const wchar_t* kWorldViewMapName = L"Local\\SkyrimRoR2_Monde";
	inline constexpr std::uint32_t  kViewMagic = 0x444F4D56;  // "VMOD"
	inline constexpr std::uint32_t  kViewMaxWidth = 1280;
	inline constexpr std::uint32_t  kViewMaxHeight = 720;
	inline constexpr std::uint32_t  kViewHeaderSize = 64;
	inline constexpr std::uint32_t  kViewFrameSize = kViewMaxWidth * kViewMaxHeight * 4;
	inline constexpr std::uint32_t  kViewMapSize = kViewHeaderSize + 2 * kViewFrameSize;

	struct ViewHeader
	{
		std::uint32_t magic;     // toujours kViewMagic
		std::uint32_t width;     // largeur de l'image (pixels)
		std::uint32_t height;    // hauteur
		std::uint32_t sequence;  // augmente a chaque nouvelle image
		std::uint32_t ready;     // 0 ou 1 : laquelle des deux images est complete
		std::uint32_t active;    // 1 : RoR2 filme le survivant
		float         yaw;       // (image du monde) direction de la camera Skyrim au moment de la prise de vue
		float         pitch;
	};
	// 0.65 : le survivant filme par RoR2 depuis la camera de Skyrim (vue a la 3e personne). Meme principe, en plus
	// grand ; l'image ne couvre que le rectangle de l'ecran ou se trouve le survivant (rect : 0 a 1, haut = 0).
	inline constexpr const wchar_t* kSurvivorViewMapName = L"Local\\SkyrimRoR2_Survivant";
	inline constexpr std::uint32_t  kSurvivorMaxWidth = 1920;
	inline constexpr std::uint32_t  kSurvivorMaxHeight = 1080;
	inline constexpr std::uint32_t  kSurvivorFrameSize = kSurvivorMaxWidth * kSurvivorMaxHeight * 4;
	inline constexpr std::uint32_t  kSurvivorMapSize = kViewHeaderSize + 2 * kSurvivorFrameSize;

	struct SurvivorViewHeader
	{
		std::uint32_t magic;     // toujours kViewMagic
		std::uint32_t width;     // largeur de l'image (pixels)
		std::uint32_t height;    // hauteur
		std::uint32_t sequence;  // augmente a chaque nouvelle image
		std::uint32_t ready;     // 0 ou 1 : laquelle des deux images est complete
		std::uint32_t active;    // 1 : RoR2 filme le survivant
		float         rectLeft;  // place de l'image a l'ecran (0 a 1)
		float         rectTop;
		float         rectRight;
		float         rectBottom;
	};
#pragma pack(pop)

	// Verifications automatiques : memes tailles que cote C#.
	static_assert(sizeof(BridgeData) == 104, "BridgeData ne correspond plus au cote C#");
	static_assert(sizeof(SkyrimInput) == 112, "SkyrimInput ne correspond plus au cote C#");
	static_assert(sizeof(ActorEntry) == 44, "ActorEntry ne correspond plus au cote C#");
	static_assert(sizeof(DamageEvent) == 16, "DamageEvent ne correspond plus au cote C#");
	static_assert(sizeof(BridgeData) <= kInputOffset, "La zone 1 deborde sur la zone 2");
	static_assert(kInputOffset + sizeof(SkyrimInput) <= kActorOffset, "La zone 2 deborde sur la zone 3");
	static_assert(kActorOffset + sizeof(ActorTable) <= kDamageOffset, "La zone 3 deborde sur la zone 4");
	static_assert(sizeof(HitEvent) == 16, "HitEvent ne correspond plus au cote C#");
	static_assert(kDamageOffset + sizeof(DamageRing) <= kHitOffset, "La zone 4 deborde sur la zone 5");
	static_assert(sizeof(CoopFromRoR2) == 692, "CoopFromRoR2 ne correspond plus au cote C#");
	static_assert(sizeof(CoopFromRoR2) <= kCoopSkyrimOffset, "La partie RoR2 de la coop deborde");
	static_assert(sizeof(CoopFromSkyrim) == 344, "CoopFromSkyrim ne correspond plus au cote C#");
	static_assert(kCoopSkyrimOffset + sizeof(CoopFromSkyrim) <= kCoopMapSize, "La partie Skyrim de la coop deborde");
	static_assert(sizeof(SkyrimCommand) == 224, "SkyrimCommand ne correspond plus au cote C#");
	static_assert(kHitOffset + sizeof(HitRing) <= kCommandOffset, "La zone 5 deborde sur la zone 6");
	static_assert(kCommandOffset + sizeof(SkyrimCommand) <= kHudOffset, "La zone 6 deborde sur la zone 7");
	static_assert(sizeof(ReliefHeader) == kReliefHeaderSize, "ReliefHeader ne correspond plus au cote C#");
	static_assert(sizeof(HudSlot) == 64, "HudSlot ne correspond plus au cote C#");
	static_assert(sizeof(HudItem) == 52, "HudItem ne correspond plus au cote C#");
	static_assert(sizeof(HudSpecial) == 76, "HudSpecial ne correspond plus au cote C#");
	static_assert(sizeof(HudData) == 48 + 5 * 64 + 64 * 52 + 2 * 48 + 76, "HudData ne correspond plus au cote C#");
	static_assert(kHudOffset + sizeof(HudData) <= kLootOffset, "La zone 7 deborde sur la zone 8");
	static_assert(kLootOffset + sizeof(LootRing) <= kPickupOffset, "La zone 8 deborde sur la zone 9");
	static_assert(kPickupOffset + sizeof(PickupNote) <= kMapSize, "La zone 9 deborde du pont");
}
