#include "Loot.h"
#include "Lang.h"

#include "Altars.h"
#include "Bridge.h"
#include "Combat.h"
#include "Config.h"
#include "Coop.h"
#include "Items.h"

#include <fstream>
#include <random>
#include <sstream>

namespace skyrimror2::Loot
{
	namespace
	{
		using Clock = std::chrono::steady_clock;

		constexpr float         kUnitsPerMeter = 70.0f;
		constexpr float         kStripRadius = 60.0f;  // m autour du joueur
		constexpr auto          kStripEvery = std::chrono::milliseconds(1500);
		constexpr std::uint32_t kPaidRecord = 'LOOT';
		constexpr std::uint32_t kStateRecord = 'LOT2';
		constexpr std::uint32_t kInventoryRecord = 'INVT';
		constexpr std::uint32_t kDayRecord = 'LOT3';   // jour de la derniere mort (la difficulte repart de la)
		constexpr std::uint32_t kRecordVersion = 1;

		// Rangs envoyes a RoR2 (voir bridge_protocol.h, zone 8).
		constexpr std::int32_t kWhite = 0;
		constexpr std::int32_t kGreen = 1;
		constexpr std::int32_t kRed = 2;
		constexpr std::int32_t kYellow = 3;
		constexpr std::int32_t kVoid = 4;
		constexpr std::int32_t kEquipment = 6;
		constexpr std::int32_t kAspect = 9;     // 0.71 : aspect d'elite (nombre = 1 feu, 2 glace, 3 foudre)
		constexpr std::int32_t kHallowed = 10;  // 0.71 : objet sanctifie

		// Reglages ([Objets] et [Difficulte])
		bool  g_enabled = true;
		int   g_priceSmall = 5;
		int   g_priceMedium = 10;
		int   g_priceChest = 25;
		int   g_priceBoss = 50;
		int   g_itemChance = 15;
		int   g_greenChance = 20;
		int   g_bossRedChance = 20;
		bool  g_stripGear = true;
		int   g_yellowChance = 10;
		int   g_voidChance = 35;      // 0.71 : dans les lieux daedriques (avant : ruines dwemers)
		int   g_equipDwemer = 35;     // 0.71 : equipement dans les ruines dwemers
		int   g_hallowedBoss = 15;    // 0.71 : objet sanctifie au coffre du boss d'un donjon difficile
		int   g_hallowedDragon = 5;   // 0.71 : objet sanctifie sur un dragon
		int   g_hardLevel = 30;       // 0.71 : niveau du boss a partir duquel un donjon est "difficile"
		int   g_aspectOneIn = 300;    // 0.71 : 1 chance sur N qu'une elite lache son aspect
		int   g_portalOneIn = 20;     // 0.71 : 1 chance sur N d'une porte du Bazar apres un coffre de boss
		int   g_lastBossLevel = 0;    // 0.71 : niveau du dernier boss de donjon tue (et sa cellule)
		RE::FormID g_lastBossCell = 0;
		int   g_coinChance = 3;
		float g_priceGrowth = 0.15f;   // prix x (1 + 0,15 x difficulte)
		float g_dayWeight = 0.25f;     // difficulte gagnee par jour de jeu
		float g_chargeSeconds = 60.0f;
		float g_chargeRadius = 12.0f;  // m
		float g_waveSeconds = 15.0f;
		int   g_waveSize = 2;

		std::atomic_bool g_linked{ false };

		std::mutex                     g_lock;
		std::unordered_set<RE::FormID> g_paid;      // contenants deja payes (sauvegardes)
		std::unordered_set<RE::FormID> g_stripped;  // contenants deja vides de leurs armes et armures (cette session)
		Clock::time_point              g_nextStrip{};

		// Progression (sauvegardee)
		std::atomic_int    g_dungeonsDone{ 0 };
		std::atomic_int    g_coins{ 0 };
		std::atomic_int    g_mountain{ 0 };  // autels de la Montagne actives : objets et ennemis en plus a la prochaine charge
		std::atomic<float> g_difficulty{ 0.0f };
		std::atomic<float> g_dayOffset{ 0.0f };  // jours de jeu deja passes a la derniere mort

		// La charge du coffre du boss (fil principal)
		struct Charge
		{
			bool                  active = false;
			RE::FormID            chest = 0;
			RE::NiPoint3          center;
			float                 progress = 0.0f;  // 0 a 1
			float                 sinceWave = 0.0f;
			int                   waves = 0;
			std::vector<std::pair<RE::TESNPC*, RE::FormID>> sources;  // qui faire revenir, et a cote de quoi
			bool                  mirror = false;   // 0.70 : charge d'un coequipier suivie ici (sans renforts, sans paiement)
		};
		Charge                         g_charge;
		std::atomic<float>             g_chargeShown{ -1.0f };
		std::unordered_set<RE::FormID> g_chargePaid;  // coffre du boss deja paye (charge commencee)
		Clock::time_point              g_lastFrame{};
		// 0.70 (coop) : coffres de boss deja comptes (une fois par coffre, meme termine a plusieurs), et contenants ouverts
		// par un coequipier, a vider ici quand ils sont charges.
		std::unordered_set<RE::FormID> g_counted;
		std::unordered_set<RE::FormID> g_toEmpty;
		Clock::time_point              g_nextEmpty{};

		std::uint32_t g_lastPickup = 0;
		bool          g_pickupStarted = false;
		std::uint32_t g_lastGoldEarned = 0;
		bool          g_goldStarted = false;
		float         g_goldRatio = 0.5f;  // septims par piece d'or RoR2
		std::atomic_int g_septims{ 0 };     // septims du joueur (pour le HUD, lu depuis un autre fil)
		// Les objets RoR2 de la sauvegarde (texte ecrit par RoR2 : inventaire.txt)
		// Attente de RoR2 apres un chargement : le temps de Skyrim est quasi arrete tant que RoR2 n'est pas en partie.
		bool                   g_holding = false;
		Clock::time_point      g_holdSince{};
		Clock::time_point      g_holdNextMessage{};
		int                    g_holdSeconds = 90;
		std::string            g_savedInventory;
		bool                   g_hasSavedInventory = false;
		std::atomic_uint32_t   g_inventorySequence{ 0 };
		// Texte sous le viseur (prix du contenant vise), lu par le HUD
		std::mutex             g_hoverLock;
		std::string            g_hoverText;
		RE::TESObjectCELL*  g_stripCell = nullptr;
		Clock::time_point   g_stripCellSince{};

		std::mt19937& Random()
		{
			static std::mt19937 engine{ std::random_device{}() };
			return engine;
		}

		bool Roll(int a_percent)
		{
			return std::uniform_int_distribution<int>(1, 100)(Random()) <= a_percent;
		}

		void Show(std::string a_text)
		{
			SKSE::GetTaskInterface()->AddTask([text = std::move(a_text)]() { RE::SendHUDMessage::ShowHUDMessage(text.c_str()); });
		}

		std::string Lower(std::string a_text)
		{
			for (auto& c : a_text) {
				c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
			}
			return a_text;
		}

		bool HasLocRefType(RE::TESObjectREFR* a_ref, std::string_view a_word)
		{
			const auto*        extra = a_ref->extraList.GetByType<RE::ExtraLocationRefType>();
			const RE::TESForm* type = extra ? reinterpret_cast<const RE::TESForm*>(extra->locRefType) : nullptr;
			const char*        name = type ? type->GetFormEditorID() : nullptr;
			return name && std::string_view(name).find(a_word) != std::string_view::npos;
		}

