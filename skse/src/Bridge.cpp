#include "Bridge.h"

namespace skyrimror2
{
	Bridge& Bridge::Get()
	{
		static Bridge bridge;
		return bridge;
	}

	bool Bridge::Open()
	{
		if (view_) {
			return true;
		}

		// Meme appel Windows que le cote RoR2 : le premier lance cree le pont,
		// le second ouvre le meme. Peu importe qui demarre en premier.
		mapping_ = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
			bridge::kMapSize, bridge::kMapName);
		if (!mapping_) {
			logger::error("CreateFileMapping a echoue (erreur Windows {})", ::GetLastError());
			return false;
		}

		view_ = static_cast<std::uint8_t*>(::MapViewOfFile(mapping_, FILE_MAP_ALL_ACCESS, 0, 0, bridge::kMapSize));
		if (!view_) {
			logger::error("MapViewOfFile a echoue (erreur Windows {})", ::GetLastError());
			::CloseHandle(mapping_);
			mapping_ = nullptr;
			return false;
		}

		logger::info("Pont ouvert");
		return true;
	}

	bool Bridge::Read(bridge::BridgeData& a_out) const
	{
		if (!view_) {
			return false;
		}
		std::memcpy(&a_out, view_, sizeof(a_out));
		return a_out.magic == bridge::kMagic && a_out.version == bridge::kVersion;
	}

	void Bridge::WriteActors(const bridge::ActorTable& a_table)
	{
		if (!view_) {
			return;
		}
		std::memcpy(view_ + bridge::kActorOffset, &a_table, sizeof(a_table));
	}

	const bridge::DamageRing* Bridge::DamageQueue() const
	{
		return view_ ? reinterpret_cast<const bridge::DamageRing*>(view_ + bridge::kDamageOffset) : nullptr;
	}

	void Bridge::WriteHit(std::uint32_t a_attackerFormId, float a_amount, std::uint32_t a_flags)
	{
		if (!view_) {
			return;
		}
		auto* ring = reinterpret_cast<bridge::HitRing*>(view_ + bridge::kHitOffset);
		ring->magic = bridge::kHitMagic;

		const std::uint32_t count = ring->writeCount;
		ring->events[count % bridge::kHitSlots] = bridge::HitEvent{ a_attackerFormId, a_amount, a_flags, 0 };

		// D'abord le coup, ENSUITE le compteur : RoR2 ne lit jamais un coup a moitie ecrit.
		std::atomic_thread_fence(std::memory_order_release);
		*reinterpret_cast<volatile std::uint32_t*>(&ring->writeCount) = count + 1;
	}

	void Bridge::WriteInput(const bridge::SkyrimInput& a_input)
	{
		if (!view_) {
			return;
		}
		std::memcpy(view_ + bridge::kInputOffset, &a_input, sizeof(a_input));
	}

	void Bridge::WriteCommand(const bridge::SkyrimCommand& a_command)
	{
		if (!view_) {
			return;
		}
		std::memcpy(view_ + bridge::kCommandOffset, &a_command, sizeof(a_command));
	}

	bool Bridge::ReadHud(bridge::HudData& a_out) const
	{
		if (!view_) {
			return false;
		}
		std::memcpy(&a_out, view_ + bridge::kHudOffset, sizeof(a_out));
		return a_out.magic == bridge::kHudMagic;
	}

	void Bridge::WriteLoot(std::int32_t a_tier, std::int32_t a_count)
	{
		if (!view_) {
			return;
		}
		auto* ring = reinterpret_cast<bridge::LootRing*>(view_ + bridge::kLootOffset);
		ring->magic = bridge::kLootMagic;
		const std::uint32_t count = ring->writeCount;
		ring->events[count % bridge::kLootSlots] = bridge::LootEvent{ a_tier, a_count };
		std::atomic_thread_fence(std::memory_order_release);
		*reinterpret_cast<volatile std::uint32_t*>(&ring->writeCount) = count + 1;
	}

	bool Bridge::ReadPickup(bridge::PickupNote& a_out) const
	{
		if (!view_) {
			return false;
		}
		std::memcpy(&a_out, view_ + bridge::kPickupOffset, sizeof(a_out));
		a_out.name[sizeof(a_out.name) - 1] = '\0';
		return a_out.magic == bridge::kPickupMagic;
	}
}
