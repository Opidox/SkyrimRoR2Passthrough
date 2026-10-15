#include "Shrines.h"
#include "ThirdPerson.h"

#include "Loot.h"
#include "Altars.h"

#include "Config.h"
#include "Challenges.h"
#include "Combat.h"
#include "Experience.h"
#include "Terrain.h"
#include "Decor.h"
#include "Loadouts.h"
#include "Items.h"

#include <fstream>
#include <functional>
#include <map>

// Les autels des Divins et des Daedra.
//  - Chaque autel est lie a un survivant RoR2 ([AutelsSurvivants] dans SkyrimRoR2.ini).
//  - L'activer debloque ce survivant pour cette sauvegarde (le Commando est debloque d'office).
//  - Puis une fenetre s'ouvre pour choisir parmi les survivants debloques.
//  - La sauvegarde Skyrim retient aussi le survivant en cours : en la rechargeant, on le retrouve.
//  - Les princes daedriques, eux, donnent leur survivant a la fin de leur quete ([QuetesSurvivants]).

namespace skyrimror2::Shrines
{
	namespace
	{
		constexpr std::uint32_t kUniqueId = 'SRR2';
		constexpr std::uint32_t kUnlockRecord = 'UNLK';
		constexpr std::uint32_t kCurrentRecord = 'CURR';
		constexpr std::uint32_t kRecordVersion = 1;
		constexpr const char*   kDefaultSurvivor = "Commando";
		constexpr std::size_t   kPerPage = 8;  // survivants par page de la fenetre de choix

		std::mutex               g_lock;
		std::vector<std::string> g_unlocked{ kDefaultSurvivor };  // dans l'ordre de deblocage
		std::string              g_current{ kDefaultSurvivor };
		std::string              g_select;           // dernier choix envoye a RoR2
		std::uint32_t            g_selectSequence = 0;
		std::atomic_bool         g_enabled{ true };
		std::atomic_bool         g_keysUseAll{ false };

		struct ShrineLink
		{
			std::string god;       // "akatosh" (en minuscules, cherche dans le nom de l'autel)
			std::string survivor;  // "Railgunner"
		};
		std::vector<ShrineLink> g_links;

		// Liens par defaut (si la section [AutelsSurvivants] du .ini est vide).
		constexpr std::pair<const char*, const char*> kDefaultLinks[] = {
			// Les Neuf Divins
			{ "Akatosh", "Railgunner" },
			{ "Arkay", "VoidSurvivor" },
			{ "Dibella", "Mage" },
			{ "Julianos", "Engi" },
			{ "Kynareth", "Treebot" },
			{ "Mara", "Seeker" },
			{ "Stendarr", "Captain" },
			{ "Talos", "Merc" },
			{ "Zenithar", "Toolbot" },
			// Les princes daedriques
			{ "Hircine", "Huntress" },
			{ "Nocturnal", "Bandit2" },
			{ "Namira", "Croco" },
			{ "Boethiah", "Loader" },
			{ "Mehrunes", "FalseSon" },
			{ "Sanguine", "Chef" },
			{ "Hermaeus", "DroneTech" },
			{ "Azura", "Drifter" },
		};

		// ---- Les quetes daedriques : la fin d'une quete debloque un survivant ----
		struct QuestLink
		{
			std::string    questId;   // identifiant interne de la quete ("DA05")
			std::string    survivor;  // "Huntress"
			RE::TESQuest*  quest = nullptr;
		};
		std::vector<QuestLink> g_questLinks;  // seulement lu et modifie sur le fil principal du jeu
		bool                   g_questsResolved = false;  // quetes du jeu deja retrouvees pour ces liens

		// Liens par defaut (si la section [QuetesSurvivants] du .ini est vide).
		constexpr std::pair<const char*, const char*> kDefaultQuestLinks[] = {
			{ "DA01", "Drifter" },     // Azura : L'Etoile noire
			{ "DA04", "DroneTech" },   // Hermaeus Mora : Discerner le transmondain
			{ "DA05", "Huntress" },    // Hircine : Mal rencontre au clair de lune
			{ "DA02", "Loader" },      // Boethiah : L'Appel de Boethiah
			{ "DA07", "FalseSon" },    // Mehrunes Dagon : Les fragments du passe
			{ "DA11", "Croco" },       // Namira : Le gout de la mort
			{ "DA14", "Chef" },        // Sanguine : Une nuit inoubliable
			{ "TG08B", "Bandit2" },    // Nocturnal : Le retour des tenebres (Guilde des voleurs)
		};

