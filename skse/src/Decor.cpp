#include "Decor.h"

#include "Bridge.h"

#include <cfloat>
#include <map>
#include <typeinfo>
#include <unordered_map>
#include <unordered_set>

namespace skyrimror2::Decor
{
	namespace
	{
		constexpr float kUnitsPerMeter = 70.0f;
		constexpr float kCellUnits = 4096.0f;  // une cellule exterieure de Skyrim
		constexpr auto  kScanEvery = std::chrono::milliseconds(500);

		struct Triangle
		{
			float v[9];
		};

		struct Object
		{
			std::vector<Triangle> triangles;
			std::uint32_t         seenIn = 0;
		};

		enum class Kind
		{
			kTriangle,
			kBox,
			kCapsule,
			kSphere,
			kConvexVertices,
			kContainer,
			kOther
		};

		// Ou est l'objet : la transformation de son corps physique (unites Havok), plus une echelle.
		struct Placement
		{
			float c0[3];  // colonnes de la rotation
			float c1[3];
			float c2[3];
			float t[3];   // position
			float scale;  // echelle des sommets (objets agrandis/reduits dans l'editeur)
			float toMeters;
		};

		bool          g_enabled = true;
		float         g_radius = 120.0f;  // metres
		// Les tout petits details (cailloux, debris...) : moins haut que ca et moins large que ca, ils ne sont pas
		// envoyes a RoR2 (le survivant butait dessus et decollait). Reglable dans [Sol] du .ini.
		float         g_detailHeight = 0.10f;  // m
		float         g_detailWidth = 1.0f;    // m
		std::uint32_t g_detailsSkipped = 0;
		std::uint32_t g_budget = 60000;   // triangles lus au plus par passage (pour ne pas figer le jeu)

		HANDLE        g_mapping = nullptr;
		std::uint8_t* g_view = nullptr;

		std::unordered_map<std::uint64_t, Object> g_objects;
		std::uint32_t                              g_pass = 0;
		bool                                       g_dirty = false;
		std::uint32_t                              g_published = 0;
		std::uint32_t                              g_heartbeat = 0;
		std::chrono::steady_clock::time_point      g_nextScan{};

		std::unordered_map<const void*, Kind> g_kinds;      // type de forme, d'apres sa table virtuelle
		std::unordered_map<const void*, bool> g_rigid;      // le corps est-il un vrai corps rigide ?
		std::map<std::string, int>            g_unknown;    // formes qu'on ne sait pas lire (journal)
		std::uint32_t                         g_rejected = 0;

