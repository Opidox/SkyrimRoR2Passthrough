#include "Terrain.h"

#include "Bridge.h"
#include "Decor.h"

namespace skyrimror2::Terrain
{
	namespace
	{
		constexpr float kUnitsPerMeter = 70.0f;  // 1 metre = environ 70 unites Skyrim
		constexpr float kStepUp = 1.5f;          // un point peut etre jusqu'a 1,5 m plus haut que son voisin (pente)
		constexpr float kRayBelow = 30.0f;       // on cherche le sol jusqu'a 30 m sous le voisin (falaises)
		constexpr float kWallProbe = 1.0f;       // hauteur du rayon horizontal qui cherche les murs
		constexpr float kWallSearch = 40.0f;     // on cherche le sommet d'un mur jusqu'a 40 m au-dessus du sol
		constexpr float kWallFallback = 4.0f;    // sommet introuvable : un mur de 4 m
		constexpr int   kTileCells = 16;         // la grille est decoupee en tuiles de 16 x 16 cases, fixes dans le monde

		bool  g_enabled = true;
		int   g_side = 129;       // points par cote
		float g_spacing = 2.0f;   // metres entre deux points
		int   g_pointsPerFrame = 400;  // points mesures par image (2 rayons chacun)

		HANDLE        g_mapping = nullptr;
		std::uint8_t* g_view = nullptr;

		// Ordre de mesure : du centre (le joueur) vers l'exterieur. Chaque point part de son voisin
		// un pas plus pres du centre ("parent"), deja mesure : la mesure suit le sol comme on marcherait.
		std::vector<int>   g_order;
		std::vector<int>   g_parent;

		std::vector<float> g_heights;
		std::vector<bool>  g_wall;
		std::size_t        g_next = 0;  // prochain point a mesurer dans g_order
		bool               g_passStarted = false;
		float              g_originX = 0.0f;  // coin sud-ouest de la grille (monde Skyrim, m)
		float              g_originY = 0.0f;
		float              g_feetZ = 0.0f;    // hauteur des pieds au debut de la mesure (m)
		std::uint32_t      g_published = 0;