		// Le coffre du boss en fin de donjon : Skyrim le marque "BossContainer".
		bool IsBossContainer(RE::TESObjectREFR* a_ref) { return HasLocRefType(a_ref, "BossContainer"); }

		// Le lieu ou se trouve le joueur a-t-il un mot-cle contenant ce mot ? ("Dwarven", "Daedr"...)
		bool PlaceHas(RE::PlayerCharacter* a_player, std::string_view a_word)
		{
			auto* location = a_player ? a_player->GetCurrentLocation() : nullptr;
			bool  found = false;
			while (location && !found) {
				location->ForEachKeyword([&](RE::BGSKeyword* a_keyword) {
					const char* id = a_keyword ? a_keyword->GetFormEditorID() : nullptr;
					if (id && std::string_view(id).find(a_word) != std::string_view::npos) {
						found = true;
						return RE::BSContainer::ForEachResult::kStop;
					}
					return RE::BSContainer::ForEachResult::kContinue;
				});
				location = location->parentLoc;
			}
			return found;
		}

		bool IsDwemer(RE::TESObjectREFR* a_ref, RE::PlayerCharacter* a_player)
		{
			auto*       base = a_ref->GetBaseObject();
			std::string name = Lower(base && base->GetName() ? base->GetName() : "");
			if (auto* model = base ? base->As<RE::TESModel>() : nullptr; model && model->GetModel()) {
				name += "|" + Lower(model->GetModel());  // 0.74 : le meme dans toutes les langues
			}
			for (const char* word : { "dwemer", "dwarven", "dwenmer", "dwem" }) {
				if (name.find(word) != std::string::npos) {
					return true;
				}
			}
			return PlaceHas(a_player, "Dwarven") || PlaceHas(a_player, "Dwemer");
		}

		// 0.71 : tous les lieux daedriques (sanctuaires, autels daedriques des donjons, Apocrypha, Ame-Tertre...).
		bool IsDaedric(RE::PlayerCharacter* a_player)
		{
			if (PlaceHas(a_player, "Daedr")) {
				return true;
			}
			std::string names;
			if (auto* world = a_player->GetWorldspace()) {
				names += Lower(world->GetFormEditorID() ? world->GetFormEditorID() : "");
			}
			if (auto* cell = a_player->GetParentCell()) {
				names += "|" + Lower(cell->GetFormEditorID() ? cell->GetFormEditorID() : "");
				names += "|" + Lower(cell->GetName() ? cell->GetName() : "");
			}
			for (const char* word : { "apocrypha", "soulcairn", "evergloam", "daedr", "oblivion", "shrineof", "sanctuaire" }) {
				if (names.find(word) != std::string::npos) {
					return true;
				}
			}
			return false;
		}

		enum class Size
		{
			kSmall,
			kMedium,
			kChest,
			kBoss
		};

		Size Classify(RE::TESObjectREFR* a_ref)
		{
			if (IsBossContainer(a_ref)) {
				return Size::kBoss;
			}
			auto*       base = a_ref->GetBaseObject();
			std::string name = Lower(base && base->GetName() ? base->GetName() : "");
			// 0.74 : le chemin du modele ne change pas avec la langue de Skyrim ("clutter\\containers\\chest01.nif").
			if (auto* model = base ? base->As<RE::TESModel>() : nullptr; model && model->GetModel()) {
				name += "|" + Lower(model->GetModel());
			}
			for (const char* word : { "sac", "bourse", "sacoche", "sack", "satchel", "pouch", "purse" }) {
				if (name.find(word) != std::string::npos) {
					return Size::kSmall;
				}
			}
			for (const char* word : { "coffre", "chest", "strongbox", "malle" }) {
				if (name.find(word) != std::string::npos) {
					return Size::kChest;
				}
			}
			return Size::kMedium;  // urnes, tonneaux, paniers, caisses...
		}

		int BasePrice(Size a_size)
		{
			switch (a_size) {
			case Size::kSmall:
				return g_priceSmall;
			case Size::kChest:
				return g_priceChest;
			case Size::kBoss:
				return g_priceBoss;
			default:
				return g_priceMedium;
			}
		}

		std::string SizeName(Size a_size)
		{
			switch (a_size) {
			case Size::kSmall:
				return Lang::T("petit contenant");
			case Size::kChest:
				return Lang::T("coffre");
			case Size::kBoss:
				return Lang::T("coffre du boss");
			default:
				return Lang::T("contenant");
			}
		}

		// Tes propres contenants (maison) : gratuits.
		bool OwnedByPlayer(RE::TESObjectREFR* a_ref, RE::PlayerCharacter* a_player)
		{
			const auto* owner = a_ref->GetOwner();
			if (!owner) {
				return false;
			}
			return owner == a_player->GetActorBase() || owner->GetFormID() == 0x7 || owner->GetFormID() == 0xDB1;  // PlayerFaction
		}

		bool IsContainer(RE::TESObjectREFR* a_ref)
		{
			auto* base = a_ref ? a_ref->GetBaseObject() : nullptr;
			return base && base->Is(RE::FormType::Container) && !a_ref->As<RE::Actor>();
		}

		// (0.72 : pas nos amulettes d'equipement RoR2, qui viennent de SkyrimRoR2.esp)
		bool IsOurs(RE::TESForm* a_form)
		{
			auto* file = a_form ? a_form->GetFile(0) : nullptr;
			return file && _stricmp(file->GetFilename().data(), "SkyrimRoR2.esp") == 0;
		}

		bool IsGear(RE::TESBoundObject* a_object)
		{
			return a_object && (a_object->IsWeapon() || a_object->IsArmor() || a_object->IsAmmo()) && !IsOurs(a_object);
		}

		// Retire les armes, fleches et armures d'un contenant (pas les objets de quete). Renvoie le nombre retire.
		// a_force : meme si on l'a deja fait (a l'ouverture : le butin "au hasard" de Skyrim n'est tire qu'a ce moment,
		// les armes d'un coffre visite avant revenaient donc).
		int Strip(RE::TESObjectREFR* a_ref, bool a_force = false)
		{
			if (!g_stripGear) {
				return 0;
			}
			{
				std::scoped_lock lock(g_lock);
				if (!g_stripped.insert(a_ref->GetFormID()).second && !a_force) {
					return 0;
				}
			}
			std::vector<std::pair<RE::TESBoundObject*, std::int32_t>> remove;
			for (auto& [object, data] : a_ref->GetInventory()) {
				if (!object || data.first <= 0 || !IsGear(object)) {
					continue;
				}
				if (data.second && data.second->IsQuestObject()) {
					continue;
				}
				remove.emplace_back(object, data.first);
			}
			for (auto& [object, count] : remove) {
				a_ref->RemoveItem(object, count, RE::ITEM_REMOVE_REASON::kRemove, nullptr, nullptr);
			}
			if (!remove.empty()) {
				static int logged = 0;
				if (++logged <= 10 || logged % 50 == 0) {
					logger::info("Objets : {} arme(s)/armure(s) retiree(s) du contenant {:08X}", remove.size(), a_ref->GetFormID());
				}
			}
			return static_cast<int>(remove.size());
		}

		// Les armes que les ennemis lachent en mourant tombent par terre (hors de leur inventaire) : on les retire
		// un peu apres la mort.
		std::vector<std::pair<RE::FormID, Clock::time_point>> g_deadToClean;

