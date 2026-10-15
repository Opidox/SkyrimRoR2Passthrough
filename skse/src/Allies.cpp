#include "Allies.h"
#include "ThirdPerson.h"

#include "Bridge.h"

#include <cmath>
#include <fstream>
#include <unordered_map>
#include <unordered_set>

namespace skyrimror2::Allies
{
	namespace
	{
		using Clock = std::chrono::steady_clock;
		constexpr float kUnitsPerMeter = 70.0f;

		HANDLE              g_mapping = nullptr;
		const std::uint8_t* g_view = nullptr;
		std::uint32_t       g_lastSequence = 0;

		struct Shown
		{
			RE::NiPointer<RE::NiNode> node;
			RE::NiNode*               parent = nullptr;
			std::string               model;
			bool                      seen = false;
		};
		std::unordered_map<std::uint32_t, Shown>             g_shown;      // par allie (id RoR2)
		std::unordered_map<std::string, int>                 g_installed;  // modele -> version recopiee
		std::unordered_map<std::string, RE::NiPointer<RE::NiNode>> g_loaded;  // modele charge (on en fait des copies)
		std::unordered_set<std::string>                      g_broken;
		bool                                                 g_playerHidden = false;  // personnage Skyrim cache (survivant en 3D)

		bool Open()
		{
			if (g_view) {
				return true;
			}
			g_mapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kAllyMapSize, bridge::kAllyMapName);
			if (!g_mapping) {
				return false;
			}
			g_view = static_cast<const std::uint8_t*>(::MapViewOfFile(g_mapping, FILE_MAP_READ, 0, 0, bridge::kAllyMapSize));
			return g_view != nullptr;
		}

		std::filesystem::path ExportFolder()
		{
			char folder[MAX_PATH]{};
			if (::GetEnvironmentVariableA("LOCALAPPDATA", folder, MAX_PATH) == 0) {
				return {};
			}
			return std::filesystem::path(folder) / "SkyrimRoR2" / "modeles";
		}

		// Recopie le modele et ses textures dans Data (sous MO2, ils arrivent dans "Overwrite").
		bool Install(const std::string& a_model, int a_version)
		{
			if (auto it = g_installed.find(a_model); it != g_installed.end() && it->second == a_version) {
				return true;
			}
			const auto from = ExportFolder();
			const auto meshes = std::filesystem::absolute("Data/meshes/SkyrimRoR2");
			const auto textures = std::filesystem::absolute("Data/textures/SkyrimRoR2");
			std::error_code error;
			std::filesystem::create_directories(meshes, error);
			std::filesystem::create_directories(textures, error);
			// Un modele qui n'existait pas au lancement de Skyrim : Skyrim le charge parfois a moitie et plante
			// (Capitaine, 0.55). On le laisse pour le lancement suivant (PrepareFiles le recopiera).
			// Pareil pour un modele dont seule la place reservee (fichier vide) existait : on ne la remplace pas en
			// pleine partie. Un vrai modele deja la est garde tel quel (PrepareFiles le mettra a jour au lancement).
			const bool isSprite = a_model.rfind("S_", 0) == 0;
			if (!isSprite) {
				const auto target = meshes / (a_model + ".nif");
				std::error_code e;
				const bool exists = std::filesystem::exists(target, e);
				const auto size = exists ? std::filesystem::file_size(target, e) : 0;
				if (!exists || size < 1024) {
					logger::info("Allies : modele {} converti pendant la partie : affiche au prochain lancement de Skyrim", a_model);
					return false;
				}
				g_installed[a_model] = a_version;
				return true;
			}
			// Meme taille : c'est le meme fichier, on n'y touche pas (Skyrim l'utilise peut-etre deja).
			const auto sameSize = [&](const std::filesystem::path& a_source, const std::filesystem::path& a_target) {
				std::error_code e;
				return std::filesystem::exists(a_target, e) && std::filesystem::exists(a_source, e) &&
				       std::filesystem::file_size(a_source, e) == std::filesystem::file_size(a_target, e);
			};
			const auto copySafely = [&](const std::filesystem::path& a_source, const std::filesystem::path& a_target) {
				if (sameSize(a_source, a_target)) {
					return true;
				}
				// Copie a cote puis renommage : Skyrim ne voit jamais un fichier a moitie ecrit.
				auto temporary = a_target;
				temporary += ".tmp";
				std::error_code e;
				if (!std::filesystem::copy_file(a_source, temporary, std::filesystem::copy_options::overwrite_existing, e)) {
					return false;
				}
				std::filesystem::rename(temporary, a_target, e);
				if (e) {
					std::filesystem::remove(temporary, e);
					return false;
				}
				return true;
			};
			if (!std::filesystem::exists(from / (a_model + ".nif"), error) || !copySafely(from / (a_model + ".nif"), meshes / (a_model + ".nif"))) {
				logger::warn("Allies : modele {} introuvable ou non recopie", a_model);
				return false;
			}
			int count = 0;
			// Les sprites ("S_1a2b3c4d_2_A") partagent la texture de leur niveau 0 : on recopie tout ce qui commence par "S_1a2b3c4d_".
			const std::string prefix = a_model.rfind("S_", 0) == 0 && a_model.find('_', 2) != std::string::npos ?
			                               a_model.substr(0, a_model.find('_', 2) + 1) :
			                               a_model + "_";
			for (const auto& entry : std::filesystem::directory_iterator(from, error)) {
				const auto name = entry.path().filename().string();
				if (entry.path().extension() == ".dds" && (name.rfind(prefix, 0) == 0 || name == "plat_n.dds")) {
					copySafely(entry.path(), textures / name);
					++count;
				}
			}
			g_installed[a_model] = a_version;
			g_loaded.erase(a_model);
			logger::info("Allies : modele {} recopie pour Skyrim ({} textures)", a_model, count);
			return true;
		}

