#include "Items.h"

#include "Bridge.h"
#include "Config.h"
#include "Coop.h"
#include "InputWatcher.h"
#include "Loot.h"
#include "Shrines.h"

#include <fstream>
#include <map>
#include <random>
#include <set>
#include <sstream>
#include <tuple>

namespace skyrimror2::Items
{
	namespace
	{
		using Clock = std::chrono::steady_clock;

		constexpr const char*   kPluginFile = "SkyrimRoR2.esp";
		constexpr std::uint32_t kFirstItemId = 0x900;  // voir tools/make_esp.py
		constexpr int           kSlots = 600;
		constexpr std::uint32_t kMapRecord = 'IMAP';    // la table "objet RoR2 -> objet de Skyrim" (secours)
		constexpr std::uint32_t kStateRecord = 'ISTA';  // dernier lit, objets deja migres, bazar
		constexpr std::uint32_t kJournalRecord = 'IJRN';  // 0.71 : objets RoR2 deja trouves (journal)
		constexpr std::uint32_t kJournalId = 0xC30;       // 0.71 : le livre "Journal RoR2" (voir tools/make_esp.py)
		constexpr std::uint32_t kRecordVersion = 1;
		constexpr auto          kTickEvery = std::chrono::milliseconds(400);

		// Reglages ([ObjetsSkyrim])
		bool g_enabled = true;
		bool g_wake = true;
		bool g_sleepSwitch = true;
		int  g_bazaarPrice = 2;    // pieces lunaires par objet du bazar
		int  g_shinyChance = 10;   // % de perle irradiante a la purification lunaire

		// ---- Le catalogue (objets_infos.txt, ecrit par RoR2) ----
		struct Info
		{
			int         tier = kOther;
			std::string display;
			std::string description;
			bool        scrap = false;
			bool        findable = false;
		};
		std::map<std::string, Info>                   g_catalog;
		std::filesystem::file_time_type               g_catalogTime{};
		Clock::time_point                             g_nextCatalogCheck{};

		// ---- Les objets divers de Skyrim et leur objet RoR2 ----
		std::array<RE::TESObjectMISC*, kSlots>        g_forms{};
		bool                                          g_formsSearched = false;
		int                                           g_formsFound = 0;
		std::vector<std::string>                      g_slotName;  // emplacement -> nom interne RoR2
		std::unordered_map<std::string, int>          g_slotOf;    // nom interne RoR2 -> emplacement
		std::unordered_map<RE::FormID, int>           g_slotOfForm;
		bool                                          g_namesDirty = true;

		// ---- La synchronisation avec RoR2 ----
		std::uint32_t                                 g_sequence = 0;    // numero de la derniere demande ecrite
		std::atomic_uint32_t                          g_published{ 0 };  // celle que RoR2 voit (0 : aucune encore)
		std::uint32_t                                 g_pending = 0;     // demande pas encore appliquee par RoR2 (0 : aucune)
		Clock::time_point                             g_pendingSince{};
		std::map<std::string, int>                    g_believed;        // ce que RoR2 a, d'apres nous
		bool                                          g_needReplace = false;
		bool                                          g_clearEquipment = false;
		std::string                                   g_equipmentLine;   // "equipement:..." de la sauvegarde (a renvoyer au chargement)
		std::string                                   g_migrateText;     // anciens objets de la sauvegarde (a mettre dans l'inventaire)
		bool                                          g_migrated = false;
		std::unordered_map<RE::FormID, int>           g_allowedLoss;     // retraits voulus d'objets du vide / lunaires (pour ContainerSink)
		std::unordered_map<std::string, int>          g_intendedLoss;    // les memes, pour la synchronisation (purification, mort)
		Clock::time_point                             g_nextTick{};

		// ---- Mort, lit, reveil ----
		std::atomic_uint32_t                          g_revive{ 0 };
		Clock::time_point                             g_reviveAt{};
		bool                                          g_reviveScheduled = false;
		RE::FormID                                    g_lastBed = 0;
		RE::FormID                                    g_lastFurniture = 0;   // dernier meuble active (lit, fonderie...)
		std::string                                   g_lastFurnitureName;
		Clock::time_point                             g_lastFurnitureTime{};
		RE::FormID                                    g_lastTalked = 0;      // dernier chef de caravane a qui on a parle
		Clock::time_point                             g_lastTalkedTime{};

		// ---- Bazar lunaire : objets deja achetes (chef, semaine, case) ----
		std::set<std::tuple<RE::FormID, int, int>>    g_sold;

		// ---- 0.72 : les equipements RoR2 = des amulettes (emplacement 55) ----
		constexpr std::uint32_t kFirstEquipId = 0xD00;  // voir tools/make_esp.py
		constexpr int           kEquipSlots = 160;
		struct EquipInfo
		{
			std::string display;
			std::string description;
			bool        lunar = false;
			bool        aspect = false;
		};
		std::map<std::string, EquipInfo>               g_eqCatalog;
		std::filesystem::file_time_type                g_eqCatalogTime{};
		std::array<RE::TESObjectARMO*, kEquipSlots>    g_eqForms{};
		int                                            g_eqFormsFound = 0;
		bool                                           g_eqFormsSearched = false;
		std::vector<std::string>                       g_eqSlotName;
		std::unordered_map<std::string, int>           g_eqSlotOf;
		std::unordered_map<RE::FormID, int>            g_eqSlotOfForm;
		bool                                           g_eqNamesDirty = true;
		std::string                                    g_eqBelieved;          // l'equipement actif de RoR2, d'apres nous
		bool                                           g_eqBelievedKnown = false;
		std::string                                    g_eqMigrate;           // equipement d'une sauvegarde d'avant la 0.72
		long long                                      g_gainRead = -1;       // dernier equipement gagne lu (-1 : ignorer l'existant)
		std::string                                    g_amuletModel;

		// ---- 0.71 : journal des objets trouves ----
		std::set<std::string>                         g_found;
		bool                                          g_journalGiven = false;   // livre deja donne dans cette partie


		std::mt19937& Random()
		{
			static std::mt19937 engine{ std::random_device{}() };
			return engine;
		}

		void Show(const std::string& a_text) { RE::SendHUDMessage::ShowHUDMessage(a_text.c_str()); }

		std::string Lower(std::string a_text)
		{
			for (auto& c : a_text) {
				c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
			}
			return a_text;
		}

		std::filesystem::path Folder()
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

		void WriteAtomic(const std::filesystem::path& a_path, const std::string& a_text)
		{
			std::error_code error;
			std::filesystem::create_directories(a_path.parent_path(), error);
			const auto temporary = a_path.string() + ".tmp";
			{
				std::ofstream file(temporary, std::ios::binary | std::ios::trunc);
				file << a_text;
			}
			std::filesystem::rename(temporary, a_path, error);
			if (error) {
				std::filesystem::remove(a_path, error);
				std::filesystem::rename(temporary, a_path, error);
			}
		}

		std::vector<std::string> Split(const std::string& a_line, char a_separator)
		{
			std::vector<std::string> parts;
			std::size_t              start = 0;
			for (std::size_t at = a_line.find(a_separator); at != std::string::npos; at = a_line.find(a_separator, start)) {
				parts.push_back(a_line.substr(start, at - start));
				start = at + 1;
			}
			parts.push_back(a_line.substr(start));
			return parts;
		}

		// "item:Nom=3" -> (Nom, 3)
		std::map<std::string, int> ParseItems(const std::string& a_text, std::uint32_t* a_applied = nullptr, std::string* a_equipment = nullptr)
		{
			std::map<std::string, int> items;
			std::istringstream         lines(a_text);
			std::string                line;
			while (std::getline(lines, line)) {
				if (!line.empty() && line.back() == '\r') {
					line.pop_back();
				}
				if (line.rfind("item:", 0) == 0) {
					const auto equal = line.rfind('=');
					if (equal == std::string::npos || equal <= 5) {
						continue;
					}
					const int count = std::atoi(line.c_str() + equal + 1);
					if (count > 0) {
						items[line.substr(5, equal - 5)] += count;
					}
				} else if (a_applied && line.rfind("applique:", 0) == 0) {
					*a_applied = static_cast<std::uint32_t>(std::strtoul(line.c_str() + 9, nullptr, 10));
				} else if (a_equipment && line.rfind("equipement:", 0) == 0) {
					*a_equipment = line;
				}
			}
			return items;
		}

		// ---- Formes et table des emplacements ----

		void FindForms()
		{
			g_formsSearched = true;
			auto* data = RE::TESDataHandler::GetSingleton();
			if (!data) {
				return;
			}
			g_formsFound = 0;
			for (int i = 0; i < kSlots; ++i) {
				g_forms[i] = data->LookupForm<RE::TESObjectMISC>(kFirstItemId + static_cast<std::uint32_t>(i), kPluginFile);
				if (g_forms[i]) {
					g_slotOfForm[g_forms[i]->GetFormID()] = i;
					++g_formsFound;
				}
			}
			if (g_formsFound == kSlots) {
				logger::info("Objets Skyrim : {} objets divers trouves dans {}", g_formsFound, kPluginFile);
			} else {
				logger::error("Objets Skyrim : {} incomplet ({} objets sur {}) : remplace SkyrimRoR2.esp par celui de la 0.61", kPluginFile,
					g_formsFound, kSlots);
			}
		}

		std::filesystem::path MapPath() { return Folder() / "objets_skyrim.txt"; }

