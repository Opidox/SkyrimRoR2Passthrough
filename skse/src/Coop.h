#pragma once

// 0.70 : la coop, cote Skyrim.
//
// Chaque joueur a son Skyrim et son RoR2 ; les RoR2 se rejoignent dans une vraie partie coop de RoR2, qui fait tout
// le reseau. Ici, Skyrim echange avec SON RoR2 (pont "SkyrimRoR2_Coop") :
//  - RoR2 -> Skyrim : la difficulte commune (celle de l'hote), "a terre" et le relevement en cours, les messages
//    (coequipier a terre, arrivee d'un joueur...), les contenants ouverts et les coffres de boss termines par les
//    coequipiers, la charge du coffre du boss d'un coequipier dans le meme lieu ;
//  - Skyrim -> RoR2 : les contenants que ce joueur ouvre (paie), les coffres de boss qu'il termine, sa charge en cours.
// Sans coop (RoR2 seul, ou ancien RoR2), rien ne change : Active() est faux.
namespace skyrimror2::Coop
{
	// Fil principal, a chaque image.
	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked);

	bool        Active();               // une partie coop est en cours (au moins un coequipier)
	int         Players();              // joueurs dans la partie
	float       DifficultyOverride();   // difficulte commune, < 0 : aucune (celle de ce Skyrim)
	bool        KeepDifficulty();       // a la mort, la difficulte reste (des coequipiers sont encore debout)
	bool        Downed();               // le survivant est a terre (un coequipier peut le relever)
	float       Revive();               // relevement en cours (0 a 1), < 0 : aucun
	std::string ReviveLabel();

	// Appeles par Loot (fil principal).
	void ReportOpened(RE::FormID a_container);    // contenant ouvert et paye par ce joueur
	void ReportFinished(RE::FormID a_chest);      // coffre du boss termine par ce joueur
	void ReportCharge(RE::FormID a_chest, float a_progress);  // sa charge en cours (0 : aucune)
	bool RemoteCharge(RE::FormID& a_chest, float& a_progress); // la charge d'un coequipier dans ce lieu
}
