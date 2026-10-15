#include "Experience.h"

#include "Bridge.h"
#include "Shrines.h"

#include <chrono>
#include <map>
#include <unordered_map>

namespace skyrimror2::Experience
{
	namespace
	{
		// Cases d'attaque envoyees par RoR2 avec chaque coup.
		constexpr int kSlotCount = 4;  // M1, M2, Maj, R
		constexpr int kSlotAllies = 4;  // drones, tourelles, balises, allies d'objets
		constexpr int kSlotOther = 5;   // objets, equipement, inconnu : comme M1

		struct Skills
		{
			std::array<std::vector<RE::ActorValue>, kSlotCount> dealt;  // par case, puis par variante
			RE::ActorValue taken = RE::ActorValue::kNone;               // coups recus
		};

		float                         g_multiplier = 1.0f;
		bool                          g_perksEnabled = true;
		RE::ActorValue                g_allies = RE::ActorValue::kConjuration;
		std::map<std::string, Skills> g_skills;  // survivant (en minuscules) -> competences

		struct DefaultLink
		{
			const char* survivor;
			const char* text;
		};

		// Element des attaques de Destruction (pour les atouts Feu / Foudre / Gel de Skyrim).
		enum class Element { kFire, kShock, kFrost };
		std::map<std::string, std::array<std::vector<Element>, kSlotCount>> g_elements;  // survivant -> case -> variante

		// Par defaut (section [ElementsDestruction] du .ini) : "M1, M2, Maj, R", variantes separees par "/".
		constexpr DefaultLink kDefaultElements[] = {
			{ "Commando", "Feu, Feu, Feu, Feu" },
			{ "Toolbot", "Feu, Feu, Feu, Feu" },
			{ "Engi", "Feu, Feu, Feu, Feu" },
			{ "Mage", "Feu/Foudre, Foudre/Gel, Gel, Feu/Foudre" },
			{ "Loader", "Foudre, Foudre, Foudre, Foudre" },
			{ "Captain", "Feu, Feu, Feu, Feu" },
			{ "VoidSurvivor", "Foudre, Foudre, Foudre, Foudre" },
			{ "FalseSon", "Foudre, Foudre, Foudre, Foudre" },
			{ "Chef", "Feu, Feu, Feu, Feu" },
			{ "Enforcer2", "Feu, Feu, Feu, Feu" },
			{ "Acolyte", "Feu, Feu, Feu, Feu" },
		};

		// Liens par defaut (si la section [ExperienceSurvivants] du .ini est vide).
		// "M1, M2, Maj, R, coups recus" ; "Archerie/Destruction" = variante 1 / variante 2.
		constexpr DefaultLink kDefaults[] = {
			{ "Commando", "Archerie, Archerie, Archerie, Archerie/Destruction, Armure legere" },
			{ "Huntress", "Archerie, Archerie, Archerie, Archerie, Armure legere" },
			{ "Bandit2", "Archerie, Une main, Discretion, Archerie, Armure legere" },
			{ "Toolbot", "Archerie/Archerie/Destruction/Deux mains, Destruction, Deux mains, Archerie, Armure lourde" },
			{ "Engi", "Destruction, Conjuration, Parade/Archerie, Conjuration, Armure lourde" },
			{ "Mage", "Destruction, Destruction, Destruction, Destruction, Armure legere" },
			{ "Merc", "Une main, Une main, Une main, Une main, Armure legere" },
			{ "Treebot", "Alchimie, Alchimie, Alteration, Guerison, Armure lourde" },
			{ "Loader", "Deux mains, Deux mains, Deux mains, Destruction/Deux mains, Armure lourde" },
			{ "Croco", "Une main, Alchimie, Alchimie, Alchimie, Armure lourde" },
			{ "Captain", "Archerie, Archerie, Destruction, Conjuration, Armure legere" },
			{ "Railgunner", "Archerie, Archerie, Archerie/Alteration, Archerie, Armure legere" },
			{ "VoidSurvivor", "Destruction, Destruction, Destruction, Destruction, Armure legere" },
			{ "Seeker", "Une main, Guerison, Guerison, Guerison, Armure legere" },
			{ "FalseSon", "Deux mains, Archerie, Deux mains, Destruction, Armure lourde" },
			{ "Chef", "Une main, Une main, Une main, Une main, Armure lourde" },
			{ "DroneTech", "Archerie, Conjuration, Conjuration, Conjuration, Armure legere" },
			{ "Drifter", "Deux mains, Deux mains, Deux mains, Deux mains, Armure legere" },
			{ "Enforcer2", "Une main/Destruction, Parade, Parade/Alteration, Armure lourde, Armure lourde" },
			{ "Acolyte", "Une main, Archerie/Conjuration, Archerie, Destruction, Armure legere" },
		};

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