		void SaveMap()
		{
			std::string text;
			for (std::size_t i = 0; i < g_slotName.size(); ++i) {
				text += std::format("{}\t{}\n", i, g_slotName[i]);
			}
			WriteAtomic(MapPath(), text);
		}

		// La table est fixe : un objet RoR2 garde toujours le meme objet de Skyrim (sinon les sauvegardes melangeraient tout).
		void LoadMap()
		{
			if (!g_slotName.empty()) {
				return;
			}
			std::istringstream lines(ReadText(MapPath()));
			std::string        line;
			while (std::getline(lines, line)) {
				const auto parts = Split(line, '\t');
				if (parts.size() < 2) {
					continue;
				}
				const int   index = std::atoi(parts[0].c_str());
				std::string name = parts[1];
				if (!name.empty() && name.back() == '\r') {
					name.pop_back();
				}
				if (index < 0 || index >= kSlots || name.empty()) {
					continue;
				}
				if (static_cast<int>(g_slotName.size()) <= index) {
					g_slotName.resize(static_cast<std::size_t>(index) + 1);
				}
				g_slotName[index] = name;
				g_slotOf[name] = index;
			}
			logger::info("Objets Skyrim : {} objet(s) RoR2 deja associes a un objet de Skyrim", g_slotOf.size());
		}

		int SlotFor(const std::string& a_name, bool a_create)
		{
			if (const auto it = g_slotOf.find(a_name); it != g_slotOf.end()) {
				return it->second;
			}
			if (!a_create || g_slotName.size() >= static_cast<std::size_t>(kSlots)) {
				return -1;
			}
			const int index = static_cast<int>(g_slotName.size());
			g_slotName.push_back(a_name);
			g_slotOf[a_name] = index;
			g_namesDirty = true;
			SaveMap();
			return index;
		}

		RE::TESObjectMISC* FormFor(const std::string& a_name, bool a_create = true)
		{
			const int slot = SlotFor(a_name, a_create);
			return slot >= 0 ? g_forms[slot] : nullptr;
		}

		const Info* InfoOf(const std::string& a_name)
		{
			const auto it = g_catalog.find(a_name);
			return it != g_catalog.end() ? &it->second : nullptr;
		}

		int TierOf(const std::string& a_name)
		{
			const auto* info = InfoOf(a_name);
			return info ? info->tier : kOther;
		}

		bool Bound(int a_tier) { return a_tier == kVoid || a_tier == kLunar; }  // ne quittent jamais le joueur

		// Une pierre precieuse de Skyrim par rang, pour l'apparence (dans l'inventaire et posee par terre).
		std::array<std::string, kTierCount> g_models;
		bool                       g_modelsSearched = false;

		void FindModels()
		{
			g_modelsSearched = true;
			auto* data = RE::TESDataHandler::GetSingleton();
			if (!data) {
				return;
			}
			constexpr const char* kWords[kTierCount]{ "diamond", "emerald", "ruby", "garnet", "amethyst", "sapphire", "soulgem", "flawless", "pearl" };
			std::string anyGem;
			for (auto* misc : data->GetFormArray<RE::TESObjectMISC>()) {
				const char* raw = misc ? static_cast<RE::TESModel*>(misc)->GetModel() : nullptr;
				if (!raw || !raw[0]) {
					continue;
				}
				const std::string path = Lower(raw);
				const bool        gem = path.find("gems") != std::string::npos || path.find("soulgem") != std::string::npos;
				if (!gem) {
					continue;
				}
				if (anyGem.empty() && path.find("gems") != std::string::npos) {
					anyGem = raw;
				}
				for (int tier = 0; tier < kTierCount; ++tier) {
					if (g_models[tier].empty() && path.find(kWords[tier]) != std::string::npos) {
						g_models[tier] = raw;
					}
				}
			}
			if (g_models[kHallowed].empty()) {
				g_models[kHallowed] = g_models[kWhite];  // sanctifies : un diamant
			}
			for (auto& model : g_models) {
				if (model.empty()) {
					model = anyGem;
				}
			}
			logger::info("Objets Skyrim : apparences {} / {} / {} / {} / {} / {} / {}", g_models[0], g_models[1], g_models[2], g_models[3],
				g_models[4], g_models[5], g_models[6]);
		}

		// Les objets de Skyrim prennent le nom (et la pierre) de leur objet RoR2.
		void ApplyNames()
		{
			if (!g_namesDirty || g_formsFound == 0) {
				return;
			}
			g_namesDirty = false;
			if (!g_modelsSearched) {
				FindModels();
			}
			for (std::size_t i = 0; i < g_slotName.size() && i < g_forms.size(); ++i) {
				auto* form = g_forms[i];
				if (!form || g_slotName[i].empty()) {
					continue;
				}
				const auto* info = InfoOf(g_slotName[i]);
				const int   tier = info && info->tier >= 0 && info->tier < kTierCount ? info->tier : kOther;
				const std::string display = info && !info->display.empty() ? info->display : g_slotName[i];
				form->SetFullName(std::format("{} [RoR2 {}]", display, TierWord(tier)).c_str());
				if (!g_models[tier].empty()) {
					static_cast<RE::TESModel*>(form)->SetModel(g_models[tier].c_str());
				}
			}
		}

		void ReadCatalog()
		{
			const auto now = Clock::now();
			if (now < g_nextCatalogCheck) {
				return;
			}
			g_nextCatalogCheck = now + std::chrono::seconds(3);
			const auto      path = Folder() / "objets_infos.txt";
			std::error_code error;
			const auto      time = std::filesystem::last_write_time(path, error);
			if (error || time == g_catalogTime) {
				return;
			}
			g_catalogTime = time;
			std::map<std::string, Info> catalog;
			std::istringstream          lines(ReadText(path));
			std::string                 line;
			while (std::getline(lines, line)) {
				if (!line.empty() && line.back() == '\r') {
					line.pop_back();
				}
				const auto parts = Split(line, '\t');
				if (parts.size() < 5 || parts[0].empty()) {
					continue;
				}
				Info info;
				info.tier = std::clamp(std::atoi(parts[1].c_str()), 0, static_cast<int>(kTierCount) - 1);  // 0.71 : 7 sanctifie, 8 repas
				info.display = parts[2];
				info.description = parts[3];
				info.scrap = parts[4].find('f') != std::string::npos;
				info.findable = parts[4].find('t') != std::string::npos;
				catalog[parts[0]] = std::move(info);
			}
			if (catalog.empty()) {
				return;
			}
			g_catalog = std::move(catalog);
			LoadMap();
			for (const auto& [name, info] : g_catalog) {
				SlotFor(name, true);  // chaque objet du catalogue a son objet de Skyrim, une fois pour toutes
			}
			g_namesDirty = true;
			logger::info("Objets Skyrim : catalogue de {} objets lu ({} associes)", g_catalog.size(), g_slotOf.size());
		}

		// ---- 0.72 : les amulettes d'equipement ----

		bool EquipEnabled() { return g_eqFormsFound == kEquipSlots && !g_eqCatalog.empty(); }

		void FindEquipForms()
		{
			g_eqFormsSearched = true;
			auto* data = RE::TESDataHandler::GetSingleton();
			if (!data) {
				return;
			}
			g_eqFormsFound = 0;
			for (int i = 0; i < kEquipSlots; ++i) {
				g_eqForms[i] = data->LookupForm<RE::TESObjectARMO>(kFirstEquipId + static_cast<std::uint32_t>(i), kPluginFile);
				if (g_eqForms[i]) {
					g_eqSlotOfForm[g_eqForms[i]->GetFormID()] = i;
					++g_eqFormsFound;
				}
			}
			// Une apparence d'amulette de Skyrim, pour une amulette posee par terre.
			for (auto* armor : data->GetFormArray<RE::TESObjectARMO>()) {
				if (!armor || g_eqSlotOfForm.contains(armor->GetFormID()) || !armor->HasPartOf(RE::BGSBipedObjectForm::BipedObjectSlot::kAmulet)) {
					continue;
				}
				const char* model = armor->worldModels[0].GetModel();
				if (model && model[0]) {
					g_amuletModel = model;
					break;
				}
			}
			logger::info("Equipements : {} amulettes trouvees dans {} (apparence {})", g_eqFormsFound, kPluginFile, g_amuletModel);
			if (g_eqFormsFound != kEquipSlots) {
				logger::error("Equipements : {} incomplet ({} / {}) : remplace SkyrimRoR2.esp par celui de la 0.72 (build-skyrim-plugin.bat)", kPluginFile, g_eqFormsFound, kEquipSlots);
			}
		}

		std::filesystem::path EquipMapPath() { return Folder() / "equipements_skyrim.txt"; }

		void SaveEquipMap()
		{
			std::string text;
			for (std::size_t i = 0; i < g_eqSlotName.size(); ++i) {
				text += std::format("{}\t{}\n", i, g_eqSlotName[i]);
			}
			WriteAtomic(EquipMapPath(), text);
		}

		void LoadEquipMap()
		{
			if (!g_eqSlotName.empty()) {
				return;
			}
			std::istringstream lines(ReadText(EquipMapPath()));
			std::string        line;
			while (std::getline(lines, line)) {
				const auto parts = Split(line, '\t');
				if (parts.size() < 2) {
					continue;
				}
				const int   index = std::atoi(parts[0].c_str());
				std::string name = parts[1];
				while (!name.empty() && (name.back() == '\r' || name.back() == ' ')) {
					name.pop_back();
				}
				if (index < 0 || index >= kEquipSlots || name.empty()) {
					continue;
				}
				if (static_cast<int>(g_eqSlotName.size()) <= index) {
					g_eqSlotName.resize(static_cast<std::size_t>(index) + 1);
				}
				g_eqSlotName[index] = name;
				g_eqSlotOf[name] = index;
			}
		}