		// Un modele vide (un seul noeud) : place reservee, remplacee plus tard par le vrai modele.
		std::vector<std::uint8_t> EmptyNif()
		{
			std::vector<std::uint8_t> out;
			const auto put = [&](const void* a_data, std::size_t a_size) {
				const auto* bytes = static_cast<const std::uint8_t*>(a_data);
				out.insert(out.end(), bytes, bytes + a_size);
			};
			const auto u32 = [&](std::uint32_t v) { put(&v, 4); };
			const auto i32 = [&](std::int32_t v) { put(&v, 4); };
			const auto f32 = [&](float v) { put(&v, 4); };
			const auto sized = [&](std::string_view s) { u32(static_cast<std::uint32_t>(s.size())); put(s.data(), s.size()); };
			const auto exportString = [&](std::string_view s) {
				const std::uint8_t length = static_cast<std::uint8_t>(s.size() + 1);
				put(&length, 1);
				put(s.data(), s.size());
				const std::uint8_t zero = 0;
				put(&zero, 1);
			};
			const std::string_view title = "Gamebryo File Format, Version 20.2.0.7\n";
			put(title.data(), title.size());
			u32(0x14020007);
			const std::uint8_t little = 1;
			put(&little, 1);
			u32(12);   // version utilisateur
			u32(1);    // un bloc
			u32(100);  // Skyrim Special Edition
			exportString("SkyrimRoR2");
			exportString("");
			exportString("");
			const std::uint16_t types = 1;
			put(&types, 2);
			sized("NiNode");
			const std::uint16_t typeIndex = 0;
			put(&typeIndex, 2);
			u32(4 + 4 + 4 + 4 + 12 + 36 + 4 + 4 + 4 + 4);  // taille du bloc
			u32(1);    // un nom
			u32(10);
			sized("SkyrimRoR2");
			u32(0);    // groupes
			// Le NiNode
			i32(0);    // nom
			u32(0);    // donnees en plus
			i32(-1);   // controleur
			u32(14);   // drapeaux
			f32(0); f32(0); f32(0);
			f32(1); f32(0); f32(0); f32(0); f32(1); f32(0); f32(0); f32(0); f32(1);
			f32(1);    // echelle
			i32(-1);   // collision
			u32(0);    // enfants
			u32(0);    // effets
			u32(1);    // une racine
			i32(0);
			return out;
		}

		// Une petite texture grise (DDS 4 x 4) : place reservee.
		std::vector<std::uint8_t> EmptyDds()
		{
			std::vector<std::uint32_t> words = { 0x20534444, 124, 0x1 | 0x2 | 0x4 | 0x8 | 0x1000, 4, 4, 16, 0, 1 };
			for (int i = 0; i < 11; ++i) {
				words.push_back(0);
			}
			for (std::uint32_t w : { 32u, 0x41u, 0u, 32u, 0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u, 0x1000u, 0u, 0u, 0u, 0u }) {
				words.push_back(w);
			}
			for (int i = 0; i < 16; ++i) {
				words.push_back(0xFF808080);
			}
			std::vector<std::uint8_t> out(words.size() * 4);
			std::memcpy(out.data(), words.data(), out.size());
			return out;
		}

		void WriteFile(const std::filesystem::path& a_path, const std::vector<std::uint8_t>& a_bytes)
		{
			std::ofstream file(a_path, std::ios::binary);
			file.write(reinterpret_cast<const char*>(a_bytes.data()), static_cast<std::streamsize>(a_bytes.size()));
		}

