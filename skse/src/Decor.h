#pragma once

// Le decor de Skyrim recopie dans RoR2 avec ses VRAIES formes de collision.
//
// Chaque objet fixe autour du joueur (sol, maisons, arches, rochers, barrieres, arbres...) a une forme
// de collision faite de triangles et de formes simples : c'est elle que Skyrim utilise pour savoir ou
// l'on marche. On la lit, on la convertit en triangles (monde de Skyrim, en metres) et on l'envoie a RoR2
// par un pont a part. Un objet fixe n'est envoye qu'une fois : RoR2 le garde tant qu'il reste autour du joueur.
// Reglages : [Decor] dans SkyrimRoR2.ini.
namespace skyrimror2::Decor
{
	// Relit les reglages du .ini.
	void LoadSettings();

	// Vrai si le decor est recopie (sinon, l'ancien relief en grille prend le relais).
	bool Enabled();

	// Appele a chaque image depuis le fil principal (crochet du joueur).
	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked);
}
