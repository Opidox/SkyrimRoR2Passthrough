#include "Challenges.h"

#include <map>

namespace skyrimror2::Challenges
{
	namespace
	{
		enum class Kind
		{
			kSkill,    // niveau d'une competence Skyrim
			kLevel,    // niveau du personnage
			kDragons,  // dragons tues
		};

		struct Challenge
		{
			Kind           kind = Kind::kSkill;
			RE::ActorValue skill = RE::ActorValue::kNone;
			float          level = 0.0f;
			std::string    text;  // tel qu'ecrit dans le .ini
		};

		// ---- Les dragons tues (compte par le plugin, enregistre dans la sauvegarde) ----
		// Les degats des competences RoR2 n'ont pas d'auteur dans Skyrim : on compte donc tout dragon
		// qui meurt a moins de 100 m du joueur (une seule fois par dragon).
		constexpr std::uint32_t kDragonRecord = 'DRGN';
		constexpr float         kDragonRange = 100.0f * 70.0f;  // en unites Skyrim
		std::mutex              g_dragonLock;
		std::vector<RE::FormID> g_dragons;

		bool IsDragon(RE::Actor* a_actor)
		{
			auto* race = a_actor ? a_actor->GetRace() : nullptr;
			return race && race->HasKeywordString("ActorTypeDragon");
		}

		class DeathSink final : public RE::BSTEventSink<RE::TESDeathEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESDeathEvent* a_event, RE::BSTEventSource<RE::TESDeathEvent>*) override
			{
				if (!a_event || !a_event->dead || !a_event->actorDying) {
					return RE::BSEventNotifyControl::kContinue;
				}
				auto* actor = a_event->actorDying->As<RE::Actor>();
				auto* player = RE::PlayerCharacter::GetSingleton();
				if (!player || !IsDragon(actor) || actor->GetPosition().GetDistance(player->GetPosition()) > kDragonRange) {
					return RE::BSEventNotifyControl::kContinue;
				}
				std::size_t count = 0;
				{
					std::scoped_lock lock(g_dragonLock);
					if (std::ranges::find(g_dragons, actor->GetFormID()) != g_dragons.end()) {
						return RE::BSEventNotifyControl::kContinue;
					}
					g_dragons.push_back(actor->GetFormID());
					count = g_dragons.size();
				}
				logger::info("Defis : dragon tue ({} au total)", count);
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		float DragonCount()
		{
			std::scoped_lock lock(g_dragonLock);
			return static_cast<float>(g_dragons.size());
		}

		bool                             g_allUnlocked = false;
		std::map<std::string, Challenge> g_challenges;  // "Huntress.2.1" (competence de 1 a 4)

		std::string Trim(std::string a_text)
		{
			const auto first = a_text.find_first_not_of(" \t\r\n");
			if (first == std::string::npos) {
				return {};
			}
			const auto last = a_text.find_last_not_of(" \t\r\n");
			return a_text.substr(first, last - first + 1);
		}