		// Meme facon d'ecrire les competences que pour les defis (sans accents, sans espaces).
		std::string Simplify(const std::string& a_text)
		{
			std::string out;
			for (std::size_t i = 0; i < a_text.size(); ++i) {
				auto c = static_cast<unsigned char>(a_text[i]);
				if (c == 0xC3 && i + 1 < a_text.size()) {
					c = static_cast<unsigned char>(a_text[++i]) + 0x40;
				}
				if ((c >= 0xE8 && c <= 0xEB) || (c >= 0xC8 && c <= 0xCB)) {
					c = 'e';
				} else if ((c >= 0xE0 && c <= 0xE5) || (c >= 0xC0 && c <= 0xC5)) {
					c = 'a';
				}
				if (c == ' ' || c == '-' || c == '_' || c == '\'') {
					continue;
				}
				out += static_cast<char>(std::tolower(c));
			}
			return out;
		}

		RE::ActorValue ParseSkill(const std::string& a_name)
		{
			static const std::pair<const char*, RE::ActorValue> kSkills[] = {
				{ "archerie", RE::ActorValue::kArchery }, { "unemain", RE::ActorValue::kOneHanded },
				{ "deuxmains", RE::ActorValue::kTwoHanded }, { "parade", RE::ActorValue::kBlock },
				{ "forge", RE::ActorValue::kSmithing }, { "armurelourde", RE::ActorValue::kHeavyArmor },
				{ "armurelegere", RE::ActorValue::kLightArmor }, { "volalatire", RE::ActorValue::kPickpocket },
				{ "crochetage", RE::ActorValue::kLockpicking }, { "discretion", RE::ActorValue::kSneak },
				{ "alchimie", RE::ActorValue::kAlchemy }, { "eloquence", RE::ActorValue::kSpeech },
				{ "alteration", RE::ActorValue::kAlteration }, { "conjuration", RE::ActorValue::kConjuration },
				{ "invocation", RE::ActorValue::kConjuration }, { "destruction", RE::ActorValue::kDestruction },
				{ "illusion", RE::ActorValue::kIllusion }, { "guerison", RE::ActorValue::kRestoration },
				{ "retablissement", RE::ActorValue::kRestoration }, { "enchantement", RE::ActorValue::kEnchanting },
				{ "aucune", RE::ActorValue::kNone },
				// 0.74 : les noms anglais (le .ini est en anglais)
				{ "archery", RE::ActorValue::kArchery }, { "marksman", RE::ActorValue::kArchery },
				{ "onehanded", RE::ActorValue::kOneHanded }, { "twohanded", RE::ActorValue::kTwoHanded },
				{ "block", RE::ActorValue::kBlock }, { "smithing", RE::ActorValue::kSmithing },
				{ "heavyarmor", RE::ActorValue::kHeavyArmor }, { "lightarmor", RE::ActorValue::kLightArmor },
				{ "pickpocket", RE::ActorValue::kPickpocket }, { "lockpicking", RE::ActorValue::kLockpicking },
				{ "sneak", RE::ActorValue::kSneak }, { "alchemy", RE::ActorValue::kAlchemy },
				{ "speech", RE::ActorValue::kSpeech }, { "restoration", RE::ActorValue::kRestoration },
				{ "enchanting", RE::ActorValue::kEnchanting }, { "none", RE::ActorValue::kNone },
			};
			const std::string name = Simplify(a_name);
			for (const auto& [key, skill] : kSkills) {
				if (name == key) {
					return skill;
				}
			}
			logger::warn("Experience : competence inconnue '{}'", a_name);
			return RE::ActorValue::kNone;
		}

		// Le survivant joue : celui que RoR2 annonce (le plus sur), sinon celui que Skyrim croit jouer.
		std::string SurvivorName()
		{
			bridge::BridgeData data{};
			if (Bridge::Get().Read(data) && data.survivor[0] != '\0') {
				char name[sizeof(data.survivor) + 1]{};
				std::memcpy(name, data.survivor, sizeof(data.survivor));
				return name;
			}
			return Shrines::CurrentSurvivor();
		}

