#pragma once

// Les autels de RoR2 dans les donjons de Skyrim (voir [Autels RoR2] dans SkyrimRoR2.ini).
//
// En entrant dans un donjon pour la premiere fois, un autel peut y apparaitre (une fois par donjon et par sauvegarde),
// pose devant un des contenants du donjon, avec son vrai modele RoR2 (converti par RoR2, comme les allies).
// On l'utilise en s'en approchant et en appuyant sur la touche "Utiliser" du .ini (E par defaut).
//   Chance    : payer des septims ; une chance sur deux environ de gagner un objet (deux gains au plus)
//   Ordre     : melange les objets du survivant (gratuit, une fois)
//   Combat    : fait venir des ennemis du donjon (gratuit, une fois)
//   Sang      : le survivant perd la moitie de ses PV, tu gagnes des septims (deux fois)
//   Montagne  : le prochain coffre de boss donne un objet de plus, avec plus de renforts (une fois)
//   Lunaire   : une capsule lunaire, ouverte avec une piece lunaire (toujours la dans un sanctuaire daedrique)
namespace skyrimror2::Altars
{
	void LoadSettings();
	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked);
	std::string Hint();  // l'autel du lieu et sa distance, pour le HUD (vide : aucun)

	// 0.71 : une porte bleue vers le Bazar de Newt, pres de ce coffre de boss (fil principal).
	void SpawnBazaarPortal(RE::TESObjectREFR* a_chest);

	void Save(SKSE::SerializationInterface* a_intfc);
	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t a_version);  // false : pas pour nous
	void Revert();
}