		std::string Lower(std::string a_text)
		{
			std::ranges::transform(a_text, a_text.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
			return a_text;
		}

		// En minuscules, sans accents (que le .ini soit en UTF-8 ou en Windows-1252), sans espaces ni tirets.
		std::string Simplify(const std::string& a_text)
		{
			std::string out;
			for (std::size_t i = 0; i < a_text.size(); ++i) {
				auto c = static_cast<unsigned char>(a_text[i]);
				if (c == 0xC3 && i + 1 < a_text.size()) {  // lettre accentuee en UTF-8 : C3 xx
					c = static_cast<unsigned char>(a_text[++i]) + 0x40;
				}
				if ((c >= 0xE0 && c <= 0xE5) || (c >= 0xC0 && c <= 0xC5)) {
					c = 'a';
				} else if (c == 0xE7 || c == 0xC7) {
					c = 'c';
				} else if ((c >= 0xE8 && c <= 0xEB) || (c >= 0xC8 && c <= 0xCB)) {
					c = 'e';
				} else if ((c >= 0xEC && c <= 0xEF) || (c >= 0xCC && c <= 0xCF)) {
					c = 'i';
				} else if ((c >= 0xF2 && c <= 0xF6) || (c >= 0xD2 && c <= 0xD6)) {
					c = 'o';
				} else if ((c >= 0xF9 && c <= 0xFC) || (c >= 0xD9 && c <= 0xDC)) {
					c = 'u';
				}
				if (c == ' ' || c == '-' || c == '_' || c == '\'') {
					continue;
				}
				out += static_cast<char>(std::tolower(c));
			}
			return out;
		}

		// Nom de competence (francais ou anglais) -> valeur de Skyrim. false si inconnu.
		bool ParseSkill(const std::string& a_name, RE::ActorValue& a_skill);

		// "Archerie", "Niveau", "Dragons" -> le type de defi. false si inconnu.
		bool ParseKind(const std::string& a_name, Challenge& a_challenge)
		{
			const std::string name = Simplify(a_name);
			if (name == "niveau" || name == "level") {
				a_challenge.kind = Kind::kLevel;
				return true;
			}
			if (name == "dragons" || name == "dragon" || name == "dragonstues") {
				a_challenge.kind = Kind::kDragons;
				return true;
			}
			a_challenge.kind = Kind::kSkill;
			return ParseSkill(a_name, a_challenge.skill);
		}

		bool ParseSkill(const std::string& a_name, RE::ActorValue& a_skill)
		{
			static const std::pair<const char*, RE::ActorValue> kSkills[] = {
				{ "archerie", RE::ActorValue::kArchery }, { "archery", RE::ActorValue::kArchery },
				{ "unemain", RE::ActorValue::kOneHanded }, { "onehanded", RE::ActorValue::kOneHanded },
				{ "deuxmains", RE::ActorValue::kTwoHanded }, { "twohanded", RE::ActorValue::kTwoHanded },
				{ "parade", RE::ActorValue::kBlock }, { "block", RE::ActorValue::kBlock },
				{ "forge", RE::ActorValue::kSmithing }, { "smithing", RE::ActorValue::kSmithing },
				{ "armurelourde", RE::ActorValue::kHeavyArmor }, { "heavyarmor", RE::ActorValue::kHeavyArmor },
				{ "armurelegere", RE::ActorValue::kLightArmor }, { "lightarmor", RE::ActorValue::kLightArmor },
				{ "voalatire", RE::ActorValue::kPickpocket }, { "volalatire", RE::ActorValue::kPickpocket }, { "pickpocket", RE::ActorValue::kPickpocket },
				{ "crochetage", RE::ActorValue::kLockpicking }, { "lockpicking", RE::ActorValue::kLockpicking },
				{ "discretion", RE::ActorValue::kSneak }, { "furtivite", RE::ActorValue::kSneak }, { "sneak", RE::ActorValue::kSneak },
				{ "alchimie", RE::ActorValue::kAlchemy }, { "alchemy", RE::ActorValue::kAlchemy },
				{ "eloquence", RE::ActorValue::kSpeech }, { "speech", RE::ActorValue::kSpeech },
				{ "alteration", RE::ActorValue::kAlteration },
				{ "invocation", RE::ActorValue::kConjuration }, { "conjuration", RE::ActorValue::kConjuration },
				{ "destruction", RE::ActorValue::kDestruction },
				{ "illusion", RE::ActorValue::kIllusion },
				{ "guerison", RE::ActorValue::kRestoration }, { "retablissement", RE::ActorValue::kRestoration }, { "restoration", RE::ActorValue::kRestoration },
				{ "enchantement", RE::ActorValue::kEnchanting }, { "enchanting", RE::ActorValue::kEnchanting },
			};
			const std::string name = Simplify(a_name);
			for (const auto& [key, skill] : kSkills) {
				if (name == key) {
					a_skill = skill;
					return true;
				}
			}
			return false;
		}

		std::filesystem::path IniPath()
		{
			return std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini");
		}
	}

	void Load()
	{
		const auto ini = IniPath().string();
		g_allUnlocked = ::GetPrivateProfileIntA("Variantes", "ToutDebloque", 0, ini.c_str()) != 0;
		// 0.70.1 : [Test] ModeTest = 1 -> toutes les variantes et tous les skins libres.
		if (::GetPrivateProfileIntA("Test", "ModeTest", 0, ini.c_str()) != 0) {
			g_allUnlocked = true;
			logger::info("Mode test : toutes les variantes et tous les skins libres");
		}

		std::map<std::string, Challenge> challenges;
		std::vector<char>                buffer(32768, '\0');
		const DWORD size = ::GetPrivateProfileSectionA("DefisVariantes", buffer.data(), static_cast<DWORD>(buffer.size()), ini.c_str());
		int         errors = 0;
		for (const char* line = buffer.data(); size > 0 && *line != '\0'; line += std::strlen(line) + 1) {
			std::string text = line;
			const auto  equal = text.find('=');
			if (text.empty() || text[0] == ';' || equal == std::string::npos) {
				continue;
			}
			const std::string key = Trim(text.substr(0, equal));
			std::string       value = Trim(text.substr(equal + 1));
			if (const auto comment = value.find(';'); comment != std::string::npos) {
				value = Trim(value.substr(0, comment));
			}
			// "Archerie 50" : le nombre a la fin, la competence avant.
			const auto space = value.find_last_of(' ');
			Challenge  challenge;
			if (space == std::string::npos || !ParseKind(value.substr(0, space), challenge)) {
				logger::warn("Defis : '{}' : competence inconnue dans '{}'", key, value);
				++errors;
				continue;
			}
			challenge.level = static_cast<float>(std::atof(value.c_str() + space + 1));
			challenge.text = value;
			challenges[Lower(key)] = std::move(challenge);
		}
		g_challenges = std::move(challenges);
		logger::info("Defis : {} defis de variantes ({} erreurs), tout debloque = {}", g_challenges.size(), errors,
			g_allUnlocked ? "oui" : "non");
	}

