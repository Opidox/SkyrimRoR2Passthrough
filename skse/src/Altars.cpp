#include "Altars.h"
#include "Lang.h"

#include "Allies.h"
#include "Config.h"
#include "InputWatcher.h"
#include "Items.h"
#include "Loot.h"

#include <deque>
#include <random>

namespace skyrimror2::Altars
{
	namespace
	{
		constexpr float         kUnitsPerMeter = 70.0f;
		constexpr std::uint32_t kRecord = 'ALTR';
		constexpr std::uint32_t kRecordVersion = 3;  // 2 : imprimantes et drones (0.61) ; 3 : contenant et placement (0.66)
		constexpr std::uint32_t kIdBase = 0xA17A0000;  // identifiants des modeles poses (Allies::ShowStatic)
		constexpr float         kUseDistance = 2.8f;    // m

		enum Type : std::int32_t
		{
			kChance,
			kOrder,
			kCombat,
			kBlood,
			kMountain,
			kLunar,
			kPrinter,  // 0.61 : imprimante (kind = rang : 0 blanc, 1 vert, 2 rouge, 3 jaune)
			kDrone,    // 0.61 : drone ou tourelle casse, a reparer (kind : voir kDrones)
			kPortal,   // 0.71 : porte bleue vers le Bazar de Newt (apres un coffre de boss, parfois)
			kTypeCount
		};

		constexpr const char* kModels[kTypeCount]{ "ShrineChance_A", "ShrineRestack_A", "ShrineCombat_A", "ShrineBlood_A", "ShrineBoss_A", "LunarChest_A", "", "",
			"PortalShop_A" };
		constexpr const char* kNames[kTypeCount]{ "Autel de la Chance", "Autel de l'Ordre", "Autel du Combat", "Autel du Sang", "Autel de la Montagne", "Capsule lunaire",
			"Imprimante", "Drone casse", "Porte du Bazar" };
		constexpr int         kUses[kTypeCount]{ 2, 1, 1, 2, 1, 1, 1000000, 1, 1000000 };  // Chance : nombre de GAINS ; imprimante, porte : sans limite

		constexpr const char* kPrinterModels[4]{ "Duplicator_A", "DuplicatorLarge_A", "DuplicatorMilitary_A", "DuplicatorWild_A" };

		// Les drones et tourelles casses (comme dans RoR2) : modele, nom, prix en septims (avant la difficulte), rarete.
		struct DroneKind
		{
			const char* model;
			const char* name;
			int         price;
			int         weight;
		};
		constexpr DroneKind kDrones[]{
			{ "Drone1Broken_A", "Drone de tir casse", 40, 30 },
			{ "Drone2Broken_A", "Drone de soin casse", 40, 25 },
			{ "MissileDroneBroken_A", "Drone lance-missiles casse", 60, 12 },
			{ "Turret1Broken_A", "Tourelle cassee", 35, 20 },
			{ "FlameDroneBroken_A", "Drone incendiaire casse", 100, 6 },
			{ "EmergencyDroneBroken_A", "Drone de secours casse", 100, 5 },
			{ "MegaDroneBroken_A", "TC-280 casse", 350, 2 },
		};
		constexpr int kDroneKinds = static_cast<int>(sizeof(kDrones) / sizeof(kDrones[0]));

		// Version 1 (avant la 0.61), pour relire les anciennes sauvegardes.
		struct AltarV1
		{
			RE::FormID   cell = 0;
			std::int32_t type = kChance;
			float        x = 0.0f, y = 0.0f, z = 0.0f, yaw = 0.0f;
			std::int32_t uses = 0;
			std::int32_t tries = 0;
		};

		// Version 2 (0.61 a 0.65).
		struct AltarV2
		{
			RE::FormID   cell = 0;
			std::int32_t type = kChance;
			float        x = 0.0f, y = 0.0f, z = 0.0f, yaw = 0.0f;
			std::int32_t uses = 0;
			std::int32_t tries = 0;
			std::int32_t kind = 0;
			std::int32_t exterior = 0;
			char         item[48]{};
		};

		struct Altar
		{
			RE::FormID   cell = 0;      // cellule (interieur) ou monde (exterieur)
			std::int32_t type = kChance;
			float        x = 0.0f, y = 0.0f, z = 0.0f, yaw = 0.0f;
			std::int32_t uses = 0;      // utilisations (gains pour la Chance)
			std::int32_t tries = 0;     // essais payes (la Chance devient plus chere a chaque essai)
			std::int32_t kind = 0;      // imprimante : rang ; drone : sorte
			std::int32_t exterior = 0;  // 1 : dehors (cell = le monde, visible tant qu'on est a moins de 150 m)
			char         item[48]{};    // imprimante : l'objet imprime (nom interne RoR2)
			RE::FormID   container = 0; // 0.66 : le contenant a cote duquel il est pose (un seul objet par contenant)
			std::int32_t placed = 0;    // 0.66 : 1 = place avec les nouvelles regles ; 0 = ancienne place (a refaire)
		};

		const char* ModelOf(const Altar& a_altar)
		{
			if (a_altar.type == kPrinter) {
				return kPrinterModels[std::clamp(a_altar.kind, 0, 3)];
			}
			if (a_altar.type == kDrone) {
				return kDrones[std::clamp(a_altar.kind, 0, kDroneKinds - 1)].model;
			}
			return kModels[a_altar.type];
		}

		std::string NameOf(const Altar& a_altar)
		{
			if (a_altar.type == kPrinter) {
				return a_altar.item[0] ? Lang::F("Imprimante : {} [{}]", Items::DisplayName(a_altar.item), Items::TierWord(a_altar.kind)) :
				                         Lang::F("Imprimante ({})", Items::TierWord(a_altar.kind));
			}
			if (a_altar.type == kDrone) {
				const auto& drone = kDrones[std::clamp(a_altar.kind, 0, kDroneKinds - 1)];
				return Lang::F("{} ({} septims)", Lang::T(drone.name), Loot::ScaledPrice(drone.price));
			}
			return Lang::T(kNames[a_altar.type]);
		}

