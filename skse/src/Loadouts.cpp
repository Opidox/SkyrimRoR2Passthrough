#include "Loadouts.h"

#include "Challenges.h"
#include "Shrines.h"

#include <fstream>
#include <map>

namespace skyrimror2::Loadouts
{
	namespace
	{
		using Clock = std::chrono::steady_clock;

		constexpr const char*   kPluginFile = "SkyrimRoR2.esp";
		constexpr std::uint32_t kFirstPowerId = 0x800;   // voir tools/make_esp.py
		constexpr int           kSlots = 9;              // principale, secondaire, utilitaire, speciale, le skin, puis 4 cases en plus
		constexpr int           kSkillSlots = 4;         // les vraies competences (envoyees dans "variants")
		constexpr int           kSkinSlot = 4;
		constexpr int           kFirstExtraSlot = 5;     // balises du Capitaine, drones de l'Operateur... (envoyees dans "extraVariants")
		constexpr int           kMaxVariants = 5;
		constexpr std::uint32_t kLoadoutRecord = 'LDOT';
		constexpr std::uint32_t kRecordVersion = 3;  // 1 : sans le skin, 2 : sans les cases en plus

		constexpr const char* kSlotNames[kSlots] = { "1 Principale", "2 Secondaire", "3 Utilitaire", "4 Speciale", "Skin",
			"Case 5", "Case 6", "Case 7", "Case 8" };

		using Loadout = std::array<std::int32_t, kSlots>;  // -1 = variante de RoR2 par defaut

		Loadout EmptyLoadout()
		{
			Loadout loadout;
			loadout.fill(-1);
			return loadout;
		}

		// --- Partage avec le fil des touches (FillCommand) ---
		std::mutex                     g_lock;
		std::map<std::string, Loadout> g_loadouts;      // survivant -> variantes choisies
		std::string                    g_sentSurvivor;  // survivant dont on envoie les variantes
		std::uint32_t                  g_sequence = 0;
		std::array<std::array<bool, kMaxVariants>, kSlots> g_unlocked{};  // variantes debloquees du survivant envoye

		// --- Fil principal seulement ---
		RE::SpellItem* g_powers[kSlots][kMaxVariants]{};
		bool           g_powersSearched = false;
		bool           g_powersFound = false;

		// Noms des variantes de chaque survivant, ecrits par RoR2 :
		// %LOCALAPPDATA%\SkyrimRoR2\variantes.txt, une ligne "survivant|competence|variante|nom".
		std::map<std::string, std::array<std::vector<std::string>, kSlots>> g_names;
		std::map<std::string, std::array<std::string, kSlots>>              g_titles;  // titre des cases en plus ("Balise 1"...)
		std::filesystem::file_time_type g_namesTime{};
		Clock::time_point                g_nextNamesCheck{};

		std::string     g_shownSurvivor;   // survivant dont les pouvoirs sont donnes au joueur
		bool            g_powersDirty = true;
		bool            g_silentUnlocks = true;   // apres un chargement : pas d'annonce pour les defis deja remplis
		Clock::time_point g_nextUnlockCheck{};
		RE::TESForm*    g_handled = nullptr;      // dernier pouvoir des variantes deja pris en compte
		RE::TESForm*    g_previousPower = nullptr; // cri ou pouvoir equipe avant (rendu apres un choix)

		std::filesystem::path NamesPath()
		{
			char folder[MAX_PATH]{};
			if (::GetEnvironmentVariableA("LOCALAPPDATA", folder, MAX_PATH) == 0) {
				return {};
			}
			return std::filesystem::path(folder) / "SkyrimRoR2" / "variantes.txt";
		}

		void FindPowers()
		{
			g_powersSearched = true;
			auto* data = RE::TESDataHandler::GetSingleton();
			if (!data) {
				return;
			}
			int found = 0;
			for (int slot = 0; slot < kSlots; ++slot) {
				for (int variant = 0; variant < kMaxVariants; ++variant) {
					const auto id = kFirstPowerId + static_cast<std::uint32_t>(slot * kMaxVariants + variant);
					g_powers[slot][variant] = data->LookupForm<RE::SpellItem>(id, kPluginFile);
					found += g_powers[slot][variant] ? 1 : 0;
				}
			}
			g_powersFound = found == kSlots * kMaxVariants;
			if (g_powersFound) {
				logger::info("Variantes : {} pouvoirs trouves dans {}", found, kPluginFile);
			} else {
				logger::error("Variantes : {} introuvable ou incomplet ({} pouvoirs sur {}). Active-le dans MO2.",
					kPluginFile, found, kSlots * kMaxVariants);
			}
		}