		void CleanDroppedWeapons()
		{
			if (g_deadToClean.empty() || !g_stripGear) {
				return;
			}
			const auto now = Clock::now();
			auto*      tes = RE::TES::GetSingleton();
			for (auto it = g_deadToClean.begin(); it != g_deadToClean.end();) {
				if (now < it->second) {
					++it;
					continue;
				}
				auto* dead = RE::TESForm::LookupByID<RE::Actor>(it->first);
				int   removed = 0;
				if (dead && tes) {
					tes->ForEachReferenceInRange(dead, 8.0f * kUnitsPerMeter, [&](RE::TESObjectREFR* a_ref) {
						auto* base = a_ref ? a_ref->GetBaseObject() : nullptr;
						// Seulement les objets crees en cours de jeu (FF......) : pas les armes posees du donjon (presentoirs, quetes).
						if (base && a_ref != dead && (a_ref->GetFormID() >> 24) == 0xFF && !a_ref->IsDisabled() && (base->IsWeapon() || base->IsAmmo()) &&
							!a_ref->As<RE::Actor>()) {
							a_ref->Disable();
							a_ref->SetDelete(true);
							++removed;
						}
						return RE::BSContainer::ForEachResult::kContinue;
					});
				}
				if (removed > 0) {
					logger::info("Objets : {} arme(s) lachee(s) par {:08X} retiree(s)", removed, it->first);
				}
				it = g_deadToClean.erase(it);
			}
		}

		std::int32_t GoldCount(RE::PlayerCharacter* a_player)
		{
			std::int32_t count = 0;
			for (auto& [object, data] : a_player->GetInventory([](RE::TESBoundObject& a_object) { return a_object.IsGold(); })) {
				count += data.first;
			}
			return count;
		}

		void CloseContainer()
		{
			if (auto* queue = RE::UIMessageQueue::GetSingleton()) {
				queue->AddMessage(RE::BSFixedString(RE::ContainerMenu::MENU_NAME.data()), RE::UI_MESSAGE_TYPE::kHide, nullptr);
			}
		}

		// Le rang de l'objet du coffre du boss : vert, ou rouge par chance.
		std::int32_t BossTier() { return Roll(g_bossRedChance) ? kRed : kGreen; }


		std::filesystem::path DataFolder()
		{
			char folder[MAX_PATH]{};
			if (::GetEnvironmentVariableA("LOCALAPPDATA", folder, MAX_PATH) == 0) {
				return {};
			}
			return std::filesystem::path(folder) / "SkyrimRoR2";
		}

		std::string ReadText(const std::filesystem::path& a_path)
		{
			std::ifstream      file(a_path, std::ios::binary);
			std::ostringstream text;
			text << file.rdbuf();
			return text.str();
		}

		// Le prix du contenant sous le viseur.
		void UpdateHover(RE::PlayerCharacter* a_player)
		{
			std::string text;
			auto*       pick = RE::CrosshairPickData::GetSingleton();
			auto        targetPtr = pick ? pick->GetActiveTarget().get() : RE::NiPointer<RE::TESObjectREFR>{};
			auto*       ref = targetPtr.get();
			if (ref && IsContainer(ref) && !OwnedByPlayer(ref, a_player)) {
				bool paid = false;
				{
					std::scoped_lock lock(g_lock);
					paid = g_paid.contains(ref->GetFormID());
				}
				if (!paid) {
					const Size size = Classify(ref);
					const int  price = ScaledPrice(BasePrice(size));
					if (size == Size::kBoss) {
						text = g_chargePaid.contains(ref->GetFormID()) ? std::string(Lang::T("Charge du coffre du boss (deja payee)")) :
						                                                    Lang::F("Charge du coffre du boss : {} septims", price);
					} else if (price > 0) {
						text = Lang::F("{} septims", price);
					}
				}
			}
			std::scoped_lock lock(g_hoverLock);
			g_hoverText = std::move(text);
		}

		// ---------- La charge du coffre du boss ----------

		// Les ennemis du donjon (vivants ou morts) : ce sont eux qui reviennent en renfort, la ou ils sont tombes.
		void CollectSources(RE::PlayerCharacter* a_player, RE::TESObjectREFR* a_chest)
		{
			g_charge.sources.clear();
			auto* tes = RE::TES::GetSingleton();
			if (!tes) {
				return;
			}
			std::unordered_set<RE::TESNPC*> bases;
			tes->ForEachReferenceInRange(a_chest, 80.0f * kUnitsPerMeter, [&](RE::TESObjectREFR* a_ref) {
				auto* actor = a_ref ? a_ref->As<RE::Actor>() : nullptr;
				if (!actor || actor == a_player || actor->IsPlayerTeammate() || actor->IsCommandedActor()) {
					return RE::BSContainer::ForEachResult::kContinue;
				}
				auto* base = actor->GetActorBase();
				if (!base || base->IsUnique() || base->IsEssential()) {
					return RE::BSContainer::ForEachResult::kContinue;
				}
				// Les morts du donjon, ou les vivants hostiles.
				if (actor->IsDead() || actor->IsHostileToActor(a_player)) {
					if (g_charge.sources.size() < 24) {
						g_charge.sources.emplace_back(base, actor->GetFormID());
					}
					bases.insert(base);
				}
				return RE::BSContainer::ForEachResult::kContinue;
			});
			logger::info("Objets : charge du coffre du boss : {} point(s) de renfort, {} sorte(s) d'ennemis", g_charge.sources.size(), bases.size());
		}

		void SpawnWave(RE::TESObjectREFR* a_chest)
		{
			if (g_charge.sources.empty()) {
				return;
			}
			const int count = std::min(g_waveSize + static_cast<int>(g_difficulty.load() / 2.0f) + g_mountain.load(), 6);
			int       spawned = 0;
			for (int i = 0; i < count; ++i) {
				const auto& [base, at] = g_charge.sources[std::uniform_int_distribution<std::size_t>(0, g_charge.sources.size() - 1)(Random())];
				auto*       anchor = RE::TESForm::LookupByID<RE::TESObjectREFR>(at);
				if (!anchor || anchor->GetParentCell() != a_chest->GetParentCell()) {
					anchor = a_chest;
				}
				auto placed = anchor->PlaceObjectAtMe(base, false);
				if (placed) {
					++spawned;
				}
			}
			++g_charge.waves;
			logger::info("Objets : charge : vague {} ({} renfort(s))", g_charge.waves, spawned);
			if (spawned > 0) {
				Show(Lang::T("Des renforts arrivent !"));
			}
		}

		void StartCharge(RE::TESObjectREFR* a_chest, RE::PlayerCharacter* a_player)
		{
			g_charge = Charge{};
			g_charge.active = true;
			g_charge.chest = a_chest->GetFormID();
			g_charge.center = a_chest->GetPosition();
			g_charge.sinceWave = g_waveSeconds - 3.0f;  // premiere vague tout de suite
			CollectSources(a_player, a_chest);
			Show(Lang::F("Charge du coffre ! Reste a moins de {:.0f} m pendant {:.0f} s", g_chargeRadius, g_chargeSeconds));
		}