		bool Open()
		{
			if (g_view) {
				return true;
			}
			g_mapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kDecorMapSize, bridge::kDecorMapName);
			if (!g_mapping) {
				return false;
			}
			g_view = static_cast<std::uint8_t*>(::MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, bridge::kDecorMapSize));
			if (g_view) {
				logger::info("Decor : pont ouvert ({} objets, {} triangles au plus)", bridge::kDecorMaxObjects, bridge::kDecorMaxTriangles);
			}
			return g_view != nullptr;
		}

		const void* VTable(const void* a_object) { return *reinterpret_cast<const void* const*>(a_object); }

		Kind Classify(const RE::hkpShape* a_shape)
		{
			const void* vtable = VTable(a_shape);
			if (auto it = g_kinds.find(vtable); it != g_kinds.end()) {
				return it->second;
			}
			const std::string name = typeid(*a_shape).name();
			Kind              kind = Kind::kOther;
			// hkpTriangleShape, et sa variante de Bethesda hkNormalTriangleShape (meme disposition des sommets,
			// plus une normale) : sans elle, des morceaux de sol manquaient dans RoR2 (trous ou l'on tombait).
			if (name.find("hkpTriangleShape") != std::string::npos || name.find("hkNormalTriangleShape") != std::string::npos) {
				kind = Kind::kTriangle;
			} else if (name.find("hkpBoxShape") != std::string::npos) {
				kind = Kind::kBox;
			} else if (name.find("hkpCapsuleShape") != std::string::npos) {
				kind = Kind::kCapsule;
			} else if (name.find("hkpSphereShape") != std::string::npos) {
				kind = Kind::kSphere;
			} else if (name.find("hkpConvexVerticesShape") != std::string::npos) {
				kind = Kind::kConvexVertices;
			} else if (a_shape->GetContainer()) {
				kind = Kind::kContainer;  // MOPP, liste, maillage compresse... : on lit leurs enfants
			}
			if (kind == Kind::kOther && g_unknown[name]++ == 0) {
				logger::info("Decor : forme pas encore recopiee : {}", name);
			}
			g_kinds[vtable] = kind;
			return kind;
		}

		const float* Vec(const RE::hkpShape* a_shape, std::size_t a_offset)
		{
			return reinterpret_cast<const float*>(reinterpret_cast<const std::uint8_t*>(a_shape) + a_offset);
		}

		float Float(const RE::hkpShape* a_shape, std::size_t a_offset) { return *Vec(a_shape, a_offset); }

		bool Sane(float a_value) { return std::isfinite(a_value) && std::abs(a_value) < 100000.0f; }

		// Un point de la forme (unites Havok, repere de l'objet) -> monde de Skyrim, en metres.
		void ToWorld(const Placement& a_at, const float* a_local, float* a_out)
		{
			const float x = a_local[0] * a_at.scale;
			const float y = a_local[1] * a_at.scale;
			const float z = a_local[2] * a_at.scale;
			for (int i = 0; i < 3; ++i) {
				a_out[i] = (a_at.c0[i] * x + a_at.c1[i] * y + a_at.c2[i] * z + a_at.t[i]) * a_at.toMeters;
			}
		}

		void AddTriangle(const Placement& a_at, const float* a_a, const float* a_b, const float* a_c, std::vector<Triangle>& a_out)
		{
			for (int i = 0; i < 3; ++i) {
				if (!Sane(a_a[i]) || !Sane(a_b[i]) || !Sane(a_c[i])) {
					++g_rejected;
					return;
				}
			}
			Triangle triangle{};
			ToWorld(a_at, a_a, triangle.v);
			ToWorld(a_at, a_b, triangle.v + 3);
			ToWorld(a_at, a_c, triangle.v + 6);
			a_out.push_back(triangle);
		}

		// Une boite (centre et demi-tailles, repere de l'objet) : 12 triangles.
		void AddBox(const Placement& a_at, const float* a_center, const float* a_half, std::vector<Triangle>& a_out)
		{
			for (int i = 0; i < 3; ++i) {
				if (!Sane(a_center[i]) || !Sane(a_half[i]) || a_half[i] < 0.0f) {
					++g_rejected;
					return;
				}
			}
			float corner[8][3];
			for (int k = 0; k < 8; ++k) {
				corner[k][0] = a_center[0] + ((k & 1) ? a_half[0] : -a_half[0]);
				corner[k][1] = a_center[1] + ((k & 2) ? a_half[1] : -a_half[1]);
				corner[k][2] = a_center[2] + ((k & 4) ? a_half[2] : -a_half[2]);
			}
			static constexpr int faces[6][4] = { { 0, 2, 3, 1 }, { 4, 5, 7, 6 }, { 0, 1, 5, 4 }, { 2, 6, 7, 3 }, { 0, 4, 6, 2 }, { 1, 3, 7, 5 } };
			for (const auto& face : faces) {
				AddTriangle(a_at, corner[face[0]], corner[face[1]], corner[face[2]], a_out);
				AddTriangle(a_at, corner[face[0]], corner[face[2]], corner[face[3]], a_out);
			}
		}

		// Convertit une forme (et ses enfants) en triangles.
		void Emit(const RE::hkpShape* a_shape, const Placement& a_at, std::vector<Triangle>& a_out, int a_depth)
		{
			if (!a_shape || a_depth > 6 || a_out.size() > 400000) {
				return;
			}
			// Formes convexes : rayon (epaisseur) a 0x20 ; sommets/demi-tailles a partir de 0x30.
			switch (Classify(a_shape)) {
			case Kind::kTriangle:
				AddTriangle(a_at, Vec(a_shape, 0x30), Vec(a_shape, 0x40), Vec(a_shape, 0x50), a_out);
				return;
			case Kind::kBox:
				{
					const float  radius = Float(a_shape, 0x20);
					const float* half = Vec(a_shape, 0x30);
					const float  center[3]{ 0.0f, 0.0f, 0.0f };
					const float  grown[3]{ half[0] + radius, half[1] + radius, half[2] + radius };
					AddBox(a_at, center, grown, a_out);
					return;
				}
			case Kind::kCapsule:
				{
					const float  radius = Float(a_shape, 0x20);
					const float* a = Vec(a_shape, 0x30);
					const float* b = Vec(a_shape, 0x40);
					float        center[3];
					float        half[3];
					for (int i = 0; i < 3; ++i) {
						center[i] = (a[i] + b[i]) * 0.5f;
						half[i] = std::abs(a[i] - b[i]) * 0.5f + radius;
					}
					AddBox(a_at, center, half, a_out);
					return;
				}
			case Kind::kSphere:
				{
					const float radius = Float(a_shape, 0x20);
					const float center[3]{ 0.0f, 0.0f, 0.0f };
					const float half[3]{ radius, radius, radius };
					AddBox(a_at, center, half, a_out);
					return;
				}
			case Kind::kConvexVertices:
				{
					// Sa boite englobante (demi-tailles puis centre) : assez pour un rocher ou une caisse.
					AddBox(a_at, Vec(a_shape, 0x40), Vec(a_shape, 0x30), a_out);
					return;
				}
			case Kind::kContainer:
				{
					const RE::hkpShapeContainer* container = a_shape->GetContainer();
					if (!container) {
						return;
					}
					alignas(16) std::uint8_t buffer[512];
					constexpr auto           kInvalid = static_cast<RE::hkpShapeKey>(0xFFFFFFFF);
					int                      guard = 0;
					for (RE::hkpShapeKey key = container->GetFirstKey(); key != kInvalid && guard < 500000; key = container->GetNextKey(key), ++guard) {
						const RE::hkpShape* child = container->GetChildShape(key, *reinterpret_cast<RE::hkpShapeBuffer*>(buffer));
						Emit(child, a_at, a_out, a_depth + 1);
					}
					return;
				}
			default:
				return;
			}
		}

		bool Wanted(RE::COL_LAYER a_layer)
		{
			switch (a_layer) {
			case RE::COL_LAYER::kStatic:
			case RE::COL_LAYER::kAnimStatic:
			case RE::COL_LAYER::kTransparent:
			case RE::COL_LAYER::kTrees:
			case RE::COL_LAYER::kProps:
			case RE::COL_LAYER::kTerrain:
			case RE::COL_LAYER::kGround:
				return true;
			default:
				return false;
			}
		}

		struct Scan
		{
			std::unordered_set<std::uint64_t> seen;
			std::uint32_t                     read = 0;      // triangles lus pendant ce passage
			std::uint32_t                     waiting = 0;   // objets remis au passage suivant (budget)
			std::uint32_t                     added = 0;
		};

		// Vrai pour un tout petit objet (caillou, debris) : plus bas que g_detailHeight et plus etroit que g_detailWidth.
		bool IsTinyDetail(const std::vector<Triangle>& a_triangles)
		{
			if (a_triangles.empty() || g_detailHeight <= 0.0f) {
				return false;
			}
			float low[3]{ FLT_MAX, FLT_MAX, FLT_MAX };
			float high[3]{ -FLT_MAX, -FLT_MAX, -FLT_MAX };
			for (const auto& triangle : a_triangles) {
				for (int k = 0; k < 9; ++k) {
					low[k % 3] = (std::min)(low[k % 3], triangle.v[k]);
					high[k % 3] = (std::max)(high[k % 3], triangle.v[k]);
				}
			}
			return high[2] - low[2] < g_detailHeight && high[0] - low[0] < g_detailWidth && high[1] - low[1] < g_detailWidth;
		}

		void AddBody(RE::bhkNiCollisionObject* a_collision, Scan& a_scan)
		{
			auto* body = a_collision ? a_collision->body.get() : nullptr;
			auto* object = body ? static_cast<RE::hkpWorldObject*>(body->referencedObject.get()) : nullptr;
			if (!object) {
				return;
			}
			// Seulement les vrais corps (pas les zones de detection ni les "fantomes").
			const void* vtable = VTable(object);
			auto        rigid = g_rigid.find(vtable);
			if (rigid == g_rigid.end()) {
				const std::string name = typeid(*object).name();
				rigid = g_rigid.emplace(vtable, name.find("RigidBody") != std::string::npos).first;
			}
			if (!rigid->second) {
				return;
			}
			const RE::COL_LAYER layer = object->collidable.GetCollisionLayer();
			if (!Wanted(layer)) {
				return;
			}
			const RE::hkpShape* shape = object->GetShape();
			const auto*         motion = static_cast<const float*>(object->collidable.motion);
			if (!shape || !motion) {
				return;
			}

			// La transformation du corps (rotation en colonnes, puis position), en unites Havok.
			Placement at{};
			for (int i = 0; i < 3; ++i) {
				at.c0[i] = motion[0 + i];
				at.c1[i] = motion[4 + i];
				at.c2[i] = motion[8 + i];
				at.t[i] = motion[12 + i];
			}
			at.scale = 1.0f;
			at.toMeters = 1.0f / (RE::bhkWorld::GetWorldScale() * kUnitsPerMeter);
			// Les maillages "agrandis" de Bethesda gardent leurs sommets d'origine : on applique l'echelle de l'objet.
			if (std::string_view(typeid(*shape).name()).find("Scaled") != std::string_view::npos && a_collision->sceneObject) {
				at.scale = a_collision->sceneObject->world.scale;
			}
			for (int i = 0; i < 3; ++i) {
				if (!Sane(at.t[i])) {
					return;
				}
			}

			// Identite : le corps, sa position ET son orientation (un objet qui bouge ou qui tourne devient un "nouvel"
			// objet). Une porte tourne sur ses gonds sans changer de position : sans l'orientation, RoR2 la gardait
			// fermee, et les allies buttaient dessus.
			std::uint64_t id = reinterpret_cast<std::uintptr_t>(object);
			for (int i = 0; i < 3; ++i) {
				id = id * 1099511628211ull ^ static_cast<std::uint64_t>(static_cast<std::int64_t>(std::lround(at.t[i] * 20.0f)));
			}
			for (int i = 0; i < 3; ++i) {
				id = id * 1099511628211ull ^ static_cast<std::uint64_t>(static_cast<std::int64_t>(std::lround(at.c0[i] * 50.0f)));
				id = id * 1099511628211ull ^ static_cast<std::uint64_t>(static_cast<std::int64_t>(std::lround(at.c1[i] * 50.0f)));
			}
			if (id == 0) {
				id = 1;
			}
			if (!a_scan.seen.insert(id).second) {
				return;
			}
			if (auto it = g_objects.find(id); it != g_objects.end()) {
				it->second.seenIn = g_pass;
				return;
			}
			if (a_scan.read >= g_budget) {
				++a_scan.waiting;  // lu au prochain passage
				a_scan.seen.erase(id);
				return;
			}
			Object fresh;
			fresh.seenIn = g_pass;
			Emit(shape, at, fresh.triangles, 0);
			a_scan.read += static_cast<std::uint32_t>(fresh.triangles.size());
			if (IsTinyDetail(fresh.triangles)) {
				fresh.triangles.clear();  // garde en memoire (pas relu a chaque passage), mais rien n'est envoye
				if (++g_detailsSkipped <= 3 || g_detailsSkipped % 200 == 0) {
					logger::info("Decor : petit detail ignore (moins de {:.0f} cm de haut ; {} en tout)", g_detailHeight * 100.0f, g_detailsSkipped);
				}
			}
			g_objects.emplace(id, std::move(fresh));
			++a_scan.added;
			g_dirty = true;
		}

		// Le terrain d'une cellule exterieure (4096 unites de cote) : sa hauteur exacte tous les 128 unites,
		// demandee a Skyrim lui-meme (la hauteur du sol naturel a cet endroit). Une cellule pas encore chargee
		// ne repond pas : on reessaiera au passage suivant.
		std::uint32_t g_landCells = 0;
		bool          g_landChecked = false;

		void AddLandCell(RE::TES* a_tes, int a_cellX, int a_cellY, const RE::NiPoint3& a_feet, Scan& a_scan)
		{
			const std::uint64_t id = 0x4C414E4400000000ull ^ (static_cast<std::uint64_t>(static_cast<std::uint32_t>(a_cellX)) << 20) ^
			                         static_cast<std::uint64_t>(static_cast<std::uint32_t>(a_cellY));
			if (!a_scan.seen.insert(id).second) {
				return;
			}
			if (auto it = g_objects.find(id); it != g_objects.end()) {
				it->second.seenIn = g_pass;
				return;
			}
			constexpr int   kSide = 33;
			constexpr float kStep = 128.0f;
			const float     x0 = a_cellX * kCellUnits;
			const float     y0 = a_cellY * kCellUnits;
			float           heights[kSide][kSide];
			bool            known[kSide][kSide];
			int             found = 0;
			for (int r = 0; r < kSide; ++r) {
				for (int c = 0; c < kSide; ++c) {
					// Un peu a l'interieur pour les bords, pour rester dans la bonne cellule.
					const float x = std::clamp(x0 + c * kStep, x0 + 1.0f, x0 + kCellUnits - 1.0f);
					const float y = std::clamp(y0 + r * kStep, y0 + 1.0f, y0 + kCellUnits - 1.0f);
					float       height = 0.0f;
					known[r][c] = a_tes->GetLandHeight(RE::NiPoint3{ x, y, a_feet.z }, height) && Sane(height);
					heights[r][c] = height;
					found += known[r][c] ? 1 : 0;
				}
			}
			if (found < kSide * kSide / 2) {
				a_scan.seen.erase(id);  // pas encore charge
				return;
			}

			Object fresh;
			fresh.seenIn = g_pass;
			fresh.triangles.reserve(32 * 32 * 2);
			const auto point = [&](int c, int r, float* out) {
				out[0] = (x0 + c * kStep) / kUnitsPerMeter;
				out[1] = (y0 + r * kStep) / kUnitsPerMeter;
				out[2] = heights[r][c] / kUnitsPerMeter;
			};
			for (int r = 0; r < kSide - 1; ++r) {
				for (int c = 0; c < kSide - 1; ++c) {
					if (!known[r][c] || !known[r][c + 1] || !known[r + 1][c] || !known[r + 1][c + 1]) {
						continue;
					}
					float sw[3], se[3], nw[3], ne[3];
					point(c, r, sw);
					point(c + 1, r, se);
					point(c, r + 1, nw);
					point(c + 1, r + 1, ne);
					Triangle a{};
					Triangle b{};
					std::memcpy(a.v, sw, 12); std::memcpy(a.v + 3, se, 12); std::memcpy(a.v + 6, ne, 12);
					std::memcpy(b.v, sw, 12); std::memcpy(b.v + 3, ne, 12); std::memcpy(b.v + 6, nw, 12);
					fresh.triangles.push_back(a);
					fresh.triangles.push_back(b);
				}
			}

			// Verification (une fois) : le terrain sous le joueur doit etre a la hauteur de ses pieds.
			if (!g_landChecked && a_feet.x >= x0 && a_feet.x < x0 + kCellUnits && a_feet.y >= y0 && a_feet.y < y0 + kCellUnits) {
				g_landChecked = true;
				const int c = std::clamp(static_cast<int>(std::round((a_feet.x - x0) / kStep)), 0, kSide - 1);
				const int r = std::clamp(static_cast<int>(std::round((a_feet.y - y0) / kStep)), 0, kSide - 1);
				logger::info("Decor : sol naturel sous le joueur a {:.1f} m, pieds a {:.1f} m (doit etre proche, sauf sur un batiment)",
					heights[r][c] / kUnitsPerMeter, a_feet.z / kUnitsPerMeter);
			}

			++g_landCells;
			if (g_landCells <= 3 || g_landCells % 50 == 0) {
				logger::info("Decor : terrain de la cellule ({}, {}) lu ({} triangles ; {} cellules en tout)", a_cellX, a_cellY,
					fresh.triangles.size(), g_landCells);
			}
			a_scan.read += static_cast<std::uint32_t>(fresh.triangles.size());
			g_objects.emplace(id, std::move(fresh));
			++a_scan.added;
			g_dirty = true;
		}

		void Collect(RE::NiAVObject* a_root, Scan& a_scan)
		{
			if (!a_root) {
				return;
			}
			RE::BSVisit::TraverseScenegraphCollision(a_root, [&](RE::bhkNiCollisionObject* a_collision) {
				AddBody(a_collision, a_scan);
				return RE::BSVisit::BSVisitControl::kContinue;
			});
		}

		void Publish()
		{
			auto*         header = reinterpret_cast<std::uint32_t*>(g_view);
			auto*         entries = g_view + bridge::kDecorHeaderSize;
			auto*         triangles = reinterpret_cast<Triangle*>(g_view + bridge::kDecorTrianglesOffset);
			std::uint32_t objects = 0;
			std::uint32_t count = 0;
			bool          full = false;

			header[1] = g_published * 2 + 1;  // impair : ecriture en cours
			std::atomic_thread_fence(std::memory_order_release);
			for (const auto& [id, object] : g_objects) {
				const auto size = static_cast<std::uint32_t>(object.triangles.size());
				if (size == 0) {
					continue;
				}
				if (objects >= bridge::kDecorMaxObjects || count + size > bridge::kDecorMaxTriangles) {
					full = true;
					continue;
				}
				std::memcpy(entries + objects * bridge::kDecorEntrySize, &id, 8);
				std::memcpy(entries + objects * bridge::kDecorEntrySize + 8, &count, 4);
				std::memcpy(entries + objects * bridge::kDecorEntrySize + 12, &size, 4);
				std::memcpy(triangles + count, object.triangles.data(), size * sizeof(Triangle));
				count += size;
				++objects;
			}
			header[2] = objects;
			header[3] = count;
			header[0] = bridge::kDecorMagic;
			std::atomic_thread_fence(std::memory_order_release);
			++g_published;
			header[1] = g_published * 2;  // pair : complet

			if (g_published <= 3 || g_published % 100 == 0 || full) {
				logger::info("Decor : {} objets, {} triangles envoyes a RoR2{}{}", objects, count,
					full ? " (pont plein : decor trop charge, le reste est ignore)" : "",
					g_rejected ? fmt::format(" ; {} morceaux illisibles ignores", g_rejected) : "");
			}
		}
	}

	bool Enabled() { return g_enabled; }

	void LoadSettings()
	{
		const auto ini = std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini").string();
		g_enabled = ::GetPrivateProfileIntA("Decor", "Activer", 1, ini.c_str()) != 0;
		g_radius = static_cast<float>(std::clamp(static_cast<int>(::GetPrivateProfileIntA("Decor", "Rayon", 120, ini.c_str())), 30, 300));
		g_budget = static_cast<std::uint32_t>(std::clamp(static_cast<int>(::GetPrivateProfileIntA("Decor", "TrianglesParPassage", 60000, ini.c_str())), 5000, 1000000));
		g_detailHeight = static_cast<float>(std::clamp(static_cast<int>(::GetPrivateProfileIntA("Sol", "PetitsDetails", 10, ini.c_str())), 0, 50)) / 100.0f;
		g_detailWidth = static_cast<float>(std::clamp(static_cast<int>(::GetPrivateProfileIntA("Sol", "PetitsDetailsLargeur", 100, ini.c_str())), 10, 300)) / 100.0f;
		logger::info("Sol : petits details ignores sous {:.0f} cm de haut et {:.0f} cm de large", g_detailHeight * 100.0f, g_detailWidth * 100.0f);
		logger::info("Decor : {} (rayon {:.0f} m, {} triangles lus au plus par passage)", g_enabled ? "vraies formes de Skyrim" : "desactive (relief en grille)",
			g_radius, g_budget);
	}

	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked)
	{
		if (!g_enabled || !a_linked || !a_player || !Open()) {
			return;
		}
		const auto now = std::chrono::steady_clock::now();
		if (now < g_nextScan) {
			return;
		}
		g_nextScan = now + kScanEvery;
		auto* tes = RE::TES::GetSingleton();
		if (!tes) {
			return;
		}

		++g_pass;
		Scan scan;
		const auto feet = a_player->GetPosition();
		const float radius = g_radius * kUnitsPerMeter;

		// Les objets (maisons, arches, rochers, arbres...) autour du joueur.
		tes->ForEachReferenceInRange(a_player, radius, [&](RE::TESObjectREFR* a_ref) {
			if (a_ref && !a_ref->IsDisabled() && !a_ref->As<RE::Actor>()) {
				Collect(a_ref->Get3D(), scan);
			}
			return RE::BSContainer::ForEachResult::kContinue;
		});

		// Le sol naturel : le terrain des cellules exterieures proches (pas en interieur).
		auto* here = a_player->GetParentCell();
		if (here && here->IsExteriorCell()) {
			const int reach = static_cast<int>(std::ceil(radius / kCellUnits));
			const int cellX = static_cast<int>(std::floor(feet.x / kCellUnits));
			const int cellY = static_cast<int>(std::floor(feet.y / kCellUnits));
			for (int dx = -reach; dx <= reach; ++dx) {
				for (int dy = -reach; dy <= reach; ++dy) {
					AddLandCell(tes, cellX + dx, cellY + dy, feet, scan);
				}
			}
		}

		// Les objets qui ne sont plus autour du joueur sont oublies.
		for (auto it = g_objects.begin(); it != g_objects.end();) {
			if (it->second.seenIn != g_pass && !scan.seen.contains(it->first)) {
				it = g_objects.erase(it);
				g_dirty = true;
			} else {
				++it;
			}
		}

		if (g_dirty) {
			g_dirty = false;
			Publish();
		}
		auto* header = reinterpret_cast<std::uint32_t*>(g_view);
		header[0] = bridge::kDecorMagic;
		header[4] = ++g_heartbeat;  // "Skyrim est la" : RoR2 garde le decor tant que ca bouge
	}
}