		// Relit variantes.txt quand RoR2 l'a reecrit (controle toutes les 2 secondes).
		void ReloadNamesIfChanged()
		{
			const auto now = Clock::now();
			if (now < g_nextNamesCheck) {
				return;
			}
			g_nextNamesCheck = now + 2s;

			const auto      path = NamesPath();
			std::error_code error;
			const auto      time = std::filesystem::last_write_time(path, error);
			if (error || time == g_namesTime) {
				return;
			}
			g_namesTime = time;

			std::map<std::string, std::array<std::vector<std::string>, kSlots>> names;
			std::map<std::string, std::array<std::string, kSlots>>              titles;
			std::ifstream file(path);
			std::string   line;
			int           count = 0;
			while (std::getline(file, line)) {
				if (!line.empty() && line.back() == '\r') {
					line.pop_back();
				}
				std::array<std::string, 4> parts;
				std::size_t                start = 0;
				for (int i = 0; i < 4; ++i) {
					const auto bar = i < 3 ? line.find('|', start) : std::string::npos;
					parts[i] = line.substr(start, bar == std::string::npos ? std::string::npos : bar - start);
					if (bar == std::string::npos && i < 3) {
						parts[0].clear();  // ligne incomplete
						break;
					}
					start = bar + 1;
				}
				if (parts[0].empty()) {
					continue;
				}
				const int slot = std::atoi(parts[1].c_str());
				const int variant = std::atoi(parts[2].c_str());
				if (variant == -1 && slot >= kFirstExtraSlot && slot < kSlots) {
					titles[parts[0]][slot] = parts[3];  // titre de la case
					continue;
				}
				if (slot < 0 || slot >= kSlots || variant < 0 || variant >= kMaxVariants) {
					continue;
				}
				auto& list = names[parts[0]][slot];
				if (list.size() <= static_cast<std::size_t>(variant)) {
					list.resize(variant + 1);
				}
				list[variant] = parts[3];
				++count;
			}
			g_names = std::move(names);
			g_titles = std::move(titles);
			g_powersDirty = true;
			logger::info("Variantes : {} variantes lues pour {} survivants", count, g_names.size());
		}

		Loadout CurrentLoadout(const std::string& a_survivor)
		{
			std::scoped_lock lock(g_lock);
			const auto       it = g_loadouts.find(a_survivor);
			return it != g_loadouts.end() ? it->second : EmptyLoadout();
		}

		// Les variantes debloquees de ce survivant (defis Skyrim).
		std::array<std::array<bool, kMaxVariants>, kSlots> UnlockedFor(const std::string& a_survivor)
		{
			std::array<std::array<bool, kMaxVariants>, kSlots> unlocked{};
			for (int slot = 0; slot < kSlots; ++slot) {
				for (int variant = 0; variant < kMaxVariants; ++variant) {
					unlocked[slot][variant] = Challenges::IsUnlocked(a_survivor, slot, variant);
				}
			}
			return unlocked;
		}

