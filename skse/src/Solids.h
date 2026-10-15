#pragma once

// 0.66 : les objets de RoR2 qui sont solides dans Skyrim (le cube de detritus de la Vagabonde).
//
// RoR2 envoie la place et la taille de chaque objet solide une fois pose (pont "SkyrimRoR2_Solids", voir
// SolidMirror.cs). Ici, on pose a sa place un objet fixe INVISIBLE de SkyrimRoR2.esp, fait seulement d'une boite
// de collision (modeles Solide_040.nif a Solide_200.nif, ecrits au lancement) : il arrete le joueur, les
// ennemis, les fleches et les sorts, et on peut monter dessus. Il disparait avec l'objet de RoR2.
namespace skyrimror2::Solids
{
	// Au lancement de Skyrim : ecrit les modeles de collision dans Data\meshes\SkyrimRoR2.
	void PrepareFiles();

	// Fil principal, a chaque image.
	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked);

	// Apres un chargement : retire les boites restees dans la sauvegarde.
	void OnGameLoaded();
}
