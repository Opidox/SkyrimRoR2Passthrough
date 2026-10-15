#pragma once

#include "bridge_protocol.h"

namespace skyrimror2
{
	// Acces au "tableau blanc" partage avec RoR2, cote Skyrim.
	class Bridge
	{
	public:
		static Bridge& Get();

		// Cree le pont s'il n'existe pas encore, ou ouvre celui de RoR2.
		bool Open();

		// Zone 1 : copie l'etat du survivant ecrit par RoR2. false si vide ou inconnu.
		bool Read(bridge::BridgeData& a_out) const;

		// Zone 2 : publie les commandes du joueur pour RoR2.
		void WriteInput(const bridge::SkyrimInput& a_input);

		// Zone 3 : publie la liste des ennemis proches pour RoR2.
		void WriteActors(const bridge::ActorTable& a_table);

		// Zone 4 : la file des degats ecrite par RoR2 (nullptr si le pont n'est pas ouvert).
		const bridge::DamageRing* DamageQueue() const;

		// Zone 5 : ajoute un coup recu par le joueur, pour que RoR2 blesse le survivant.
		void WriteHit(std::uint32_t a_attackerFormId, float a_amount, std::uint32_t a_flags);

		// Zone 6 : publie les ordres pour RoR2 (lancer une partie, quel survivant...).
		void WriteCommand(const bridge::SkyrimCommand& a_command);

		// Zone 7 : copie le HUD ecrit par RoR2. false si vide.
		bool ReadHud(bridge::HudData& a_out) const;

		// Zone 8 : demande a RoR2 de donner un objet au hasard de ce rang (0 blanc ... 6 equipement).
		void WriteLoot(std::int32_t a_tier, std::int32_t a_count);

		// Zone 9 : le dernier objet donne par RoR2. false si aucun.
		bool ReadPickup(bridge::PickupNote& a_out) const;

	private:
		Bridge() = default;

		HANDLE        mapping_ = nullptr;
		std::uint8_t* view_ = nullptr;
	};
}
