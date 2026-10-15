#include "Coop.h"

#include "Bridge.h"
#include "Loot.h"

namespace skyrimror2::Coop
{
	namespace
	{
		using Clock = std::chrono::steady_clock;

		HANDLE        g_mapping = nullptr;
		std::uint8_t* g_view = nullptr;
		bool          g_openTried = false;

		// Lu a chaque image (fil principal), lu par le HUD (autre fil) : sous verrou.
		std::mutex  g_lock;
		bool        g_active = false;
		int         g_players = 1;
		float       g_difficulty = -1.0f;
		bool        g_keep = false;
		bool        g_downed = false;
		float       g_revive = -1.0f;
		std::string g_reviveLabel;
		RE::FormID  g_remoteChest = 0;
		float       g_remoteProgress = 0.0f;

		std::uint32_t     g_lastSequence = 0;
		Clock::time_point g_lastChange{};
		std::uint32_t     g_textSeen = 0;
		bool              g_textStarted = false;
		std::uint32_t     g_openedRead = 0;
		bool              g_openedStarted = false;
		std::uint32_t     g_finishedRead = 0;
		bool              g_finishedStarted = false;
		std::uint32_t     g_skySequence = 0;

		bridge::CoopFromRoR2* FromRoR2() { return reinterpret_cast<bridge::CoopFromRoR2*>(g_view); }
		bridge::CoopFromSkyrim* FromSkyrim() { return reinterpret_cast<bridge::CoopFromSkyrim*>(g_view + bridge::kCoopSkyrimOffset); }

