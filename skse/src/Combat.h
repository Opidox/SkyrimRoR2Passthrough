#pragma once

#include "Bridge.h"

namespace skyrimror2::Combat
{
	// Branche l'ecoute des coups recus par le joueur (a appeler une fois, au chargement des donnees).
	void Install();

	// Appele a chaque image depuis le crochet du joueur (voir Movement.cpp) :
	//  - publie la liste des ennemis proches pour RoR2 (qui leur cree des doubles) ;
	//  - applique aux ennemis Skyrim les degats envoyes par RoR2 ;
	//  - envoie a RoR2 les PV perdus par le joueur, puis lui rend ses PV (c'est RoR2 qui decide).
	void PerFrame(RE::PlayerCharacter* a_player);

	// [PV] dans SkyrimRoR2.ini : vie partagee entre les deux jeux (relu a chaque chargement de partie).
	void LoadSettings();

	// 0.71 : l'elite d'un ennemi (0 aucune, 1 feu, 2 glace, 3 foudre). Fil principal.
	int EliteType(RE::FormID a_actor);

	// Remplit les PV bonus du survivant dans l'ordre envoye a RoR2 (appele depuis le fil des touches).
	void FillCommand(bridge::SkyrimCommand& a_command);

	// Sauvegarde Skyrim : notre part de la vie maximum du joueur (appele par Shrines).
	void Save(SKSE::SerializationInterface* a_intfc);
	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t a_version);  // false : pas pour nous
	void Revert();
}
