#pragma once

#include "bridge_protocol.h"

// Les objets RoR2 dans Skyrim (voir [Objets] dans SkyrimRoR2.ini).
//  - Ouvrir un contenant pour la premiere fois coute des septims (selon le contenant) ; sans assez de septims,
//    il se referme. Une fois paye, il peut donner un objet RoR2 au survivant (le coffre du boss : toujours).
//  - Les armes et armures sont retirees des contenants autour du joueur, avant qu'il ne les ouvre.
//  - Chaque objet donne par RoR2 est annonce dans le coin de l'ecran.
namespace skyrimror2::Loot
{
	// Relit [Objets] (au chargement d'une partie).
	void LoadSettings();

	// Ecoute l'ouverture des contenants (a appeler quand les donnees du jeu sont chargees).
	void Install();

	// A chaque image (fil principal) : retire les armes et armures, annonce les objets recus.
	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked);

	// Sauvegarde Skyrim : les contenants deja payes (appele par Shrines, qui gere le fichier du plugin).
	void Save(SKSE::SerializationInterface* a_intfc);
	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t a_version);  // false : pas pour nous
	void Revert();

	// ---- Pour les autels, le HUD et le pont ----
	int   ScaledPrice(int a_base);                            // prix qui monte avec la difficulte
	void  RequestLoot(std::int32_t a_tier, std::int32_t a_count);  // demande un objet a RoR2 (rangs : voir le pont, zone 8)
	float Difficulty();
	int   Coins();                                            // pieces lunaires
	int   Septims();                                          // septims du joueur (pour le HUD)
	bool  SpendCoins(int a_count);
	float ChargeProgress();                                   // charge du coffre du boss (0 a 1), -1 : aucune
	int   DungeonsDone();
	void  ResetDifficulty();                                  // mort du survivant : la difficulte repart a 1
	void  AddMountain();                                      // autel de la Montagne : plus d'objets et d'ennemis a la prochaine charge
	bool  PayGold(int a_price);                               // fil principal
	void  GiveGold(int a_amount);                             // fil principal
	bool  IsDungeon(RE::PlayerCharacter* a_player);           // donjon, grotte, ruine, fort...
	bool  InDaedricPlace(RE::PlayerCharacter* a_player);      // sanctuaire daedrique
	void  FillCommand(bridge::SkyrimCommand& a_command);      // la difficulte et les objets a reprendre, pour RoR2
	std::string HoverText();                                  // prix du contenant sous le viseur (vide : rien)
	void  OnGameLoaded(bool a_newGame);
	void  OnRemoteOpened(RE::FormID a_container);              // 0.70 : contenant ouvert par un coequipier (fil principal)
	void  OnRemoteFinished(RE::FormID a_chest);                // 0.70 : coffre du boss termine par un coequipier
	void  HoldUntilReady();                                   // fil principal, chaque image : fige le monde tant que RoR2 n'est pas pret                       // apres le chargement : RoR2 reprend les objets de la sauvegarde
}