		Skills Current()
		{
			const auto it = g_skills.find(Lower(SurvivorName()));
			return it != g_skills.end() ? it->second : Skills{};
		}

		// La competence d'un coup : case (0 a 3, 4 allies, 5 autre) et variante de la case.
		RE::ActorValue Dealt(int a_slot, int a_variant)
		{
			if (a_slot == kSlotAllies) {
				return g_allies;
			}
			const Skills skills = Current();
			const int    slot = (a_slot >= 0 && a_slot < kSlotCount) ? a_slot : 0;
			const auto&  list = skills.dealt[slot];
			if (list.empty()) {
				return RE::ActorValue::kNone;
			}
			return list[static_cast<std::size_t>(std::clamp(a_variant, 0, static_cast<int>(list.size()) - 1))];
		}

		// "Archerie, Une main/Destruction, ..., Armure legere" -> competences. Ancien format a 2 valeurs
		// ("Archerie, Armure legere") : la meme competence pour toutes les cases.
		Skills ParseLink(const std::string& a_text)
		{
			std::vector<std::string> parts;
			std::size_t              start = 0;
			while (true) {
				const auto comma = a_text.find(',', start);
				parts.push_back(Trim(a_text.substr(start, comma == std::string::npos ? std::string::npos : comma - start)));
				if (comma == std::string::npos) {
					break;
				}
				start = comma + 1;
			}
			Skills skills;
			auto   variants = [](const std::string& a_part) {
                std::vector<RE::ActorValue> list;
                std::size_t                 from = 0;
                while (true) {
                    const auto slash = a_part.find('/', from);
                    list.push_back(ParseSkill(Trim(a_part.substr(from, slash == std::string::npos ? std::string::npos : slash - from))));
                    if (slash == std::string::npos) {
                        break;
                    }
                    from = slash + 1;
                }
                return list;
			};
			if (parts.size() <= 2) {
				const auto all = variants(parts[0]);
				for (auto& slot : skills.dealt) {
					slot = all;
				}
				skills.taken = parts.size() == 2 ? ParseSkill(parts[1]) : RE::ActorValue::kNone;
				return skills;
			}
			for (int slot = 0; slot < kSlotCount; ++slot) {
				skills.dealt[slot] = slot < static_cast<int>(parts.size()) - 1 ? variants(parts[slot]) : skills.dealt[0];
			}
			skills.taken = ParseSkill(parts.back());
			return skills;
		}

		Element ParseElement(const std::string& a_name)
		{
			const std::string name = Simplify(a_name);
			if (name == "foudre" || name == "choc" || name == "electricite" || name == "shock" || name == "lightning") {
				return Element::kShock;
			}
			if (name == "gel" || name == "glace" || name == "froid" || name == "frost" || name == "ice" || name == "cold") {
				return Element::kFrost;
			}
			return Element::kFire;
		}

		std::array<std::vector<Element>, kSlotCount> ParseElements(const std::string& a_text)
		{
			std::array<std::vector<Element>, kSlotCount> slots;
			std::size_t                                    start = 0;
			for (int slot = 0; slot < kSlotCount; ++slot) {
				const auto  comma = a_text.find(',', start);
				std::string part = Trim(a_text.substr(start, comma == std::string::npos ? std::string::npos : comma - start));
				std::size_t from = 0;
				while (!part.empty()) {
					const auto slash = part.find('/', from);
					slots[slot].push_back(ParseElement(Trim(part.substr(from, slash == std::string::npos ? std::string::npos : slash - from))));
					if (slash == std::string::npos) {
						break;
					}
					from = slash + 1;
				}
				if (slots[slot].empty()) {
					slots[slot] = slot > 0 ? slots[slot - 1] : std::vector<Element>{ Element::kFire };
				}
				if (comma == std::string::npos) {
					for (int rest = slot + 1; rest < kSlotCount; ++rest) {
						slots[rest] = slots[slot];
					}
					break;
				}
				start = comma + 1;
			}
			return slots;
		}