		std::filesystem::path IniPath()
		{
			return std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini");
		}

		std::string Lower(std::string a_text)
		{
			std::ranges::transform(a_text, a_text.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
			return a_text;
		}

		std::string Trim(std::string a_text)
		{
			const auto first = a_text.find_first_not_of(" \t\r\n");
			if (first == std::string::npos) {
				return {};
			}
			const auto last = a_text.find_last_not_of(" \t\r\n");
			return a_text.substr(first, last - first + 1);
		}

		void LoadSettings()
		{
			const auto ini = IniPath().string();
			g_enabled = ::GetPrivateProfileIntA("Autels", "Activer", 1, ini.c_str()) != 0;
			g_keysUseAll = ::GetPrivateProfileIntA("Autels", "ToucheTousLesSurvivants", 0, ini.c_str()) != 0;
			// 0.70.1 : [Test] ModeTest = 1 -> F8 / F7 passent par tous les survivants (meme en changeant en dormant).
			if (::GetPrivateProfileIntA("Test", "ModeTest", 0, ini.c_str()) != 0) {
				g_keysUseAll = true;
				logger::info("Mode test : tous les survivants avec F8 / F7");
			}

			std::vector<ShrineLink> links;
			std::vector<char>       buffer(8192, '\0');
			const DWORD size = ::GetPrivateProfileSectionA("AutelsSurvivants", buffer.data(), static_cast<DWORD>(buffer.size()), ini.c_str());
			for (const char* line = buffer.data(); size > 0 && *line != '\0'; line += std::strlen(line) + 1) {
				std::string text = line;
				if (text.empty() || text[0] == ';') {
					continue;
				}
				const auto equal = text.find('=');
				if (equal == std::string::npos) {
					continue;
				}
				std::string god = Lower(Trim(text.substr(0, equal)));
				std::string survivor = Trim(text.substr(equal + 1));
				if (!god.empty() && !survivor.empty()) {
					links.push_back({ std::move(god), std::move(survivor) });
				}
			}
			if (links.empty()) {
				for (const auto& [god, survivor] : kDefaultLinks) {
					links.push_back({ Lower(god), survivor });
				}
			}
			// Les quetes daedriques ("DA05 = Huntress").
			std::vector<QuestLink> questLinks;
			std::fill(buffer.begin(), buffer.end(), '\0');
			const DWORD questSize = ::GetPrivateProfileSectionA("QuetesSurvivants", buffer.data(), static_cast<DWORD>(buffer.size()), ini.c_str());
			for (const char* line = buffer.data(); questSize > 0 && *line != '\0'; line += std::strlen(line) + 1) {
				std::string text = line;
				const auto  equal = text.find('=');
				if (text.empty() || text[0] == ';' || equal == std::string::npos) {
					continue;
				}
				std::string questId = Trim(text.substr(0, equal));
				std::string survivor = Trim(text.substr(equal + 1));
				if (const auto comment = survivor.find(';'); comment != std::string::npos) {
					survivor = Trim(survivor.substr(0, comment));
				}
				if (!questId.empty() && !survivor.empty()) {
					questLinks.push_back({ std::move(questId), std::move(survivor) });
				}
			}
			if (questLinks.empty()) {
				for (const auto& [questId, survivor] : kDefaultQuestLinks) {
					questLinks.push_back({ questId, survivor });
				}
			}
			g_questLinks = std::move(questLinks);
			g_questsResolved = false;  // les quetes seront retrouvees au prochain controle

			std::scoped_lock lock(g_lock);
			g_links = std::move(links);
			logger::info("Autels : {} ({} autels lies a un survivant), F8/F7 {}", g_enabled ? "actifs" : "desactives",
				g_links.size(), g_keysUseAll ? "sur tous les survivants" : "sur les survivants debloques");
		}

		// Noms affiches des survivants, ecrits par RoR2 dans %LOCALAPPDATA%\SkyrimRoR2\survivants.txt
		std::map<std::string, std::string> ReadDisplayNames()
		{
			std::map<std::string, std::string> names;
			char folder[MAX_PATH]{};
			if (::GetEnvironmentVariableA("LOCALAPPDATA", folder, MAX_PATH) == 0) {
				return names;
			}
			std::ifstream file(std::filesystem::path(folder) / "SkyrimRoR2" / "survivants.txt");
			std::string   line;
			while (std::getline(file, line)) {
				const auto bar = line.find('|');
				if (bar != std::string::npos) {
					names[Trim(line.substr(0, bar))] = Trim(line.substr(bar + 1));
				}
			}
			return names;
		}

		// Les survivants dans l'ordre de l'ecran de selection de RoR2 (survivants.txt est ecrit dans cet ordre).
		std::vector<std::string> ReadSurvivorOrder()
		{
			std::vector<std::string> order;
			char folder[MAX_PATH]{};
			if (::GetEnvironmentVariableA("LOCALAPPDATA", folder, MAX_PATH) == 0) {
				return order;
			}
			std::ifstream file(std::filesystem::path(folder) / "SkyrimRoR2" / "survivants.txt");
			std::string   line;
			while (std::getline(file, line)) {
				const auto bar = line.find('|');
				if (bar != std::string::npos) {
					order.push_back(Trim(line.substr(0, bar)));
				}
			}
			return order;
		}

		// Range une liste de survivants dans l'ordre de RoR2 (ceux qu'il ne connait pas : a la fin, dans leur ordre).
		std::vector<std::string> InRoR2Order(std::vector<std::string> a_survivors)
		{
			const auto order = ReadSurvivorOrder();
			const auto rank = [&order](const std::string& a_name) {
				const auto it = std::ranges::find(order, a_name);
				return it != order.end() ? static_cast<std::size_t>(it - order.begin()) : order.size();
			};
			std::ranges::stable_sort(a_survivors, [&rank](const std::string& a, const std::string& b) { return rank(a) < rank(b); });
			return a_survivors;
		}

		std::string DisplayName(const std::map<std::string, std::string>& a_names, const std::string& a_survivor)
		{
			const auto it = a_names.find(a_survivor);
			return it != a_names.end() && !it->second.empty() ? it->second : a_survivor;
		}

		void Show(const std::string& a_text)
		{
			RE::SendHUDMessage::ShowHUDMessage(a_text.c_str());
		}

		// Envoie un survivant a RoR2 (ecrit dans l'ordre au prochain envoi, ~60 fois par seconde).
		void Select(const std::string& a_survivor)
		{
			std::scoped_lock lock(g_lock);
			g_current = a_survivor;
			g_select = a_survivor;
			++g_selectSequence;
			logger::info("Autels : survivant choisi {}", a_survivor);
		}

		bool Unlock(const std::string& a_survivor)
		{
			std::scoped_lock lock(g_lock);
			if (std::ranges::find(g_unlocked, a_survivor) != g_unlocked.end()) {
				return false;
			}
			g_unlocked.push_back(a_survivor);
			return true;
		}

		// ---- La fenetre de choix (une boite de message de Skyrim, avec un bouton par survivant) ----
		class ChoiceCallback : public RE::IMessageBoxCallback
		{
		public:
			explicit ChoiceCallback(std::function<void(std::uint32_t)> a_onChoice) :
				onChoice_(std::move(a_onChoice))
			{}

			void Run(std::uint8_t a_button) override
			{
				onChoice_(a_button);  // numero du bouton (on a mis le decalage a 0)
			}

		private:
			std::function<void(std::uint32_t)> onChoice_;
		};

		void ShowBox(const std::string& a_body, const std::vector<std::string>& a_buttons, std::function<void(std::uint32_t)> a_onChoice)
		{
			auto* factoryManager = RE::MessageDataFactoryManager::GetSingleton();
			auto* strings = RE::InterfaceStrings::GetSingleton();
			auto* factory = factoryManager && strings ? factoryManager->GetCreator<RE::MessageBoxData>(strings->messageBoxData) : nullptr;
			auto* box = factory ? factory->Create() : nullptr;
			if (!box) {
				logger::error("Autels : impossible d'ouvrir la fenetre de choix");
				return;
			}
			box->callback = RE::BSTSmartPointer<RE::IMessageBoxCallback>(new ChoiceCallback(std::move(a_onChoice)));
			box->bodyText = a_body.c_str();
			for (const auto& text : a_buttons) {
				box->buttonText.push_back(text.c_str());
			}
			box->warningType = 25;  // priorite haute, comme les boites de message des scripts
			box->menuDepth = 4;
			box->buttonPressOffset = 0;
			box->verticalButtons = true;  // boutons en liste, un survivant par ligne
			box->isCancellable = true;
			box->cancelButtonIndex = static_cast<std::int32_t>(a_buttons.size()) - 1;  // Echap = "Annuler"
			// On passe par la file d'attente des boites de message du jeu (comme les scripts Papyrus) :
			// c'est elle qui ouvre la fenetre. Envoyer directement "afficher" au menu ne marchait pas.
			RE::MessageBoxMenu::QueueMessage(box);
			logger::info("Autels : fenetre de choix envoyee ({} boutons)", a_buttons.size());
		}

		void OpenChoice(std::size_t a_page, const std::string& a_header)
		{
			std::vector<std::string> unlocked;
			std::string              current;
			{
				std::scoped_lock lock(g_lock);
				unlocked = g_unlocked;
				current = g_current;
			}
			unlocked = InRoR2Order(std::move(unlocked));  // dans l'ordre de l'ecran de selection de RoR2
			const auto names = ReadDisplayNames();
			const std::size_t pages = (unlocked.size() + kPerPage - 1) / kPerPage;
			const std::size_t page = pages > 0 ? a_page % pages : 0;
			const std::size_t first = page * kPerPage;
			const std::size_t count = std::min(kPerPage, unlocked.size() - first);

			std::vector<std::string> buttons;
			std::vector<std::string> survivors;
			for (std::size_t i = 0; i < count; ++i) {
				const std::string& survivor = unlocked[first + i];
				survivors.push_back(survivor);
				buttons.push_back(DisplayName(names, survivor) + (survivor == current ? " (actuel)" : ""));
			}
			const bool hasMore = pages > 1;
			if (hasMore) {
				buttons.push_back("Page suivante");
			}
			buttons.push_back("Annuler");

			std::string body = a_header;
			if (!body.empty()) {
				body += "\n\n";
			}
			body += std::format("Choisis ton survivant ({} debloque{})", unlocked.size(), unlocked.size() > 1 ? "s" : "");
			if (hasMore) {
				body += std::format(" - page {}/{}", page + 1, pages);
			}

			ShowBox(body, buttons, [survivors, hasMore, page, names](std::uint32_t a_index) {
				if (a_index < survivors.size()) {
					const std::string& chosen = survivors[a_index];
					Select(chosen);
					Show("Survivant : " + DisplayName(names, chosen));
				} else if (hasMore && a_index == survivors.size()) {
					SKSE::GetTaskInterface()->AddTask([page]() { OpenChoice(page + 1, {}); });
				}
			});
		}

		// Retrouve les quetes du jeu a partir de leur identifiant ("DA05").
		void ResolveQuests()
		{
			auto* data = RE::TESDataHandler::GetSingleton();
			if (!data) {
				return;
			}
			std::size_t found = 0;
			for (auto* quest : data->GetFormArray<RE::TESQuest>()) {
				const char* id = quest ? quest->GetFormEditorID() : nullptr;
				if (!id || !id[0]) {
					continue;
				}
				for (auto& link : g_questLinks) {
					if (!link.quest && _stricmp(link.questId.c_str(), id) == 0) {
						link.quest = quest;
						++found;
					}
				}
			}
			for (const auto& link : g_questLinks) {
				if (!link.quest) {
					logger::warn("Quetes : quete {} introuvable (survivant {} non debloquable par quete)", link.questId, link.survivor);
				}
			}
			logger::info("Quetes : {} quete(s) daedrique(s) surveillee(s) sur {}", found, g_questLinks.size());
		}

		// Debloque les survivants des quetes terminees. Renvoie les survivants nouvellement debloques.
		std::vector<std::pair<std::string, std::string>> CheckQuests()  // (nom de la quete, survivant)
		{
			if (!g_questsResolved) {
				ResolveQuests();
				g_questsResolved = true;
			}
			std::vector<std::pair<std::string, std::string>> unlocked;
			for (const auto& link : g_questLinks) {
				if (link.quest && link.quest->IsCompleted() && Unlock(link.survivor)) {
					const char* name = link.quest->GetName();
					unlocked.emplace_back(name && name[0] ? name : link.questId, link.survivor);
					logger::info("Quetes : {} terminee, survivant {} debloque", link.questId, link.survivor);
				}
			}
			return unlocked;
		}

		// L'autel lie a ce nom d'objet (vide si ce n'est pas un autel connu).
		std::string SurvivorForShrine(const std::string& a_name)
		{
			const std::string name = Lower(a_name);
			std::scoped_lock  lock(g_lock);
			for (const auto& link : g_links) {
				if (name.find(link.god) != std::string::npos) {
					return link.survivor;
				}
			}
			return {};
		}

		// ---- Activation d'un objet par le joueur ----
		class ActivateSink : public RE::BSTEventSink<RE::TESActivateEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESActivateEvent* a_event, RE::BSTEventSource<RE::TESActivateEvent>*) override
			{
				if (!a_event || !g_enabled || !a_event->objectActivated || !a_event->actionRef) {
					return RE::BSEventNotifyControl::kContinue;
				}
				if (a_event->actionRef.get() != RE::PlayerCharacter::GetSingleton()) {
					return RE::BSEventNotifyControl::kContinue;
				}
				auto* base = a_event->objectActivated->GetBaseObject();
				// Seulement les objets fixes qu'on active (autels, statues) : pas une amulette de Talos ramassee.
				if (!base || !(base->Is(RE::FormType::Activator) || base->Is(RE::FormType::Furniture))) {
					return RE::BSEventNotifyControl::kContinue;
				}
				const char* rawName = base->GetName();
				const std::string shrineName = rawName ? rawName : "";
				const std::string survivor = SurvivorForShrine(shrineName);
				if (survivor.empty()) {
					return RE::BSEventNotifyControl::kContinue;
				}