		// Donne au joueur les pouvoirs des variantes du survivant en cours, avec leurs vrais noms.
		// Les variantes verrouillees sont aussi montrees, avec leur defi : on sait quoi viser.
		void RefreshPowers(RE::PlayerCharacter* a_player, const std::string& a_survivor)
		{
			const auto  it = g_names.find(a_survivor);
			const auto  loadout = CurrentLoadout(a_survivor);
			const auto  unlocked = UnlockedFor(a_survivor);
			int         given = 0;
			for (int slot = 0; slot < kSlots; ++slot) {
				const std::vector<std::string>* list = it != g_names.end() ? &it->second[slot] : nullptr;
				const bool hasChoice = list && list->size() > 1;  // une seule variante : rien a choisir
				for (int variant = 0; variant < kMaxVariants; ++variant) {
					auto* power = g_powers[slot][variant];
					const bool exists = hasChoice && variant < static_cast<int>(list->size()) && !(*list)[variant].empty();
					if (exists) {
						const bool active = unlocked[slot][variant] && (loadout[slot] == variant || (loadout[slot] < 0 && variant == 0));
						std::string state = active ? " (actif)" : "";
						if (!unlocked[slot][variant]) {
							state = std::format(" (verrouille : {})", Challenges::Requirement(a_survivor, slot, variant));
						}
						std::string slotName = kSlotNames[slot];
						if (const auto title = g_titles.find(a_survivor); slot >= kFirstExtraSlot && title != g_titles.end() && !title->second[slot].empty()) {
							slotName = title->second[slot];
						}
						const auto name = std::format("RoR2 {} - {}{}", slotName, (*list)[variant], state);  // "RoR2 Skin - ..." pour les skins
						power->SetFullName(name.c_str());
						if (!a_player->HasSpell(power)) {
							a_player->AddSpell(power);
						}
						++given;
					} else if (a_player->HasSpell(power)) {
						a_player->RemoveSpell(power);
					}
				}
			}
			g_shownSurvivor = a_survivor;
			g_powersDirty = false;
			{
				std::scoped_lock lock(g_lock);
				if (g_unlocked != unlocked && g_sentSurvivor == a_survivor) {
					++g_sequence;  // une variante debloquee ou reverrouillee : RoR2 recoit le loadout a jour
				}
				g_unlocked = unlocked;
			}
			logger::info("Variantes : {} pouvoirs donnes pour {}", given, a_survivor);
		}

		bool FindPower(const RE::TESForm* a_form, int& a_slot, int& a_variant)
		{
			for (int slot = 0; slot < kSlots; ++slot) {
				for (int variant = 0; variant < kMaxVariants; ++variant) {
					if (a_form && g_powers[slot][variant] == a_form) {
						a_slot = slot;
						a_variant = variant;
						return true;
					}
				}
			}
			return false;
		}

		// Remet le cri ou le pouvoir que le joueur avait avant de choisir une variante.
		void RestorePreviousPower(RE::PlayerCharacter* a_player)
		{
			auto* equip = RE::ActorEquipManager::GetSingleton();
			if (!equip || !g_previousPower) {
				return;
			}
			if (auto* shout = g_previousPower->As<RE::TESShout>()) {
				equip->EquipShout(a_player, shout);
			} else if (auto* spell = g_previousPower->As<RE::SpellItem>(); spell && a_player->HasSpell(spell)) {
				equip->EquipSpell(a_player, spell, spell->GetEquipSlot());
			}
		}

		// Le joueur vient de choisir un pouvoir de variante dans le menu.
		void Choose(RE::PlayerCharacter* a_player, int a_slot, int a_variant)
		{
			g_handled = g_powers[a_slot][a_variant];
			const std::string survivor = g_shownSurvivor;
			if (!Challenges::IsUnlocked(survivor, a_slot, a_variant)) {
				const auto requirement = Challenges::Requirement(survivor, a_slot, a_variant);
				logger::info("Variantes : {} competence {} variante {} verrouillee ({})", survivor, a_slot + 1, a_variant, requirement);
				RE::SendHUDMessage::ShowHUDMessage(std::format("Variante verrouillee : il faut {}", requirement).c_str());
				RestorePreviousPower(a_player);
				return;
			}
			{
				std::scoped_lock lock(g_lock);
				auto [it, added] = g_loadouts.try_emplace(survivor, EmptyLoadout());
				it->second[a_slot] = a_variant;
				++g_sequence;
			}
			std::string name = std::format("variante {}", a_variant + 1);
			if (const auto it = g_names.find(survivor); it != g_names.end() && a_variant < static_cast<int>(it->second[a_slot].size())) {
				name = it->second[a_slot][a_variant];
			}
			logger::info("Variantes : {} competence {} -> variante {} ({})", survivor, a_slot + 1, a_variant, name);
			std::string slotName = kSlotNames[a_slot];
			if (const auto title = g_titles.find(survivor); a_slot >= kFirstExtraSlot && title != g_titles.end() && !title->second[a_slot].empty()) {
				slotName = title->second[a_slot];
			}
			const auto message = a_slot == kSkinSlot       ? std::format("Skin : {}", name) :
			                     a_slot >= kFirstExtraSlot ? std::format("{} : {}", slotName, name) :
			                                                 std::format("Competence {} : {}", a_slot + 1, name);
			RE::SendHUDMessage::ShowHUDMessage(message.c_str());
			RefreshPowers(a_player, survivor);  // met a jour la mention "(actif)"
			RestorePreviousPower(a_player);
			// Le menu Magie deja ouvert garde les anciens noms : on lui demande de se redessiner.
			if (auto* queue = RE::UIMessageQueue::GetSingleton()) {
				queue->AddMessage(RE::MagicMenu::MENU_NAME, RE::UI_MESSAGE_TYPE::kUpdate, nullptr);
			}
		}

