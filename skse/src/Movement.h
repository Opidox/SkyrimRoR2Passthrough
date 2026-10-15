#pragma once

#include "Bridge.h"

namespace skyrimror2::Movement
{
	// Installe le crochet appele a chaque image sur le joueur Skyrim
	// (le meme que SkyCraft : PlayerCharacter::Update).
	void Install();

	// Dit si Skyrim pilote actuellement RoR2 (au premier plan, en jeu, hors menu).
	// Appele par le fil qui envoie les touches.
	void SetSkyrimDriving(bool a_driving);

	// Vrai quand les deux jeux sont lies : RoR2 repond, le survivant est en jeu,
	// et Skyrim pilote (au premier plan, hors menu). Mis a jour a chaque image.
	bool IsLinked();

	// Le fil des touches dit si le joueur veut sprinter (touche enfoncee en se deplacant)...
	void SetSprintRequested(bool a_requested);

	// ...et demande s'il en a le droit (faux quand l'endurance de Skyrim est vide).
	bool SprintAllowed();

	// Vrai pendant un court instant apres un atterrissage du joueur Skyrim : les PV perdus sans attaquant
	// sont alors des degats de chute de Skyrim, a ignorer (RoR2 compte deja la chute du survivant).
	bool JustLanded();

	// Remplit la distance au sol et la vitesse verticale du joueur Skyrim dans l'ordre envoye a RoR2.
	void FillCommand(bridge::SkyrimCommand& a_command);
}
