#pragma once

#include "Bridge.h"

// Les variantes de competences des survivants, choisies dans le menu Magie > Pouvoirs de Skyrim.
//
// SkyrimRoR2.esp contient 20 pouvoirs mineurs generiques (4 competences x 5 variantes).
// Le plugin les renomme selon le survivant en cours et ne donne au joueur que ceux qui existent.
// Choisir un de ces pouvoirs envoie la variante a RoR2, puis remet le cri ou le pouvoir d'avant.
// Le choix de chaque survivant est retenu dans la sauvegarde Skyrim (son "loadout").
namespace skyrimror2::Loadouts
{
	// Appele a chaque image depuis le fil principal du jeu.
	void PerFrame();

	// Remplit la partie "variantes" de l'ordre envoye a RoR2 (appele depuis le fil des touches).
	void FillCommand(bridge::SkyrimCommand& a_command);

	// Sauvegarde Skyrim (appele par Shrines, qui gere le fichier de sauvegarde du plugin).
	void Save(SKSE::SerializationInterface* a_intfc);
	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t a_version);  // false : pas pour nous
	void Revert();
}