		bool OpenRelief()
		{
			if (g_view) {
				return true;
			}
			g_mapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kReliefMapSize, bridge::kReliefMapName);
			if (!g_mapping) {
				return false;
			}
			g_view = static_cast<std::uint8_t*>(::MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, bridge::kReliefMapSize));
			if (g_view) {
				logger::info("Relief : pont ouvert ({} x {} points, tous les {:.1f} m)", g_side, g_side, g_spacing);
			}
			return g_view != nullptr;
		}

		bridge::ReliefHeader* Header() { return reinterpret_cast<bridge::ReliefHeader*>(g_view); }
		float*                Heights() { return reinterpret_cast<float*>(g_view + bridge::kReliefHeaderSize); }

		// L'ordre de mesure (anneaux autour du point de depart, sous le joueur) et le parent de chaque point.
		void BuildOrder(int a_startRow, int a_startColumn)
		{
			const int side = g_side;
			g_order.clear();
			g_order.reserve(static_cast<std::size_t>(side) * side);
			g_parent.assign(static_cast<std::size_t>(side) * side, -1);
			const int maxRing = std::max({ a_startRow, a_startColumn, side - 1 - a_startRow, side - 1 - a_startColumn });
			for (int ring = 0; ring <= maxRing; ++ring) {
				for (int row = std::max(0, a_startRow - ring); row <= std::min(side - 1, a_startRow + ring); ++row) {
					for (int column = std::max(0, a_startColumn - ring); column <= std::min(side - 1, a_startColumn + ring); ++column) {
						const int dx = column - a_startColumn;
						const int dy = row - a_startRow;
						if (std::max(std::abs(dx), std::abs(dy)) != ring) {
							continue;
						}
						const int index = row * side + column;
						g_order.push_back(index);
						if (ring > 0) {
							// Un pas vers le depart, sur l'axe le plus eloigne.
							int parentRow = row;
							int parentColumn = column;
							if (std::abs(dx) >= std::abs(dy)) {
								parentColumn -= dx > 0 ? 1 : -1;
							} else {
								parentRow -= dy > 0 ? 1 : -1;
							}
							g_parent[index] = parentRow * side + parentColumn;
						}
					}
				}
			}
		}

		// Fraction libre (0 a 1) d'un rayon entre deux points (en metres, monde Skyrim). 1 = rien touche.
		float RayFraction(RE::bhkWorld* a_world, const RE::CFilter& a_filter, const RE::NiPoint3& a_from, const RE::NiPoint3& a_to)
		{
			const float     scale = RE::bhkWorld::GetWorldScale() * kUnitsPerMeter;  // metres -> metres physiques
			RE::bhkPickData pick{};
			pick.rayInput.from = RE::hkVector4(a_from.x * scale, a_from.y * scale, a_from.z * scale, 0.0f);
			pick.rayInput.to = RE::hkVector4(a_to.x * scale, a_to.y * scale, a_to.z * scale, 0.0f);
			pick.rayInput.filterInfo = a_filter;
			a_world->PickObject(pick);
			return pick.rayOutput.HasHit() ? std::clamp(pick.rayOutput.hitFraction, 0.0f, 1.0f) : 1.0f;
		}

		// Mesure un point : un rayon horizontal depuis son parent (y a-t-il un mur entre les deux ?),
		// puis un rayon vertical qui part un peu au-dessus du sol du parent (et pas du ciel : sous une porte,
		// un linteau ou un plafond n'est donc jamais pris pour le sol).
		void MeasurePoint(RE::bhkWorld* a_world, const RE::CFilter& a_filter, int a_index)
		{
			const int   side = g_side;
			const float x = g_originX + (a_index % side) * g_spacing;
			const float y = g_originY + (a_index / side) * g_spacing;
			const int   parent = g_parent[a_index];

			if (parent < 0) {  // le centre : sous le joueur
				const float f = RayFraction(a_world, a_filter, { x, y, g_feetZ + kStepUp }, { x, y, g_feetZ - kRayBelow });
				g_heights[a_index] = f < 1.0f ? g_feetZ + kStepUp - f * (kStepUp + kRayBelow) : g_feetZ;
				g_wall[a_index] = false;
				return;
			}

			const float parentX = g_originX + (parent % side) * g_spacing;
			const float parentY = g_originY + (parent / side) * g_spacing;
			const float base = g_heights[parent];

			// Un mur entre le parent et ce point ? On cherche alors son vrai sommet (toit, haut de falaise,
			// haut de palissade) avec un rayon qui descend de haut. Les points suivants repartent de ce
			// sommet : derriere une palissade on retrouve le sol, en haut d'une falaise on reste en haut.
			const float probe = base + kWallProbe;
			if (RayFraction(a_world, a_filter, { parentX, parentY, probe }, { x, y, probe }) < 1.0f) {
				const float high = base + kWallSearch;
				const float f = RayFraction(a_world, a_filter, { x, y, high }, { x, y, base });
				g_heights[a_index] = f < 1.0f ? high - f * kWallSearch : base + kWallFallback;
				g_wall[a_index] = true;
				return;
			}

			// Le sol sous ce point, en partant d'un peu au-dessus du sol du parent.
			const float top = base + kStepUp;
			const float f = RayFraction(a_world, a_filter, { x, y, top }, { x, y, base - kRayBelow });
			g_heights[a_index] = f < 1.0f ? top - f * (kStepUp + kRayBelow) : base;  // rien : on garde la hauteur du parent
			g_wall[a_index] = false;
		}

		void Publish()
		{
			auto* header = Header();
			const auto count = static_cast<std::size_t>(g_side) * g_side;
			header->sequence = g_published * 2 + 1;  // impair : ecriture en cours
			std::atomic_thread_fence(std::memory_order_release);
			header->side = static_cast<std::uint32_t>(g_side);
			header->spacing = g_spacing;
			header->tileCells = kTileCells;
			header->originX = g_originX;
			header->originY = g_originY;
			std::memcpy(Heights(), g_heights.data(), count * sizeof(float));
			std::atomic_thread_fence(std::memory_order_release);
			++g_published;
			header->sequence = g_published * 2;  // pair : grille complete
			if (g_published == 1 || g_published % 200 == 0) {
				logger::info("Relief : grille {} envoyee a RoR2", g_published);
			}
		}
	}

	bool Enabled() { return g_enabled; }

	void LoadSettings()
	{
		const auto ini = std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini").string();
		g_enabled = ::GetPrivateProfileIntA("Relief", "Activer", 1, ini.c_str()) != 0;
		char text[32]{};
		::GetPrivateProfileStringA("Relief", "Pas", "2", text, sizeof(text), ini.c_str());
		g_spacing = std::clamp(static_cast<float>(std::atof(text)), 0.5f, 5.0f);
		const int size = ::GetPrivateProfileIntA("Relief", "Taille", 200, ini.c_str());
		// Assez de tuiles pour couvrir "Taille" autour du joueur, ou qu'il soit dans sa tuile.
		const float tileSize = kTileCells * g_spacing;
		int tiles = static_cast<int>(std::ceil((size + tileSize) / tileSize));
		while (tiles > 1 && tiles * kTileCells + 1 > static_cast<int>(bridge::kReliefMaxSide)) {
			--tiles;
		}
		g_side = tiles * kTileCells + 1;
		g_pointsPerFrame = std::clamp(static_cast<int>(::GetPrivateProfileIntA("Relief", "PointsParImage", 400, ini.c_str())), 20, 5000);
		g_passStarted = false;
		logger::info("Relief : {} ({} x {} points, tous les {:.1f} m, soit {:.0f} m de cote ; {} points par image)",
			g_enabled ? "actif" : "desactive", g_side, g_side, g_spacing, (g_side - 1) * g_spacing, g_pointsPerFrame);
	}

	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked)
	{
		if (!g_enabled || !a_linked || !a_player || !OpenRelief()) {
			return;
		}
		auto* cell = a_player->GetParentCell();
		auto* world = cell ? cell->GetbhkWorld() : nullptr;
		if (!world) {
			return;
		}

		// La position du joueur, a chaque image.
		const auto feet = a_player->GetPosition();
		auto*      header = Header();
		header->magic = bridge::kReliefMagic;
		header->playerX = feet.x / kUnitsPerMeter;
		header->playerY = feet.y / kUnitsPerMeter;
		header->playerZ = feet.z / kUnitsPerMeter;
		std::atomic_thread_fence(std::memory_order_release);
		++header->playerSequence;

		// Le decor est recopie avec ses vraies formes (Decor.cpp) : plus besoin de la grille, seule la
		// position du joueur (ci-dessus) sert encore a RoR2 pour caler le decor.
		if (Decor::Enabled()) {
			return;
		}

		// Debut d'une mesure : grille calee sur le monde (pas sur le joueur), centree sur lui.
		if (!g_passStarted) {
			// Coin sud-ouest cale sur les tuiles (fixes dans le monde) : RoR2 ne refait que les tuiles qui changent.
			const float tileSize = kTileCells * g_spacing;
			const float extent = (g_side - 1) * g_spacing;
			g_originX = std::floor((header->playerX - extent * 0.5f) / tileSize + 0.5f) * tileSize;
			g_originY = std::floor((header->playerY - extent * 0.5f) / tileSize + 0.5f) * tileSize;
			const int startColumn = std::clamp(static_cast<int>(std::round((header->playerX - g_originX) / g_spacing)), 0, g_side - 1);
			const int startRow = std::clamp(static_cast<int>(std::round((header->playerY - g_originY) / g_spacing)), 0, g_side - 1);
			BuildOrder(startRow, startColumn);
			g_feetZ = header->playerZ;
			g_heights.assign(static_cast<std::size_t>(g_side) * g_side, g_feetZ);
			g_wall.assign(static_cast<std::size_t>(g_side) * g_side, false);
			g_next = 0;
			g_passStarted = true;
		}

		// Quelques dizaines de points par image. Les rayons ignorent le joueur (meme groupe de collision).
		RE::CFilter filter{};
		a_player->GetCollisionFilterInfo(filter);
		filter.SetCollisionLayer(RE::COL_LAYER::kLOS);
		for (int i = 0; i < g_pointsPerFrame && g_next < g_order.size(); ++i, ++g_next) {
			MeasurePoint(world, filter, g_order[g_next]);
		}

		if (g_next >= g_order.size()) {
			Publish();
			g_passStarted = false;
		}
	}
}
