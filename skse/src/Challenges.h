#pragma once

// Les defis Skyrim qui debloquent les variantes de competences des survivants.
//
// Dans SkyrimRoR2.ini :
//   [Variantes]       ToutDebloque = 1 : toutes les variantes sont libres (pour les tests)
//   [DefisVariantes]  Huntress.2.1 = Archerie 50
//                     (survivant . competence de 1 a 4 . numero de variante, 0 = celle de base)
//                     Huntress.skin.1 = Dragons 5   (ou *.skin.1 pour tous les survivants)
//   Defis possibles : une competence Skyrim ("Archerie 50"), "Niveau 20", "Dragons 5" (dragons tues).
//
// La premiere variante de chaque competence est toujours libre, comme une variante sans defi.
// Les defis sont lus en direct dans la partie (niveaux de competence de base, sans les bonus).
namespace skyrimror2::Challenges
{
	// Relit les reglages du .ini (au chargement d'une partie : pas besoin de redemarrer Skyrim).
	void Load();

	// Fil principal seulement. a_slot de 0 a 3 (competences) ou 4 (skin), a_variant a partir de 0.
	bool IsUnlocked(const std::string& a_survivor, int a_slot, int a_variant);

	// Le defi a remplir ("Archerie 50"), vide s'il n'y en a pas.
	std::string Requirement(const std::string& a_survivor, int a_slot, int a_variant);

	// Ecoute la mort des dragons (a appeler quand les donnees du jeu sont chargees).
	void Install();

	// Sauvegarde Skyrim : le nombre de dragons tues (appele par Shrines, qui gere le fichier du plugin).
	void Save(SKSE::SerializationInterface* a_intfc);
	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t a_version);  // false : pas pour nous
	void Revert();
}
