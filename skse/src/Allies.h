#pragma once

// Les allies de RoR2 (drones, tourelles...) affiches dans Skyrim avec leur vrai modele.
//
// RoR2 convertit le modele de chaque type d'allie au format de Skyrim (.nif + textures .dds) dans
// %LOCALAPPDATA%\SkyrimRoR2\modeles, et envoie a chaque image la liste des allies (modele, position,
// rotation). Ici, on recopie les fichiers dans Data\meshes et Data\textures\SkyrimRoR2, on charge le modele
// et on le pose dans le monde a la bonne place : il est eclaire et cache par le decor comme le reste.
namespace skyrimror2::Allies
{
	// Au lancement de Skyrim : Skyrim ne voit les fichiers de modeles que s'ils existent des le lancement.
	// On cree donc d'avance (vides si besoin) ceux de tous les allies connus ; RoR2 les remplit ensuite.
	void PrepareFiles();

	// Appele a chaque image depuis le fil principal (crochet du joueur).
	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked);

	// Objets fixes (autels RoR2...) : un modele converti par RoR2 ("ShrineChance_A"), pose dans le monde
	// (position en unites Skyrim, orientation en radians). Fil principal seulement.
	void ShowStatic(std::uint32_t a_id, const std::string& a_model, const RE::NiPoint3& a_position, float a_yaw, float a_scale);
	void HideStatic(std::uint32_t a_id);
}