		RE::TESObjectARMO* EquipFormFor(const std::string& a_name, bool a_create = true)
		{
			if (a_name.empty()) {
				return nullptr;
			}
			if (const auto it = g_eqSlotOf.find(a_name); it != g_eqSlotOf.end()) {
				return g_eqForms[it->second];
			}
			if (!a_create || g_eqSlotName.size() >= static_cast<std::size_t>(kEquipSlots)) {
				return nullptr;
			}
			const int index = static_cast<int>(g_eqSlotName.size());
			g_eqSlotName.push_back(a_name);
			g_eqSlotOf[a_name] = index;
			g_eqNamesDirty = true;
			SaveEquipMap();
			return g_eqForms[index];
		}

		std::string EquipNameOf(RE::FormID a_form)
		{
			const auto it = g_eqSlotOfForm.find(a_form);
			return it != g_eqSlotOfForm.end() && it->second < static_cast<int>(g_eqSlotName.size()) ? g_eqSlotName[it->second] : std::string();
		}

		void ReadEquipCatalog()
		{
			const auto      path = Folder() / "equipements_infos.txt";
			std::error_code error;
			const auto      time = std::filesystem::last_write_time(path, error);
			if (error || time == g_eqCatalogTime) {
				return;
			}
			g_eqCatalogTime = time;
			std::map<std::string, EquipInfo> catalog;
			std::istringstream               lines(ReadText(path));
			std::string                      line;
			while (std::getline(lines, line)) {
				if (!line.empty() && line.back() == '\r') {
					line.pop_back();
				}
				const auto parts = Split(line, '\t');
				if (parts.size() < 3 || parts[0].empty()) {
					continue;
				}
				EquipInfo info;
				info.display = parts[1];
				info.description = parts[2];
				const std::string flags = parts.size() > 3 ? parts[3] : "";
				info.lunar = flags.find('l') != std::string::npos;
				info.aspect = flags.find('e') != std::string::npos;
				catalog[parts[0]] = std::move(info);
			}
			g_eqCatalog = std::move(catalog);
			LoadEquipMap();
			g_eqNamesDirty = true;
			logger::info("Equipements : catalogue de {} equipements lu ({} amulettes associees)", g_eqCatalog.size(), g_eqSlotOf.size());
		}

		std::string EquipDisplay(const std::string& a_name)
		{
			const auto it = g_eqCatalog.find(a_name);
			return it != g_eqCatalog.end() && !it->second.display.empty() ? it->second.display : a_name;
		}

		void ApplyEquipNames()
		{
			if (!g_eqNamesDirty) {
				return;
			}
			g_eqNamesDirty = false;
			for (std::size_t i = 0; i < g_eqSlotName.size() && i < g_eqForms.size(); ++i) {
				auto* form = g_eqForms[i];
				if (!form || g_eqSlotName[i].empty()) {
					continue;
				}
				const auto it = g_eqCatalog.find(g_eqSlotName[i]);
				const char* kind = it != g_eqCatalog.end() && it->second.aspect ? "aspect" : (it != g_eqCatalog.end() && it->second.lunar ? "equipement lunaire" : "equipement");
				form->SetFullName(std::format("{} [RoR2 {}]", EquipDisplay(g_eqSlotName[i]), kind).c_str());
				if (!g_amuletModel.empty()) {
					form->worldModels[0].SetModel(g_amuletModel.c_str());
					form->worldModels[1].SetModel(g_amuletModel.c_str());
				}
			}
		}

		std::map<std::string, int> CarriedEquipment(RE::PlayerCharacter* a_player)
		{
			std::map<std::string, int> carried;
			for (auto& [object, data] : a_player->GetInventory([](RE::TESBoundObject& a_object) { return g_eqSlotOfForm.contains(a_object.GetFormID()); })) {
				const std::string name = EquipNameOf(object->GetFormID());
				if (!name.empty() && data.first > 0) {
					carried[name] += data.first;
				}
			}
			return carried;
		}

		// L'amulette portee (l'equipement actif). Vide : aucune.
		std::string WornEquipment(RE::PlayerCharacter* a_player)
		{
			auto* worn = a_player->GetWornArmor(RE::BGSBipedObjectForm::BipedObjectSlot::kModFaceJewelry);
			return worn ? EquipNameOf(worn->GetFormID()) : std::string();
		}

		void Wear(RE::PlayerCharacter* a_player, RE::TESObjectARMO* a_form, bool a_on)
		{
			auto* manager = RE::ActorEquipManager::GetSingleton();
			if (!manager || !a_form) {
				return;
			}
			if (a_on) {
				manager->EquipObject(a_player, a_form, nullptr, 1, nullptr, false, false, true, true);
			} else {
				manager->UnequipObject(a_player, a_form, nullptr, 1, nullptr, false, false, true, true);
			}
		}

		void GiveAmulet(RE::PlayerCharacter* a_player, const std::string& a_name, bool a_wear)
		{
			auto* form = EquipFormFor(a_name);
			if (!form) {
				logger::warn("Equipements : plus d'amulette libre pour {}", a_name);
				return;
			}
			ApplyEquipNames();
			a_player->AddObjectToContainer(form, nullptr, 1, nullptr);
			if (a_wear) {
				Wear(a_player, form, true);
			}
		}

		void TakeAmulet(RE::PlayerCharacter* a_player, const std::string& a_name, int a_count)
		{
			auto* form = EquipFormFor(a_name, false);
			if (!form || a_count <= 0) {
				return;
			}
			if (WornEquipment(a_player) == a_name) {
				Wear(a_player, form, false);
			}
			a_player->RemoveItem(form, a_count, RE::ITEM_REMOVE_REASON::kRemove, nullptr, nullptr);
		}

		// Les equipements gagnes dans RoR2 (coffre, aspect, marmite) : dans le sac, sans changer l'equipement actif.
		void ReadGains(RE::PlayerCharacter* a_player)
		{
			std::istringstream lines(ReadText(Folder() / "equipements_gagnes.txt"));
			std::string        line;
			long long          newest = g_gainRead;
			while (std::getline(lines, line)) {
				const auto tab = line.find('\t');
				if (tab == std::string::npos) {
					continue;
				}
				const long long sequence = std::atoll(line.substr(0, tab).c_str());
				std::string     name = line.substr(tab + 1);
				while (!name.empty() && (name.back() == '\r' || name.back() == ' ')) {
					name.pop_back();
				}
				if (g_gainRead >= 0 && sequence > g_gainRead && !name.empty()) {
					GiveAmulet(a_player, name, false);
					logger::info("Equipements : {} gagne, dans le sac", name);
				}
				newest = (std::max)(newest, sequence);
			}
			g_gainRead = (std::max)(newest, 0LL);  // la premiere fois : ce qui etait deja la est ignore
		}

		// ---- Inventaire du joueur ----

		std::map<std::string, int> Carried(RE::PlayerCharacter* a_player)
		{
			std::map<std::string, int> carried;
			for (auto& [object, data] : a_player->GetInventory([](RE::TESBoundObject& a_object) { return g_slotOfForm.contains(a_object.GetFormID()); })) {
				const auto it = g_slotOfForm.find(object->GetFormID());
				if (it == g_slotOfForm.end() || data.first <= 0 || it->second >= static_cast<int>(g_slotName.size())) {
					continue;
				}
				carried[g_slotName[it->second]] += data.first;
			}
			return carried;
		}

		int CarriedCount(RE::PlayerCharacter* a_player, const std::string& a_name)
		{
			const auto carried = Carried(a_player);
			const auto it = carried.find(a_name);
			return it != carried.end() ? it->second : 0;
		}

		void Give(RE::PlayerCharacter* a_player, const std::string& a_name, int a_count)
		{
			auto* form = FormFor(a_name);
			if (form && a_count > 0) {
				ApplyNames();
				a_player->AddObjectToContainer(form, nullptr, a_count, nullptr);
			}
		}

		// Retire des objets portes (en autorisant le retrait d'un objet du vide ou lunaire).
		// a_intended : retrait decide dans Skyrim (a envoyer a RoR2) ; faux : on recopie un retrait fait par RoR2.
		int Take(RE::PlayerCharacter* a_player, const std::string& a_name, int a_count, bool a_intended = true)
		{
			auto* form = FormFor(a_name, false);
			const int have = CarriedCount(a_player, a_name);
			const int take = (std::min)(have, a_count);
			if (!form || take <= 0) {
				return 0;
			}
			if (Bound(TierOf(a_name))) {
				g_allowedLoss[form->GetFormID()] += take;
				if (a_intended) {
					g_intendedLoss[a_name] += take;
				}
			}
			a_player->RemoveItem(form, take, RE::ITEM_REMOVE_REASON::kRemove, nullptr, nullptr);
			return take;
		}

		// ---- La synchronisation (fil principal, quelques fois par seconde) ----

		std::string ItemLines(const std::map<std::string, int>& a_items, const char* a_prefix)
		{
			std::string text;
			for (const auto& [name, count] : a_items) {
				if (count > 0) {
					text += std::format("{}{}={}\n", a_prefix, name, count);
				}
			}
			return text;
		}