				const bool isNew = Unlock(survivor);
				logger::info("Autels : {} active (survivant {}{})", shrineName, survivor, isNew ? ", nouveau" : "");
				// Bassins de purification : Arkay retire les objets du vide, Stendarr les objets lunaires.
				const std::string lower = Lower(shrineName);
				const int         purify = lower.find("arkay") != std::string::npos ? 1 : (lower.find("stendarr") != std::string::npos ? 2 : 0);
				SKSE::GetTaskInterface()->AddTask([shrineName, survivor, isNew, purify]() {
					const auto names = ReadDisplayNames();
					if (Items::SleepToSwitch()) {
						// 0.61 : l'autel debloque seulement ; on change de survivant en dormant.
						if (isNew) {
							Show(std::format("{} t'accorde un nouveau survivant : {} ! (change de survivant en dormant)", shrineName,
								DisplayName(names, survivor)));
						}
						if (purify != 0) {
							Items::OfferPurify(purify == 1, shrineName);
						}
						return;
					}
					const std::string header = isNew ?
					                               std::format("{} t'accorde un nouveau survivant : {} !", shrineName, DisplayName(names, survivor)) :
					                               shrineName;
					OpenChoice(0, header);
				});
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		// ---- Sauvegarde dans le fichier de sauvegarde de SKSE (.skse, a cote de la sauvegarde Skyrim) ----
		void WriteString(SKSE::SerializationInterface* a_intfc, const std::string& a_text)
		{
			const auto length = static_cast<std::uint32_t>(a_text.size());
			a_intfc->WriteRecordData(&length, sizeof(length));
			if (length > 0) {
				a_intfc->WriteRecordData(a_text.data(), length);
			}
		}

