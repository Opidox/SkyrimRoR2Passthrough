#pragma once

namespace skyrimror2::Hud
{
	// Branche le dessin du HUD RoR2 sur le rendu de Skyrim (a appeler une fois, au chargement du plugin,
	// AVANT que Skyrim ne cree sa fenetre : c'est-a-dire dans SKSEPluginLoad).
	void Install();

	// Appele a chaque image depuis le fil principal : cache ou remontre la barre de vie de Skyrim.
	void PerFrame(bool a_linked);
}