		// Surveille le pouvoir equipe : un pouvoir de variante = un choix.
		void WatchSelectedPower(RE::PlayerCharacter* a_player)
		{
			RE::TESForm* selected = a_player->GetActorRuntimeData().selectedPower;
			int          slot = 0;
			int          variant = 0;
			if (FindPower(selected, slot, variant)) {
				if (selected != g_handled) {
					g_handled = selected;
					Choose(a_player, slot, variant);
				}
			} else {
				g_previousPower = selected;
				g_handled = nullptr;
			}
		}

		// Un cri, ou un pouvoir (ce qui va dans l'emplacement "voix").
		bool IsShoutOrPower(RE::TESForm* a_form)
		{
			if (!a_form) {
				return false;
			}
			if (a_form->Is(RE::FormType::Shout)) {
				return true;
			}
			const auto* spell = a_form->As<RE::SpellItem>();
			if (!spell) {
				return false;
			}
			const auto type = spell->GetSpellType();
			return type == RE::MagicSystem::SpellType::kPower || type == RE::MagicSystem::SpellType::kLesserPower ||
			       type == RE::MagicSystem::SpellType::kVoicePower;
		}

		// Skyrim previent a chaque objet equipe, meme menu ouvert (le jeu est alors en pause et
		// PerFrame ne tourne pas). On peut donc choisir plusieurs variantes d'affilee sans fermer le menu.
		class EquipSink final : public RE::BSTEventSink<RE::TESEquipEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESEquipEvent* a_event, RE::BSTEventSource<RE::TESEquipEvent>*) override
			{
				auto* player = RE::PlayerCharacter::GetSingleton();
				if (!a_event || !a_event->equipped || !a_event->actor || a_event->actor.get() != player || !g_powersFound) {
					return RE::BSEventNotifyControl::kContinue;
				}
				auto* form = RE::TESForm::LookupByID(a_event->baseObject);
				int   slot = 0;
				int   variant = 0;
				if (FindPower(form, slot, variant)) {
					// Apres l'evenement (on ne change pas l'equipement en plein milieu de celui-ci).
					SKSE::GetTaskInterface()->AddTask([slot, variant]() {
						if (auto* player = RE::PlayerCharacter::GetSingleton()) {
							Choose(player, slot, variant);
						}
					});
				} else if (IsShoutOrPower(form)) {
					g_previousPower = form;  // le cri ou pouvoir a remettre apres un choix de variante
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};

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
	}

	void PerFrame()
	{
		if (!g_powersSearched) {
			Challenges::Load();
			FindPowers();
			static EquipSink sink;
			if (auto* events = RE::ScriptEventSourceHolder::GetSingleton()) {
				events->AddEventSink<RE::TESEquipEvent>(&sink);
			}
		}
		if (!g_powersFound) {
			return;
		}
		auto* player = RE::PlayerCharacter::GetSingleton();
		if (!player || !player->Is3DLoaded()) {
			return;
		}

		ReloadNamesIfChanged();

		const std::string survivor = Shrines::CurrentSurvivor();

		// Toutes les 2 secondes : un defi vient-il d'etre rempli (competence montee de niveau) ?
		if (const auto now = Clock::now(); now >= g_nextUnlockCheck && survivor == g_shownSurvivor && !g_powersDirty) {
			g_nextUnlockCheck = now + 2s;
			const auto unlocked = UnlockedFor(survivor);
			std::array<std::array<bool, kMaxVariants>, kSlots> before;
			{
				std::scoped_lock lock(g_lock);
				before = g_unlocked;
			}
			if (unlocked != before) {
				const auto names = g_names.find(survivor);
				for (int slot = 0; slot < kSlots && !g_silentUnlocks && names != g_names.end(); ++slot) {
					for (int variant = 0; variant < static_cast<int>(names->second[slot].size()) && variant < kMaxVariants; ++variant) {
						if (unlocked[slot][variant] && !before[slot][variant] && names->second[slot].size() > 1) {
							const auto message = slot == kSkinSlot ? std::format("Nouveau skin debloque : {}", names->second[slot][variant]) :
							                                         std::format("Nouvelle variante debloquee : {}", names->second[slot][variant]);
							RE::SendHUDMessage::ShowHUDMessage(message.c_str());
							logger::info("Variantes : {} competence {} variante {} debloquee", survivor, slot + 1, variant);
						}
					}
				}
				g_powersDirty = true;
			}
			g_silentUnlocks = false;
		}

		if (survivor != g_shownSurvivor || g_powersDirty) {
			RefreshPowers(player, survivor);
			std::scoped_lock lock(g_lock);
			if (g_sentSurvivor != survivor) {
				g_sentSurvivor = survivor;
				++g_sequence;  // nouveau survivant : RoR2 recoit son loadout
			}
		}

		WatchSelectedPower(player);
	}