		void Send(const std::map<std::string, int>& a_wanted, bool a_replace, bool a_newSequence)
		{
			std::string text = a_replace ? "mode:remplacer\n" : "mode:ajuster\n";
			if (!a_replace) {
				text += ItemLines(g_believed, "base:");
			}
			text += ItemLines(a_wanted, "item:");
			if (EquipEnabled()) {
				text += g_equipmentLine + "\n";  // 0.72 : toujours l'amulette portee (voir Tick)
			} else if (a_replace && !g_equipmentLine.empty()) {
				text += g_equipmentLine + "\n";
			}
			if (g_clearEquipment) {
				text += "equipement:\n";
			}
			WriteAtomic(Folder() / "inventaire_charge.txt", text);
			if (a_newSequence) {
				g_pending = ++g_sequence;
				g_published = g_pending;
				g_pendingSince = Clock::now();
				g_clearEquipment = false;
			}
		}

		void Tick(RE::PlayerCharacter* a_player)
		{
			if (!g_formsSearched) {
				FindForms();
			}
			if (!g_eqFormsSearched) {
				FindEquipForms();
			}
			ReadCatalog();
			ReadEquipCatalog();
			if (g_formsFound == 0 || g_catalog.empty()) {
				return;  // RoR2 n'a pas encore ecrit son catalogue
			}
			ApplyNames();
			const bool amulets = EquipEnabled();
			if (amulets) {
				ApplyEquipNames();
				ReadGains(a_player);
				// Sauvegarde d'avant la 0.72 : son equipement devient une amulette, portee.
				if (!g_eqMigrate.empty()) {
					if (CarriedEquipment(a_player).empty()) {
						GiveAmulet(a_player, g_eqMigrate, true);
						logger::info("Equipements : {} de l'ancienne sauvegarde mis en amulette", g_eqMigrate);
					}
					g_eqMigrate.clear();
				}
				for (const auto& [name, count] : CarriedEquipment(a_player)) {
					if (g_found.insert("eq:" + name).second) {
						logger::info("Journal : equipement {} trouve", name);
					}
				}
			}

			// Anciennes sauvegardes : les objets RoR2 qu'elles contenaient passent dans l'inventaire, une fois.
			if (!g_migrated) {
				g_migrated = true;
				int moved = 0;
				for (const auto& [name, count] : ParseItems(g_migrateText)) {
					if (InfoOf(name)) {
						Give(a_player, name, count);
						moved += count;
					}
				}
				g_migrateText.clear();
				if (moved > 0) {
					logger::info("Objets Skyrim : {} objet(s) RoR2 de l'ancienne sauvegarde mis dans l'inventaire", moved);
					Show(std::format("Tes {} objets RoR2 sont maintenant dans ton inventaire (objets divers)", moved));
				}
			}

			auto carried = Carried(a_player);
			for (const auto& [name, count] : carried) {
				if (count > 0 && g_found.insert(name).second) {
					logger::info("Journal : {} trouve", name);
				}
			}

			// Les objets du vide et lunaires ne quittent pas le joueur (sauf retrait voulu) : on ne compte pas leur depart.
			for (const auto& [name, before] : g_believed) {
				if (!Bound(TierOf(name))) {
					continue;
				}
				const int now = carried.contains(name) ? carried[name] : 0;
				const int allowed = g_intendedLoss.contains(name) ? g_intendedLoss[name] : 0;
				if (now < before) {
					const int lost = before - now;
					const int ok = (std::min)(lost, allowed);
					g_intendedLoss[name] -= ok;
					carried[name] = now + (lost - ok);  // le reste revient (voir ContainerSink)
				}
			}
			std::erase_if(g_intendedLoss, [](const auto& a_pair) { return a_pair.second <= 0; });

			// Lecture de ce que RoR2 a (inventaire.txt).
			std::uint32_t applied = 0;
			std::string   equipment;
			const auto    ror2 = ParseItems(ReadText(Folder() / "inventaire.txt"), &applied, &equipment);
			if (g_pending != 0) {
				if (applied != g_pending) {
					static Clock::time_point lastWarn{};
					if (Clock::now() - g_pendingSince > std::chrono::seconds(30) && Clock::now() - lastWarn > std::chrono::seconds(30)) {
						lastWarn = Clock::now();
						logger::info("Objets Skyrim : RoR2 n'a pas encore applique la demande {} (il applique {})", g_pending, applied);
					}
					return;  // RoR2 n'a pas encore pris notre derniere demande : on attend (les changements de Skyrim s'accumulent)
				}
				g_pending = 0;
			}

			if (!g_needReplace) {
				// Ce que RoR2 a change tout seul depuis : recopie dans Skyrim.
				bool        changed = false;
				std::string summary;
				std::set<std::string> names;
				for (const auto& [name, count] : ror2) {
					names.insert(name);
				}
				for (const auto& [name, count] : g_believed) {
					names.insert(name);
				}
				for (const auto& name : names) {
					const int now = ror2.contains(name) ? ror2.at(name) : 0;
					const int before = g_believed.contains(name) ? g_believed[name] : 0;
					const int delta = now - before;
					if (delta == 0) {
						continue;
					}
					changed = true;
					if (delta > 0) {
						Give(a_player, name, delta);
						carried[name] += delta;
					} else {
						const int taken = Take(a_player, name, -delta, false);
						carried[name] -= taken;
					}
					summary += std::format(" {}{:+d}", name, delta);
				}
				g_believed = ror2;
				if (changed) {
					logger::info("Objets Skyrim : change dans RoR2 :{}", summary);
				}
				// 0.72 : RoR2 a change l'equipement actif tout seul (equipement consomme, ingredient de la marmite...) :
				// l'amulette portee est remplacee par le nouvel equipement (ou retiree s'il n'y en a plus).
				const std::string ror2Equipment = equipment.size() > 11 ? equipment.substr(11) : std::string();
				if (amulets && g_eqBelievedKnown && !equipment.empty() && ror2Equipment != g_eqBelieved) {
					if (!g_eqBelieved.empty()) {
						TakeAmulet(a_player, g_eqBelieved, 1);
					}
					if (!ror2Equipment.empty()) {
						GiveAmulet(a_player, ror2Equipment, true);
					}
					logger::info("Equipements : change dans RoR2 : {} -> {}", g_eqBelieved.empty() ? "aucun" : g_eqBelieved,
						ror2Equipment.empty() ? "aucun" : ror2Equipment);
					g_eqBelieved = ror2Equipment;
				}
				std::erase_if(carried, [](const auto& a_pair) { return a_pair.second <= 0; });
				if (changed && carried == g_believed && !g_clearEquipment) {
					Send(g_believed, false, false);  // meme demande, a jour (pour un RoR2 relance)
				}
			}

			// 0.72 : l'equipement actif = l'amulette portee.
			std::string wornEquipment;
			if (amulets) {
				wornEquipment = WornEquipment(a_player);
				g_equipmentLine = "equipement:" + wornEquipment;
			}
			const bool equipmentChanged = amulets && (!g_eqBelievedKnown || wornEquipment != g_eqBelieved);
			if (g_needReplace || carried != g_believed || g_clearEquipment || equipmentChanged) {
				const bool replace = g_needReplace;
				if (amulets) {
					if (equipmentChanged) {
						logger::info("Equipements : amulette portee -> {}", wornEquipment.empty() ? "aucun equipement" : wornEquipment);
					}
					g_eqBelieved = wornEquipment;
					g_eqBelievedKnown = true;
				}
				Send(carried, replace, true);
				logger::info("Objets Skyrim : demande {} envoyee a RoR2 ({}, {} sorte(s) d'objets portes)", g_pending,
					replace ? "tout remplacer" : "ajuster", carried.size());
				g_believed = carried;
				g_needReplace = false;
			}
		}

		// ---- Evenements ----