		RE::NiPointer<RE::NiNode> Load(const std::string& a_model)
		{
			if (auto it = g_loaded.find(a_model); it != g_loaded.end()) {
				return it->second;
			}
			RE::NiPointer<RE::NiNode>         node;
			RE::BSModelDB::DBTraits::ArgsType args{};
			std::string                       path;
			int                               result = 0;
			// Selon les versions, le chemin se donne a partir de "meshes" ou de "Data" : on essaie les deux.
			for (const char* prefix : { "SkyrimRoR2\\", "meshes\\SkyrimRoR2\\" }) {
				path = std::string(prefix) + a_model + ".nif";
				result = static_cast<int>(RE::BSModelDB::Demand(path.c_str(), node, args));
				if (node) {
					break;
				}
			}
			if (!node) {
				const auto file = std::filesystem::absolute("Data/meshes/SkyrimRoR2") / (a_model + ".nif");
				std::error_code error;
				const bool exists = std::filesystem::exists(file, error);
				logger::warn("Allies : modele {} illisible par Skyrim (code {}) ; fichier {} : {} ({} octets)", a_model, result,
					file.string(), exists ? "present" : "ABSENT", exists ? std::filesystem::file_size(file, error) : 0);
				g_broken.insert(a_model);
				return nullptr;
			}
			logger::info("Allies : modele {} charge", path);
			g_loaded[a_model] = node;
			return node;
		}