		// Reglages
		bool g_enabled = true;
		int  g_chance = 60;       // % de donjons avec un autel
		int  g_lunarChance = 15;  // % de donjons avec une capsule lunaire en plus
		int  g_chancePrice = 15;  // septims (avant la difficulte)
		int  g_chanceFail = 45;   // % d'echec de l'autel de la Chance
		int  g_lunarCost = 1;     // pieces lunaires
		int  g_bloodGold = 40;    // septims (avant la difficulte)
		int  g_printerChance = 30;  // % de donjons avec une imprimante
		int  g_droneChance = 35;    // % de donjons avec un drone casse
		int  g_droneOutside = 5;    // % de cellules exterieures avec un drone casse
		// 0.66 : tailles (1 = taille du modele RoR2) et nombre d'objets par lieu
		float g_printerSize = 0.7f;
		float g_lunarSize = 0.7f;
		float g_shrineSize = 0.28f;  // les autels de RoR2 sont tres grands : 0,4 x 70 %
		float g_droneHeight = 1.1f;  // m
		int   g_maxPerPlace = 4;
		std::deque<std::pair<RE::NiPoint3, std::chrono::steady_clock::time_point>> g_trail;  // ou le joueur est passe dehors

		std::vector<Altar>             g_altars;
		std::unordered_set<RE::FormID> g_rolled;  // cellules deja tirees
		std::unordered_set<std::uint32_t> g_shownIds;
		RE::TESObjectCELL*             g_cell = nullptr;
		std::chrono::steady_clock::time_point g_cellSince{};
		int                            g_near = -1;  // autel a portee (index), -1 : aucun
		std::mutex                     g_hintLock;
		std::string                    g_hint;       // "Autel de la Chance : 23 m" (lu par le HUD)
		bool                           g_useWasDown = false;

		std::mt19937& Random()
		{
			static std::mt19937 engine{ std::random_device{}() };
			return engine;
		}

		bool Roll(int a_percent) { return std::uniform_int_distribution<int>(1, 100)(Random()) <= a_percent; }

		void Show(std::string a_text)
		{
			SKSE::GetTaskInterface()->AddTask([text = std::move(a_text)]() { RE::SendHUDMessage::ShowHUDMessage(text.c_str()); });
		}

		int ChancePrice(const Altar& a_altar)
		{
			// Comme dans RoR2 : chaque essai coute 40 % de plus.
			return Loot::ScaledPrice(static_cast<int>(std::lround(g_chancePrice * std::pow(1.4f, static_cast<float>(a_altar.tries)))));
		}

		std::string Prompt(const Altar& a_altar)
		{
			const std::string key = Config::Get().KeyName(Action::kUse);
			switch (a_altar.type) {
			case kChance:
				return Lang::F("{} : {} ({} septims)", Lang::T(kNames[kChance]), key, ChancePrice(a_altar));
			case kLunar:
				return Lang::F("{} : {} ({} piece(s) lunaire(s), tu en as {})", Lang::T(kNames[kLunar]), key, g_lunarCost, Loot::Coins());
			case kBlood:
				return Lang::F("{} : {} (la moitie de tes PV contre {} septims)", Lang::T(kNames[kBlood]), key, Loot::ScaledPrice(g_bloodGold));
			case kPrinter:
				return Lang::F("{} : {} (prend une ferraille ou un objet {} porte)", NameOf(a_altar), key, Items::TierWord(a_altar.kind));
			case kDrone:
				return Lang::F("{} : {} pour le reparer", NameOf(a_altar), key);
			case kPortal:
				return Lang::F("{} : {} pour entrer au Bazar de Newt", Lang::T(kNames[kPortal]), key);
			default:
				return fmt::format("{} : {}", Lang::T(kNames[a_altar.type]), key);
			}
		}

		bool Used(const Altar& a_altar) { return a_altar.uses >= kUses[a_altar.type]; }

		// ---------- 0.66 : placement ----------
		// Chaque objet RoR2 est pose sur le cote d'un contenant different, sur le sol, jamais devant une porte,
		// devant l'ouverture d'un contenant ni dans un passage etroit, et loin des autres objets RoR2.

		// Part libre (0 a 1) du trajet entre deux points (rayon invisible de Skyrim, comme une ligne de vue).
		float FreeFraction(RE::PlayerCharacter* a_player, const RE::NiPoint3& a_from, const RE::NiPoint3& a_to)
		{
			auto* cell = a_player->GetParentCell();
			auto* world = cell ? cell->GetbhkWorld() : nullptr;
			if (!world) {
				return 1.0f;
			}
			const float     scale = RE::bhkWorld::GetWorldScale();
			RE::bhkPickData pick{};
			pick.rayInput.from = RE::hkVector4(a_from.x * scale, a_from.y * scale, a_from.z * scale, 0.0f);
			pick.rayInput.to = RE::hkVector4(a_to.x * scale, a_to.y * scale, a_to.z * scale, 0.0f);
			RE::CFilter filter{};
			a_player->GetCollisionFilterInfo(filter);
			filter.SetCollisionLayer(RE::COL_LAYER::kLOS);
			pick.rayInput.filterInfo = filter;
			world->PickObject(pick);
			return pick.rayOutput.HasHit() ? std::clamp(pick.rayOutput.hitFraction, 0.0f, 1.0f) : 1.0f;
		}

		struct PlaceInfo
		{
			std::vector<RE::TESObjectREFR*> containers;
			std::vector<RE::NiPoint3>       doors;
			float                           extent = 0.0f;  // m : la plus grande dimension du lieu
		};

