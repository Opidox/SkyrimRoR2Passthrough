#pragma once

// Le relief de Skyrim autour du joueur, envoye a RoR2 (qui en fait une surface invisible sous le survivant).
//
// Une grille de rayons invisibles verticaux mesure la hauteur du sol autour du joueur ([Relief] dans
// SkyrimRoR2.ini : taille et pas de la grille). Pour ne pas ralentir le jeu, on mesure une ligne de la
// grille par image ; la grille complete est publiee une fois toutes les lignes mesurees.
// La position du joueur est publiee a chaque image : RoR2 s'en sert pour caler la surface sous le survivant.
namespace skyrimror2::Terrain
{
	// Relit les reglages du .ini.
	void LoadSettings();

	// Vrai si le relief est recopie dans RoR2 ([Relief] Activer).
	bool Enabled();

	// Appele a chaque image depuis le fil principal (crochet du joueur).
	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked);
}