		void Remove(Shown& a_shown)
		{
			// On detache le modele de son parent ACTUEL (celui que le modele connait) : le noeud de la cellule
			// d'ou l'on vient peut deja etre detruit (sortie d'un donjon), et s'en servir faisait planter Skyrim.
			if (a_shown.node && a_shown.node->parent) {
				a_shown.node->parent->DetachChild(a_shown.node.get());
			}
			a_shown.node.reset();
			a_shown.parent = nullptr;
		}
	}

	// ---------------- Sprites : les vrais effets de RoR2 (particules, trainees, rayons) ----------------
	// RoR2 envoie a chaque image la liste des sprites (SpriteMirror.cs) : quelle texture d'effet, ou, quelle taille,
	// quelle forme. Chaque sprite est un carre de 1 m dans le plan X-Z ; on garde une reserve de copies par texture,
	// qu'on place et qu'on tourne vers la camera. Les copies en trop sont cachees.
	namespace
	{
		HANDLE              g_spriteMapping = nullptr;
		const std::uint8_t* g_spriteView = nullptr;
		bool                g_spritesLogged = false;

		struct SpritePool
		{
			std::vector<RE::NiPointer<RE::NiNode>> nodes;
			std::size_t                            used = 0;
			RE::NiNode*                            parent = nullptr;
		};
		std::unordered_map<std::string, SpritePool> g_spritePools;
		constexpr std::size_t                       kMaxPerSprite = 400;
		constexpr int                               kMaxNewPerFrame = 24;    // nouvelles copies par image
		constexpr std::size_t                       kMaxSprites = 3000;      // copies en tout
		std::size_t                                 g_spriteTotal = 0;

		bool OpenSprites()
		{
			if (g_spriteView) {
				return true;
			}
			g_spriteMapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kSpriteMapSize, bridge::kSpriteMapName);
			if (!g_spriteMapping) {
				return false;
			}
			g_spriteView = static_cast<const std::uint8_t*>(::MapViewOfFile(g_spriteMapping, FILE_MAP_READ, 0, 0, bridge::kSpriteMapSize));
			return g_spriteView != nullptr;
		}

		struct Vec
		{
			float x = 0.0f, y = 0.0f, z = 0.0f;
		};
		Vec   operator+(const Vec& a, const Vec& b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
		Vec   operator-(const Vec& a, const Vec& b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
		Vec   operator*(const Vec& a, float k) { return { a.x * k, a.y * k, a.z * k }; }
		Vec   Cross(const Vec& a, const Vec& b) { return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x }; }
		float Length(const Vec& a) { return std::sqrt(a.x * a.x + a.y * a.y + a.z * a.z); }
		Vec   Normalized(const Vec& a, const Vec& a_fallback)
		{
			const float length = Length(a);
			return length > 1e-5f ? a * (1.0f / length) : a_fallback;
		}

		void HidePool(SpritePool& a_pool, std::size_t a_from)
		{
			for (std::size_t i = a_from; i < a_pool.nodes.size(); ++i) {
				if (a_pool.nodes[i]) {
					a_pool.nodes[i]->CullNode(true);
				}
			}
		}

		void ClearPool(SpritePool& a_pool)
		{
			for (auto& node : a_pool.nodes) {
				if (node && node->parent) {
					node->parent->DetachChild(node.get());  // son parent actuel (l'ancien peut etre detruit)
				}
			}
			g_spriteTotal -= std::min(g_spriteTotal, a_pool.nodes.size());
			a_pool.nodes.clear();
			a_pool.used = 0;
			a_pool.parent = nullptr;
		}

		// ---- 0.73.5 (essai) : lasers avec l'apparence des sorts de Skyrim ----
		// RoR2 envoie les morceaux de laser en forme 5, avec une famille (octet 7) : 0 foudre, 1 feu, 2 givre, 3 drain rouge,
		// 4 soin. On prend le carre du sprite du laser (bien oriente, a la bonne taille) et on lui donne la matiere d'effet
		// (texture, couleurs, animation) d'un projectile de sort de Skyrim de cette famille.
		struct BeamLook
		{
			RE::NiPointer<RE::BSShaderProperty> shader;
			RE::NiPointer<RE::NiAlphaProperty>  alpha;
			std::string                   from;
		};
		std::array<BeamLook, 5> g_beams{};
		bool                    g_beamsSearched = false;

		void FindBeamLooks()
		{
			g_beamsSearched = true;
			auto* data = RE::TESDataHandler::GetSingleton();
			if (!data) {
				return;
			}
			const auto lower = [](std::string a_text) {
				std::ranges::transform(a_text, a_text.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
				return a_text;
			};
			const auto has = [](const std::string& a_text, std::initializer_list<const char*> a_words) {
				for (const char* word : a_words) {
					if (a_text.find(word) != std::string::npos) {
						return true;
					}
				}
				return false;
			};
			// Deux passages : d'abord les modeles qui sont de vrais rayons ("beam"), puis n'importe quel projectile.
			for (int pass = 0; pass < 2; ++pass) {
				for (auto* projectile : data->GetFormArray<RE::BGSProjectile>()) {
					const char* path = projectile ? projectile->GetModel() : nullptr;
					if (!path || !*path) {
						continue;
					}
					const std::string model = lower(path);
					if (pass == 0 && !has(model, { "beam" })) {
						continue;
					}
					int family = -1;
					if (has(model, { "shock", "lightning", "spark" })) {
						family = 0;
					} else if (has(model, { "fire", "flame" })) {
						family = 1;
					} else if (has(model, { "frost", "ice" })) {
						family = 2;
					} else if (has(model, { "drain", "absorb", "vampir", "blood" })) {
						family = 3;
					} else if (has(model, { "heal", "restor" })) {
						family = 4;
					}
					if (family < 0 || g_beams[family].shader) {
						continue;
					}
					RE::NiPointer<RE::NiNode>         node;
					RE::BSModelDB::DBTraits::ArgsType args{};
					RE::BSModelDB::Demand(path, node, args);
					if (!node) {
						continue;
					}
					RE::BSVisit::TraverseScenegraphGeometries(node.get(), [&](RE::BSGeometry* a_geometry) {
						auto& runtime = a_geometry->GetGeometryRuntimeData();
						auto* effect = runtime.shaderProperty.get();
						if (effect && netimmerse_cast<RE::BSEffectShaderProperty*>(effect)) {
							g_beams[family].shader = runtime.shaderProperty;
							g_beams[family].alpha = runtime.alphaProperty;
							g_beams[family].from = path;
							return RE::BSVisit::BSVisitControl::kStop;
						}
						return RE::BSVisit::BSVisitControl::kContinue;
					});
				}
			}
			const char* names[5]{ "foudre", "feu", "givre", "drain", "soin" };
			for (int i = 0; i < 5; ++i) {
				logger::info("Lasers facon Skyrim : {} -> {}", names[i], g_beams[i].shader ? g_beams[i].from : std::string("(aucun, laser de RoR2 garde)"));
			}
		}

		// Donne a une copie de sprite la matiere du sort de Skyrim.
		void ApplyBeamLook(RE::NiAVObject* a_node, int a_family)
		{
			const auto& look = g_beams[a_family];
			RE::BSVisit::TraverseScenegraphGeometries(a_node, [&](RE::BSGeometry* a_geometry) {
				auto& runtime = a_geometry->GetGeometryRuntimeData();
				runtime.shaderProperty = look.shader;
				if (look.alpha) {
					runtime.alphaProperty = look.alpha;
				}
				return RE::BSVisit::BSVisitControl::kContinue;
			});
		}

		void PerFrameSprites(RE::PlayerCharacter* a_player, RE::NiNode* a_parent, bool a_linked)
		{
			if (!OpenSprites()) {
				return;
			}
			std::uint32_t magic = 0, count = 0, names = 0;
			std::memcpy(&magic, g_spriteView, 4);
			std::memcpy(&count, g_spriteView + 8, 4);
			std::memcpy(&names, g_spriteView + 12, 4);
			for (auto& [model, pool] : g_spritePools) {
				pool.used = 0;
			}
			if (!a_linked || magic != bridge::kSpriteMagic || !a_parent) {
				count = 0;
			}
			count = std::min(count, bridge::kSpriteMax);
			names = std::min(names, bridge::kSpriteNames);

			// La camera de Skyrim (les sprites lui font face).
			Vec eye{};
			if (auto* camera = RE::PlayerCamera::GetSingleton(); camera && camera->cameraRoot) {
				const auto& t = camera->cameraRoot->world.translate;
				eye = { t.x, t.y, t.z };
			} else {
				const auto p = a_player->GetPosition();
				eye = { p.x, p.y, p.z + 120.0f };
			}
			const RE::NiPoint3 origin = a_parent ? a_parent->world.translate : RE::NiPoint3{};
			auto*              playerCamera = RE::PlayerCamera::GetSingleton();
			const bool         firstPerson = playerCamera && playerCamera->IsInFirstPerson();

			int shown = 0;
			int created = 0;
			for (std::uint32_t i = 0; i < count; ++i) {
				const std::uint8_t* entry = g_spriteView + bridge::kSpriteEntriesOffset + i * bridge::kSpriteEntrySize;
				std::int32_t nameIndex = -1;
				std::memcpy(&nameIndex, entry, 4);
				if (nameIndex < 0 || static_cast<std::uint32_t>(nameIndex) >= names) {
					continue;
				}
				std::uint8_t       kind = entry[4];
				const std::uint8_t level = std::min<std::uint8_t>(entry[5], 3);
				char key[bridge::kSpriteNameSize]{};
				std::memcpy(key, g_spriteView + bridge::kSpriteNamesOffset + nameIndex * bridge::kSpriteNameSize, bridge::kSpriteNameSize - 1);
				const std::string model = std::string(key) + "_" + std::to_string(level) + "_A";
				if (key[0] == '\0' || g_broken.contains(model)) {
					continue;
				}
				// 0.73.5 : laser facon sort de Skyrim (sinon, comme un rayon normal).
				int beamFamily = -1;
				if (kind == 5) {
					if (!g_beamsSearched) {
						FindBeamLooks();
					}
					const int family = std::min<int>(entry[7], 4);
					if (g_beams[family].shader) {
						beamFamily = family;
					}
					kind = 4;
				}

				auto& pool = g_spritePools[beamFamily >= 0 ? model + "|sort" + std::to_string(beamFamily) : model];
				if (pool.parent != a_parent) {
					ClearPool(pool);
					pool.parent = a_parent;
				}
				float values[10];
				std::memcpy(values, entry + 8, sizeof(values));  // centre (3), largeur, hauteur, rotation, axe (3)
				bool finite = true;
				for (float v : values) {
					finite = finite && std::isfinite(v) && std::fabs(v) < 1.0e6f;
				}
				if (!finite) {
					continue;
				}
				if (pool.used >= pool.nodes.size()) {
					// Pas trop de nouvelles copies d'un coup (rafale du fusil du Capitaine...) ni au total.
					if (pool.nodes.size() >= kMaxPerSprite || created >= kMaxNewPerFrame || g_spriteTotal >= kMaxSprites) {
						continue;
					}
					++created;
					if (!Install(model, 1)) {
						g_broken.insert(model);
						continue;
					}
					auto original = Load(model);
					if (!original) {
						continue;
					}
					RE::NiPointer<RE::NiObject> copy;
					original->CreateDeepCopy(copy);
					auto* node = copy ? copy->AsNode() : nullptr;
					if (!node) {
						continue;
					}
					if (beamFamily >= 0) {
						// Note dans le journal AVANT (s'il y a un plantage, on saura que c'est ici et avec quel sort).
						static std::array<bool, 5> logged{};
						if (!logged[beamFamily]) {
							logged[beamFamily] = true;
							logger::info("Lasers facon Skyrim : premier rayon de la famille {} ({})", beamFamily, g_beams[beamFamily].from);
						}
						ApplyBeamLook(node, beamFamily);
					}
					pool.nodes.emplace_back(node);
					++g_spriteTotal;
					a_parent->AttachChild(node, true);
				}
				auto& node = pool.nodes[pool.used++];
				node->CullNode(false);

				// Orientation : X = largeur, Y = face visible, Z = hauteur du carre.
				const Vec center{ values[0] * kUnitsPerMeter, values[1] * kUnitsPerMeter, values[2] * kUnitsPerMeter };
				const float width = std::max(values[3], 0.001f);
				const float height = std::max(values[4], 0.001f);
				const float roll = values[5];
				const Vec   axis = Normalized({ values[6], values[7], values[8] }, { 0.0f, 0.0f, 1.0f });
				// Porte par le survivant (reacteurs, auras, depart du lance-flammes) : en premiere personne, on ne
				// dessine pas ce qui est colle a l'oeil (moins de 1,5 m), sinon ca boucherait la vue.
				if (entry[6] == 1 && firstPerson && Length(eye - center) < 1.5f * kUnitsPerMeter) {
					--pool.used;
					node->CullNode(true);
					continue;
				}
				const Vec   toEye = Normalized(eye - center, { 0.0f, 1.0f, 0.0f });
				const Vec   worldUp{ 0.0f, 0.0f, 1.0f };
				Vec right{}, face{}, up{};
				if (kind == 1) {  // etire le long de l'axe (trainees, rayons, particules etirees)
					up = axis;
					right = Normalized(Cross(up, toEye), Normalized(Cross(up, { 1.0f, 0.0f, 0.0f }), { 0.0f, 1.0f, 0.0f }));
					face = Cross(right, up);
				} else if (kind == 4) {  // rayon, trainee : la texture court dans la longueur (X = longueur, Z = epaisseur)
					right = axis;
					up = Normalized(Cross(axis, toEye), Normalized(Cross(axis, { 1.0f, 0.0f, 0.0f }), { 0.0f, 1.0f, 0.0f }));
					face = Cross(up, right);
				} else if (kind == 2) {  // a plat (cercles au sol)
					face = worldUp;
					right = { std::cos(roll), std::sin(roll), 0.0f };
					up = { -std::sin(roll), std::cos(roll), 0.0f };
				} else if (kind == 3) {  // debout, tourne seulement autour de la verticale
					up = worldUp;
					face = Normalized({ toEye.x, toEye.y, 0.0f }, { 0.0f, 1.0f, 0.0f });
					right = Cross(up, face);
				} else {  // face a la camera, avec sa rotation
					face = toEye;
					const Vec r0 = Normalized(Cross(worldUp, toEye), { 1.0f, 0.0f, 0.0f });
					const Vec u0 = Cross(toEye, r0);
					const float c = std::cos(roll), sn = std::sin(roll);
					right = r0 * c + u0 * sn;
					up = u0 * c - r0 * sn;
				}
				const float size = std::max(width, height);
				const Vec   columns[3] = { right * ((kind == 4 ? height : width) / size), face, up * ((kind == 4 ? width : height) / size) };
				auto&       local = node->local;
				for (int row = 0; row < 3; ++row) {
					const float* c0 = &columns[0].x;
					const float* c1 = &columns[1].x;
					const float* c2 = &columns[2].x;
					local.rotate.entry[row][0] = c0[row];
					local.rotate.entry[row][1] = c1[row];
					local.rotate.entry[row][2] = c2[row];
				}
				local.scale = size;
				local.translate = RE::NiPoint3(center.x - origin.x, center.y - origin.y, center.z - origin.z);
				RE::NiUpdateData update{};
				node->Update(update);
				++shown;
			}
			for (auto& [model, pool] : g_spritePools) {
				HidePool(pool, pool.used);
			}
			if (shown > 0 && !g_spritesLogged) {
				g_spritesLogged = true;
				logger::info("Sprites : premiers effets de RoR2 affiches ({} sprites)", shown);
			}
		}
	}

	void PrepareFiles()
	{
		const auto from = ExportFolder();
		const auto meshes = std::filesystem::absolute("Data/meshes/SkyrimRoR2");
		const auto textures = std::filesystem::absolute("Data/textures/SkyrimRoR2");
		std::error_code error;
		std::filesystem::create_directories(meshes, error);
		std::filesystem::create_directories(textures, error);

		// La liste des allies possibles, ecrite par RoR2 (corps.txt).
		std::ifstream list(from / "corps.txt");
		std::string   name;
		int           created = 0;
		int           total = 0;
		const auto    nif = EmptyNif();
		const auto    dds = EmptyDds();
		// Un fichier deja la est garde, SAUF si RoR2 en a converti une version differente depuis (fichier vide de
		// reserve, ou modele refait) : Skyrim ne relit pas un fichier change pendant la partie, il faut donc le
		// mettre a jour maintenant, au lancement.
		const auto    ensure = [&](const std::filesystem::path& a_target, const std::filesystem::path& a_source, const std::vector<std::uint8_t>& a_empty) {
			if (std::filesystem::exists(a_target, error)) {
				std::error_code sizeError;
				const bool hasSource = std::filesystem::exists(a_source, sizeError);
				const auto sourceSize = hasSource ? std::filesystem::file_size(a_source, sizeError) : 0;
				const auto targetSize = std::filesystem::file_size(a_target, sizeError);
				if (hasSource && !sizeError && sourceSize != targetSize &&
					std::filesystem::copy_file(a_source, a_target, std::filesystem::copy_options::overwrite_existing, error)) {
					++created;
				}
				return;
			}
			if (!std::filesystem::exists(a_source, error) ||
				!std::filesystem::copy_file(a_source, a_target, std::filesystem::copy_options::overwrite_existing, error)) {
				WriteFile(a_target, a_empty);
			}
			++created;
		};
		while (std::getline(list, name)) {
			while (!name.empty() && (name.back() == '\r' || name.back() == ' ')) {
				name.pop_back();
			}
			if (name.empty()) {
				continue;
			}
			++total;
			const std::string model = name + "_A";
			ensure(meshes / (model + ".nif"), from / (model + ".nif"), nif);
			if (name.rfind("S_", 0) == 0) {
				// Sprite : une seule texture, celle du niveau 0 ("S_1a2b3c4d_0_A_0.dds"), partagee par les 4 niveaux.
				if (name.size() > 2 && name.ends_with("_0")) {
					ensure(textures / (model + "_0.dds"), from / (model + "_0.dds"), dds);
				}
				continue;
			}
			for (int i = 0; i < 16; ++i) {
				const std::string texture = model + "_" + std::to_string(i) + ".dds";
				ensure(textures / texture, from / texture, dds);
			}
		}
		ensure(textures / "plat_n.dds", from / "plat_n.dds", dds);
		logger::info("Allies : {} allies possibles, {} fichiers de modeles prepares ou mis a jour d'avance", total, created);
	}

	// ---------------- Objets fixes (autels RoR2...) ----------------
	namespace
	{
		struct Static
		{
			Shown        shown;
			std::string  model;
			RE::NiPoint3 position;
			float        yaw = 0.0f;
			float        scale = 1.0f;  // negatif : mis a la taille de -scale metres (drones casses...)
			float        fitted = 0.0f; // echelle calculee pour cette taille (0 : pas encore)
		};
		std::unordered_map<std::uint32_t, Static> g_statics;

		void PlaceStatics(RE::NiNode* a_parent)
		{
			for (auto& [id, item] : g_statics) {
				auto& shown = item.shown;
				if (!a_parent) {
					Remove(shown);
					continue;
				}
				if (!shown.node || shown.parent != a_parent || shown.model != item.model) {
					Remove(shown);
					if (g_broken.contains(item.model) || !Install(item.model, 1)) {
						g_broken.insert(item.model);
						continue;
					}
					auto original = Load(item.model);
					if (!original) {
						continue;
					}
					RE::NiPointer<RE::NiObject> copy;
					original->CreateDeepCopy(copy);
					auto* node = copy ? copy->AsNode() : nullptr;
					if (!node) {
						continue;
					}
					shown.node.reset(node);
					shown.model = item.model;
					shown.parent = a_parent;
					a_parent->AttachChild(node, true);
					item.fitted = 0.0f;
				}
				auto& local = shown.node->local;
				// Taille voulue en metres : on mesure le modele une fois (a l'echelle 1), puis on le met a cette taille.
				if (item.scale < 0.0f && item.fitted <= 0.0f) {
					local.scale = 1.0f;
					RE::NiUpdateData measure{};
					shown.node->Update(measure);
					const float diameter = shown.node->worldBound.radius * 2.0f / kUnitsPerMeter;
					item.fitted = diameter > 0.05f ? std::clamp(-item.scale / diameter, 0.02f, 5.0f) : 1.0f;
					logger::info("Allies : {} mesure {:.1f} m, mis a {:.1f} m (x{:.2f})", item.model, diameter, -item.scale, item.fitted);
				}
				local.translate = item.position - a_parent->world.translate;
				// Rotation autour de la verticale (Skyrim : angle dans le sens des aiguilles d'une montre).
				const float c = std::cos(item.yaw);
				const float n = std::sin(item.yaw);
				local.rotate.entry[0][0] = c;
				local.rotate.entry[0][1] = n;
				local.rotate.entry[0][2] = 0.0f;
				local.rotate.entry[1][0] = -n;
				local.rotate.entry[1][1] = c;
				local.rotate.entry[1][2] = 0.0f;
				local.rotate.entry[2][0] = 0.0f;
				local.rotate.entry[2][1] = 0.0f;
				local.rotate.entry[2][2] = 1.0f;
				local.scale = item.scale < 0.0f ? item.fitted : item.scale;
				RE::NiUpdateData update{};
				shown.node->Update(update);
			}
		}
	}

	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked)
	{
		if (!a_player || !Open()) {
			return;
		}
		auto* root = a_player->Get3D();
		RE::NiNode* parent = root ? root->parent : nullptr;  // le noeud des objets de la cellule du joueur

		std::uint32_t magic = 0;
		std::uint32_t count = 0;
		std::memcpy(&magic, g_view, 4);
		std::memcpy(&count, g_view + 8, 4);
		if (!a_linked || magic != bridge::kAllyMagic || !parent) {
			count = 0;
		}
		count = std::min(count, bridge::kAllyMax);

		for (auto& [id, shown] : g_shown) {
			shown.seen = false;
		}
		// Premiere personne "stable" (menus compris) : voir ThirdPerson.cpp.
		const bool firstPerson = ThirdPerson::FirstPersonStable();
		bool       survivorShown = false;
		for (std::uint32_t i = 0; i < count; ++i) {
			const std::uint8_t* entry = g_view + bridge::kAllyHeaderSize + i * bridge::kAllyEntrySize;
			char                name[48]{};
			std::memcpy(name, entry, 47);
			const std::string model(name);
			float             position[3];
			float             rotation[9];
			std::uint32_t     id = 0;
			int               version = 0;
			std::memcpy(position, entry + 48, 12);
			std::memcpy(rotation, entry + 60, 36);
			std::memcpy(&id, entry + 96, 4);
			std::memcpy(&version, entry + 100, 4);
			// Taille du modele ; negative : c'est le survivant lui-meme (position par rapport aux pieds du joueur).
			float size = 1.0f;
			std::memcpy(&size, entry + 108, 4);
			const bool isPlayer = size < 0.0f;
			size = std::fabs(size);
			if (!(size > 0.01f && size < 10.0f)) {
				size = 1.0f;
			}
			if (model.empty() || g_broken.contains(model)) {
				continue;
			}

			auto& shown = g_shown[id];
			shown.seen = true;
			if (!shown.node || shown.model != model || shown.parent != parent) {
				Remove(shown);
				if (!Install(model, version)) {
					g_broken.insert(model);
					continue;
				}
				auto original = Load(model);
				if (!original) {
					continue;
				}
				RE::NiPointer<RE::NiObject> copy;
				original->CreateDeepCopy(copy);
				auto* node = copy ? copy->AsNode() : nullptr;
				if (!node) {
					continue;
				}
				shown.node.reset(node);
				shown.model = model;
				shown.parent = parent;
				parent->AttachChild(node, true);
			}

			// Position et rotation dans le monde (le noeud parent est cale sur le monde).
			auto&        local = shown.node->local;
			RE::NiPoint3 world(position[0] * kUnitsPerMeter, position[1] * kUnitsPerMeter, position[2] * kUnitsPerMeter);
			if (isPlayer) {
				world += a_player->GetPosition();  // pile sur le joueur Skyrim
				shown.node->CullNode(firstPerson);  // en premiere personne : on verrait l'interieur du modele
				survivorShown = true;
			}
			local.translate = world - parent->world.translate;
			for (int row = 0; row < 3; ++row) {
				for (int column = 0; column < 3; ++column) {
					local.rotate.entry[row][column] = rotation[row * 3 + column];
				}
			}
			local.scale = size;

			// Les pieces animees (une par os) : enfants de la racine du modele, dans l'ordre.
			std::uint32_t bones = 0;
			std::memcpy(&bones, entry + 104, 4);
			bones = std::min(bones, bridge::kAllyBones);
			auto& children = shown.node->GetChildren();
			const std::uint8_t* pose = g_view + bridge::kAllyPoseOffset + i * bridge::kAllyPoseSize;
			for (std::uint32_t b = 0; b < bones && b < children.size(); ++b) {
				auto* piece = children[b].get();
				if (!piece) {
					continue;
				}
				float values[12];
				std::memcpy(values, pose + b * 48, sizeof(values));
				piece->local.translate = RE::NiPoint3(values[0] * kUnitsPerMeter, values[1] * kUnitsPerMeter, values[2] * kUnitsPerMeter);
				for (int row = 0; row < 3; ++row) {
					for (int column = 0; column < 3; ++column) {
						piece->local.rotate.entry[row][column] = values[3 + row * 3 + column];
					}
				}
			}
			RE::NiUpdateData update{};
			shown.node->Update(update);
		}

		// Le survivant en 3D remplace le personnage Skyrim (vue a la troisieme personne).
		if (root) {
			// 0.65 : ou le survivant filme par RoR2 (vue a la 3e personne filmee, voir ThirdPerson).
			const bool hidePlayer = (survivorShown || ThirdPerson::FilmShown()) && !firstPerson;
			if (hidePlayer || g_playerHidden) {
				root->CullNode(hidePlayer);
			}
			g_playerHidden = hidePlayer;
		}

		// Les allies disparus (morts, trop loin...) sont retires.
		for (auto it = g_shown.begin(); it != g_shown.end();) {
			if (!it->second.seen) {
				Remove(it->second);
				it = g_shown.erase(it);
			} else {
				++it;
			}
		}

		// Les objets fixes (autels RoR2...).
		PlaceStatics(parent);

		// Les vrais effets de RoR2 (particules, trainees, rayons).
		PerFrameSprites(a_player, parent, a_linked);
	}

	void ShowStatic(std::uint32_t a_id, const std::string& a_model, const RE::NiPoint3& a_position, float a_yaw, float a_scale)
	{
		auto& item = g_statics[a_id];
		item.model = a_model;
		item.position = a_position;
		item.yaw = a_yaw;
		item.scale = a_scale;
	}

	void HideStatic(std::uint32_t a_id)
	{
		if (auto it = g_statics.find(a_id); it != g_statics.end()) {
			Remove(it->second.shown);
			g_statics.erase(it);
		}
	}
}