		Element CurrentElement(int a_slot, int a_variant)
		{
			const auto it = g_elements.find(Lower(SurvivorName()));
			if (it == g_elements.end()) {
				return Element::kFire;
			}
			const auto& list = it->second[(a_slot >= 0 && a_slot < kSlotCount) ? a_slot : 0];
			if (list.empty()) {
				return Element::kFire;
			}
			return list[static_cast<std::size_t>(std::clamp(a_variant, 0, static_cast<int>(list.size()) - 1))];
		}

		// 0.63 : code de competence des objets et de l'equipement, envoye par RoR2 (voir SkyrimRoR2/SkillSource.cs).
		bool g_itemSkills = true;  // [Experience] ObjetsSelonSource

		struct Resolved
		{
			RE::ActorValue skill = RE::ActorValue::kNone;
			Element        element = Element::kFire;
		};

		bool FromCode(int a_code, Resolved& a_out)
		{
			switch (a_code) {
			case 1: a_out.skill = RE::ActorValue::kArchery; return true;
			case 2: a_out.skill = RE::ActorValue::kOneHanded; return true;
			case 3: a_out.skill = RE::ActorValue::kTwoHanded; return true;
			case 4: a_out = { RE::ActorValue::kDestruction, Element::kFire }; return true;
			case 5: a_out = { RE::ActorValue::kDestruction, Element::kFrost }; return true;
			case 6: a_out = { RE::ActorValue::kDestruction, Element::kShock }; return true;
			case 7: a_out.skill = RE::ActorValue::kAlteration; return true;
			case 8: a_out.skill = RE::ActorValue::kConjuration; return true;
			case 9: a_out.skill = RE::ActorValue::kIllusion; return true;
			case 10: a_out.skill = RE::ActorValue::kRestoration; return true;
			case 11: a_out.skill = RE::ActorValue::kAlchemy; return true;
			case 12: a_out.skill = RE::ActorValue::kSneak; return true;
			case 13: a_out.skill = RE::ActorValue::kBlock; return true;
			case 14: a_out.skill = RE::ActorValue::kEnchanting; return true;
			case 15: a_out.skill = RE::ActorValue::kSpeech; return true;
			default: return false;
			}
		}

		// La competence (et l'element, pour la Destruction) d'un coup.
		Resolved Resolve(int a_slot, int a_variant, int a_code)
		{
			Resolved out;
			if (g_itemSkills && a_code != 0 && FromCode(a_code, out)) {
				return out;
			}
			out.skill = Dealt(a_slot, a_variant);
			if (out.skill == RE::ActorValue::kDestruction) {
				out.element = a_slot == kSlotAllies ? Element::kFire : CurrentElement(a_slot, a_variant);
			}
			return out;
		}

		void Give(RE::ActorValue a_skill, float a_damage)
		{
			auto* player = RE::PlayerCharacter::GetSingleton();
			if (!player || a_skill == RE::ActorValue::kNone || !(a_damage > 0.0f) || g_multiplier <= 0.0f) {
				return;
			}
			player->AddSkillExperience(a_skill, a_damage * g_multiplier);
		}
		// ---------------- 0.63 : effets des atouts de Destruction ----------------
		// Sorts de SkyrimRoR2.esp (voir tools/make_esp.py), lances par le joueur sur l'ennemi touche.
		constexpr RE::FormID kSpellBurn = 0xC10;      // brulure (sante, sur 3 s)
		constexpr RE::FormID kSpellSlow = 0xC11;      // ralenti de 50 % (3 s)
		constexpr RE::FormID kSpellFear = 0xC12;      // fuite (8 s)
		constexpr RE::FormID kSpellParalyze = 0xC13;  // paralysie (4 s)

		// Atouts de fin d'arbre de Destruction (Skyrim.esm).
		constexpr RE::FormID kIntenseFlames = 0x000F392E;
		constexpr RE::FormID kDeepFreeze = 0x000F3933;
		constexpr RE::FormID kDisintegrate = 0x000F3F0E;

		struct EffectSettings
		{
			bool  enabled = true;          // [EffetsDestruction] Effets
			bool  endPerks = true;         // AtoutsFinArbre
			float burn = 0.25f;            // Brulure : part du coup en feu sur 3 s
			float frost = 1.0f;            // Gel : vigueur perdue (x degats)
			float shock = 0.5f;            // Foudre : magie perdue (x degats)
			float fearBelow = 0.2f;        // SeuilFuite
			float paralyzeBelow = 0.2f;    // SeuilParalysie
			float disintegrateBelow = 0.15f;  // SeuilDesintegration
		};
		EffectSettings g_effects;