	void FillCommand(bridge::SkyrimCommand& a_command)
	{
		std::scoped_lock lock(g_lock);
		strncpy_s(a_command.variantSurvivor, g_sentSurvivor.c_str(), _TRUNCATE);
		const auto it = g_loadouts.find(g_sentSurvivor);
		for (int slot = 0; slot < kSlots; ++slot) {
			// Jamais "ne pas toucher" : sinon RoR2 garderait le loadout de son profil, avec des variantes
			// pas encore debloquees dans cette partie Skyrim. Rien de choisi = la variante de base.
			std::int32_t variant = it != g_loadouts.end() ? it->second[slot] : 0;
			if (variant < 0 || variant >= kMaxVariants || !g_unlocked[slot][variant]) {
				variant = 0;  // rien de choisi, ou verrouillee maintenant (ToutDebloque retire...) : variante de base
			}
			if (slot == kSkinSlot) {
				a_command.skin = variant;
			} else if (slot >= kFirstExtraSlot) {
				a_command.extraVariants[slot - kFirstExtraSlot] = variant;
			} else {
				a_command.variants[slot] = variant;
			}
		}
		a_command.variantSequence = g_sequence;
	}

	void Save(SKSE::SerializationInterface* a_intfc)
	{
		std::scoped_lock lock(g_lock);
		if (!a_intfc->OpenRecord(kLoadoutRecord, kRecordVersion)) {
			return;
		}
		const auto count = static_cast<std::uint32_t>(g_loadouts.size());
		a_intfc->WriteRecordData(&count, sizeof(count));
		for (const auto& [survivor, loadout] : g_loadouts) {
			WriteString(a_intfc, survivor);
			a_intfc->WriteRecordData(loadout.data(), sizeof(loadout));
		}
	}

	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t a_version)
	{
		if (a_type != kLoadoutRecord) {
			return false;
		}
		std::map<std::string, Loadout> loadouts;
		std::uint32_t                  count = 0;
		// Version 1 : 4 valeurs (sans le skin). Version 2 : 5 valeurs. Version 3 : 9 valeurs.
		const std::uint32_t values = a_version == 1 ? kSkillSlots : a_version == 2 ? 5 : (a_version == kRecordVersion ? kSlots : 0);
		if (values > 0 && a_intfc->ReadRecordData(&count, sizeof(count)) == sizeof(count) && count <= 256) {
			for (std::uint32_t i = 0; i < count; ++i) {
				std::string survivor;
				Loadout     loadout{ -1, -1, -1, -1, -1 };
				const auto  bytes = values * sizeof(std::int32_t);
				if (!ReadString(a_intfc, survivor) || a_intfc->ReadRecordData(loadout.data(), bytes) != bytes) {
					break;
				}
				for (auto& variant : loadout) {
					variant = std::clamp(variant, -1, kMaxVariants - 1);
				}
				loadouts[survivor] = loadout;
			}
		}
		std::scoped_lock lock(g_lock);
		g_loadouts = std::move(loadouts);
		g_sentSurvivor.clear();  // renvoye a RoR2 a la prochaine image
		g_powersDirty = true;
		g_silentUnlocks = true;
		Challenges::Load();      // relit les defis : pas besoin de redemarrer Skyrim
		logger::info("Variantes : loadouts de {} survivant(s) dans cette sauvegarde", g_loadouts.size());
		return true;
	}

	void Revert()
	{
		std::scoped_lock lock(g_lock);
		g_loadouts.clear();
		g_sentSurvivor.clear();
		g_powersDirty = true;
		g_silentUnlocks = true;
		Challenges::Load();
	}
}