		// Un objet du vide ou lunaire a quitte le joueur (jete, range, vendu, donne) : il revient.
		class ContainerSink final : public RE::BSTEventSink<RE::TESContainerChangedEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESContainerChangedEvent* a_event, RE::BSTEventSource<RE::TESContainerChangedEvent>*) override
			{
				if (!a_event || !g_enabled || a_event->oldContainer != 0x14 || a_event->newContainer == 0x14) {
					return RE::BSEventNotifyControl::kContinue;
				}
				const auto slot = g_slotOfForm.find(a_event->baseObj);
				if (slot == g_slotOfForm.end() || slot->second >= static_cast<int>(g_slotName.size()) || !Bound(TierOf(g_slotName[slot->second]))) {
					return RE::BSEventNotifyControl::kContinue;
				}
				const RE::FormID form = a_event->baseObj;
				const int        count = a_event->itemCount;
				if (auto it = g_allowedLoss.find(form); it != g_allowedLoss.end() && it->second > 0) {
					it->second -= count;  // retrait voulu (purification, mort, recopie de RoR2)
					if (it->second <= 0) {
						g_allowedLoss.erase(it);
					}
					return RE::BSEventNotifyControl::kContinue;
				}
				const RE::FormID      target = a_event->newContainer;
				const RE::ObjectRefHandle dropped = a_event->reference;
				const bool            isVoid = TierOf(g_slotName[slot->second]) == kVoid;
				SKSE::GetTaskInterface()->AddTask([form, count, target, dropped, isVoid]() {
					auto* player = RE::PlayerCharacter::GetSingleton();
					auto* object = RE::TESForm::LookupByID<RE::TESBoundObject>(form);
					if (!player || !object) {
						return;
					}
					bool back = false;
					if (target != 0) {
						if (auto* container = RE::TESForm::LookupByID<RE::TESObjectREFR>(target)) {
							container->RemoveItem(object, count, RE::ITEM_REMOVE_REASON::kStoreInContainer, nullptr, player);
							back = true;
						}
					} else if (auto ref = dropped.get()) {
						ref->Disable();
						ref->SetDelete(true);
					}
					if (!back) {
						player->AddObjectToContainer(object, nullptr, count, nullptr);
					}
					Show(isVoid ? "Les objets du vide ne te quittent pas : purifie-toi a un autel d'Arkay" :
					              "Les objets lunaires ne te quittent pas : purifie-toi a un autel de Stendarr");
				});
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		// Le dernier meuble active (lit, fonderie) et le dernier chef de caravane a qui on parle.
		class ActivateSink final : public RE::BSTEventSink<RE::TESActivateEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESActivateEvent* a_event, RE::BSTEventSource<RE::TESActivateEvent>*) override
			{
				if (!a_event || !a_event->objectActivated || !a_event->actionRef ||
					a_event->actionRef.get() != RE::PlayerCharacter::GetSingleton()) {
					return RE::BSEventNotifyControl::kContinue;
				}
				auto* ref = a_event->objectActivated.get();
				auto* base = ref->GetBaseObject();
				if (base && base->Is(RE::FormType::Furniture)) {
					g_lastFurniture = ref->GetFormID();
					g_lastFurnitureName = Lower(base->GetName() ? base->GetName() : "");
					g_lastFurnitureTime = Clock::now();
				} else if (auto* actor = ref->As<RE::Actor>()) {
					auto*             npc = actor->GetActorBase();
					const std::string name = Lower(npc && npc->GetName() ? npc->GetName() : "");
					for (const char* leader : { "ri'saad", "ma'dran", "ahkari", "risaad", "madran" }) {
						if (name.find(leader) != std::string::npos) {
							g_lastTalked = actor->GetFormID();
							g_lastTalkedTime = Clock::now();
						}
					}
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		void OpenScrapper(std::size_t a_page);
		void OpenBazaar(RE::FormID a_leader);
		void OpenKitchen(std::size_t a_page);
		void OpenJournal();

		// 0.71 : le journal. En ouvrant le livre "Journal RoR2", on le referme et on le rouvre avec la liste a jour.
		RE::TESObjectBOOK* JournalForm()
		{
			auto* data = RE::TESDataHandler::GetSingleton();
			return data ? data->LookupForm<RE::TESObjectBOOK>(kJournalId, kPluginFile) : nullptr;
		}

		// En quittant une fonderie : le broyeur. En quittant une conversation avec un chef de caravane : le bazar.
		class MenuSink final : public RE::BSTEventSink<RE::MenuOpenCloseEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::MenuOpenCloseEvent* a_event, RE::BSTEventSource<RE::MenuOpenCloseEvent>*) override
			{
				if (!a_event || a_event->opening || !g_enabled) {
					return RE::BSEventNotifyControl::kContinue;
				}
				const std::string_view menu = a_event->menuName.c_str();
				const auto             now = Clock::now();
				if (menu == RE::CraftingMenu::MENU_NAME && now - g_lastFurnitureTime < std::chrono::minutes(5)) {
					const bool smelter = g_lastFurnitureName.find("fonderie") != std::string::npos ||
					                     g_lastFurnitureName.find("smelter") != std::string::npos;
					if (smelter) {
						SKSE::GetTaskInterface()->AddTask([]() { OpenScrapper(0); });
					}
					// 0.71 : la marmite devient le CHEF errant de RoR2.
					bool pot = false;
					for (const char* word : { "marmite", "cooking pot", "cookpot", "chaudron", "broche", "spit" }) {
						pot = pot || g_lastFurnitureName.find(word) != std::string::npos;
					}
					if (pot) {
						SKSE::GetTaskInterface()->AddTask([]() { OpenKitchen(0); });
					}
				} else if (menu == RE::DialogueMenu::MENU_NAME && g_lastTalked != 0 && now - g_lastTalkedTime < std::chrono::minutes(5)) {
					const RE::FormID leader = g_lastTalked;
					g_lastTalked = 0;
					SKSE::GetTaskInterface()->AddTask([leader]() { OpenBazaar(leader); });
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		// Le lit ou l'on s'endort ; au reveil, le choix du survivant (pas apres une simple attente).
		bool g_sleptInBed = false;

		bool IsBed(const std::string& a_name)
		{
			for (const char* word : { "lit", "bed", "couchage", "paillasse", "hamac", "cercueil", "coffin" }) {
				if (a_name.find(word) != std::string::npos) {
					return true;
				}
			}
			return false;
		}

		class SleepStartSink final : public RE::BSTEventSink<RE::TESSleepStartEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESSleepStartEvent*, RE::BSTEventSource<RE::TESSleepStartEvent>*) override
			{
				g_sleptInBed = g_lastFurniture != 0 && Clock::now() - g_lastFurnitureTime < std::chrono::seconds(60) && IsBed(g_lastFurnitureName);
				if (g_sleptInBed) {
					g_lastBed = g_lastFurniture;
					logger::info("Objets Skyrim : lit {:08X} retenu pour le reveil apres une mort", g_lastBed);
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		class SleepStopSink final : public RE::BSTEventSink<RE::TESSleepStopEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESSleepStopEvent*, RE::BSTEventSource<RE::TESSleepStopEvent>*) override
			{
				if (g_sleepSwitch && Shrines::Enabled() && g_sleptInBed) {
					g_sleptInBed = false;
					SKSE::GetTaskInterface()->AddTask([]() { Shrines::OpenSurvivorChoice("Tu te reveilles repose."); });
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		// ---- Le broyeur ----

		const char* ScrapName(int a_tier)
		{
			switch (a_tier) {
			case kWhite:
				return "ScrapWhite";
			case kGreen:
				return "ScrapGreen";
			case kRed:
				return "ScrapRed";
			case kYellow:
				return "ScrapYellow";
			default:
				return nullptr;
			}
		}

		void OpenScrapper(std::size_t a_page)
		{
			auto* player = RE::PlayerCharacter::GetSingleton();
			if (!player) {
				return;
			}
			std::vector<std::pair<std::string, int>> list;
			for (const auto& [name, count] : Carried(player)) {
				const auto* info = InfoOf(name);
				if (info && info->tier <= kYellow && !info->scrap) {
					list.emplace_back(name, count);
				}
			}
			if (list.empty()) {
				return;  // rien a fondre : pas de fenetre
			}
			constexpr std::size_t kPerPage = 7;
			const std::size_t     pages = (list.size() + kPerPage - 1) / kPerPage;
			const std::size_t     page = a_page % pages;
			std::vector<std::string> buttons;
			std::vector<std::string> names;
			for (std::size_t i = page * kPerPage; i < list.size() && i < (page + 1) * kPerPage; ++i) {
				const auto& [name, count] = list[i];
				names.push_back(name);
				buttons.push_back(std::format("{} [{}] x{}", DisplayName(name), TierWord(TierOf(name)), count));
			}
			if (pages > 1) {
				buttons.push_back("Page suivante");
			}
			buttons.push_back("Terminer");
			const std::string body = std::format("Broyeur RoR2 : fondre un objet porte en ferraille de sa couleur{}",
				pages > 1 ? std::format(" (page {}/{})", page + 1, pages) : std::string());
			Shrines::ShowMessageBox(body, buttons, [names, page, pages](std::uint32_t a_index) {
				auto* player = RE::PlayerCharacter::GetSingleton();
				if (!player) {
					return;
				}
				if (a_index < names.size()) {
					const std::string& name = names[a_index];
					const int          tier = TierOf(name);
					const char*        scrap = ScrapName(tier);
					if (scrap && Take(player, name, 1) == 1) {
						Give(player, scrap, 1);
						Show(std::format("{} fondu : +1 ferraille {}", DisplayName(name), TierWord(tier)));
						logger::info("Broyeur : {} -> {}", name, scrap);
					}
					SKSE::GetTaskInterface()->AddTask([page]() { OpenScrapper(page); });
				} else if (pages > 1 && a_index == names.size()) {
					SKSE::GetTaskInterface()->AddTask([page]() { OpenScrapper(page + 1); });
				}
			});
		}

		// ---- Le bazar lunaire (caravanes khajiites) ----

		std::vector<std::string> Findable(int a_tier)
		{
			std::vector<std::string> list;
			for (const auto& [name, info] : g_catalog) {
				if (info.tier == a_tier && info.findable) {
					list.push_back(name);
				}
			}
			return list;
		}

		int Week()
		{
			auto* calendar = RE::Calendar::GetSingleton();
			return calendar ? static_cast<int>(calendar->GetDaysPassed() / 7.0f) : 0;
		}

		void OpenBazaar(RE::FormID a_leader)
		{
			auto* leader = RE::TESForm::LookupByID<RE::Actor>(a_leader);
			if (!leader || g_catalog.empty()) {
				return;
			}
			const int  week = Week();
			auto       pool = Findable(kLunar);
			if (pool.empty()) {
				return;
			}
			std::mt19937 seeded(static_cast<std::uint32_t>(a_leader) * 2654435761u ^ static_cast<std::uint32_t>(week) * 40503u);
			std::shuffle(pool.begin(), pool.end(), seeded);
			pool.resize((std::min)(pool.size(), std::size_t{ 3 }));

			std::vector<std::string> buttons;
			for (std::size_t i = 0; i < pool.size(); ++i) {
				const bool sold = g_sold.contains({ a_leader, week, static_cast<int>(i) });
				buttons.push_back(sold ? std::format("{} (vendu)", DisplayName(pool[i])) :
				                         std::format("{} ({} pieces lunaires)", DisplayName(pool[i]), g_bazaarPrice));
			}
			buttons.push_back("Partir");
			const char*       name = leader->GetDisplayFullName();
			const std::string body = std::format("{} : \"J'ai des choses venues d'une autre lune, ami.\"\n\nBazar lunaire (tu as {} piece(s) lunaire(s), "
			                                     "nouveaux objets chaque semaine)",
				name ? name : "Caravane", Loot::Coins());
			Shrines::ShowMessageBox(body, buttons, [pool, a_leader, week](std::uint32_t a_index) {
				auto* player = RE::PlayerCharacter::GetSingleton();
				if (!player || a_index >= pool.size()) {
					return;
				}
				const auto key = std::make_tuple(a_leader, week, static_cast<int>(a_index));
				if (g_sold.contains(key)) {
					Show("Deja vendu cette semaine");
				} else if (!Loot::SpendCoins(g_bazaarPrice)) {
					Show(std::format("Il faut {} pieces lunaires (tu en as {})", g_bazaarPrice, Loot::Coins()));
				} else {
					g_sold.insert(key);
					Give(player, pool[a_index], 1);
					Show(std::format("Objet lunaire : {}", DisplayName(pool[a_index])));
					logger::info("Bazar : {} achete", pool[a_index]);
				}
				SKSE::GetTaskInterface()->AddTask([a_leader]() { OpenBazaar(a_leader); });
			});
		}

		// ---- 0.71 : la marmite (CHEF errant de RoR2) ----
		// RoR2 ecrit dans recettes.txt les recettes possibles avec les objets PORTES : "numero<TAB>texte".
		// Choisir une recette la demande a RoR2 (rang 11, nombre = numero) : RoR2 retire les deux ingredients et donne
		// le plat ; Skyrim recopie ensuite ces changements dans l'inventaire, comme pour un objet ramasse.
		void OpenKitchen(std::size_t a_page)
		{
			std::vector<std::pair<int, std::string>> recipes;
			{
				std::istringstream lines(ReadText(Folder() / "recettes.txt"));
				std::string        line;
				while (std::getline(lines, line)) {
					while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) {
						line.pop_back();
					}
					const auto tab = line.find('\t');
					if (tab == std::string::npos) {
						continue;
					}
					const int id = std::atoi(line.substr(0, tab).c_str());
					if (id > 0) {
						recipes.emplace_back(id, line.substr(tab + 1));
					}
				}
			}
			if (recipes.empty()) {
				Show("CHEF errant : aucune recette avec les objets RoR2 que tu portes");
				return;
			}
			constexpr std::size_t kPerPage = 7;
			const std::size_t     pages = (recipes.size() + kPerPage - 1) / kPerPage;
			const std::size_t     page = a_page % pages;
			std::vector<std::string> buttons;
			std::vector<int>         ids;
			for (std::size_t i = page * kPerPage; i < recipes.size() && i < (page + 1) * kPerPage; ++i) {
				ids.push_back(recipes[i].first);
				buttons.push_back(recipes[i].second);
			}
			if (pages > 1) {
				buttons.push_back("Page suivante");
			}
			buttons.push_back("Terminer");
			const std::string body = std::format("CHEF errant : combiner deux objets RoR2 portes en un nouveau{}",
				pages > 1 ? std::format(" (page {}/{})", page + 1, pages) : std::string());
			Shrines::ShowMessageBox(body, buttons, [ids, page, pages](std::uint32_t a_index) {
				if (a_index < ids.size()) {
					Loot::RequestLoot(11, ids[a_index]);
					Show("Le CHEF errant cuisine...");
					logger::info("Marmite : recette {} demandee a RoR2", ids[a_index]);
				} else if (pages > 1 && a_index == ids.size()) {
					SKSE::GetTaskInterface()->AddTask([page]() { OpenKitchen(page + 1); });
				}
			});
		}

		// 0.72 : le journal en fenetres (ouvrir un livre avec notre texte faisait planter Skyrim).
		//   a_category < 0 : le sommaire ; sinon la liste des objets trouves de ce rang (8 = equipements), page a_page.
		void OpenJournalAt(int a_category, std::size_t a_page)
		{
			constexpr int         kOrder[]{ kWhite, kGreen, kRed, kYellow, kVoid, kLunar, kHallowed, kMeal };
			constexpr const char* kTitles[]{ "Objets blancs", "Objets verts", "Objets rouges", "Objets jaunes (boss)", "Objets du vide",
				"Objets lunaires", "Objets sanctifies", "Repas", "Equipements" };
			constexpr int kCategories = 9;
			// Les objets trouves de chaque rubrique : (nom affiche, description), et le nombre total d'objets.
			std::array<std::vector<std::pair<std::string, std::string>>, kCategories> found;
			std::array<int, kCategories>                                             totals{};
			for (const auto& [name, info] : g_catalog) {
				for (int c = 0; c < 8; ++c) {
					if (info.tier == kOrder[c] && !info.scrap) {
						++totals[c];
						if (g_found.contains(name)) {
							found[c].emplace_back(info.display.empty() ? name : info.display, info.description);
						}
					}
				}
			}
			for (const auto& [name, info] : g_eqCatalog) {
				++totals[8];
				if (g_found.contains("eq:" + name)) {
					found[8].emplace_back(EquipDisplay(name), info.description);
				}
			}
			std::vector<std::string> buttons;
			if (a_category < 0 || a_category >= kCategories) {
				std::vector<int> shown;
				int              all = 0, total = 0;
				for (int c = 0; c < kCategories; ++c) {
					all += static_cast<int>(found[c].size());
					total += totals[c];
					if (totals[c] > 0) {
						shown.push_back(c);
						buttons.push_back(std::format("{} ({} / {})", kTitles[c], found[c].size(), totals[c]));
					}
				}
				buttons.push_back("Fermer");
				const std::string body = std::format("Journal RoR2 : {} objets trouves sur {}.\nChaque objet porte une fois est note ici, avec ce qu'il fait.", all, total);
				Shrines::ShowMessageBox(body, buttons, [shown](std::uint32_t a_index) {
					if (a_index < shown.size()) {
						const int category = shown[a_index];
						SKSE::GetTaskInterface()->AddTask([category]() { OpenJournalAt(category, 0); });
					}
				});
				return;
			}
			const auto&           list = found[a_category];
			constexpr std::size_t kPerPage = 4;
			const std::size_t     pages = (std::max)(std::size_t{ 1 }, (list.size() + kPerPage - 1) / kPerPage);
			const std::size_t     page = a_page % pages;
			std::string           body = std::format("{} : {} / {}{}\n", kTitles[a_category], list.size(), totals[a_category],
				pages > 1 ? std::format(" (page {}/{})", page + 1, pages) : std::string());
			if (list.empty()) {
				body += "\nAucun pour l'instant.";
			}
			for (std::size_t i = page * kPerPage; i < list.size() && i < (page + 1) * kPerPage; ++i) {
				std::string description = list[i].second;
				if (description.size() > 160) {
					description = description.substr(0, 157) + "...";
				}
				body += std::format("\n{} : {}\n", list[i].first, description);
			}
			if (pages > 1) {
				buttons.push_back("Page suivante");
			}
			buttons.push_back("Retour");
			buttons.push_back("Fermer");
			const bool more = pages > 1;
			Shrines::ShowMessageBox(body, buttons, [a_category, page, more](std::uint32_t a_index) {
				if (more && a_index == 0) {
					SKSE::GetTaskInterface()->AddTask([a_category, page]() { OpenJournalAt(a_category, page + 1); });
				} else if (a_index == (more ? 1u : 0u)) {
					SKSE::GetTaskInterface()->AddTask([]() { OpenJournalAt(-1, 0); });
				}
			});
		}

		void OpenJournal()
		{
			OpenJournalAt(-1, 0);
			logger::info("Journal : ouvert ({} objets trouves)", g_found.size());
		}

		// 0.72 : le livre de la 0.71 faisait planter Skyrim a l'ouverture : il est retire, le journal s'ouvre avec une touche.
		void GiveJournal(RE::PlayerCharacter* a_player)
		{
			if (g_journalGiven) {
				return;
			}
			g_journalGiven = true;
			auto* journal = JournalForm();
			if (!journal) {
				return;
			}
			int count = 0;
			for (auto& [object, data] : a_player->GetInventory([journal](RE::TESBoundObject& a_object) { return &a_object == journal; })) {
				count += data.first;
			}
			if (count > 0) {
				a_player->RemoveItem(journal, count, RE::ITEM_REMOVE_REASON::kRemove, nullptr, nullptr);
				Show(std::format("Le Journal RoR2 s'ouvre maintenant avec la touche {}", Config::Get().KeyName(Action::kJournal)));
				logger::info("Journal : ancien livre retire de l'inventaire");
			}
		}

		int ReadInt(const char* a_key, int a_default, const std::string& a_ini)
		{
			return static_cast<int>(::GetPrivateProfileIntA("ObjetsSkyrim", a_key, a_default, a_ini.c_str()));
		}
	}