		PlaceInfo Survey(RE::PlayerCharacter* a_player)
		{
			PlaceInfo info;
			auto*     tes = RE::TES::GetSingleton();
			auto*     cell = a_player->GetParentCell();
			if (!tes || !cell) {
				return info;
			}
			float minX = 1.0e9f, minY = 1.0e9f, maxX = -1.0e9f, maxY = -1.0e9f;
			tes->ForEachReferenceInRange(a_player, 400.0f * kUnitsPerMeter, [&](RE::TESObjectREFR* a_ref) {
				if (!a_ref || a_ref->GetParentCell() != cell || a_ref->IsDisabled()) {
					return RE::BSContainer::ForEachResult::kContinue;
				}
				const auto at = a_ref->GetPosition();
				minX = std::min(minX, at.x);
				minY = std::min(minY, at.y);
				maxX = std::max(maxX, at.x);
				maxY = std::max(maxY, at.y);
				auto* base = a_ref->GetBaseObject();
				if (base && base->Is(RE::FormType::Door)) {
					info.doors.push_back(at);
				} else if (base && base->Is(RE::FormType::Container) && !a_ref->As<RE::Actor>()) {
					info.containers.push_back(a_ref);
				}
				return RE::BSContainer::ForEachResult::kContinue;
			});
			if (maxX > minX) {
				info.extent = std::max(maxX - minX, maxY - minY) / kUnitsPerMeter;
			}
			return info;
		}

		// Nombre d'objets RoR2 au plus dans ce lieu, selon sa taille : 1 (petit) a 4 (grand).
		int MaxObjects(const PlaceInfo& a_info)
		{
			const int containers = static_cast<int>(a_info.containers.size());
			int       count = a_info.extent < 50.0f || containers < 4 ? 1 : (a_info.extent < 100.0f ? 2 : (a_info.extent < 160.0f ? 3 : 4));
			if (containers >= 25) {
				count = std::max(count, 3);
			}
			return std::clamp(std::min(count, containers), 0, std::max(0, g_maxPerPlace));
		}

		// La place est-elle bonne pour un objet RoR2 ? (a_spot.z est recale sur le sol)
		bool GoodSpot(RE::PlayerCharacter* a_player, const PlaceInfo& a_info, RE::TESObjectREFR* a_owner, RE::NiPoint3& a_spot,
			const std::vector<RE::NiPoint3>& a_others)
		{
			const float m = kUnitsPerMeter;
			const auto  owner = a_owner->GetPosition();
			// Pas de mur entre le contenant et la place (le rayon part du bord du contenant, pas de dedans).
			const float        toX = a_spot.x - owner.x, toY = a_spot.y - owner.y;
			const float        toLength = std::max(std::sqrt(toX * toX + toY * toY), 1.0f);
			const float        edge = std::min(0.7f * m, toLength * 0.5f) / toLength;
			const RE::NiPoint3 eyeFrom(owner.x + toX * edge, owner.y + toY * edge, owner.z + 1.0f * m);
			const RE::NiPoint3 eyeTo(a_spot.x, a_spot.y, owner.z + 1.0f * m);
			if (FreeFraction(a_player, eyeFrom, eyeTo) < 0.999f) {
				return false;
			}
			// Du sol sous la place, a peu pres a la hauteur du contenant.
			const RE::NiPoint3 down0(a_spot.x, a_spot.y, owner.z + 1.2f * m);
			const RE::NiPoint3 down1(a_spot.x, a_spot.y, owner.z - 2.0f * m);
			const float        fall = FreeFraction(a_player, down0, down1);
			if (fall >= 0.999f) {
				return false;
			}
			const float ground = down0.z + (down1.z - down0.z) * fall;
			if (std::fabs(ground - owner.z) > 0.7f * m) {
				return false;
			}
			a_spot.z = ground;
			// Pas un passage etroit : la largeur libre la plus petite autour de la place (4 axes) doit laisser
			// de quoi passer a cote de l'objet (environ 1,2 m d'objet + 1,5 m de passage).
			constexpr float    kRay = 4.0f;  // m
			const float        height = ground + 0.9f * m;
			const RE::NiPoint3 center(a_spot.x, a_spot.y, height);
			float              narrowest = 1.0e9f;
			for (int axis = 0; axis < 4; ++axis) {
				const float angle = static_cast<float>(axis) * 0.785398f;
				const float dx = std::cos(angle) * kRay * m;
				const float dy = std::sin(angle) * kRay * m;
				const float a = FreeFraction(a_player, center, RE::NiPoint3(center.x + dx, center.y + dy, height)) * kRay;
				const float b = FreeFraction(a_player, center, RE::NiPoint3(center.x - dx, center.y - dy, height)) * kRay;
				narrowest = std::min(narrowest, a + b);
				if (std::min(a, b) < 0.5f && std::max(a, b) < 2.2f) {
					return false;  // l'objet ne tient pas la
				}
			}
			if (narrowest < 2.7f) {
				return false;
			}
			// Loin des portes.
			for (const auto& door : a_info.doors) {
				if (door.GetDistance(a_spot) < 3.5f * m) {
					return false;
				}
			}
			// Jamais devant l'ouverture d'un contenant (le sien compris), ni colle a un contenant.
			for (auto* other : a_info.containers) {
				const auto at = other->GetPosition();
				if (std::fabs(a_spot.z - at.z) > 2.0f * m) {
					continue;
				}
				const float angle = other->GetAngleZ();
				const float fx = std::sin(angle), fy = std::cos(angle);
				const float vx = (a_spot.x - at.x) / m, vy = (a_spot.y - at.y) / m;
				const float ahead = vx * fx + vy * fy;
				const float aside = std::fabs(vx * fy - vy * fx);
				if (std::sqrt(vx * vx + vy * vy) < 1.0f || (ahead > -0.2f && ahead < 2.2f && aside < 1.0f)) {
					return false;
				}
			}
			// Loin des autres objets RoR2 de ce lieu.
			for (const auto& other : a_others) {
				if (other.GetDistance(a_spot) < 4.0f * m) {
					return false;
				}
			}
			return true;
		}