		void FinishCharge(RE::TESObjectREFR* a_chest)
		{
			const int extra = g_mountain.exchange(0);
			Bridge::Get().WriteLoot(BossTier(), 1);
			for (int i = 0; i < extra; ++i) {
				Bridge::Get().WriteLoot(BossTier(), 1);
			}
			// 0.71 : donjon difficile (boss de haut niveau) -> chance d'un objet sanctifie en plus.
			if (auto* player = RE::PlayerCharacter::GetSingleton()) {
				auto*     cell = player->GetParentCell();
				const int level = cell && cell->GetFormID() == g_lastBossCell ? g_lastBossLevel : static_cast<int>(player->GetLevel());
				if (level >= g_hardLevel && Roll(g_hallowedBoss)) {
					Bridge::Get().WriteLoot(kHallowed, 1);
					logger::info("Objets : donjon difficile (niveau {}) : objet sanctifie", level);
				}
			}
			// 0.71 : parfois, une porte bleue vers le Bazar de Newt.
			if (a_chest && g_portalOneIn > 0 && std::uniform_int_distribution<int>(1, g_portalOneIn)(Random()) == 1) {
				Altars::SpawnBazaarPortal(a_chest);
			}
			{
				std::scoped_lock lock(g_lock);
				g_paid.insert(g_charge.chest);
			}
			g_chargePaid.insert(g_charge.chest);
			// Coop : le coffre ne compte qu'une fois pour la difficulte, meme si plusieurs joueurs le terminent.
			const bool counted = !g_counted.insert(g_charge.chest).second;
			Coop::ReportFinished(g_charge.chest);
			const int done = counted ? g_dungeonsDone.load() : ++g_dungeonsDone;
			g_charge.active = false;
			g_chargeShown = -1.0f;
			logger::info("Objets : charge terminee ({} objet(s) en plus), {} donjon(s) termine(s)", extra, done);
			Show(Lang::F("Coffre charge ! Difficulte : {:.1f}", g_difficulty.load() + 1.0f));
		}

		float g_remoteProgressScratch = 0.0f;

		// 0.70 (coop) : un coequipier charge le coffre du boss de ce lieu : on suit sa charge ici (sans payer, sans
		// renforts de notre cote). Rester dans la zone la fait avancer aussi ; a la fin, chacun recoit son objet.
		void FollowRemoteCharge(RE::PlayerCharacter* a_player)
		{
			RE::FormID chest = 0;
			float      progress = 0.0f;
			if (g_charge.active || !Coop::RemoteCharge(chest, progress) || g_counted.contains(chest)) {
				return;
			}
			auto* ref = RE::TESForm::LookupByID<RE::TESObjectREFR>(chest);
			if (!ref || ref->GetParentCell() != a_player->GetParentCell()) {
				return;
			}
			g_charge = Charge{};
			g_charge.active = true;
			g_charge.mirror = true;
			g_charge.chest = chest;
			g_charge.center = ref->GetPosition();
			g_charge.progress = progress;
			g_chargePaid.insert(chest);
			logger::info("Objets : charge du coffre {:08X} d'un coequipier suivie ({:.0f} %)", chest, progress * 100.0f);
			Show(Lang::F("Un coequipier charge le coffre du boss : reste a moins de {:.0f} m pour avoir ton objet", g_chargeRadius));
		}

		// 0.70 (coop) : les contenants ouverts par un coequipier sont vides ici (sauf objets de quete), quand ils sont charges.
		void EmptyRemoteContainers()
		{
			const auto now = Clock::now();
			if (g_toEmpty.empty() || now < g_nextEmpty) {
				return;
			}
			g_nextEmpty = now + std::chrono::seconds(1);
			for (auto it = g_toEmpty.begin(); it != g_toEmpty.end();) {
				auto* ref = RE::TESForm::LookupByID<RE::TESObjectREFR>(*it);
				if (!ref) {
					it = g_toEmpty.erase(it);
					continue;
				}
				auto* cell = ref->GetParentCell();
				if (!cell || !cell->IsAttached() || !IsContainer(ref)) {
					++it;
					continue;
				}
				std::vector<std::pair<RE::TESBoundObject*, std::int32_t>> remove;
				for (auto& [object, data] : ref->GetInventory()) {
					if (object && data.first > 0 && !(data.second && data.second->IsQuestObject())) {
						remove.emplace_back(object, data.first);
					}
				}
				for (auto& [object, count] : remove) {
					ref->RemoveItem(object, count, RE::ITEM_REMOVE_REASON::kRemove, nullptr, nullptr);
				}
				logger::info("Objets : contenant {:08X} ouvert par un coequipier, vide ici ({} sorte(s) d'objets)", *it, remove.size());
				it = g_toEmpty.erase(it);
			}
		}

		void UpdateCharge(RE::PlayerCharacter* a_player, float a_delta)
		{
			FollowRemoteCharge(a_player);
			Coop::ReportCharge(g_charge.active && !g_charge.mirror ? g_charge.chest : 0, g_charge.progress);
			if (!g_charge.active) {
				g_chargeShown = -1.0f;
				return;
			}
			auto* chest = RE::TESForm::LookupByID<RE::TESObjectREFR>(g_charge.chest);
			if (!chest || chest->GetParentCell() != a_player->GetParentCell()) {
				g_chargeShown = g_charge.progress;  // on est parti : la charge attend
				return;
			}
			const float distance = a_player->GetPosition().GetDistance(g_charge.center) / kUnitsPerMeter;
			if (distance <= g_chargeRadius) {
				g_charge.progress = std::min(1.0f, g_charge.progress + a_delta / std::max(g_chargeSeconds, 5.0f));
			}
			g_charge.sinceWave += a_delta;
			// La charge d'un coequipier sur le meme coffre avance aussi la notre (la plus avancee des deux).
			if (RE::FormID remote = 0; Coop::RemoteCharge(remote, g_remoteProgressScratch) && remote == g_charge.chest) {
				g_charge.progress = std::max(g_charge.progress, g_remoteProgressScratch);
			}
			if (!g_charge.mirror && g_charge.sinceWave >= g_waveSeconds && g_charge.progress < 0.95f) {
				g_charge.sinceWave = 0.0f;
				SpawnWave(chest);
			}
			g_chargeShown = g_charge.progress;
			if (g_charge.progress >= 1.0f) {
				FinishCharge(chest);
			}
		}

		// ---------- Ouverture d'un contenant ----------