		bool ReadString(SKSE::SerializationInterface* a_intfc, std::string& a_text)
		{
			std::uint32_t length = 0;
			if (a_intfc->ReadRecordData(&length, sizeof(length)) != sizeof(length) || length > 256) {
				return false;
			}
			a_text.assign(length, '\0');
			return length == 0 || a_intfc->ReadRecordData(a_text.data(), length) == length;
		}

		void OnSave(SKSE::SerializationInterface* a_intfc)
		{
			std::scoped_lock lock(g_lock);
			if (a_intfc->OpenRecord(kUnlockRecord, kRecordVersion)) {
				const auto count = static_cast<std::uint32_t>(g_unlocked.size());
				a_intfc->WriteRecordData(&count, sizeof(count));
				for (const auto& survivor : g_unlocked) {
					WriteString(a_intfc, survivor);
				}
			}
			if (a_intfc->OpenRecord(kCurrentRecord, kRecordVersion)) {
				WriteString(a_intfc, g_current);
			}
			Loadouts::Save(a_intfc);  // les variantes de competences de chaque survivant
			Challenges::Save(a_intfc);  // les dragons tues
			Combat::Save(a_intfc);      // notre part de la vie max du joueur
			Loot::Save(a_intfc);        // les contenants deja payes
			Altars::Save(a_intfc);      // les autels RoR2 poses dans les donjons
			Items::Save(a_intfc);       // table des objets RoR2, dernier lit, bazar
		}