		using Clock = std::chrono::steady_clock;
		struct TargetState
		{
			float             burn = 0.0f;
			Clock::time_point lastBurn{};
			Clock::time_point lastSlow{};
			Clock::time_point lastFear{};
			Clock::time_point lastParalyze{};
		};
		std::unordered_map<RE::FormID, TargetState> g_targets;

		float IniFloat(const std::string& a_ini, const char* a_key, float a_default)
		{
			char text[32]{};
			::GetPrivateProfileStringA("EffetsDestruction", a_key, "", text, sizeof(text), a_ini.c_str());
			return text[0] != '\0' ? static_cast<float>(std::atof(text)) : a_default;
		}

		void LoadDestruction(const std::string& a_ini)
		{
			g_effects.enabled = ::GetPrivateProfileIntA("EffetsDestruction", "Effets", 1, a_ini.c_str()) != 0;
			g_effects.endPerks = ::GetPrivateProfileIntA("EffetsDestruction", "AtoutsFinArbre", 1, a_ini.c_str()) != 0;
			g_effects.burn = std::max(0.0f, IniFloat(a_ini, "Brulure", 0.25f));
			g_effects.frost = std::max(0.0f, IniFloat(a_ini, "Gel", 1.0f));
			g_effects.shock = std::max(0.0f, IniFloat(a_ini, "Foudre", 0.5f));
			g_effects.fearBelow = IniFloat(a_ini, "SeuilFuite", 0.2f);
			g_effects.paralyzeBelow = IniFloat(a_ini, "SeuilParalysie", 0.2f);
			g_effects.disintegrateBelow = IniFloat(a_ini, "SeuilDesintegration", 0.15f);
			g_targets.clear();
		}

		RE::SpellItem* BaseSpell(Element a_element)
		{
			constexpr RE::FormID kFlames = 0x00012FCD;     // feu
			constexpr RE::FormID kSparks = 0x0002DD2A;     // foudre
			constexpr RE::FormID kFrostbite = 0x0002B96B;  // gel
			const RE::FormID id = a_element == Element::kShock ? kSparks : (a_element == Element::kFrost ? kFrostbite : kFlames);
			return RE::TESForm::LookupByID<RE::SpellItem>(id);
		}

		bool HasPerk(RE::PlayerCharacter* a_player, RE::FormID a_id)
		{
			auto* perk = RE::TESForm::LookupByID<RE::BGSPerk>(a_id);
			return perk && a_player->HasPerk(perk);
		}

		RE::SpellItem* OurSpell(RE::FormID a_local)
		{
			static std::unordered_map<RE::FormID, RE::SpellItem*> cache;
			if (const auto it = cache.find(a_local); it != cache.end()) {
				return it->second;
			}
			RE::SpellItem* spell = nullptr;
			if (auto* data = RE::TESDataHandler::GetSingleton()) {
				spell = data->LookupForm<RE::SpellItem>(a_local, "SkyrimRoR2.esp");
			}
			if (!spell) {
				logger::warn("Destruction : sort {:03X} introuvable dans SkyrimRoR2.esp (fichier pas a jour ?)", a_local);
			}
			cache[a_local] = spell;
			return spell;
		}

		void Cast(RE::FormID a_local, RE::Actor* a_target, float a_magnitude)
		{
			auto* player = RE::PlayerCharacter::GetSingleton();
			auto* spell = OurSpell(a_local);
			auto* caster = player ? player->GetMagicCaster(RE::MagicSystem::CastingSource::kInstant) : nullptr;
			if (!spell || !caster) {
				return;
			}
			caster->CastSpellImmediate(spell, false, a_target, 1.0f, false, a_magnitude, player);
		}

		int g_notes = 0;
		void Note(const char* a_perk, RE::Actor* a_target)
		{
			if (++g_notes <= 20) {
				logger::info("Destruction : {} sur {:08X}", a_perk, a_target->GetFormID());
			}
		}
	}