		// Un contenant vient d'etre ouvert (fil principal).
		void OnOpened(RE::RefHandle a_handle)
		{
			if (!g_enabled || !g_linked) {
				return;
			}
			RE::NiPointer<RE::TESObjectREFR> refPtr;
			if (!RE::TESObjectREFR::LookupByHandle(a_handle, refPtr) || !refPtr) {
				return;
			}
			auto* ref = refPtr.get();
			auto* player = RE::PlayerCharacter::GetSingleton();
			if (!player || !IsContainer(ref) || OwnedByPlayer(ref, player)) {
				return;
			}
			{
				std::scoped_lock lock(g_lock);
				if (g_paid.contains(ref->GetFormID())) {
					return;
				}
			}
			if (Strip(ref, true) > 0) {
				// Le menu est deja ouvert : on lui demande de se remettre a jour (sinon les armes restaient affichees).
				if (auto* queue = RE::UIMessageQueue::GetSingleton()) {
					queue->AddMessage(RE::BSFixedString(RE::ContainerMenu::MENU_NAME.data()), RE::UI_MESSAGE_TYPE::kUpdate, nullptr);
				}
			}

			const Size size = Classify(ref);

			// Le coffre du boss : sa charge d'abord (payee une seule fois).
			if (size == Size::kBoss) {
				CloseContainer();
				if (g_charge.active && g_charge.chest == ref->GetFormID()) {
					Show(Lang::F("Charge en cours : {:.0f} %", g_charge.progress * 100.0f));
					return;
				}
				if (!g_chargePaid.contains(ref->GetFormID())) {
					const int price = ScaledPrice(BasePrice(size));
					const auto gold = GoldCount(player);
					if (price > 0 && gold < price) {
						Show(Lang::F("Il faut {} septims pour charger le coffre du boss (tu en as {})", price, gold));
						return;
					}
					if (price > 0) {
						if (auto* septim = RE::TESForm::LookupByID<RE::TESBoundObject>(0xF)) {
							player->RemoveItem(septim, price, RE::ITEM_REMOVE_REASON::kRemove, nullptr, nullptr);
						}
					}
					g_chargePaid.insert(ref->GetFormID());
				}
				StartCharge(ref, player);
				return;
			}

			const int price = ScaledPrice(BasePrice(size));
			if (price > 0) {
				const auto gold = GoldCount(player);
				if (gold < price) {
					CloseContainer();
					Show(Lang::F("Il faut {} septims pour ouvrir ce {} (tu en as {})", price, SizeName(size), gold));
					return;
				}
				if (auto* septim = RE::TESForm::LookupByID<RE::TESBoundObject>(0xF)) {
					player->RemoveItem(septim, price, RE::ITEM_REMOVE_REASON::kRemove, nullptr, nullptr);
				}
			}
			{
				std::scoped_lock lock(g_lock);
				g_paid.insert(ref->GetFormID());
			}
			Coop::ReportOpened(ref->GetFormID());  // 0.70 : vide aussi chez les coequipiers

			std::int32_t tier = -1;
			if (Roll(g_itemChance)) {
				tier = Roll(g_greenChance) ? kGreen : kWhite;
				// 0.71 : ruines dwemers -> equipements ; lieux daedriques -> objets du vide.
				if (Roll(g_equipDwemer) && IsDwemer(ref, player)) {
					tier = kEquipment;
				} else if (Roll(g_voidChance) && IsDaedric(player)) {
					tier = kVoid;
				}
			}
			if (tier >= 0) {
				Bridge::Get().WriteLoot(tier, 1);
			}
			logger::info("Objets : {} {:08X} ouvert ({} septims){}", SizeName(size), ref->GetFormID(), price,
				tier >= 0 ? fmt::format(", objet RoR2 de rang {} demande", tier) : "");
			if (price > 0 && tier < 0) {
				Show(Lang::F("-{} septims", price));
			}
		}

		class MenuSink final : public RE::BSTEventSink<RE::MenuOpenCloseEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::MenuOpenCloseEvent* a_event, RE::BSTEventSource<RE::MenuOpenCloseEvent>*) override
			{
				if (!a_event || !a_event->opening || std::string_view(a_event->menuName.c_str()) != RE::ContainerMenu::MENU_NAME) {
					return RE::BSEventNotifyControl::kContinue;
				}
				const auto mode = RE::ContainerMenu::GetContainerMode();
				if (mode != RE::ContainerMenu::ContainerMode::kLoot && mode != RE::ContainerMenu::ContainerMode::kSteal) {
					return RE::BSEventNotifyControl::kContinue;  // poches d'un PNJ, inventaire d'un compagnon : gratuit
				}
				const RE::RefHandle handle = RE::ContainerMenu::GetTargetRefHandle();
				SKSE::GetTaskInterface()->AddTask([handle]() {
					try {
						OnOpened(handle);
					} catch (const std::exception& e) {
						logger::error("Objets : {}", e.what());
					}
				});
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		// ---------- Morts : objets jaunes et pieces lunaires ----------

		class DeathSink final : public RE::BSTEventSink<RE::TESDeathEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESDeathEvent* a_event, RE::BSTEventSource<RE::TESDeathEvent>*) override
			{
				if (!a_event || !a_event->dead || !a_event->actorDying || !g_enabled || !g_linked) {
					return RE::BSEventNotifyControl::kContinue;
				}
				auto* actor = a_event->actorDying->As<RE::Actor>();
				auto* player = RE::PlayerCharacter::GetSingleton();
				if (!actor || !player || actor == player || actor->IsPlayerTeammate() || actor->IsCommandedActor()) {
					return RE::BSEventNotifyControl::kContinue;
				}
				if (actor->GetPosition().GetDistance(player->GetPosition()) > 100.0f * kUnitsPerMeter) {
					return RE::BSEventNotifyControl::kContinue;
				}
				// Ses armes et armures disparaissent (inutiles pour un survivant), sauf objets de quete.
				if (g_stripGear) {
					const RE::FormID id = actor->GetFormID();
					SKSE::GetTaskInterface()->AddTask([id]() {
						auto* dead = RE::TESForm::LookupByID<RE::Actor>(id);
						if (!dead) {
							return;
						}
						std::vector<std::pair<RE::TESBoundObject*, std::int32_t>> remove;
						for (auto& [object, data] : dead->GetInventory()) {
							if (object && data.first > 0 && IsGear(object) && !(data.second && data.second->IsQuestObject())) {
								remove.emplace_back(object, data.first);
							}
						}
						for (auto& [object, count] : remove) {
							dead->RemoveItem(object, count, RE::ITEM_REMOVE_REASON::kRemove, nullptr, nullptr);
						}
						// L'arme en main tombe par terre (parfois tard, ou loin quand le corps est projete) : deux passages.
						g_deadToClean.emplace_back(id, Clock::now() + std::chrono::milliseconds(1500));
						g_deadToClean.emplace_back(id, Clock::now() + std::chrono::seconds(6));
					});
				}

				auto*      killer = a_event->actorKiller ? a_event->actorKiller.get() : nullptr;
				const bool byPlayer = !killer || killer == player;

				// Dragons et boss de donjon : petite chance d'objet jaune.
				auto*      race = actor->GetRace();
				const bool dragon = race && race->HasKeywordString("ActorTypeDragon");
				const bool boss = HasLocRefType(actor, "Boss");
				if ((dragon || boss) && Roll(g_yellowChance)) {
					Bridge::Get().WriteLoot(kYellow, 1);
					logger::info("Objets : {} {:08X} tue : objet jaune", dragon ? "dragon" : "boss", actor->GetFormID());
				}
				if (boss) {
					g_lastBossLevel = static_cast<int>(actor->GetLevel());
					g_lastBossCell = actor->GetParentCell() ? actor->GetParentCell()->GetFormID() : 0;
				}
				// 0.71 : dragons -> petite chance d'objet sanctifie.
				if (dragon && Roll(g_hallowedDragon)) {
					Bridge::Get().WriteLoot(kHallowed, 1);
					logger::info("Objets : dragon {:08X} tue : objet sanctifie", actor->GetFormID());
				}
				// 0.71 : une elite tuee peut lacher son aspect (son equipement d'elite).
				if (byPlayer && g_aspectOneIn > 0) {
					const int elite = Combat::EliteType(actor->GetFormID());
					if (elite > 0 && std::uniform_int_distribution<int>(1, g_aspectOneIn)(Random()) == 1) {
						Bridge::Get().WriteLoot(kAspect, elite);
						Show(Lang::T("L'elite lache son aspect !"));
						logger::info("Objets : elite {:08X} tuee : aspect {}", actor->GetFormID(), elite);
					}
				}