		void OnRevert(SKSE::SerializationInterface*)
		{
			std::scoped_lock lock(g_lock);
			g_unlocked = { kDefaultSurvivor };
			g_current = kDefaultSurvivor;
			Loadouts::Revert();
			Challenges::Revert();
			Combat::Revert();
			Loot::Revert();
			Altars::Revert();
			Items::Revert();
		}

		void OnLoad(SKSE::SerializationInterface* a_intfc)
		{
			std::uint32_t type = 0, version = 0, length = 0;
			std::scoped_lock lock(g_lock);
			while (a_intfc->GetNextRecordInfo(type, version, length)) {
				if (type == kUnlockRecord) {
					std::uint32_t count = 0;
					if (a_intfc->ReadRecordData(&count, sizeof(count)) != sizeof(count) || count > 256) {
						continue;
					}
					std::vector<std::string> unlocked;
					for (std::uint32_t i = 0; i < count; ++i) {
						std::string survivor;
						if (!ReadString(a_intfc, survivor)) {
							break;
						}
						if (!survivor.empty() && std::ranges::find(unlocked, survivor) == unlocked.end()) {
							unlocked.push_back(survivor);
						}
					}
					if (std::ranges::find(unlocked, kDefaultSurvivor) == unlocked.end()) {
						unlocked.insert(unlocked.begin(), kDefaultSurvivor);
					}
					g_unlocked = std::move(unlocked);
				} else if (type == kCurrentRecord) {
					std::string current;
					if (ReadString(a_intfc, current) && !current.empty()) {
						g_current = current;
					}
				} else {
					if (!Loadouts::Load(a_intfc, type, version) &&  // les variantes de competences
						!Challenges::Load(a_intfc, type, version) &&  // les dragons tues
						!Loot::Load(a_intfc, type, version) &&        // les contenants deja payes
						!Altars::Load(a_intfc, type, version) &&      // les autels RoR2
						!Items::Load(a_intfc, type, version)) {       // les objets RoR2 dans l'inventaire
						Combat::Load(a_intfc, type, version);         // notre part de la vie max du joueur
					}
				}
			}
			logger::info("Autels : {} survivant(s) debloque(s) dans cette sauvegarde, survivant en cours {}", g_unlocked.size(), g_current);
		}
	}