		// Une place sur le cote d'un contenant encore libre, loin de l'entree. Faux s'il n'y en a pas.
		bool FindSpot(RE::PlayerCharacter* a_player, const PlaceInfo& a_info, const RE::NiPoint3& a_entry,
			const std::unordered_set<RE::FormID>& a_usedContainers, const std::vector<RE::NiPoint3>& a_others,
			RE::NiPoint3& a_spot, float& a_yaw, RE::FormID& a_container)
		{
			std::vector<RE::TESObjectREFR*> candidates;
			for (auto* ref : a_info.containers) {
				if (!a_usedContainers.contains(ref->GetFormID()) && ref->GetPosition().GetDistance(a_entry) > 12.0f * kUnitsPerMeter) {
					candidates.push_back(ref);
				}
			}
			std::shuffle(candidates.begin(), candidates.end(), Random());
			for (auto* ref : candidates) {
				const float angle = ref->GetAngleZ();
				const auto  at = ref->GetPosition();
				const float fx = std::sin(angle), fy = std::cos(angle);   // l'avant du contenant
				const float sx = std::cos(angle), sy = -std::sin(angle);  // son cote
				for (const float side : { 1.0f, -1.0f }) {
					for (const float distance : { 1.6f, 2.1f, 2.6f }) {
						for (const float back : { 0.0f, -0.4f }) {
							RE::NiPoint3 spot(at.x + (sx * side * distance + fx * back) * kUnitsPerMeter,
								at.y + (sy * side * distance + fy * back) * kUnitsPerMeter, at.z);
							if (GoodSpot(a_player, a_info, ref, spot, a_others)) {
								a_spot = spot;
								a_yaw = angle;  // tourne comme le contenant (face a la piece)
								a_container = ref->GetFormID();
								return true;
							}
						}
					}
				}
			}
			return false;
		}

		// Les contenants deja pris et les places deja occupees dans ce lieu.
		void Occupied(RE::FormID a_cell, std::unordered_set<RE::FormID>& a_containers, std::vector<RE::NiPoint3>& a_spots, const Altar* a_except)
		{
			for (const auto& altar : g_altars) {
				if (&altar == a_except || altar.exterior || altar.cell != a_cell) {
					continue;
				}
				if (altar.container) {
					a_containers.insert(altar.container);
				}
				if (altar.placed) {
					a_spots.emplace_back(altar.x, altar.y, altar.z);
				}
			}
		}

		// Taille d'affichage (voir Allies::ShowStatic : negatif = hauteur voulue en metres).
		float DisplayScale(const Altar& a_altar)
		{
			switch (a_altar.type) {
			case kDrone:
				return -std::clamp(g_droneHeight, 0.2f, 5.0f);
			case kPrinter:
				return std::clamp(g_printerSize, 0.05f, 3.0f);
			case kLunar:
				return std::clamp(g_lunarSize, 0.05f, 3.0f);
			case kPortal:
				return -3.0f;  // 3 m de haut
			default:
				return std::clamp(g_shrineSize, 0.05f, 3.0f);
			}
		}

		// Le rang d'une imprimante (comme dans RoR2 : surtout blanches) ou la sorte d'un drone, et l'objet imprime.
		void Prepare(Altar& a_altar)
		{
			if (a_altar.type == kPrinter) {
				const int roll = std::uniform_int_distribution<int>(1, 100)(Random());
				a_altar.kind = roll <= 60 ? 0 : (roll <= 90 ? 1 : (roll <= 97 ? 2 : 3));
			} else if (a_altar.type == kDrone) {
				int total = 0;
				for (const auto& drone : kDrones) {
					total += drone.weight;
				}
				int roll = std::uniform_int_distribution<int>(1, total)(Random());
				for (int i = 0; i < kDroneKinds; ++i) {
					roll -= kDrones[i].weight;
					if (roll <= 0) {
						a_altar.kind = i;
						break;
					}
				}
			}
		}

		// Une imprimante sans objet (catalogue pas encore connu a sa pose) en recoit un.
		void FillPrinter(Altar& a_altar)
		{
			if (a_altar.type != kPrinter || a_altar.item[0]) {
				return;
			}
			const std::string item = Items::RandomFindable(a_altar.kind);
			if (!item.empty()) {
				strncpy_s(a_altar.item, item.c_str(), _TRUNCATE);
			}
		}

		// Dehors : un drone casse, parfois, la ou le joueur est passe il y a un moment (donc sur un chemin praticable).
		void RollOutside(RE::PlayerCharacter* a_player, RE::TESObjectCELL* a_cell)
		{
			if (!g_rolled.insert(a_cell->GetFormID()).second || !Roll(g_droneOutside)) {
				return;
			}
			auto* world = a_player->GetWorldspace();
			if (!world) {
				return;
			}
			const auto feet = a_player->GetPosition();
			for (const auto& [spot, when] : g_trail) {
				const float distance = spot.GetDistance(feet) / kUnitsPerMeter;
				if (distance < 20.0f || distance > 50.0f) {
					continue;
				}
				Altar altar;
				altar.cell = world->GetFormID();
				altar.exterior = 1;
				altar.type = kDrone;
				altar.x = spot.x;
				altar.y = spot.y;
				altar.z = spot.z;
				altar.yaw = std::uniform_real_distribution<float>(0.0f, 6.28f)(Random());
				Prepare(altar);
				g_altars.push_back(altar);
				logger::info("Autels : {} pose dehors a {:.0f} m du joueur", NameOf(altar), distance);
				Show(Lang::F("Dans les environs : {}", NameOf(altar)));
				return;
			}
		}

