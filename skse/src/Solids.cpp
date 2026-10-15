#include "Solids.h"

#include "Bridge.h"
#include "SolidNif.h"

#include <fstream>

namespace skyrimror2::Solids
{
	namespace
	{
		using Clock = std::chrono::steady_clock;
		constexpr float         kUnitsPerMeter = 70.0f;
		constexpr const char*   kPluginFile = "SkyrimRoR2.esp";
		constexpr std::uint32_t kFirstSolidId = 0xC20;  // voir tools/make_esp.py (SOLID_FIRST_ID, SOLID_SIZES)
		constexpr int           kSizes[]{ 40, 60, 80, 100, 125, 150, 200 };  // cm
		constexpr int           kSizeCount = static_cast<int>(sizeof(kSizes) / sizeof(kSizes[0]));

		HANDLE              g_mapping = nullptr;
		const std::uint8_t* g_view = nullptr;

		struct Placed
		{
			RE::ObjectRefHandle handle;
			int                 size = -1;   // index dans kSizes
			RE::NiPoint3        center;      // unites Skyrim
			Clock::time_point   lastSeen{};
			bool                checked = false;  // collision verifiee une fois
		};
		std::unordered_map<std::int32_t, Placed> g_placed;  // par id RoR2
		RE::TESObjectCELL*                        g_cell = nullptr;
		bool                                      g_warned = false;
		bool                                      g_cleanPending = true;

		bool Open()
		{
			if (g_view) {
				return true;
			}
			g_mapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kSolidMapSize, bridge::kSolidMapName);
			if (!g_mapping) {
				return false;
			}
			g_view = static_cast<const std::uint8_t*>(::MapViewOfFile(g_mapping, FILE_MAP_READ, 0, 0, bridge::kSolidMapSize));
			return g_view != nullptr;
		}

		RE::TESObjectSTAT* Base(int a_size)
		{
			static RE::TESObjectSTAT* forms[kSizeCount]{};
			static bool               looked = false;
			if (!looked) {
				looked = true;
				if (auto* data = RE::TESDataHandler::GetSingleton()) {
					for (int i = 0; i < kSizeCount; ++i) {
						forms[i] = data->LookupForm<RE::TESObjectSTAT>(kFirstSolidId + static_cast<std::uint32_t>(i), kPluginFile);
					}
				}
				if (!forms[0]) {
					logger::warn("Solides : objets introuvables dans SkyrimRoR2.esp (remplace-le par celui de la 0.66)");
				}
			}
			return a_size >= 0 && a_size < kSizeCount ? forms[a_size] : nullptr;
		}

		bool IsOurs(RE::TESObjectREFR* a_ref)
		{
			auto* base = a_ref ? a_ref->GetBaseObject() : nullptr;
			if (!base) {
				return false;
			}
			for (int i = 0; i < kSizeCount; ++i) {
				if (Base(i) && base == Base(i)) {
					return true;
				}
			}
			return false;
		}

		void Delete(Placed& a_placed)
		{
			if (auto ref = a_placed.handle.get()) {
				ref->Disable();
				ref->SetDelete(true);
			}
			a_placed.handle.reset();
		}

		// La taille d'objet la plus proche du cube (cote moyen, en cm).
		int NearestSize(float a_halfX, float a_halfY, float a_halfZ)
		{
			const float side = (a_halfX + a_halfY + a_halfZ) * 2.0f / 3.0f * 100.0f;
			int         best = 0;
			for (int i = 1; i < kSizeCount; ++i) {
				if (std::fabs(static_cast<float>(kSizes[i]) - side) < std::fabs(static_cast<float>(kSizes[best]) - side)) {
					best = i;
				}
			}
			return best;
		}

		bool Place(RE::PlayerCharacter* a_player, Placed& a_placed, int a_size, const RE::NiPoint3& a_center)
		{
			auto* base = Base(a_size);
			if (!base) {
				return false;
			}
			auto ref = a_player->PlaceObjectAtMe(base, false);
			if (!ref) {
				return false;
			}
			// Pose a la bonne place PUIS (re)chargee : sa collision est creee la, pas sur le joueur.
			ref->Disable();
			ref->SetPosition(a_center);
			ref->Enable(false);
			a_placed.handle = ref->CreateRefHandle();
			a_placed.checked = false;
			a_placed.size = a_size;
			a_placed.center = a_center;
			return true;
		}