	void RegisterSerialization()
	{
		auto* serialization = SKSE::GetSerializationInterface();
		serialization->SetUniqueID(kUniqueId);
		serialization->SetSaveCallback(OnSave);
		serialization->SetLoadCallback(OnLoad);
		serialization->SetRevertCallback(OnRevert);
	}

	void Install()
	{
		LoadSettings();
		static ActivateSink sink;
		if (auto* events = RE::ScriptEventSourceHolder::GetSingleton()) {
			events->AddEventSink<RE::TESActivateEvent>(&sink);
			Challenges::Install();
			Experience::Load();
			Terrain::LoadSettings();
			Decor::LoadSettings();
			logger::info("Autels : ecoute des activations installee");
		}
	}

	void OnGameLoaded()
	{
		LoadSettings();  // relit le .ini : pas besoin de redemarrer Skyrim
		Experience::Load();
		Combat::LoadSettings();
		Terrain::LoadSettings();
		Decor::LoadSettings();
		ThirdPerson::LoadSettings();
		if (!g_enabled) {
			return;
		}
		std::string current;
		{
			std::scoped_lock lock(g_lock);
			current = g_current;
		}
		Select(current);  // RoR2 reprend le survivant de cette sauvegarde

		// Partie deja avancee : les quetes daedriques deja terminees debloquent leur survivant tout de suite.
		const auto unlocked = CheckQuests();
		if (!unlocked.empty()) {
			const std::size_t count = unlocked.size();
			SKSE::GetTaskInterface()->AddTask([count]() {
				Show(std::format("{} survivant{} debloque{} par tes quetes daedriques deja terminees.", count, count > 1 ? "s" : "", count > 1 ? "s" : ""));
			});
		}
	}