	void Load()
	{
		const auto ini = std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini").string();
		char       text[32]{};
		::GetPrivateProfileStringA("Experience", "Multiplicateur", "1", text, sizeof(text), ini.c_str());
		g_multiplier = static_cast<float>(std::atof(text));
		g_perksEnabled = ::GetPrivateProfileIntA("Experience", "AtoutsSurDegats", 1, ini.c_str()) != 0;
		g_itemSkills = ::GetPrivateProfileIntA("Experience", "ObjetsSelonSource", 1, ini.c_str()) != 0;
		LoadDestruction(ini);

		::GetPrivateProfileStringA("Experience", "Allies", "Conjuration", text, sizeof(text), ini.c_str());
		g_allies = ParseSkill(Trim(text));

		std::map<std::string, Skills> skills;
		for (const auto& link : kDefaults) {
			skills[Lower(link.survivor)] = ParseLink(link.text);
		}
		std::vector<char> buffer(8192, '\0');
		const DWORD size = ::GetPrivateProfileSectionA("ExperienceSurvivants", buffer.data(), static_cast<DWORD>(buffer.size()), ini.c_str());
		for (const char* line = buffer.data(); size > 0 && *line != '\0'; line += std::strlen(line) + 1) {
			std::string entry = line;
			const auto  equal = entry.find('=');
			if (entry.empty() || entry[0] == ';' || equal == std::string::npos) {
				continue;
			}
			std::string value = Trim(entry.substr(equal + 1));
			if (const auto comment = value.find(';'); comment != std::string::npos) {
				value = Trim(value.substr(0, comment));
			}
			skills[Lower(Trim(entry.substr(0, equal)))] = ParseLink(value);
		}
		g_skills = std::move(skills);

		std::map<std::string, std::array<std::vector<Element>, kSlotCount>> elements;
		for (const auto& link : kDefaultElements) {
			elements[Lower(link.survivor)] = ParseElements(link.text);
		}
		std::fill(buffer.begin(), buffer.end(), '\0');
		const DWORD elementSize = ::GetPrivateProfileSectionA("ElementsDestruction", buffer.data(), static_cast<DWORD>(buffer.size()), ini.c_str());
		for (const char* line = buffer.data(); elementSize > 0 && *line != '\0'; line += std::strlen(line) + 1) {
			std::string entry = line;
			const auto  equal = entry.find('=');
			if (entry.empty() || entry[0] == ';' || equal == std::string::npos) {
				continue;
			}
			std::string value = Trim(entry.substr(equal + 1));
			if (const auto comment = value.find(';'); comment != std::string::npos) {
				value = Trim(value.substr(0, comment));
			}
			elements[Lower(Trim(entry.substr(0, equal)))] = ParseElements(value);
		}
		g_elements = std::move(elements);
		logger::info("Experience : x{} pour {} survivants, atouts sur les degats = {}", g_multiplier, g_skills.size(), g_perksEnabled ? "oui" : "non");
	}

	float PerkMultiplier(RE::Actor* a_target, int a_slot, int a_variant, int a_code)
	{
		auto* player = RE::PlayerCharacter::GetSingleton();
		if (!player || !g_perksEnabled) {
			return 1.0f;
		}
		// Armes et sort de base du jeu (Skyrim.esm), utilises seulement pour le calcul.
		constexpr RE::FormID kHuntingBow = 0x00013985;
		constexpr RE::FormID kIronSword = 0x00012EB7;
		constexpr RE::FormID kIronGreatsword = 0x0001359D;

		float          value = 100.0f;
		const Resolved hit = Resolve(a_slot, a_variant, a_code);
		switch (hit.skill) {
		case RE::ActorValue::kArchery:
		case RE::ActorValue::kOneHanded:
		case RE::ActorValue::kTwoHanded:
			{
				const auto skill = hit.skill;
				const RE::FormID id = skill == RE::ActorValue::kArchery ? kHuntingBow : (skill == RE::ActorValue::kOneHanded ? kIronSword : kIronGreatsword);
				auto* weapon = RE::TESForm::LookupByID<RE::TESObjectWEAP>(id);
				if (!weapon) {
					return 1.0f;
				}
				RE::BGSEntryPoint::HandleEntryPoint(RE::BGSEntryPoint::ENTRY_POINT::kModAttackDamage, player, weapon, a_target, std::addressof(value));
				break;
			}
		case RE::ActorValue::kDestruction:
			{
				// Le sort de base de l'element : les atouts "Feu / Foudre / Gel augmentes" ne comptent que pour lui.
				auto* spell = BaseSpell(hit.element);
				if (!spell) {
					return 1.0f;
				}
				RE::BGSEntryPoint::HandleEntryPoint(RE::BGSEntryPoint::ENTRY_POINT::kModSpellMagnitude, player, spell, a_target, std::addressof(value));
				break;
			}
		default:
			return 1.0f;
		}
		return std::clamp(value / 100.0f, 0.1f, 10.0f);
	}