		// Les boites restees d'avant (sauvegarde, lieu quitte) : on les retire.
		void CleanOrphans(RE::PlayerCharacter* a_player)
		{
			auto* tes = RE::TES::GetSingleton();
			if (!tes || !Base(0)) {
				return;
			}
			std::unordered_set<RE::TESObjectREFR*> known;
			for (auto& [id, placed] : g_placed) {
				if (auto ref = placed.handle.get()) {
					known.insert(ref.get());
				}
			}
			int removed = 0;
			tes->ForEachReferenceInRange(a_player, 400.0f * kUnitsPerMeter, [&](RE::TESObjectREFR* a_ref) {
				if (a_ref && !known.contains(a_ref) && IsOurs(a_ref) && !a_ref->IsDeleted()) {
					a_ref->Disable();
					a_ref->SetDelete(true);
					++removed;
				}
				return RE::BSContainer::ForEachResult::kContinue;
			});
			if (removed > 0) {
				logger::info("Solides : {} ancienne(s) boite(s) retiree(s)", removed);
			}
		}
	}

	void PrepareFiles()
	{
		const auto folder = std::filesystem::absolute("Data/meshes/SkyrimRoR2");
		std::error_code error;
		std::filesystem::create_directories(folder, error);
		int written = 0;
		for (int i = 0; i < kSizeCount; ++i) {
			auto        bytes = SolidNif::kBytes;
			const float half = std::max(0.05f, kSizes[i] / 200.0f - SolidNif::kRadius);
			for (int axis = 0; axis < 3; ++axis) {
				std::memcpy(bytes.data() + SolidNif::kDimensionsOffset + axis * 4, &half, 4);
			}
			const auto path = folder / std::format("Solide_{:03}.nif", kSizes[i]);
			// Deja la, et pareil : on n'y touche pas.
			if (std::filesystem::exists(path, error) && std::filesystem::file_size(path, error) == bytes.size()) {
				std::ifstream in(path, std::ios::binary);
				std::vector<char> old(bytes.size());
				in.read(old.data(), static_cast<std::streamsize>(old.size()));
				if (in && std::memcmp(old.data(), bytes.data(), bytes.size()) == 0) {
					continue;
				}
			}
			std::ofstream out(path, std::ios::binary | std::ios::trunc);
			out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
			++written;
		}
		logger::info("Solides : {} modele(s) de collision ecrit(s)", written);
	}

	void OnGameLoaded()
	{
		g_placed.clear();  // les poignees d'avant le chargement ne valent plus rien
		g_cleanPending = true;
	}

	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked)
	{
		if (!a_player || !Open()) {
			return;
		}
		auto* cell = a_player->GetParentCell();
		if (cell != g_cell) {
			g_cell = cell;
			g_cleanPending = true;
		}
		if (g_cleanPending && cell && a_player->Is3DLoaded()) {
			g_cleanPending = false;
			CleanOrphans(a_player);
		}

		std::uint32_t magic = 0, count = 0;
		std::memcpy(&magic, g_view, 4);
		std::memcpy(&count, g_view + 8, 4);
		if (!a_linked || magic != bridge::kSolidMagic || !cell) {
			count = 0;
		}
		count = std::min(count, bridge::kSolidMax);
		const auto now = Clock::now();
		for (std::uint32_t i = 0; i < count; ++i) {
			const std::uint8_t* entry = g_view + bridge::kSolidHeaderSize + i * bridge::kSolidEntrySize;
			std::int32_t id = 0;
			float        values[7];
			std::memcpy(&id, entry, 4);
			std::memcpy(values, entry + 4, sizeof(values));
			bool finite = true;
			for (float v : values) {
				finite = finite && std::isfinite(v) && std::fabs(v) < 1.0e6f;
			}
			if (!finite) {
				continue;
			}
			const RE::NiPoint3 center(values[0] * kUnitsPerMeter, values[1] * kUnitsPerMeter, values[2] * kUnitsPerMeter);
			const int          size = NearestSize(values[3], values[4], values[5]);
			auto&              placed = g_placed[id];
			placed.lastSeen = now;
			auto ref = placed.handle.get();
			// Deja posee au meme endroit, de la meme taille : rien a faire.
			if (ref && placed.size == size && placed.center.GetDistance(center) < 0.3f * kUnitsPerMeter) {
				// 0.73 : on note une fois si Skyrim a bien cree sa collision (pour comprendre un cube "traversable").
				static int checks = 0;
				if (!placed.checked && checks < 5) {
					if (auto* root = ref->Get3D()) {
						placed.checked = true;
						++checks;
						logger::info("Solides : boite chargee, collision Havok {}", root->collisionObject ? "presente" : "ABSENTE");
					}
				}
				continue;
			}
			Delete(placed);
			if (Place(a_player, placed, size, center)) {
				logger::info("Solides : boite de {} cm posee ({:.0f} m du joueur)", kSizes[size], center.GetDistance(a_player->GetPosition()) / kUnitsPerMeter);
			} else if (!g_warned) {
				g_warned = true;
				logger::warn("Solides : impossible de poser la boite de collision");
			}
		}
		// Les objets disparus dans RoR2 (ou un instant pas envoyes) : retires apres 0,5 s.
		for (auto it = g_placed.begin(); it != g_placed.end();) {
			if (now - it->second.lastSeen > std::chrono::milliseconds(500)) {
				Delete(it->second);
				it = g_placed.erase(it);
			} else {
				++it;
			}
		}
	}
}