				// Pieces lunaires : rarement, sur les ennemis tues par toi (ou tes allies RoR2).
				if (byPlayer && Roll(g_coinChance)) {
					const int coins = ++g_coins;
					Show(Lang::F("Piece lunaire ! ({} en tout)", coins));
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		// L'arme qu'un ennemi tient tombe par terre a sa mort (elle quitte son inventaire) : on la retire a ce moment
		// precis, comme ses armures (le nettoyage apres coup reste en secours).
		class DropSink final : public RE::BSTEventSink<RE::TESContainerChangedEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESContainerChangedEvent* a_event, RE::BSTEventSource<RE::TESContainerChangedEvent>*) override
			{
				if (!a_event || !g_stripGear || !g_enabled || !g_linked || a_event->newContainer != 0 || a_event->oldContainer == 0 ||
					a_event->oldContainer == 0x14) {
					return RE::BSEventNotifyControl::kContinue;
				}
				// Jamais pendant un chargement : Skyrim remet alors les inventaires en place (et y toucher le faisait planter).
				if (auto* ui = RE::UI::GetSingleton(); !ui || ui->IsMenuOpen(RE::LoadingMenu::MENU_NAME)) {
					return RE::BSEventNotifyControl::kContinue;
				}
				auto* object = RE::TESForm::LookupByID<RE::TESBoundObject>(a_event->baseObj);
				if (!object || !(object->IsWeapon() || object->IsAmmo())) {
					return RE::BSEventNotifyControl::kContinue;
				}
				auto* owner = RE::TESForm::LookupByID<RE::Actor>(a_event->oldContainer);
				if (!owner || owner->IsPlayerTeammate() || owner->IsCommandedActor()) {
					return RE::BSEventNotifyControl::kContinue;
				}
				const RE::FormID          ownerId = a_event->oldContainer;
				const RE::ObjectRefHandle dropped = a_event->reference;
				SKSE::GetTaskInterface()->AddTask([ownerId, dropped]() {
					auto* actor = RE::TESForm::LookupByID<RE::Actor>(ownerId);
					auto  ref = dropped.get();
					auto* ui = RE::UI::GetSingleton();
					// Seulement un ennemi vraiment mort, une arme creee en jeu (FF......), hors chargement.
					if (!actor || !ref || !actor->IsDead() || (ref->GetFormID() >> 24) != 0xFF || !g_linked ||
						!ui || ui->IsMenuOpen(RE::LoadingMenu::MENU_NAME) || ref->IsDisabled()) {
						return;  // un ennemi vivant desarme garde son arme par terre (il va la ramasser)
					}
					ref->Disable();
					ref->SetDelete(true);
					static int logged = 0;
					if (++logged <= 10 || logged % 50 == 0) {
						logger::info("Objets : arme lachee par {:08X} retiree des sa chute", ownerId);
					}
				});
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		int ReadInt(const char* a_section, const char* a_key, int a_default, const std::string& a_ini)
		{
			return static_cast<int>(::GetPrivateProfileIntA(a_section, a_key, a_default, a_ini.c_str()));
		}

		float ReadFloat(const char* a_section, const char* a_key, float a_default, const std::string& a_ini)
		{
			char text[32]{};
			const auto fallback = fmt::format("{}", a_default);
			::GetPrivateProfileStringA(a_section, a_key, fallback.c_str(), text, sizeof(text), a_ini.c_str());
			return static_cast<float>(std::atof(text));
		}
	}

	int ScaledPrice(int a_base)
	{
		if (a_base <= 0) {
			return 0;
		}
		return static_cast<int>(std::lround(a_base * (1.0f + g_priceGrowth * g_difficulty.load())));
	}

	void RequestLoot(std::int32_t a_tier, std::int32_t a_count) { Bridge::Get().WriteLoot(a_tier, a_count); }

	float Difficulty() { return g_difficulty.load(); }

	void OnRemoteOpened(RE::FormID a_container)
	{
		{
			std::scoped_lock lock(g_lock);
			if (!g_paid.insert(a_container).second) {
				return;  // deja ouvert ici
			}
		}
		if (g_toEmpty.size() < 512) {
			g_toEmpty.insert(a_container);
		}
	}

	void OnRemoteFinished(RE::FormID a_chest)
	{
		// Notre charge sur ce coffre : terminee aussi, avec notre objet.
		if (g_charge.active && g_charge.chest == a_chest) {
			if (auto* chest = RE::TESForm::LookupByID<RE::TESObjectREFR>(a_chest)) {
				g_charge.progress = 1.0f;
				FinishCharge(chest);
				return;
			}
		}
		{
			std::scoped_lock lock(g_lock);
			g_paid.insert(a_chest);
		}
		g_chargePaid.insert(a_chest);
		if (g_counted.insert(a_chest).second) {
			const int done = ++g_dungeonsDone;
			logger::info("Objets : coffre du boss {:08X} termine par un coequipier, {} donjon(s) termine(s)", a_chest, done);
		}
	}
	int   Coins() { return g_coins.load(); }
	int   Septims() { return g_septims.load(); }

	std::string HoverText()
	{
		std::scoped_lock lock(g_hoverLock);
		return g_hoverText;
	}

	void HoldUntilReady()
	{
		if (!g_holding) {
			return;
		}
		const auto now = Clock::now();
		bridge::BridgeData data{};
		const bool ready = Bridge::Get().Read(data) && data.inRun == 1;
		const bool timeout = now - g_holdSince > std::chrono::seconds(g_holdSeconds);
		auto*      timer = RE::BSTimer::GetSingleton();
		if (ready || timeout) {
			g_holding = false;
			if (timer) {
				timer->SetGlobalTimeMultiplier(1.0f, true);
			}
			logger::info("Attente de RoR2 : {} apres {:.0f} s", ready ? "RoR2 en partie, on y va" : "trop long, on joue sans attendre",
				std::chrono::duration<float>(now - g_holdSince).count());
			Show(ready ? Lang::T("RoR2 est pret !") : Lang::T("RoR2 ne repond pas : la partie reprend sans lui"));
			return;
		}
		if (timer) {
			timer->SetGlobalTimeMultiplier(0.001f, true);  // le monde est quasi fige
		}
		if (now >= g_holdNextMessage) {
			g_holdNextMessage = now + std::chrono::seconds(4);
			Show(Lang::F("En attente de RoR2... ({:.0f} s)", std::chrono::duration<float>(now - g_holdSince).count()));
		}
	}

	void OnGameLoaded(bool a_newGame)
	{
		// Tant que RoR2 n'est pas en partie, le temps de Skyrim est quasi arrete (reglage AttendreRoR2).
		if (g_holdSeconds > 0 && Config::Get().AutoLaunch() && Config::Get().AutoRun()) {
			g_holding = true;
			g_holdSince = Clock::now();
			g_holdNextMessage = g_holdSince + std::chrono::seconds(1);
			logger::info("Attente de RoR2 : le monde est fige jusqu'au debut de la partie RoR2 ({} s au plus)", g_holdSeconds);
		}

		// 0.61 : les objets sont dans l'inventaire de Skyrim (voir Items.cpp), qui les envoie lui-meme a RoR2.
		if (Items::Enabled()) {
			Items::OnGameLoaded(a_newGame, g_hasSavedInventory ? g_savedInventory : std::string());
			return;
		}
		// Les objets de cette sauvegarde (aucun pour une nouvelle partie) : RoR2 les reprend.
		if (!a_newGame && !g_hasSavedInventory) {
			logger::info("Objets : pas d'objets RoR2 dans cette sauvegarde (ancienne sauvegarde) : on garde ceux de la partie RoR2");
			return;
		}
		const auto folder = DataFolder();
		std::error_code error;
		std::filesystem::create_directories(folder, error);
		{
			std::ofstream file(folder / "inventaire_charge.txt", std::ios::binary | std::ios::trunc);
			file << (a_newGame ? std::string() : g_savedInventory);
		}
		++g_inventorySequence;
		logger::info("Objets : objets RoR2 de la sauvegarde envoyes a RoR2 ({} octets)", a_newGame ? 0 : g_savedInventory.size());
	}
	float ChargeProgress() { return g_chargeShown.load(); }
	int   DungeonsDone() { return g_dungeonsDone.load(); }