	void OnDamageDealt(float a_damage, int a_slot, int a_variant, int a_code)
	{
		Give(Resolve(a_slot, a_variant, a_code).skill, a_damage);
	}

	void OnSkillUse(int a_code, float a_amount)
	{
		Resolved hit;
		if (g_itemSkills && FromCode(a_code, hit)) {
			Give(hit.skill, a_amount);
		}
	}

	void OnDamageTaken(float a_damage)
	{
		Give(Current().taken, a_damage);
	}

	bool ApplyDestruction(RE::Actor* a_target, float a_damage, int a_slot, int a_variant, int a_code)
	{
		auto* player = RE::PlayerCharacter::GetSingleton();
		if (!player || !a_target || !g_effects.enabled || a_target->IsDead()) {
			return false;
		}
		const Resolved hit = Resolve(a_slot, a_variant, a_code);
		if (hit.skill != RE::ActorValue::kDestruction) {
			return false;
		}
		// Seulement sur les ennemis : un sort hostile sur un passant serait une agression (prime, garde...).
		if (!a_target->IsHostileToActor(player) && !a_target->IsInCombat()) {
			return false;
		}
		auto* values = a_target->AsActorValueOwner();
		if (!values) {
			return false;
		}
		const float maxHealth = std::max(values->GetPermanentActorValue(RE::ActorValue::kHealth), 1.0f);
		const float health = values->GetActorValue(RE::ActorValue::kHealth);
		const float fraction = health / maxHealth;
		if (g_targets.size() > 512) {
			g_targets.clear();
		}
		auto&      state = g_targets[a_target->GetFormID()];
		const auto now = Clock::now();

		switch (hit.element) {
		case Element::kFire:
			{
				// Brulure : une part du coup en degats de feu sur 3 secondes (au plus un nouveau feu par seconde).
				state.burn += a_damage * g_effects.burn;
				if (state.burn > 0.0f && now - state.lastBurn >= std::chrono::seconds(1)) {
					Cast(kSpellBurn, a_target, state.burn / 3.0f);
					state.burn = 0.0f;
					state.lastBurn = now;
				}
				// Flammes intenses : un ennemi presque mort s'enfuit.
				if (g_effects.endPerks && fraction < g_effects.fearBelow && HasPerk(player, kIntenseFlames) &&
					now - state.lastFear >= std::chrono::seconds(10)) {
					Cast(kSpellFear, a_target, 0.0f);
					state.lastFear = now;
					Note("Flammes intenses", a_target);
				}
				break;
			}
		case Element::kFrost:
			{
				// Gel : vigueur perdue (comme le Givre de Skyrim) et ralentissement.
				values->DamageActorValue(RE::ActorValue::kStamina, a_damage * g_effects.frost);
				if (now - state.lastSlow >= std::chrono::seconds(1)) {
					Cast(kSpellSlow, a_target, 0.0f);
					state.lastSlow = now;
				}
				// Gel profond : un ennemi presque mort est paralyse.
				if (g_effects.endPerks && fraction < g_effects.paralyzeBelow && HasPerk(player, kDeepFreeze) &&
					now - state.lastParalyze >= std::chrono::seconds(8)) {
					Cast(kSpellParalyze, a_target, 0.0f);
					state.lastParalyze = now;
					Note("Gel profond", a_target);
				}
				break;
			}
		case Element::kShock:
			{
				// Foudre : magie perdue (la moitie du coup, comme les Etincelles de Skyrim).
				values->DamageActorValue(RE::ActorValue::kMagicka, a_damage * g_effects.shock);
				// Desintegration : un ennemi presque mort est reduit en cendres.
				if (g_effects.endPerks && fraction < g_effects.disintegrateBelow && HasPerk(player, kDisintegrate) && !a_target->IsEssential()) {
					a_target->KillImpl(player, health + 1.0f, true, false);
					Note("Desintegration", a_target);
					return true;
				}
				break;
			}
		}
		return false;
	}
}