	void OpenPortalBazaar(std::uint32_t a_key)
	{
		if (g_catalog.empty()) {
			Show("Le Bazar ne repond pas encore (RoR2 pas pret)");
			return;
		}
		auto pool = Findable(kLunar);
		if (pool.empty()) {
			return;
		}
		const RE::FormID key = 0xFE000000u | (a_key & 0x00FFFFFFu);  // pas un vrai objet du jeu (voir Load)
		std::mt19937     seeded(a_key * 2654435761u);
		std::shuffle(pool.begin(), pool.end(), seeded);
		pool.resize((std::min)(pool.size(), std::size_t{ 5 }));
		std::vector<std::string> buttons;
		for (std::size_t i = 0; i < pool.size(); ++i) {
			const bool sold = g_sold.contains({ key, -1, static_cast<int>(i) });
			buttons.push_back(sold ? std::format("{} (vendu)", DisplayName(pool[i])) :
			                         std::format("{} ({} pieces lunaires)", DisplayName(pool[i]), g_bazaarPrice));
		}
		buttons.push_back("Partir");
		const std::string body = std::format("Newt : \"Bienvenue au Bazar entre les temps !\"\n\nBazar lunaire (tu as {} piece(s) lunaire(s))", Loot::Coins());
		Shrines::ShowMessageBox(body, buttons, [pool, key, a_key](std::uint32_t a_index) {
			auto* player = RE::PlayerCharacter::GetSingleton();
			if (!player || a_index >= pool.size()) {
				return;
			}
			const auto slot = std::make_tuple(key, -1, static_cast<int>(a_index));
			if (g_sold.contains(slot)) {
				Show("Deja vendu");
			} else if (!Loot::SpendCoins(g_bazaarPrice)) {
				Show(std::format("Il faut {} pieces lunaires (tu en as {})", g_bazaarPrice, Loot::Coins()));
			} else {
				g_sold.insert(slot);
				Give(player, pool[a_index], 1);
				Show(std::format("Objet lunaire : {}", DisplayName(pool[a_index])));
				logger::info("Bazar de Newt : {} achete", pool[a_index]);
			}
			SKSE::GetTaskInterface()->AddTask([a_key]() { OpenPortalBazaar(a_key); });
		});
	}

