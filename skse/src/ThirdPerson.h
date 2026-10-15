#pragma once

#include "Bridge.h"

// 0.65 : la vue a la 3e personne filmee par RoR2 ([TroisiemePersonne] dans SkyrimRoR2.ini).
//
// A la 3e personne, RoR2 filme le survivant (avec son vrai skin, ses animations, ses effets) exactement depuis
// la camera de Skyrim, sur fond transparent, eclaire comme le joueur l'est dans Skyrim (jour, nuit, interieur).
// Skyrim affiche cette image par-dessus le monde (voir Hud.cpp) et cache son propre personnage.
// Ici : l'etat de la camera (vraiment a la 3e personne ou non, menus compris), sa direction, et la lumiere.
namespace skyrimror2::ThirdPerson
{
	void LoadSettings();
	bool Enabled();
	bool Crosshair();  // [TroisiemePersonne] Viseur

	// Fil principal, a chaque image, AVANT Allies::PerFrame.
	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked);

	// Premiere personne "stable" : pendant un menu et 3 s apres, l'etat vu avant le menu ; sinon il faut
	// 0,4 s d'affilee de vraie 3e personne pour en sortir (pas d'apparition du modele en quittant un menu).
	bool FirstPersonStable();

	// Faut-il filmer et afficher le survivant (3e personne stable, relie a RoR2, reglage actif) ?
	bool Wanted();

	// Hud : l'image du survivant vient d'etre dessinee (n'importe quel fil).
	void NoteFilmShown();
	// Une image du survivant a ete dessinee il y a moins d'une seconde : le personnage Skyrim est cache.
	bool FilmShown();

	// N'importe quel fil : camera et lumiere pour RoR2.
	void FillInput(bridge::SkyrimInput& a_input);
}
