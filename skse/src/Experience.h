#pragma once

// L'experience des competences Skyrim gagnee en jouant un survivant RoR2.
//
// Les attaques de Skyrim etant bloquees, les competences ne montaient plus. Chaque survivant est donc lie
// a deux competences Skyrim ([ExperienceSurvivants] dans SkyrimRoR2.ini) :
//   - celle des coups DONNES (la Chasseuse fait monter l'Archerie...),
//   - celle des coups RECUS (une armure, ou Alteration pour un mage).
// L'experience vaut les degats (en PV Skyrim) x [Experience] Multiplicateur, comme un coup normal de Skyrim.
namespace skyrimror2::Experience
{
	// Relit les reglages du .ini.
	void Load();

	// Fil principal seulement. Degats en PV Skyrim.
	// a_slot : case de l'attaque (0 M1, 1 M2, 2 Maj, 3 R, 4 allies, 5 autre) ; a_variant : sa variante.
	// a_code : competence d'un objet ou equipement envoyee par RoR2 (0 : aucune ; voir SkyrimRoR2/SkillSource.cs).
	void OnDamageDealt(float a_damage, int a_slot, int a_variant, int a_code);
	// Utilisation d'un equipement : de l'experience sans degats.
	void OnSkillUse(int a_code, float a_amount);
	void OnDamageTaken(float a_damage);

	// Les atouts Skyrim du joueur s'appliquent aux coups du survivant (fil principal) : Skyrim calcule
	// tous les bonus de degats comme si le joueur frappait a l'arme de la competence du survivant
	// (arc pour l'Archerie, epee pour Une main, epee a deux mains, sort Flammes pour Destruction).
	// Renvoie un multiplicateur (1.2 = +20 %). 1 pour les autres competences.
	float PerkMultiplier(RE::Actor* a_target, int a_slot, int a_variant, int a_code);

	// 0.63 : effets des coups de Destruction ([EffetsDestruction]), apres les degats (fil principal).
	// Feu : brulure ; Gel : vigueur et ralenti ; Foudre : magie. Atouts de fin d'arbre : Flammes intenses (fuite),
	// Gel profond (paralysie), Desintegration (tue un ennemi presque mort). Vrai si l'ennemi a ete desintegre.
	bool ApplyDestruction(RE::Actor* a_target, float a_damage, int a_slot, int a_variant, int a_code);
}