	// ---- Fonctions publiques ----

	const char* TierWord(int a_tier)
	{
		switch (a_tier) {
		case kWhite:
			return "blanc";
		case kGreen:
			return "vert";
		case kRed:
			return "rouge";
		case kYellow:
			return "jaune";
		case kVoid:
			return "vide";
		case kLunar:
			return "lunaire";
		case kHallowed:
			return "sanctifie";
		case kMeal:
			return "repas";
		default:
			return "autre";
		}
	}

	std::string DisplayName(const std::string& a_item)
	{
		const auto* info = InfoOf(a_item);
		return info && !info->display.empty() ? info->display : a_item;
	}

	std::string RandomFindable(int a_tier)
	{
		const auto list = Findable(a_tier);
		if (list.empty()) {
			return {};
		}
		return list[std::uniform_int_distribution<std::size_t>(0, list.size() - 1)(Random())];
	}

	bool Enabled() { return g_enabled; }
	bool WakeOnDeath() { return g_enabled && g_wake; }
	bool SleepToSwitch() { return g_sleepSwitch; }

	void LoadSettings()
	{
		const auto ini = std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini").string();
		g_enabled = ReadInt("Activer", 1, ini) != 0;
		g_wake = ReadInt("SansRechargement", 1, ini) != 0;
		g_sleepSwitch = ReadInt("ChangerEnDormant", 1, ini) != 0;
		g_bazaarPrice = std::clamp(ReadInt("PrixBazar", 2, ini), 0, 50);
		g_shinyChance = std::clamp(ReadInt("ChancePerleIrradiante", 10, ini), 0, 100);
		logger::info("Objets Skyrim : {} ; mort sans rechargement : {} ; survivant change en dormant : {}", g_enabled ? "dans l'inventaire" : "desactives",
			g_wake ? "oui" : "non", g_sleepSwitch ? "oui" : "non");
	}

	void Install()
	{
		LoadSettings();
		// Premiere valeur au hasard : un RoR2 reste ouvert ne confond pas nos demandes avec celles d'avant.
		g_sequence = (static_cast<std::uint32_t>(::GetTickCount()) & 0x00FFFFFFu) * 16u + 1u;
		g_published = 0;
		if (auto* events = RE::ScriptEventSourceHolder::GetSingleton()) {
			static ContainerSink   containers;
			static ActivateSink    activations;
			static SleepStartSink  sleepStart;
			static SleepStopSink   sleepStop;
			events->AddEventSink<RE::TESContainerChangedEvent>(&containers);
			events->AddEventSink<RE::TESActivateEvent>(&activations);
			events->AddEventSink<RE::TESSleepStartEvent>(&sleepStart);
			events->AddEventSink<RE::TESSleepStopEvent>(&sleepStop);
		}
		if (auto* ui = RE::UI::GetSingleton()) {
			static MenuSink menus;
			ui->AddEventSink<RE::MenuOpenCloseEvent>(&menus);
		}
	}

	void OnGameLoaded(bool a_newGame, const std::string& a_savedText)
	{
		LoadSettings();
		if (!g_enabled) {
			return;
		}
		g_believed.clear();
		g_pending = 0;
		g_needReplace = true;
		g_clearEquipment = false;
		g_reviveScheduled = false;
		g_equipmentLine.clear();
		g_eqBelievedKnown = false;  // 0.72
		g_gainRead = -1;
		g_eqMigrate.clear();
		if (!a_newGame) {
			std::string equipment;
			ParseItems(a_savedText, nullptr, &equipment);
			g_equipmentLine = equipment;
			if (equipment.size() > 11) {
				g_eqMigrate = equipment.substr(11);  // devient une amulette si tu n'en as aucune
			}
		} else {
			g_equipmentLine = "equipement:";  // nouvelle partie : aucun equipement
		}
		// Anciennes sauvegardes (avant la 0.61) : leurs objets RoR2 passent dans l'inventaire de Skyrim.
		if (!a_newGame && !g_migrated) {
			g_migrateText = a_savedText;
		}
		if (a_newGame) {
			g_migrated = true;
		}
		g_journalGiven = false;  // 0.71 : le journal est rendu s'il manque
		g_nextTick = Clock::now() + std::chrono::seconds(1);
		logger::info("Objets Skyrim : partie chargee, l'inventaire de Skyrim sera envoye a RoR2{}", g_migrated ? "" : " (anciens objets a reprendre)");
	}

	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked)
	{
		if (!g_enabled || !a_player) {
			return;
		}
		const auto now = Clock::now();
		if (g_reviveScheduled && now >= g_reviveAt) {
			g_reviveScheduled = false;
			++g_revive;
			logger::info("Mort : reveil, le survivant est releve dans RoR2");
		}
		// 0.72 : la touche du journal (hors menus).
		{
			static bool wasDown = false;
			auto*       ui = RE::UI::GetSingleton();
			const int   key = Config::Get().Key(Action::kJournal);
			const bool  down = key != 0 && !(ui && ui->GameIsPaused()) &&
			                  (InputWatcher::Get().IsDown(key) || (::GetAsyncKeyState(key) & 0x8000) != 0);
			if (down && !wasDown && !g_catalog.empty()) {
				OpenJournal();
			}
			wasDown = down;
		}
		if (!a_linked || now < g_nextTick) {
			return;
		}
		g_nextTick = now + kTickEvery;
		if (!g_journalGiven && !g_catalog.empty()) {
			GiveJournal(a_player);
		}
		try {
			Tick(a_player);
		} catch (const std::exception& e) {
			logger::error("Objets Skyrim : {}", e.what());
		}
	}

	void FillCommand(bridge::SkyrimCommand& a_command)
	{
		if (g_enabled) {
			a_command.inventorySequence = g_published.load();
		}
		a_command.reviveSequence = g_revive.load();
	}

	void OnSurvivorDeath(RE::PlayerCharacter* a_player)
	{
		if (!a_player) {
			return;
		}
		// Les objets RoR2 portes sont perdus (ceux du vide et lunaires aussi) ; ceux des coffres restent.
		int lost = 0;
		for (const auto& [name, count] : Carried(a_player)) {
			lost += Take(a_player, name, count);
		}
		// 0.72 : les amulettes d'equipement sur toi aussi (celles des coffres restent).
		for (const auto& [name, count] : CarriedEquipment(a_player)) {
			TakeAmulet(a_player, name, count);
			lost += count;
		}
		g_clearEquipment = true;
		const bool keep = Coop::KeepDifficulty();  // 0.70 : des coequipiers sont encore debout
		Loot::ResetDifficulty();
		Loot::RequestLoot(19, 1);  // les drones repares disparaissent
		logger::info("Mort : {} objet(s) RoR2 perdu(s), difficulte remise a 1", lost);

		// Le reveil, dans le dernier lit ou l'on a dormi.
		auto*            bed = g_lastBed != 0 ? RE::TESForm::LookupByID<RE::TESObjectREFR>(g_lastBed) : nullptr;
		const RE::FormID bedId = bed ? g_lastBed : 0;
		if (bed) {
			SKSE::GetTaskInterface()->AddTask([bedId]() {
				auto* player = RE::PlayerCharacter::GetSingleton();
				auto* target = RE::TESForm::LookupByID<RE::TESObjectREFR>(bedId);
				if (player && target) {
					player->MoveTo(target);
				}
			});
		}
		const char* reset = keep ? "" : ", difficulte 1";
		Show(lost > 0 ? std::format("Ton survivant est tombe... Tu te reveilles{}. {} objet(s) RoR2 perdu(s){}.", bed ? " dans ton lit" : "", lost, reset) :
		                std::format("Ton survivant est tombe... Tu te reveilles{}{}.", bed ? " dans ton lit" : "", keep ? "" : ". Difficulte 1"));
		g_reviveAt = Clock::now() + std::chrono::seconds(2);
		g_reviveScheduled = true;
	}