		bool Open()
		{
			if (g_view) {
				return true;
			}
			if (g_openTried) {
				return false;
			}
			g_openTried = true;
			g_mapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kCoopMapSize, bridge::kCoopMapName);
			if (!g_mapping) {
				logger::warn("Coop : pont impossible a creer");
				return false;
			}
			g_view = static_cast<std::uint8_t*>(::MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, bridge::kCoopMapSize));
			if (!g_view) {
				logger::warn("Coop : pont impossible a ouvrir");
				return false;
			}
			auto* sky = FromSkyrim();
			if (sky->magic != bridge::kCoopSkyMagic) {
				std::memset(sky, 0, sizeof(bridge::CoopFromSkyrim));
				sky->magic = bridge::kCoopSkyMagic;
			}
			return true;
		}

		std::string Text(const char* a_text, std::size_t a_size)
		{
			std::size_t length = 0;
			while (length < a_size && a_text[length] != '\0') {
				++length;
			}
			return std::string(a_text, length);
		}

		void Push(std::uint32_t* a_ring, std::uint32_t& a_write, std::uint32_t a_size, std::uint32_t a_value)
		{
			const std::uint32_t write = a_write;
			a_ring[write % a_size] = a_value;
			std::atomic_thread_fence(std::memory_order_release);
			a_write = write + 1;
		}

		// Lit les nouvelles entrees d'une file circulaire ecrite par RoR2.
		template <class F>
		void Drain(const std::uint32_t* a_ring, std::uint32_t a_written, std::uint32_t a_size, std::uint32_t& a_read, bool& a_started, F&& a_each)
		{
			if (!a_started) {
				a_read = a_written;  // ce qui est arrive avant nous est ignore
				a_started = true;
				return;
			}
			if (a_written - a_read > a_size) {
				a_read = a_written - a_size;
			}
			while (a_read != a_written) {
				a_each(a_ring[a_read % a_size]);
				++a_read;
			}
		}
	}

	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked)
	{
		if (!Open()) {
			return;
		}
		auto*      from = FromRoR2();
		auto*      sky = FromSkyrim();
		const auto now = Clock::now();
		sky->magic = bridge::kCoopSkyMagic;
		sky->sequence = ++g_skySequence;

		const std::uint32_t sequence = from->sequence;
		if (sequence != g_lastSequence) {
			g_lastSequence = sequence;
			g_lastChange = now;
		}
		const bool fresh = from->magic == bridge::kCoopMagic && now - g_lastChange < std::chrono::seconds(2);
		const bool active = a_linked && fresh && from->active == 1;
		{
			std::scoped_lock lock(g_lock);
			if (active != g_active) {
				logger::info("Coop : {}", active ? std::format("partie coop en cours ({} joueurs)", from->players) : "plus de partie coop");
			}
			g_active = active;
			g_players = active ? std::max(1, from->players) : 1;
			g_difficulty = active && std::isfinite(from->difficulty) ? from->difficulty : -1.0f;
			g_keep = active && from->keepDifficulty == 1;
			g_downed = active && from->downed == 1;
			g_revive = active && std::isfinite(from->revive) ? from->revive : -1.0f;
			g_reviveLabel = active ? Text(from->reviveLabel, sizeof(from->reviveLabel)) : std::string();
			g_remoteChest = active ? from->chargeChest : 0;
			g_remoteProgress = active && std::isfinite(from->chargeProgress) ? std::clamp(from->chargeProgress, 0.0f, 1.0f) : 0.0f;
		}
		if (!fresh) {
			return;
		}

		// Les messages de RoR2 (coequipier a terre, arrivee d'un joueur...).
		const std::uint32_t textSequence = from->textSequence;
		if (!g_textStarted) {
			g_textStarted = true;
			g_textSeen = textSequence;
		} else if (textSequence != g_textSeen) {
			g_textSeen = textSequence;
			const std::string text = Text(from->text, sizeof(from->text));
			if (!text.empty()) {
				RE::SendHUDMessage::ShowHUDMessage(text.c_str());
				logger::info("Coop : message \"{}\"", text);
			}
		}

		// Les contenants ouverts et les coffres de boss termines par les coequipiers.
		if (a_player) {
			Drain(from->opened, from->openedWrite, bridge::kCoopRing, g_openedRead, g_openedStarted,
				[](std::uint32_t a_id) { Loot::OnRemoteOpened(a_id); });
			Drain(from->finished, from->finishedWrite, 16, g_finishedRead, g_finishedStarted,
				[](std::uint32_t a_id) { Loot::OnRemoteFinished(a_id); });
		}
	}

	bool Active()
	{
		std::scoped_lock lock(g_lock);
		return g_active;
	}

	int Players()
	{
		std::scoped_lock lock(g_lock);
		return g_players;
	}

	float DifficultyOverride()
	{
		std::scoped_lock lock(g_lock);
		return g_difficulty;
	}

	bool KeepDifficulty()
	{
		std::scoped_lock lock(g_lock);
		return g_keep;
	}

	bool Downed()
	{
		std::scoped_lock lock(g_lock);
		return g_downed;
	}

	float Revive()
	{
		std::scoped_lock lock(g_lock);
		return g_revive;
	}

	std::string ReviveLabel()
	{
		std::scoped_lock lock(g_lock);
		return g_reviveLabel;
	}

	void ReportOpened(RE::FormID a_container)
	{
		// Seulement les contenants du jeu (pas ceux crees en cours de partie, propres a chaque Skyrim).
		if (!Open() || a_container == 0 || (a_container >> 24) == 0xFF) {
			return;
		}
		auto* sky = FromSkyrim();
		Push(sky->opened, sky->openedWrite, bridge::kCoopRing, a_container);
	}

	void ReportFinished(RE::FormID a_chest)
	{
		if (!Open() || a_chest == 0 || (a_chest >> 24) == 0xFF) {
			return;
		}
		auto* sky = FromSkyrim();
		Push(sky->finished, sky->finishedWrite, 16, a_chest);
	}

	void ReportCharge(RE::FormID a_chest, float a_progress)
	{
		if (!Open()) {
			return;
		}
		auto* sky = FromSkyrim();
		sky->chargeChest = (a_chest >> 24) == 0xFF ? 0 : a_chest;
		sky->chargeProgress = std::clamp(a_progress, 0.0f, 1.0f);
	}

	bool RemoteCharge(RE::FormID& a_chest, float& a_progress)
	{
		std::scoped_lock lock(g_lock);
		a_chest = g_remoteChest;
		a_progress = g_remoteProgress;
		return g_active && g_remoteChest != 0;
	}
}