		void RollCell(RE::PlayerCharacter* a_player, RE::TESObjectCELL* a_cell)
		{
			if (!g_rolled.insert(a_cell->GetFormID()).second) {
				return;
			}
			if (!Loot::IsDungeon(a_player)) {
				return;
			}
			const bool daedric = Loot::InDaedricPlace(a_player);
			std::vector<std::int32_t> types;
			if (Roll(g_chance)) {
				types.push_back(std::uniform_int_distribution<std::int32_t>(kChance, kMountain)(Random()));
			}
			if (Roll(g_printerChance)) {
				types.push_back(kPrinter);
			}
			if (Roll(g_droneChance)) {
				types.push_back(kDrone);
			}
			std::shuffle(types.begin(), types.end(), Random());
			if (daedric || Roll(g_lunarChance)) {
				types.insert(types.begin(), kLunar);  // un sanctuaire daedrique a toujours sa capsule
			}
			const auto info = Survey(a_player);
			const int  most = MaxObjects(info);
			if (static_cast<int>(types.size()) > most) {
				types.resize(static_cast<std::size_t>(most));
			}
			logger::info("Autels : lieu {:08X} de {:.0f} m, {} contenant(s), {} porte(s) : {} objet(s) RoR2 au plus, {} tire(s)",
				a_cell->GetFormID(), info.extent, info.containers.size(), info.doors.size(), most, types.size());
			std::unordered_set<RE::FormID> usedContainers;
			std::vector<RE::NiPoint3>      spots;
			Occupied(a_cell->GetFormID(), usedContainers, spots, nullptr);
			for (const auto type : types) {
				RE::NiPoint3 spot;
				float        yaw = 0.0f;
				RE::FormID   container = 0;
				if (!FindSpot(a_player, info, a_player->GetPosition(), usedContainers, spots, spot, yaw, container)) {
					logger::info("Autels : plus de place libre pour un objet RoR2 dans la cellule {:08X}", a_cell->GetFormID());
					break;
				}
				Altar altar;
				altar.cell = a_cell->GetFormID();
				altar.type = type;
				altar.x = spot.x;
				altar.y = spot.y;
				altar.z = spot.z;
				altar.yaw = yaw;
				altar.container = container;
				altar.placed = 1;
				usedContainers.insert(container);
				spots.push_back(spot);
				Prepare(altar);
				g_altars.push_back(altar);
				logger::info("Autels : {} pose a cote du contenant {:08X} (cellule {:08X}, a {:.0f} m du joueur)", NameOf(altar), container,
					a_cell->GetFormID(), spot.GetDistance(a_player->GetPosition()) / kUnitsPerMeter);
				Show(Lang::F("Dans ce lieu : {}", NameOf(altar)));
			}
		}

		// Les objets poses avant la 0.66 (souvent empiles au meme endroit) sont replaces avec les nouvelles regles,
		// une seule fois. S'il n'y a pas de bonne place, ils gardent l'ancienne.
		void ReplaceOld(RE::PlayerCharacter* a_player, RE::TESObjectCELL* a_cell)
		{
			const RE::FormID here = a_cell->GetFormID();
			bool             any = false;
			for (const auto& altar : g_altars) {
				any = any || (!altar.exterior && !altar.placed && altar.cell == here);
			}
			if (!any) {
				return;
			}
			const auto info = Survey(a_player);
			int        moved = 0;
			for (std::size_t i = 0; i < g_altars.size(); ++i) {
				auto& altar = g_altars[i];
				if (altar.exterior || altar.placed || altar.cell != here) {
					continue;
				}
				std::unordered_set<RE::FormID> usedContainers;
				std::vector<RE::NiPoint3>      spots;
				Occupied(here, usedContainers, spots, &altar);
				RE::NiPoint3 spot;
				float        yaw = 0.0f;
				RE::FormID   container = 0;
				// L'entree a eviter : la place du joueur (comme pour une nouvelle pose).
				if (FindSpot(a_player, info, a_player->GetPosition(), usedContainers, spots, spot, yaw, container)) {
					altar.x = spot.x;
					altar.y = spot.y;
					altar.z = spot.z;
					altar.yaw = yaw;
					altar.container = container;
					++moved;
					const std::uint32_t id = kIdBase + static_cast<std::uint32_t>(i);
					Allies::HideStatic(id);
					g_shownIds.erase(id);
				}
				altar.placed = 1;
			}
			if (moved > 0) {
				logger::info("Autels : {} objet(s) RoR2 pose(s) avant la 0.66 replace(s) dans la cellule {:08X}", moved, here);
			}
		}

		// Fait venir des ennemis du donjon (vivants ou morts) a cote de l'autel.
		int CallEnemies(RE::PlayerCharacter* a_player, const RE::NiPoint3& a_at, int a_count)
		{
			auto* tes = RE::TES::GetSingleton();
			if (!tes) {
				return 0;
			}
			std::vector<RE::Actor*> sources;
			tes->ForEachReferenceInRange(a_player, 100.0f * kUnitsPerMeter, [&](RE::TESObjectREFR* a_ref) {
				auto* actor = a_ref ? a_ref->As<RE::Actor>() : nullptr;
				auto* base = actor ? actor->GetActorBase() : nullptr;
				if (actor && actor != a_player && base && !base->IsUnique() && !base->IsEssential() && !actor->IsPlayerTeammate() &&
					!actor->IsCommandedActor() && (actor->IsDead() || actor->IsHostileToActor(a_player))) {
					sources.push_back(actor);
				}
				return RE::BSContainer::ForEachResult::kContinue;
			});
			if (sources.empty()) {
				return 0;
			}
			// Le plus proche de l'autel sert de point d'apparition (il est sur un sol ou l'on marche).
			std::ranges::sort(sources, [&](RE::Actor* a, RE::Actor* b) { return a->GetPosition().GetDistance(a_at) < b->GetPosition().GetDistance(a_at); });
			int spawned = 0;
			for (int i = 0; i < a_count; ++i) {
				auto* model = sources[std::uniform_int_distribution<std::size_t>(0, sources.size() - 1)(Random())];
				if (model->GetActorBase() && sources.front()->PlaceObjectAtMe(model->GetActorBase(), false)) {
					++spawned;
				}
			}
			return spawned;
		}

