#include "Effects.h"

#include "Bridge.h"

#include <array>

namespace skyrimror2::Effects
{
	namespace
	{
		constexpr float kUnitsPerMeter = 70.0f;
		constexpr int   kFamilies = 9;  // 1 explosion, 2 foudre, 3 gel, 4 feu, 5 soin, 6 poison, 7 magie, 8 impact

		HANDLE              g_mapping = nullptr;
		const std::uint8_t* g_view = nullptr;
		std::uint32_t       g_read = 0;
		bool                g_started = false;

		struct Choice
		{
			std::string                 model;
			RE::BGSSoundDescriptorForm* sound = nullptr;
		};
		std::array<Choice, kFamilies> g_choices{};
		bool                          g_chosen = false;

		bool Open()
		{
			if (g_view) {
				return true;
			}
			g_mapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kEffectMapSize, bridge::kEffectMapName);
			if (!g_mapping) {
				return false;
			}
			g_view = static_cast<const std::uint8_t*>(::MapViewOfFile(g_mapping, FILE_MAP_READ, 0, 0, bridge::kEffectMapSize));
			return g_view != nullptr;
		}

		std::string Lower(std::string a_text)
		{
			std::ranges::transform(a_text, a_text.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
			return a_text;
		}

		bool Has(const std::string& a_text, std::initializer_list<const char*> a_words)
		{
			for (const char* word : a_words) {
				if (a_text.find(word) != std::string::npos) {
					return true;
				}
			}
			return false;
		}

		// Pour chaque famille, une explosion de Skyrim dont le modele correspond (feu, foudre, gel...).
		void Choose()
		{
			g_chosen = true;
			auto* data = RE::TESDataHandler::GetSingleton();
			if (!data) {
				return;
			}
			for (auto* explosion : data->GetFormArray<RE::BGSExplosion>()) {
				if (!explosion) {
					continue;
				}
				const char* path = explosion->GetModel();
				if (!path || !*path) {
					continue;
				}
				const std::string model = Lower(path);
				int family = 0;
				if (Has(model, { "shock", "lightning" })) {
					family = 2;
				} else if (Has(model, { "frost", "ice" })) {
					family = 3;
				} else if (Has(model, { "heal", "restor" })) {
					family = 5;
				} else if (Has(model, { "poison" })) {
					family = 6;
				} else if (Has(model, { "illusion", "conjur", "mystic", "magic", "paralyz" })) {
					family = 7;
				} else if (Has(model, { "dust", "impact", "spark" })) {
					family = 8;
				} else if (Has(model, { "fire" })) {
					family = Has(model, { "explosion", "ball" }) ? 1 : 4;
				}
				if (family == 0 || !g_choices[family].model.empty()) {
					continue;
				}
				g_choices[family].model = path;
				g_choices[family].sound = explosion->data.sound1;
			}
			// Familles sans correspondance : une explosion de feu (ou l'inverse).
			if (g_choices[1].model.empty()) {
				g_choices[1] = g_choices[4];
			}
			if (g_choices[4].model.empty()) {
				g_choices[4] = g_choices[1];
			}
			for (int family = 1; family < kFamilies; ++family) {
				if (g_choices[family].model.empty()) {
					g_choices[family] = family == 8 ? g_choices[2] : g_choices[1];
				}
				logger::info("Effets : famille {} -> {}", family, g_choices[family].model.empty() ? "(aucun)" : g_choices[family].model);
			}
		}

		void Play(RE::TESObjectCELL* a_cell, int a_family, const RE::NiPoint3& a_position, float a_size)
		{
			if (a_family < 1 || a_family >= kFamilies) {
				return;
			}
			const auto& choice = g_choices[a_family];
			if (choice.model.empty()) {
				return;
			}
			const RE::NiPoint3 rotation{ 0.0f, 0.0f, 0.0f };
			RE::BSTempEffectParticle::Spawn(a_cell, 3.0f, choice.model.c_str(), rotation, a_position, std::clamp(a_size, 0.3f, 3.0f), 7, nullptr);
			// (Le son viendra plus tard : la version de la bibliotheque utilisee ici ne permet pas de le placer.)
		}
	}

	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked)
	{
		if (!a_player || !Open()) {
			return;
		}
		std::uint32_t magic = 0;
		std::uint32_t written = 0;
		std::memcpy(&magic, g_view, 4);
		std::memcpy(&written, g_view + 4, 4);
		if (magic != bridge::kEffectMagic) {
			return;
		}
		if (!g_started || !a_linked) {
			g_read = written;  // rien d'ancien a rejouer
			g_started = a_linked;
			return;
		}
		if (!g_chosen) {
			Choose();
		}
		auto* cell = a_player->GetParentCell();
		if (!cell) {
			g_read = written;
			return;
		}
		if (written - g_read > bridge::kEffectRing) {
			g_read = written - bridge::kEffectRing;  // trop en retard : on saute les plus anciens
		}
		int played = 0;
		for (; g_read != written && played < 20; ++g_read, ++played) {
			const std::uint8_t* entry = g_view + 16 + (g_read % bridge::kEffectRing) * 24;
			int   family = 0;
			float values[4];
			std::memcpy(&family, entry, 4);
			std::memcpy(values, entry + 4, 16);
			Play(cell, family, RE::NiPoint3(values[0] * kUnitsPerMeter, values[1] * kUnitsPerMeter, values[2] * kUnitsPerMeter), values[3]);
		}
	}
}
