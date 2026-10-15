#pragma once

#include "Bridge.h"

// Les objets RoR2 dans l'inventaire de Skyrim (0.61, voir [ObjetsSkyrim] dans SkyrimRoR2.ini).
//
//  - Chaque objet RoR2 est un objet divers de Skyrim (poids 0, valeur 0), rangé dans l'inventaire du joueur.
//    Seuls les objets PORTES comptent dans RoR2 : on peut jeter les autres, les ranger dans un coffre ou les
//    confier a un compagnon. Les objets du vide et lunaires, eux, ne quittent jamais le joueur (seule la
//    purification aux autels d'Arkay et de Stendarr les retire).
//  - Ce que RoR2 change tout seul (objet ramasse, objet du vide qui en mange un autre, objet consomme) est
//    recopie dans Skyrim, et ce que Skyrim change est envoye a RoR2 (inventaire.txt / inventaire_charge.txt).
//  - Mort du survivant : pas de rechargement. Le joueur se reveille dans le dernier lit ou il a dormi, sans les
//    objets RoR2 qu'il portait, et la difficulte repart a 1. Ce qui est range dans un coffre est garde.
//  - Changer de survivant : en dormant (fenetre de choix au reveil).
//  - Le broyeur (en quittant une fonderie), les bassins de purification (autels d'Arkay et de Stendarr), le bazar
//    lunaire (en quittant une conversation avec un chef de caravane khajiite) et les imprimantes (voir Altars).
namespace skyrimror2::Items
{
	enum Tier : int
	{
		kWhite = 0,
		kGreen = 1,
		kRed = 2,
		kYellow = 3,
		kVoid = 4,
		kLunar = 5,
		kOther = 6,
		kHallowed = 7,  // 0.71 : objets sanctifies (Hallowed Concepts)
		kMeal = 8,      // 0.71 : repas (CHEF errant)
		kTierCount = 9,
	};

	void Install();       // donnees du jeu chargees : formes, ecoute des evenements
	void LoadSettings();  // relit [ObjetsSkyrim]
	bool Enabled();       // objets dans l'inventaire de Skyrim
	bool WakeOnDeath();   // mort sans rechargement
	bool SleepToSwitch(); // changer de survivant en dormant

	// Chargement d'une partie : l'inventaire de Skyrim remplace celui de RoR2. a_savedText : les objets RoR2 ecrits
	// dans la sauvegarde par les anciennes versions (repris une seule fois dans l'inventaire de Skyrim).
	void OnGameLoaded(bool a_newGame, const std::string& a_savedText);

	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked);  // fil principal
	void FillCommand(bridge::SkyrimCommand& a_command);          // n'importe quel fil

	// Le survivant vient de mourir dans RoR2 (fil principal, une fois par mort).
	void OnSurvivorDeath(RE::PlayerCharacter* a_player);

	// Autel d'Arkay (a_void = true : objets du vide) ou de Stendarr (objets lunaires) : propose la purification.
	void OfferPurify(bool a_void, const std::string& a_shrineName);

	// Imprimante : prend une ferraille (sinon un objet porte) du rang, donne a_item. Vrai si imprime.
	bool PrintFor(const std::string& a_item, int a_tier);

	// Un objet trouvable au hasard de ce rang (vide si le catalogue n'est pas encore connu).
	std::string RandomFindable(int a_tier);
	std::string DisplayName(const std::string& a_item);  // "Seringue du soldat"
	const char* TierWord(int a_tier);                    // "blanc", "vert"...

	// 0.71 : le Bazar lunaire de Newt (porte bleue apres un coffre de boss). a_key : la porte (stock propre a elle).
	void OpenPortalBazaar(std::uint32_t a_key);

	void Save(SKSE::SerializationInterface* a_intfc);
	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t a_version);  // false : pas pour nous
	void Revert();
}