		void Use(Altar& a_altar, RE::PlayerCharacter* a_player)
		{
			if (Used(a_altar)) {
				Show(Lang::F("{} : deja utilise", NameOf(a_altar)));
				return;
			}
			const RE::NiPoint3 at(a_altar.x, a_altar.y, a_altar.z);
			switch (a_altar.type) {
			case kChance:
				{
					const int price = ChancePrice(a_altar);
					if (!Loot::PayGold(price)) {
						Show(Lang::F("Il faut {} septims", price));
						return;
					}
					++a_altar.tries;
					if (Roll(g_chanceFail)) {
						Show(Lang::T("L'autel de la Chance ne donne rien..."));
						return;
					}
					const int roll = std::uniform_int_distribution<int>(1, 100)(Random());
					Loot::RequestLoot(roll <= 1 ? 2 : (roll <= 21 ? 1 : 0), 1);
					++a_altar.uses;
					break;
				}
			case kOrder:
				Loot::RequestLoot(7, 1);
				Show(Lang::T("L'autel de l'Ordre melange tes objets"));
				++a_altar.uses;
				break;
			case kCombat:
				{
					const int count = std::min(3 + static_cast<int>(Loot::Difficulty() / 2.0f), 8);
					const int spawned = CallEnemies(a_player, at, count);
					Show(spawned > 0 ? Lang::F("L'autel du Combat appelle {} ennemi(s) !", spawned) : std::string(Lang::T("Rien ne repond a l'appel")));
					++a_altar.uses;
					break;
				}
			case kBlood:
				{
					const int gold = Loot::ScaledPrice(g_bloodGold);
					Loot::RequestLoot(8, 1);
					Loot::GiveGold(gold);
					Show(Lang::F("L'autel du Sang prend la moitie de tes PV : +{} septims", gold));
					++a_altar.uses;
					break;
				}
			case kMountain:
				Loot::AddMountain();
				Show(Lang::T("Defi de la Montagne : le prochain coffre de boss donnera un objet de plus, avec plus de renforts"));
				++a_altar.uses;
				break;
			case kLunar:
				if (!Loot::SpendCoins(g_lunarCost)) {
					Show(Lang::F("Il faut {} piece(s) lunaire(s) (tu en as {})", g_lunarCost, Loot::Coins()));
					return;
				}
				Loot::RequestLoot(5, 1);
				++a_altar.uses;
				break;
			case kPrinter:
				FillPrinter(a_altar);
				if (!a_altar.item[0]) {
					Show(Lang::T("L'imprimante ne repond pas encore (RoR2 pas pret)"));
					return;
				}
				if (Items::PrintFor(a_altar.item, a_altar.kind)) {
					++a_altar.uses;
				}
				break;
			case kDrone:
				{
					const auto& drone = kDrones[std::clamp(a_altar.kind, 0, kDroneKinds - 1)];
					const int   price = Loot::ScaledPrice(drone.price);
					if (!Loot::PayGold(price)) {
						Show(Lang::F("Il faut {} septims pour reparer ce drone", price));
						return;
					}
					Loot::RequestLoot(20 + std::clamp(a_altar.kind, 0, kDroneKinds - 1), 1);
					Show(Lang::F("{} repare : il te rejoint (visible des le prochain lancement s'il est nouveau)", Lang::T(drone.name)));
					++a_altar.uses;
					break;
				}
			case kPortal:
				{
					// Le stock de Newt est propre a chaque porte (sa place).
					const auto key = static_cast<std::uint32_t>(std::lround(a_altar.x)) * 73856093u ^ static_cast<std::uint32_t>(std::lround(a_altar.y)) * 19349663u;
					SKSE::GetTaskInterface()->AddTask([key]() { Items::OpenPortalBazaar(key); });
					return;
				}
			default:
				return;
			}
			logger::info("Autels : {} utilise ({} / {})", NameOf(a_altar), a_altar.uses, kUses[a_altar.type]);
		}

		int ReadInt(const char* a_key, int a_default, const std::string& a_ini)
		{
			return static_cast<int>(::GetPrivateProfileIntA("AutelsRoR2", a_key, a_default, a_ini.c_str()));
		}
	}

	std::string Hint()
	{
		std::scoped_lock lock(g_hintLock);
		return g_hint;
	}

