#pragma once

#include "Bridge.h"

#include <functional>

// Les autels des Divins (et des Daedra) : chaque autel debloque un survivant RoR2 (enregistre dans la
// sauvegarde Skyrim), puis ouvre l'ecran de choix parmi les survivants debloques.
namespace skyrimror2::Shrines
{
	// A appeler au chargement du plugin : enregistre la sauvegarde des survivants debloques.
	void RegisterSerialization();

	// A appeler quand les donnees du jeu sont chargees : ecoute les activations d'autels.
	void Install();

	// A appeler apres le chargement d'une sauvegarde (ou une nouvelle partie) : renvoie a RoR2
	// le survivant de cette sauvegarde.
	void OnGameLoaded();

	// Appele a chaque image depuis le fil principal : la fin d'une quete daedrique debloque son survivant
	// (controle toutes les 2 secondes).
	void PerFrame();

	// Le survivant en cours dans cette sauvegarde ("Huntress"...).
	std::string CurrentSurvivor();

	// [Autels] Activer : les autels debloquent les survivants. Sinon tout est libre (comme avant).
	bool Enabled();

	// [Autels] ToucheTousLesSurvivants : F8/F7 passent par tous les survivants de RoR2 (triche).
	bool KeysUseAllSurvivors();

	// F8 / F7 quand les autels sont actifs : survivant debloque suivant (+1) ou precedent (-1).
	void Cycle(int a_step);

	// Une fenetre de Skyrim avec des boutons (fil principal). Le dernier bouton sert d'"Annuler" (Echap).
	void ShowMessageBox(const std::string& a_body, const std::vector<std::string>& a_buttons, std::function<void(std::uint32_t)> a_onChoice);

	// La fenetre de choix du survivant (au reveil), s'il y a plus d'un survivant debloque.
	void OpenSurvivorChoice(const std::string& a_header);

	// Remplit la partie "choix de survivant" de l'ordre envoye a RoR2.
	void FillCommand(bridge::SkyrimCommand& a_command);
}
