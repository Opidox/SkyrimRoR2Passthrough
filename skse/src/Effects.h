#pragma once

// Les effets de RoR2 (explosions, flammes, eclairs, gel, soins...) rejoues avec des effets de Skyrim.
//
// RoR2 range chaque nouvel effet dans une famille et l'envoie avec sa position. Ici, on joue au meme endroit
// l'effet visuel (et le son) d'une explosion de Skyrim de la meme famille, choisie parmi celles du jeu.
namespace skyrimror2::Effects
{
	// Appele a chaque image depuis le fil principal (crochet du joueur).
	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked);
}