	void LoadSettings()
	{
		const auto ini = std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini").string();
		g_enabled = ReadInt("Activer", 1, ini) != 0;
		g_chance = std::clamp(ReadInt("ChanceAutel", 60, ini), 0, 100);
		g_lunarChance = std::clamp(ReadInt("ChanceCapsuleLunaire", 15, ini), 0, 100);
		g_chancePrice = std::max(0, ReadInt("PrixChance", 15, ini));
		g_chanceFail = std::clamp(ReadInt("EchecChance", 45, ini), 0, 100);
		g_lunarCost = std::max(0, ReadInt("PiecesCapsule", 1, ini));
		g_bloodGold = std::max(0, ReadInt("SeptimsSang", 40, ini));
		g_printerChance = std::clamp(ReadInt("ChanceImprimante", 30, ini), 0, 100);
		g_droneChance = std::clamp(ReadInt("ChanceDrone", 35, ini), 0, 100);
		g_droneOutside = std::clamp(ReadInt("ChanceDroneDehors", 5, ini), 0, 100);
		g_maxPerPlace = std::clamp(ReadInt("ObjetsParLieuMax", 4, ini), 0, 10);
		const auto readFloat = [&](const char* a_key, float a_default) {
			char buffer[32]{};
			::GetPrivateProfileStringA("AutelsRoR2", a_key, "", buffer, sizeof(buffer), ini.c_str());
			std::string text(buffer);
			std::ranges::replace(text, ',', '.');
			try {
				return text.empty() ? a_default : std::stof(text);
			} catch (...) {
				return a_default;
			}
		};
		g_printerSize = readFloat("TailleImprimante", 0.7f);
		g_lunarSize = readFloat("TailleCapsule", 0.7f);
		g_shrineSize = readFloat("TailleAutel", 0.28f);
		g_droneHeight = readFloat("HauteurDrone", 1.1f);
		logger::info("Autels RoR2 : {} ({} % des donjons, capsule lunaire {} %)", g_enabled ? "actifs" : "desactives", g_chance, g_lunarChance);
	}

	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked)
	{
		if (!a_player) {
			return;
		}
		auto* cell = a_player->GetParentCell();
		const auto now = std::chrono::steady_clock::now();
		if (cell != g_cell) {
			g_cell = cell;
			g_cellSince = now;
			g_near = -1;
		}
		// Un petit moment apres l'arrivee : le temps que le donjon soit charge.
		if (g_enabled && a_linked && cell && !cell->IsExteriorCell() && now - g_cellSince > std::chrono::seconds(3)) {
			ReplaceOld(a_player, cell);
			RollCell(a_player, cell);
		}
		// Dehors : on retient ou le joueur passe (toutes les 2 s, 40 s au plus), et chaque nouvelle cellule peut avoir un drone.
		const bool outside = cell && cell->IsExteriorCell();
		if (outside) {
			if (!a_player->IsInMidair() && (g_trail.empty() || now - g_trail.back().second > std::chrono::seconds(2))) {
				g_trail.emplace_back(a_player->GetPosition(), now);
				while (g_trail.size() > 20) {
					g_trail.pop_front();
				}
			}
			if (g_enabled && a_linked && now - g_cellSince > std::chrono::seconds(3)) {
				RollOutside(a_player, cell);
			}
		} else {
			g_trail.clear();
		}

		// Les modeles des autels de ce lieu (dehors : ceux du meme monde, a moins de 150 m).
		std::unordered_set<std::uint32_t> wanted;
		const RE::FormID here = cell ? cell->GetFormID() : 0;
		auto*            worldspace = outside ? a_player->GetWorldspace() : nullptr;
		const RE::FormID world = worldspace ? worldspace->GetFormID() : 0;
		const auto       feet = a_player->GetPosition();
		const auto       inPlace = [&](const Altar& a_altar) {
            if (!a_altar.exterior) {
                return a_altar.cell == here;
            }
            return outside && a_altar.cell == world && RE::NiPoint3(a_altar.x, a_altar.y, a_altar.z).GetDistance(feet) < 150.0f * kUnitsPerMeter;
		};
		for (std::size_t i = 0; i < g_altars.size(); ++i) {
			auto& altar = g_altars[i];
			if (!g_enabled || !inPlace(altar) || (altar.type == kDrone && Used(altar))) {
				continue;  // un drone repare n'est plus la : il a rejoint le survivant
			}
			FillPrinter(altar);
			const std::uint32_t id = kIdBase + static_cast<std::uint32_t>(i);
			wanted.insert(id);
			if (!g_shownIds.contains(id)) {
				// 0.66 : tailles reglables ([AutelsRoR2] TailleImprimante, TailleCapsule, TailleAutel, HauteurDrone).
				Allies::ShowStatic(id, ModelOf(altar), RE::NiPoint3(altar.x, altar.y, altar.z), altar.yaw, DisplayScale(altar));
				g_shownIds.insert(id);
			}
		}
		for (auto it = g_shownIds.begin(); it != g_shownIds.end();) {
			if (!wanted.contains(*it)) {
				Allies::HideStatic(*it);
				it = g_shownIds.erase(it);
			} else {
				++it;
			}
		}
		if (!g_enabled || !a_linked) {
			return;
		}

		// A portee d'un autel ?
		int   nearest = -1;
		float best = kUseDistance;
		for (std::size_t i = 0; i < g_altars.size(); ++i) {
			const auto& altar = g_altars[i];
			if (!inPlace(altar)) {
				continue;
			}
			const float dx = (altar.x - feet.x) / kUnitsPerMeter;
			const float dy = (altar.y - feet.y) / kUnitsPerMeter;
			const float dz = (altar.z - feet.z) / kUnitsPerMeter;
			const float flat = std::sqrt(dx * dx + dy * dy);
			if (flat < best && std::fabs(dz) < 3.0f) {
				best = flat;
				nearest = static_cast<int>(i);
			}
		}
		// Pour le HUD : l'autel pas encore utilise le plus proche dans ce lieu, et sa distance.
		{
			std::string hint;
			float       closest = 1.0e9f;
			for (const auto& altar : g_altars) {
				if (!inPlace(altar) || Used(altar)) {
					continue;
				}
				const float d = RE::NiPoint3(altar.x, altar.y, altar.z).GetDistance(feet) / kUnitsPerMeter;
				if (d < closest) {
					closest = d;
					hint = fmt::format("{} : {:.0f} m", NameOf(altar), d);
				}
			}
			std::scoped_lock lock(g_hintLock);
			g_hint = std::move(hint);
		}

		if (nearest != g_near) {
			g_near = nearest;
			if (nearest >= 0) {
				const auto& altar = g_altars[nearest];
				Show(Used(altar) ? Lang::F("{} (deja utilise)", NameOf(altar)) : Prompt(altar));
			}
		}

		// La touche "Utiliser" (hors menus).
		auto*      ui = RE::UI::GetSingleton();
		const int  key = Config::Get().Key(Action::kUse);
		const bool down = key != 0 && !(ui && ui->GameIsPaused()) &&
		                  (InputWatcher::Get().IsDown(key) || (::GetAsyncKeyState(key) & 0x8000) != 0);
		if (down && !g_useWasDown && g_near >= 0) {
			Use(g_altars[g_near], a_player);
		}
		g_useWasDown = down;
	}

	void SpawnBazaarPortal(RE::TESObjectREFR* a_chest)
	{
		auto* player = RE::PlayerCharacter::GetSingleton();
		auto* cell = player ? player->GetParentCell() : nullptr;
		if (!player || !cell || !a_chest) {
			return;
		}
		const auto                     info = Survey(player);
		std::unordered_set<RE::FormID> usedContainers;
		std::vector<RE::NiPoint3>      spots;
		Occupied(cell->GetFormID(), usedContainers, spots, nullptr);
		const float  angle = a_chest->GetAngleZ();
		const auto   at = a_chest->GetPosition();
		const float  sx = std::cos(angle), sy = -std::sin(angle);
		const float  fx = std::sin(angle), fy = std::cos(angle);
		RE::NiPoint3 spot;
		bool         found = false;
		for (const float distance : { 2.5f, 3.2f, 4.0f }) {
			for (const float side : { 1.0f, -1.0f }) {
				RE::NiPoint3 candidate(at.x + (sx * side * distance - fx * 0.5f) * kUnitsPerMeter, at.y + (sy * side * distance - fy * 0.5f) * kUnitsPerMeter, at.z);
				if (!found && GoodSpot(player, info, a_chest, candidate, spots)) {
					spot = candidate;
					found = true;
				}
			}
		}
		if (!found) {
			// Pas de bonne place pres du coffre : devant le joueur.
			const float yaw = player->GetAngleZ();
			const auto  feet = player->GetPosition();
			spot = RE::NiPoint3(feet.x + std::sin(yaw) * 3.0f * kUnitsPerMeter, feet.y + std::cos(yaw) * 3.0f * kUnitsPerMeter, feet.z);
		}
		Altar altar;
		altar.type = kPortal;
		altar.exterior = cell->IsExteriorCell() ? 1 : 0;
		auto* world = player->GetWorldspace();
		altar.cell = altar.exterior && world ? world->GetFormID() : cell->GetFormID();
		altar.x = spot.x;
		altar.y = spot.y;
		altar.z = spot.z;
		altar.yaw = angle;
		altar.placed = 1;
		g_altars.push_back(altar);
		logger::info("Autels : porte du Bazar ouverte pres du coffre {:08X}{}", a_chest->GetFormID(), found ? "" : " (devant le joueur)");
		Show(Lang::T("Une porte bleue s'ouvre pres du coffre : le Bazar de Newt !"));
	}

	void Save(SKSE::SerializationInterface* a_intfc)
	{
		if (!a_intfc->OpenRecord(kRecord, kRecordVersion)) {
			return;
		}
		const auto rolled = static_cast<std::uint32_t>(g_rolled.size());
		a_intfc->WriteRecordData(&rolled, sizeof(rolled));
		for (const RE::FormID id : g_rolled) {
			a_intfc->WriteRecordData(&id, sizeof(id));
		}
		const auto count = static_cast<std::uint32_t>(g_altars.size());
		a_intfc->WriteRecordData(&count, sizeof(count));
		for (const auto& altar : g_altars) {
			a_intfc->WriteRecordData(&altar, sizeof(altar));
		}
	}

	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t a_version)
	{
		if (a_type != kRecord) {
			return false;
		}
		g_rolled.clear();
		g_altars.clear();
		std::uint32_t rolled = 0;
		if (a_intfc->ReadRecordData(&rolled, sizeof(rolled)) != sizeof(rolled) || rolled > 100000) {
			return true;
		}
		for (std::uint32_t i = 0; i < rolled; ++i) {
			RE::FormID id = 0, resolved = 0;
			if (a_intfc->ReadRecordData(&id, sizeof(id)) != sizeof(id)) {
				return true;
			}
			if (a_intfc->ResolveFormID(id, resolved)) {
				g_rolled.insert(resolved);
			}
		}
		std::uint32_t count = 0;
		if (a_intfc->ReadRecordData(&count, sizeof(count)) != sizeof(count) || count > 100000) {
			return true;
		}
		for (std::uint32_t i = 0; i < count; ++i) {
			Altar altar;
			if (a_version < 2) {
				AltarV1 old;
				if (a_intfc->ReadRecordData(&old, sizeof(old)) != sizeof(old)) {
					break;
				}
				altar.cell = old.cell;
				altar.type = old.type;
				altar.x = old.x;
				altar.y = old.y;
				altar.z = old.z;
				altar.yaw = old.yaw;
				altar.uses = old.uses;
				altar.tries = old.tries;
			} else if (a_version < 3) {
				AltarV2 old;
				if (a_intfc->ReadRecordData(&old, sizeof(old)) != sizeof(old)) {
					break;
				}
				altar.cell = old.cell;
				altar.type = old.type;
				altar.x = old.x;
				altar.y = old.y;
				altar.z = old.z;
				altar.yaw = old.yaw;
				altar.uses = old.uses;
				altar.tries = old.tries;
				altar.kind = old.kind;
				altar.exterior = old.exterior;
				std::memcpy(altar.item, old.item, sizeof(altar.item));
			} else if (a_intfc->ReadRecordData(&altar, sizeof(altar)) != sizeof(altar)) {
				break;
			}
			if (a_version >= 3 && altar.container != 0) {
				RE::FormID container = 0;
				altar.container = a_intfc->ResolveFormID(altar.container, container) ? container : 0;
			}
			altar.item[sizeof(altar.item) - 1] = '\0';
			RE::FormID resolved = 0;
			if (a_intfc->ResolveFormID(altar.cell, resolved) && altar.type >= 0 && altar.type < kTypeCount) {
				altar.cell = resolved;
				g_altars.push_back(altar);
			}
		}
		logger::info("Autels RoR2 : {} autel(s) dans cette sauvegarde", g_altars.size());
		return true;
	}

	void Revert()
	{
		for (const auto id : g_shownIds) {
			Allies::HideStatic(id);
		}
		g_shownIds.clear();
		g_altars.clear();
		g_rolled.clear();
		g_cell = nullptr;
		g_near = -1;
		g_trail.clear();
	}
}