	namespace
	{
		// Le defi d'une case : "huntress.2.1" pour une variante, "huntress.skin.1" puis "*.skin.1" pour un skin.
		const Challenge* Find(const std::string& a_survivor, int a_slot, int a_variant)
		{
			std::string key = a_slot == 4 ? std::format("{}.skin.{}", a_survivor, a_variant) :
			                                 std::format("{}.{}.{}", a_survivor, a_slot + 1, a_variant);
			auto it = g_challenges.find(Lower(key));
			if (it == g_challenges.end() && a_slot == 4) {
				it = g_challenges.find(std::format("*.skin.{}", a_variant));
			}
			return it != g_challenges.end() ? &it->second : nullptr;
		}
	}

	bool IsUnlocked(const std::string& a_survivor, int a_slot, int a_variant)
	{
		if (a_variant <= 0 || g_allUnlocked) {
			return true;
		}
		const Challenge* challenge = Find(a_survivor, a_slot, a_variant);
		if (!challenge) {
			return true;  // pas de defi : libre
		}
		auto* player = RE::PlayerCharacter::GetSingleton();
		if (!player) {
			return false;
		}
		switch (challenge->kind) {
		case Kind::kLevel:
			return player->GetLevel() >= challenge->level;
		case Kind::kDragons:
			return DragonCount() >= challenge->level;
		default:
			{
				auto* values = player->AsActorValueOwner();
				return values && values->GetBaseActorValue(challenge->skill) >= challenge->level;
			}
		}
	}

	std::string Requirement(const std::string& a_survivor, int a_slot, int a_variant)
	{
		const Challenge* challenge = Find(a_survivor, a_slot, a_variant);
		if (!challenge) {
			return {};
		}
		if (challenge->kind == Kind::kDragons) {
			return std::format("{} ({} tues)", challenge->text, static_cast<int>(DragonCount()));
		}
		return challenge->text;
	}

	void Install()
	{
		static DeathSink sink;
		if (auto* events = RE::ScriptEventSourceHolder::GetSingleton()) {
			events->AddEventSink<RE::TESDeathEvent>(&sink);
		}
	}

	void Save(SKSE::SerializationInterface* a_intfc)
	{
		std::scoped_lock lock(g_dragonLock);
		if (!a_intfc->OpenRecord(kDragonRecord, 1)) {
			return;
		}
		const auto count = static_cast<std::uint32_t>(g_dragons.size());
		a_intfc->WriteRecordData(&count, sizeof(count));
		for (const auto id : g_dragons) {
			a_intfc->WriteRecordData(&id, sizeof(id));
		}
	}

	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t a_version)
	{
		if (a_type != kDragonRecord) {
			return false;
		}
		std::vector<RE::FormID> dragons;
		std::uint32_t           count = 0;
		if (a_version == 1 && a_intfc->ReadRecordData(&count, sizeof(count)) == sizeof(count) && count <= 100000) {
			for (std::uint32_t i = 0; i < count; ++i) {
				RE::FormID id = 0;
				if (a_intfc->ReadRecordData(&id, sizeof(id)) != sizeof(id)) {
					break;
				}
				RE::FormID resolved = 0;
				if (a_intfc->ResolveFormID(id, resolved)) {  // l'ordre des mods a pu changer
					dragons.push_back(resolved);
				}
			}
		}
		std::scoped_lock lock(g_dragonLock);
		g_dragons = std::move(dragons);
		logger::info("Defis : {} dragon(s) tue(s) dans cette sauvegarde", g_dragons.size());
		return true;
	}

	void Revert()
	{
		std::scoped_lock lock(g_dragonLock);
		g_dragons.clear();
	}
}