	bool SpendCoins(int a_count)
	{
		int current = g_coins.load();
		while (current >= a_count) {
			if (g_coins.compare_exchange_weak(current, current - a_count)) {
				return true;
			}
		}
		return false;
	}

	void AddMountain() { ++g_mountain; }

	void ResetDifficulty()
	{
		if (Coop::KeepDifficulty()) {
			logger::info("Difficulte gardee : des coequipiers sont encore debout");
			return;
		}
		g_dungeonsDone = 0;
		g_mountain = 0;
		if (auto* calendar = RE::Calendar::GetSingleton()) {
			g_dayOffset = calendar->GetDaysPassed();
		}
		g_difficulty = 0.0f;
		logger::info("Difficulte remise a 1 (jour {:.1f})", g_dayOffset.load());
	}

	bool PayGold(int a_price)
	{
		auto* player = RE::PlayerCharacter::GetSingleton();
		if (!player) {
			return false;
		}
		if (a_price <= 0) {
			return true;
		}
		if (GoldCount(player) < a_price) {
			return false;
		}
		if (auto* septim = RE::TESForm::LookupByID<RE::TESBoundObject>(0xF)) {
			player->RemoveItem(septim, a_price, RE::ITEM_REMOVE_REASON::kRemove, nullptr, nullptr);
		}
		return true;
	}

	void GiveGold(int a_amount)
	{
		auto* player = RE::PlayerCharacter::GetSingleton();
		auto* septim = RE::TESForm::LookupByID<RE::TESBoundObject>(0xF);
		if (player && septim && a_amount > 0) {
			player->AddObjectToContainer(septim, nullptr, a_amount, nullptr);
		}
	}

	bool IsDungeon(RE::PlayerCharacter* a_player)
	{
		auto* cell = a_player ? a_player->GetParentCell() : nullptr;
		if (!cell || cell->IsExteriorCell()) {
			return false;
		}
		for (const char* word : { "Dungeon", "Dwarven", "Draugr", "Cave", "Mine", "Ruin", "Crypt", "Fort", "Lair", "Den", "Daedr" }) {
			if (PlaceHas(a_player, word)) {
				return true;
			}
		}
		return false;
	}

	bool InDaedricPlace(RE::PlayerCharacter* a_player) { return PlaceHas(a_player, "Daedr"); }  // (la capsule : vrais sanctuaires seulement)

	void FillCommand(bridge::SkyrimCommand& a_command)
	{
		a_command.difficulty = g_difficulty.load();
		a_command.inventorySequence = g_inventorySequence.load();
	}

	void LoadSettings()
	{
		const auto ini = std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini").string();
		g_enabled = ReadInt("Objets", "Activer", 1, ini) != 0;
		g_priceSmall = std::max(0, ReadInt("Objets", "PrixPetit", 5, ini));
		g_priceMedium = std::max(0, ReadInt("Objets", "PrixMoyen", 10, ini));
		g_priceChest = std::max(0, ReadInt("Objets", "PrixCoffre", 25, ini));
		g_priceBoss = std::max(0, ReadInt("Objets", "PrixCoffreBoss", 50, ini));
		g_itemChance = std::clamp(ReadInt("Objets", "ChanceObjet", 15, ini), 0, 100);
		g_greenChance = std::clamp(ReadInt("Objets", "ChanceVert", 20, ini), 0, 100);
		g_bossRedChance = std::clamp(ReadInt("Objets", "ChanceRougeBoss", 20, ini), 0, 100);
		g_stripGear = ReadInt("Objets", "RetirerArmesArmures", 1, ini) != 0;
		g_yellowChance = std::clamp(ReadInt("Objets", "ChanceJauneBossDragon", 10, ini), 0, 100);
		// 0.71 : vide dans les lieux daedriques, equipements chez les Dwemers ("ChanceVideDwemer" ne sert plus).
		g_voidChance = std::clamp(ReadInt("Objets", "ChanceVideDaedrique", 35, ini), 0, 100);
		g_equipDwemer = std::clamp(ReadInt("Objets", "ChanceEquipementDwemer", 35, ini), 0, 100);
		g_hallowedBoss = std::clamp(ReadInt("Objets", "ChanceSanctifieBoss", 15, ini), 0, 100);
		g_hallowedDragon = std::clamp(ReadInt("Objets", "ChanceSanctifieDragon", 5, ini), 0, 100);
		g_hardLevel = std::clamp(ReadInt("Objets", "NiveauDonjonDifficile", 30, ini), 1, 1000);
		g_aspectOneIn = std::clamp(ReadInt("Objets", "AspectUnSur", 300, ini), 0, 100000);
		g_portalOneIn = std::clamp(ReadInt("Objets", "BazarUnSur", 20, ini), 0, 100000);
		g_coinChance = std::clamp(ReadInt("Objets", "ChancePieceLunaire", 3, ini), 0, 100);
		g_goldRatio = std::clamp(ReadFloat("Objets", "SeptimsParOrRoR2", 0.5f, ini), 0.0f, 10.0f);
		g_holdSeconds = std::clamp(ReadInt("Lancement", "AttendreRoR2", 90, ini), 0, 600);
		g_priceGrowth = std::clamp(ReadFloat("Difficulte", "HaussePrix", 0.15f, ini), 0.0f, 2.0f);
		g_dayWeight = std::clamp(ReadFloat("Difficulte", "ParJour", 0.25f, ini), 0.0f, 5.0f);
		g_chargeSeconds = std::clamp(ReadFloat("Difficulte", "DureeCharge", 60.0f, ini), 5.0f, 600.0f);
		g_chargeRadius = std::clamp(ReadFloat("Difficulte", "ZoneCharge", 12.0f, ini), 3.0f, 60.0f);
		g_waveSeconds = std::clamp(ReadFloat("Difficulte", "IntervalleRenforts", 15.0f, ini), 3.0f, 120.0f);
		g_waveSize = std::clamp(ReadInt("Difficulte", "Renforts", 2, ini), 0, 6);
		{
			std::scoped_lock lock(g_lock);
			g_stripped.clear();
		}
		logger::info("Objets : {} (prix {}/{}/{}/{} septims, {} % d'objet, {} % de vert, {} % de rouge au boss, armes/armures {})",
			g_enabled ? "actifs" : "desactives", g_priceSmall, g_priceMedium, g_priceChest, g_priceBoss, g_itemChance, g_greenChance,
			g_bossRedChance, g_stripGear ? "retirees" : "gardees");
	}