	void OfferPurify(bool a_void, const std::string& a_shrineName)
	{
		auto* player = RE::PlayerCharacter::GetSingleton();
		if (!player || !g_enabled) {
			return;
		}
		const int tier = a_void ? kVoid : kLunar;
		int       count = 0;
		for (const auto& [name, have] : Carried(player)) {
			if (TierOf(name) == tier) {
				count += have;
			}
		}
		if (count == 0) {
			return;
		}
		const std::string body = a_void ?
		                             std::format("{} : se purifier ?\n\nTous tes objets du vide portes ({}) seront retires.", a_shrineName, count) :
		                             std::format("{} : se purifier ?\n\nTous tes objets lunaires portes ({}) seront retires, contre une Perle chacun.",
										 a_shrineName, count);
		Shrines::ShowMessageBox(body, { "Me purifier", "Non" }, [tier](std::uint32_t a_index) {
			auto* player = RE::PlayerCharacter::GetSingleton();
			if (!player || a_index != 0) {
				return;
			}
			int removed = 0;
			for (const auto& [name, have] : Carried(player)) {
				if (TierOf(name) == tier) {
					removed += Take(player, name, have);
				}
			}
			int shiny = 0;
			if (tier == kLunar) {
				for (int i = 0; i < removed; ++i) {
					const bool irradiant = std::uniform_int_distribution<int>(1, 100)(Random()) <= g_shinyChance;
					Give(player, irradiant ? "ShinyPearl" : "Pearl", 1);
					shiny += irradiant ? 1 : 0;
				}
			}
			logger::info("Purification : {} objet(s) {} retire(s), {} perle(s) irradiante(s)", removed, TierWord(tier), shiny);
			Show(tier == kVoid ? std::format("Purifie : {} objet(s) du vide retire(s)", removed) :
			                     std::format("Purifie : {} objet(s) lunaire(s) retire(s), {} Perle(s){}", removed, removed - shiny,
									 shiny > 0 ? std::format(" et {} Perle(s) irradiante(s)", shiny) : std::string()));
		});
	}

	bool PrintFor(const std::string& a_item, int a_tier)
	{
		auto* player = RE::PlayerCharacter::GetSingleton();
		if (!player || a_item.empty() || !g_enabled) {
			return false;
		}
		// La ferraille d'abord, sinon un objet porte du meme rang au hasard (pas celui qu'on imprime).
		std::string paid;
		if (const char* scrap = ScrapName(a_tier); scrap && CarriedCount(player, scrap) > 0) {
			paid = scrap;
		} else {
			std::vector<std::string> candidates;
			for (const auto& [name, count] : Carried(player)) {
				const auto* info = InfoOf(name);
				if (info && info->tier == a_tier && !info->scrap && name != a_item) {
					candidates.push_back(name);
				}
			}
			if (candidates.empty()) {
				Show(std::format("Il faut un objet {} (ou une ferraille {}) sur toi", TierWord(a_tier), TierWord(a_tier)));
				return false;
			}
			paid = candidates[std::uniform_int_distribution<std::size_t>(0, candidates.size() - 1)(Random())];
		}
		if (Take(player, paid, 1) != 1) {
			return false;
		}
		Give(player, a_item, 1);
		Show(std::format("Imprime : {} (contre {})", DisplayName(a_item), DisplayName(paid)));
		logger::info("Imprimante : {} contre {}", a_item, paid);
		return true;
	}

	// ---- Sauvegarde ----

	void Save(SKSE::SerializationInterface* a_intfc)
	{
		if (a_intfc->OpenRecord(kMapRecord, kRecordVersion)) {
			const auto count = static_cast<std::uint32_t>(g_slotName.size());
			a_intfc->WriteRecordData(&count, sizeof(count));
			for (const auto& name : g_slotName) {
				const auto length = static_cast<std::uint32_t>(name.size());
				a_intfc->WriteRecordData(&length, sizeof(length));
				if (length > 0) {
					a_intfc->WriteRecordData(name.data(), length);
				}
			}
		}
		if (a_intfc->OpenRecord(kStateRecord, kRecordVersion)) {
			const std::uint32_t migrated = 1;  // cette sauvegarde a deja ses objets dans l'inventaire
			a_intfc->WriteRecordData(&migrated, sizeof(migrated));
			a_intfc->WriteRecordData(&g_lastBed, sizeof(g_lastBed));
			const auto sold = static_cast<std::uint32_t>(g_sold.size());
			a_intfc->WriteRecordData(&sold, sizeof(sold));
			for (const auto& [leader, week, slot] : g_sold) {
				const std::int32_t values[3]{ static_cast<std::int32_t>(leader), week, slot };
				a_intfc->WriteRecordData(values, sizeof(values));
			}
		}
		if (a_intfc->OpenRecord(kJournalRecord, kRecordVersion)) {
			const auto count = static_cast<std::uint32_t>(g_found.size());
			a_intfc->WriteRecordData(&count, sizeof(count));
			for (const auto& name : g_found) {
				const auto length = static_cast<std::uint32_t>(name.size());
				a_intfc->WriteRecordData(&length, sizeof(length));
				a_intfc->WriteRecordData(name.data(), length);
			}
		}
	}

	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t)
	{
		if (a_type == kMapRecord) {
			// Secours : si la table de ce PC a disparu (objets_skyrim.txt), on la reprend de la sauvegarde.
			std::uint32_t count = 0;
			if (a_intfc->ReadRecordData(&count, sizeof(count)) != sizeof(count) || count > static_cast<std::uint32_t>(kSlots)) {
				return true;
			}
			std::vector<std::string> names;
			for (std::uint32_t i = 0; i < count; ++i) {
				std::uint32_t length = 0;
				if (a_intfc->ReadRecordData(&length, sizeof(length)) != sizeof(length) || length > 256) {
					return true;
				}
				std::string name(length, '\0');
				if (length > 0 && a_intfc->ReadRecordData(name.data(), length) != length) {
					return true;
				}
				names.push_back(std::move(name));
			}
			LoadMap();
			if (g_slotName.empty() && !names.empty()) {
				g_slotName = names;
				for (std::size_t i = 0; i < names.size(); ++i) {
					if (!names[i].empty()) {
						g_slotOf[names[i]] = static_cast<int>(i);
					}
				}
				SaveMap();
				g_namesDirty = true;
				logger::warn("Objets Skyrim : objets_skyrim.txt manquant, table reprise de la sauvegarde ({} objets)", names.size());
			}
			return true;
		}
		if (a_type == kStateRecord) {
			std::uint32_t migrated = 0;
			if (a_intfc->ReadRecordData(&migrated, sizeof(migrated)) == sizeof(migrated)) {
				g_migrated = migrated != 0;
			}
			RE::FormID bed = 0, resolved = 0;
			if (a_intfc->ReadRecordData(&bed, sizeof(bed)) == sizeof(bed) && bed != 0 && a_intfc->ResolveFormID(bed, resolved)) {
				g_lastBed = resolved;
			}
			std::uint32_t sold = 0;
			if (a_intfc->ReadRecordData(&sold, sizeof(sold)) == sizeof(sold) && sold < 100000) {
				for (std::uint32_t i = 0; i < sold; ++i) {
					std::int32_t values[3]{};
					if (a_intfc->ReadRecordData(values, sizeof(values)) != sizeof(values)) {
						break;
					}
					RE::FormID leader = 0;
					const auto saved = static_cast<RE::FormID>(values[0]);
					if ((saved >> 24) == 0xFE) {
						g_sold.insert({ saved, values[1], values[2] });  // 0.71 : porte du Bazar (pas un objet du jeu)
					} else if (a_intfc->ResolveFormID(saved, leader)) {
						g_sold.insert({ leader, values[1], values[2] });
					}
				}
			}
			return true;
		}
		if (a_type == kJournalRecord) {
			std::uint32_t count = 0;
			if (a_intfc->ReadRecordData(&count, sizeof(count)) != sizeof(count) || count > 5000) {
				return true;
			}
			for (std::uint32_t i = 0; i < count; ++i) {
				std::uint32_t length = 0;
				if (a_intfc->ReadRecordData(&length, sizeof(length)) != sizeof(length) || length > 256) {
					break;
				}
				std::string name(length, '\0');
				if (length > 0 && a_intfc->ReadRecordData(name.data(), length) != length) {
					break;
				}
				g_found.insert(std::move(name));
			}
			return true;
		}
		return false;
	}

	void Revert()
	{
		g_found.clear();
		g_migrated = false;
		g_migrateText.clear();
		g_lastBed = 0;
		g_sold.clear();
		g_believed.clear();
		g_pending = 0;
		g_allowedLoss.clear();
		g_intendedLoss.clear();
		g_reviveScheduled = false;
	}
}