	void PerFrame()
	{
		static auto nextCheck = std::chrono::steady_clock::now();
		const auto  now = std::chrono::steady_clock::now();
		if (!g_enabled || now < nextCheck) {
			return;
		}
		nextCheck = now + 2s;  // une quete ne se termine pas a chaque image : un controle toutes les 2 secondes suffit
		const auto unlocked = CheckQuests();
		if (unlocked.empty()) {
			return;
		}
		SKSE::GetTaskInterface()->AddTask([unlocked]() {
			const auto names = ReadDisplayNames();
			for (const auto& [quest, survivor] : unlocked) {
				Show(std::format("{} : nouveau survivant debloque, {} !", quest, DisplayName(names, survivor)));
			}
			Show(Items::SleepToSwitch() ? "Change de survivant en dormant." : "Change de survivant avec F8 / F7, ou a un autel des Divins.");
		});
	}

	std::string CurrentSurvivor()
	{
		std::scoped_lock lock(g_lock);
		return g_current;
	}

	bool Enabled() { return g_enabled; }

	bool KeysUseAllSurvivors() { return g_keysUseAll; }

	void Cycle(int a_step)
	{
		// 0.61 : on change de survivant en dormant (sauf avec la triche ToucheTousLesSurvivants).
		if (Items::SleepToSwitch() && !g_keysUseAll) {
			SKSE::GetTaskInterface()->AddTask([]() { Show("Change de survivant en dormant (dans un lit)"); });
			return;
		}
		// Triche (ToucheTousLesSurvivants) : tous les survivants du jeu (liste ecrite par RoR2), sinon les debloques.
		// Le choix passe toujours par Skyrim : variantes, skins, experience suivent ainsi le bon survivant.
		// (Toujours dans l'ordre de l'ecran de selection de RoR2, pas par ordre alphabetique ni de deblocage.)
		std::vector<std::string> all;
		if (g_keysUseAll) {
			all = ReadSurvivorOrder();
		}
		std::vector<std::string> unlocked;
		{
			std::scoped_lock lock(g_lock);
			unlocked = g_unlocked;
		}
		unlocked = InRoR2Order(std::move(unlocked));
		std::string next;
		{
			std::scoped_lock lock(g_lock);
			const auto& list = !all.empty() ? all : unlocked;
			if (list.empty()) {
				return;
			}
			const auto it = std::ranges::find(list, g_current);
			const auto count = static_cast<int>(list.size());
			int index = it != list.end() ? static_cast<int>(it - list.begin()) : 0;
			index = ((index + a_step) % count + count) % count;
			next = list[static_cast<std::size_t>(index)];
		}
		Select(next);
		SKSE::GetTaskInterface()->AddTask([next]() { Show("Survivant : " + DisplayName(ReadDisplayNames(), next)); });
	}

	void ShowMessageBox(const std::string& a_body, const std::vector<std::string>& a_buttons, std::function<void(std::uint32_t)> a_onChoice)
	{
		ShowBox(a_body, a_buttons, std::move(a_onChoice));
	}

	void OpenSurvivorChoice(const std::string& a_header)
	{
		std::size_t unlocked = 0;
		{
			std::scoped_lock lock(g_lock);
			unlocked = g_unlocked.size();
		}
		if (unlocked > 1) {
			OpenChoice(0, a_header);
		}
	}

	void FillCommand(bridge::SkyrimCommand& a_command)
	{
		std::scoped_lock lock(g_lock);
		strncpy_s(a_command.selectSurvivor, g_select.c_str(), _TRUNCATE);
		a_command.selectSequence = g_selectSequence;
	}
}