	void Install()
	{
		LoadSettings();
		static MenuSink sink;
		if (auto* ui = RE::UI::GetSingleton()) {
			ui->AddEventSink<RE::MenuOpenCloseEvent>(&sink);
		}
		static DeathSink deaths;
		static DropSink  drops;
		if (auto* events = RE::ScriptEventSourceHolder::GetSingleton()) {
			events->AddEventSink<RE::TESDeathEvent>(&deaths);
			events->AddEventSink<RE::TESContainerChangedEvent>(&drops);
		}
	}

	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked)
	{
		g_linked = a_linked;
		const auto now = Clock::now();
		const float delta = g_lastFrame.time_since_epoch().count() == 0 ? 0.0f :
		                                                                    std::min(std::chrono::duration<float>(now - g_lastFrame).count(), 0.25f);
		g_lastFrame = now;
		if (!g_enabled || !a_player) {
			return;
		}

		// La difficulte : les donjons termines, et un peu chaque jour.
		float days = 0.0f;
		if (auto* calendar = RE::Calendar::GetSingleton()) {
			days = calendar->GetDaysPassed();
		}
		g_difficulty = static_cast<float>(g_dungeonsDone.load()) + (std::max)(0.0f, days - g_dayOffset.load()) * g_dayWeight;
		// 0.70 (coop) : une seule difficulte pour la partie, celle de l'hote.
		if (const float shared = Coop::DifficultyOverride(); shared >= 0.0f) {
			g_difficulty = shared;
		}

		if (!a_linked) {
			return;
		}
		UpdateCharge(a_player, delta);
		EmptyRemoteContainers();
		UpdateHover(a_player);
		CleanDroppedWeapons();

		// Les septims du joueur, pour le HUD (lus ici, sur le fil du jeu).
		{
			static Clock::time_point nextGold{};
			if (now >= nextGold) {
				nextGold = now + std::chrono::milliseconds(300);
				g_septims = GoldCount(a_player);
			}
		}

		// L'objet donne par RoR2 : annonce. L'or gagne dans RoR2 : des septims.
		bridge::PickupNote note{};
		if (Bridge::Get().ReadPickup(note)) {
			const auto earned = static_cast<std::uint32_t>(note.reserved);
			if (!g_goldStarted || earned < g_lastGoldEarned) {
				g_goldStarted = true;
				g_lastGoldEarned = earned;
			} else if (earned > g_lastGoldEarned) {
				const int septims = static_cast<int>(std::lround((earned - g_lastGoldEarned) * g_goldRatio));
				if (septims > 0) {
					g_lastGoldEarned = earned;
					GiveGold(septims);
				}
			}
			if (!g_pickupStarted) {
				g_pickupStarted = true;
				g_lastPickup = note.sequence;
			} else if (note.sequence != g_lastPickup) {
				g_lastPickup = note.sequence;
				Show(Lang::F("Objet RoR2 : {}", Lang::Packed(note.name)));
			}
		} else if (!g_goldStarted) {
			g_goldStarted = true;  // RoR2 n'a encore rien gagne : on part de zero
			g_lastGoldEarned = 0;
		}

		// Les armes et armures des contenants proches, avant qu'on ne les ouvre (pas pendant les premieres
		// secondes dans une nouvelle cellule : le temps qu'elle soit chargee).
		if (a_player->GetParentCell() != g_stripCell) {
			g_stripCell = a_player->GetParentCell();
			g_stripCellSince = now;
		}
		if (!g_stripGear || now < g_nextStrip || now - g_stripCellSince < std::chrono::seconds(3)) {
			return;
		}
		g_nextStrip = now + kStripEvery;
		auto* tes = RE::TES::GetSingleton();
		if (!tes) {
			return;
		}
		std::vector<RE::TESObjectREFR*> fresh;
		tes->ForEachReferenceInRange(a_player, kStripRadius * kUnitsPerMeter, [&](RE::TESObjectREFR* a_ref) {
			// 0.72 : aussi les corps (morts avant notre arrivee, ou tues loin) : plus d'armes ni d'armures sur eux.
			auto*      actor = a_ref ? a_ref->As<RE::Actor>() : nullptr;
			const bool corpse = actor && actor != a_player && actor->IsDead() && !actor->IsPlayerTeammate();
			if (a_ref && !a_ref->IsDisabled() && (corpse || (IsContainer(a_ref) && !OwnedByPlayer(a_ref, a_player)))) {
				std::scoped_lock lock(g_lock);
				if (!g_stripped.contains(a_ref->GetFormID())) {
					fresh.push_back(a_ref);
				}
			}
			return RE::BSContainer::ForEachResult::kContinue;
		});
		int done = 0;
		for (auto* ref : fresh) {
			if (++done > 8) {
				break;  // quelques-uns par passage : pas d'a-coup
			}
			Strip(ref);
		}
	}

	void Save(SKSE::SerializationInterface* a_intfc)
	{
		std::scoped_lock lock(g_lock);
		if (a_intfc->OpenRecord(kPaidRecord, kRecordVersion)) {
			const auto count = static_cast<std::uint32_t>(g_paid.size());
			a_intfc->WriteRecordData(&count, sizeof(count));
			for (const RE::FormID id : g_paid) {
				a_intfc->WriteRecordData(&id, sizeof(id));
			}
		}
		if (a_intfc->OpenRecord(kStateRecord, kRecordVersion)) {
			const std::int32_t values[3]{ g_dungeonsDone.load(), g_coins.load(), g_mountain.load() };
			a_intfc->WriteRecordData(values, sizeof(values));
		}
		if (a_intfc->OpenRecord(kDayRecord, kRecordVersion)) {
			const float offset = g_dayOffset.load();
			a_intfc->WriteRecordData(&offset, sizeof(offset));
		}
		// Les objets RoR2 du survivant, tels que RoR2 les a ecrits.
		if (a_intfc->OpenRecord(kInventoryRecord, kRecordVersion)) {
			const std::string text = ReadText(DataFolder() / "inventaire.txt");
			const auto        length = static_cast<std::uint32_t>(text.size());
			a_intfc->WriteRecordData(&length, sizeof(length));
			if (length > 0) {
				a_intfc->WriteRecordData(text.data(), length);
			}
			logger::info("Objets : objets RoR2 sauvegardes ({} octets)", length);
		}
	}

	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t)
	{
		if (a_type == kInventoryRecord) {
			std::uint32_t length = 0;
			if (a_intfc->ReadRecordData(&length, sizeof(length)) == sizeof(length) && length < 1000000) {
				g_savedInventory.assign(length, '\0');
				if (length == 0 || a_intfc->ReadRecordData(g_savedInventory.data(), length) == length) {
					g_hasSavedInventory = true;
				}
			}
			return true;
		}
		if (a_type == kDayRecord) {
			float offset = 0.0f;
			if (a_intfc->ReadRecordData(&offset, sizeof(offset)) == sizeof(offset) && std::isfinite(offset)) {
				g_dayOffset = (std::max)(0.0f, offset);
			}
			return true;
		}
		if (a_type == kStateRecord) {
			std::int32_t values[3]{};
			if (a_intfc->ReadRecordData(values, sizeof(values)) == sizeof(values)) {
				g_dungeonsDone = std::max(0, values[0]);
				g_coins = std::max(0, values[1]);
				g_mountain = std::max(0, values[2]);
			}
			logger::info("Objets : {} donjon(s) termine(s), {} piece(s) lunaire(s)", g_dungeonsDone.load(), g_coins.load());
			return true;
		}
		if (a_type != kPaidRecord) {
			return false;
		}
		std::scoped_lock lock(g_lock);
		g_paid.clear();
		std::uint32_t count = 0;
		if (a_intfc->ReadRecordData(&count, sizeof(count)) != sizeof(count) || count > 1000000) {
			return true;
		}
		for (std::uint32_t i = 0; i < count; ++i) {
			RE::FormID id = 0;
			if (a_intfc->ReadRecordData(&id, sizeof(id)) != sizeof(id)) {
				break;
			}
			RE::FormID resolved = 0;
			if (a_intfc->ResolveFormID(id, resolved)) {
				g_paid.insert(resolved);
			}
		}
		logger::info("Objets : {} contenant(s) deja paye(s) dans cette sauvegarde", g_paid.size());
		return true;
	}

	void Revert()
	{
		std::scoped_lock lock(g_lock);
		g_paid.clear();
		g_stripped.clear();
		g_chargePaid.clear();
		g_counted.clear();
		g_toEmpty.clear();
		g_charge = Charge{};
		g_chargeShown = -1.0f;
		g_dungeonsDone = 0;
		g_coins = 0;
		g_mountain = 0;
		g_dayOffset = 0.0f;
		g_savedInventory.clear();
		g_hasSavedInventory = false;
	}
}
