#include "Config.h"

namespace skyrimror2
{
	namespace
	{
		struct Entry
		{
			Action      action;
			const char* iniName;
			const char* defaultKey;
		};

		// Nom dans le fichier .ini, et touche par defaut si le reglage manque.
		constexpr std::array<Entry, static_cast<std::size_t>(Action::kCount)> kEntries{ {
			{ Action::kForward, "Avancer", "Z" },
			{ Action::kBack, "Reculer", "S" },
			{ Action::kLeft, "Gauche", "Q" },
			{ Action::kRight, "Droite", "D" },
			{ Action::kJump, "Saut", "Espace" },
			{ Action::kSprint, "Sprint", "AltGauche" },
			{ Action::kSkill1, "Competence1", "ClicGauche" },
			{ Action::kSkill2, "Competence2", "ClicDroit" },
			{ Action::kSkill3, "Competence3", "MajGauche" },
			{ Action::kSkill4, "Competence4", "R" },
			{ Action::kNextSurvivor, "SurvivantSuivant", "F8" },
			{ Action::kPreviousSurvivor, "SurvivantPrecedent", "F7" },
			{ Action::kEquipment, "Equipement", "G" },
			{ Action::kUse, "Utiliser", "E" },
			{ Action::kItems, "ListeObjets", "O" },
			{ Action::kJournal, "JournalRoR2", "K" },
		} };

		std::string Normalize(std::string a_text)
		{
			// Enleve les espaces et passe en majuscules : "Ctrl Gauche" == "CTRLGAUCHE".
			std::string out;
			for (char c : a_text) {
				if (c != ' ' && c != '\t' && c != '\r' && c != '\n') {
					out += static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
				}
			}
			return out;
		}

		// Transforme un nom de touche ("Z", "Espace", "ClicGauche"...) en code Windows.
		// Renvoie -1 si le nom est inconnu, 0 pour "Aucune".
		int ParseKey(const std::string& a_name)
		{
			const std::string name = Normalize(a_name);
			if (name.empty() || name == "AUCUNE") {
				return 0;
			}

			static const std::unordered_map<std::string, int> kNamed{
				{ "ESPACE", VK_SPACE },
				{ "TAB", VK_TAB },
				{ "ENTREE", VK_RETURN },
				{ "RETOUR", VK_BACK },
				{ "CTRLGAUCHE", VK_LCONTROL },
				{ "CTRLDROIT", VK_RCONTROL },
				{ "MAJGAUCHE", VK_LSHIFT },
				{ "MAJDROIT", VK_RSHIFT },
				{ "ALTGAUCHE", VK_LMENU },
				{ "ALTDROIT", VK_RMENU },
				{ "CLICGAUCHE", VK_LBUTTON },
				{ "CLICDROIT", VK_RBUTTON },
				{ "CLICMILIEU", VK_MBUTTON },
				{ "SOURIS4", VK_XBUTTON1 },
				{ "SOURIS5", VK_XBUTTON2 },
			};
			if (auto it = kNamed.find(name); it != kNamed.end()) {
				return it->second;
			}

			// F1 a F12
			if (name.size() >= 2 && name.size() <= 3 && name[0] == 'F') {
				const int number = std::atoi(name.c_str() + 1);
				if (number >= 1 && number <= 12) {
					return VK_F1 + number - 1;
				}
			}

			// Une lettre ou un chiffre : on demande a Windows quelle touche de CE clavier le tape.
			if (name.size() == 1) {
				const char  c = static_cast<char>(std::tolower(static_cast<unsigned char>(name[0])));
				const SHORT result = ::VkKeyScanA(c);
				if (result != -1) {
					return LOBYTE(result);
				}
			}

			return -1;
		}

		// Lit un multiplicateur (1.0 par defaut, entre 0 et 100). Accepte 0,8 comme 0.8.
		// 0.71 : un nombre avec sa valeur par defaut et ses bornes.
		float ReadFloat(const std::string& a_ini, const char* a_section, const char* a_key, float a_default, float a_min, float a_max)
		{
			char text[32]{};
			::GetPrivateProfileStringA(a_section, a_key, "", text, static_cast<DWORD>(sizeof(text)), a_ini.c_str());
			for (char& c : text) {
				if (c == ',') {
					c = '.';
				}
			}
			if (text[0] == '\0') {
				return a_default;
			}
			const float value = static_cast<float>(std::atof(text));
			if (!(value >= a_min && value <= a_max)) {
				logger::warn("[{}] {} invalide : \"{}\", on garde {}", a_section, a_key, text, a_default);
				return a_default;
			}
			return value;
		}

		float ReadMultiplier(const std::string& a_ini, const char* a_section, const char* a_key)
		{
			char text[32]{};
			::GetPrivateProfileStringA(a_section, a_key, "1.0", text, static_cast<DWORD>(sizeof(text)), a_ini.c_str());
			for (char& c : text) {
				if (c == ',') {
					c = '.';
				}
			}
			const float value = static_cast<float>(std::atof(text));
			if (!(value > 0.0f && value <= 100.0f)) {
				logger::warn("[{}] {} invalide : \"{}\", on garde 1.0", a_section, a_key, text);
				return 1.0f;
			}
			return value;
		}

		std::filesystem::path IniPath()
		{
			// Skyrim tourne depuis son dossier d'installation ; MO2 y fait apparaitre le fichier du mod.
			return std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini");
		}
	}

	Config& Config::Get()
	{
		static Config config;
		return config;
	}

	Config::Config()
	{
		for (const auto& entry : kEntries) {
			keys_[static_cast<std::size_t>(entry.action)] = ParseKey(entry.defaultKey);
		}
	}

	void Config::Load()
	{
		const auto path = IniPath();
		const bool exists = std::filesystem::exists(path);
		if (!exists) {
			logger::warn("{} introuvable : touches par defaut", path.string());
		}

		for (const auto& entry : kEntries) {
			char buffer[64]{};
			::GetPrivateProfileStringA("Touches", entry.iniName, entry.defaultKey, buffer,
				static_cast<DWORD>(sizeof(buffer)), path.string().c_str());

			int key = ParseKey(buffer);
			if (key < 0) {
				logger::warn("Touche inconnue pour {} : \"{}\", on garde {}", entry.iniName, buffer, entry.defaultKey);
				key = ParseKey(entry.defaultKey);
			}
			keys_[static_cast<std::size_t>(entry.action)] = key;
			{
				std::scoped_lock lock(textLock_);
				keyNames_[static_cast<std::size_t>(entry.action)] = buffer;
			}
			logger::info("Touche {} = {} (code {})", entry.iniName, buffer, key);
		}

		const auto ini = path.string();
		followEnabled_ = ::GetPrivateProfileIntA("Mouvement", "Suivre", 1, ini.c_str()) != 0;
		speedMultiplier_ = ReadMultiplier(ini, "Mouvement", "Vitesse");
		sprintUsesStamina_ = ::GetPrivateProfileIntA("Mouvement", "EnduranceSprint", 1, ini.c_str()) != 0;
		sprintStaminaCost_ = ReadMultiplier(ini, "Mouvement", "CoutSprint");
		// 0.71 : "DegatsDonnes" remplace "Degats" (ennemis 2x plus resistants par defaut).
		damageMultiplier_ = ReadFloat(ini, "Combat", "DegatsDonnes", 0.5f, 0.01f, 100.0f);
		incomingDamageMultiplier_ = ReadMultiplier(ini, "Combat", "DegatsRecus");
		meleeDamage_ = ReadFloat(ini, "Combat", "DegatsCorpsACorps", 2.0f, 0.0f, 100.0f);
		magicDamage_ = ReadFloat(ini, "Combat", "DegatsMagie", 0.5f, 0.0f, 100.0f);
		overTimeDamage_ = ReadFloat(ini, "Combat", "DegatsDuree", 0.4f, 0.0f, 100.0f);
		hitCap_ = ReadFloat(ini, "Combat", "PlafondCoup", 0.4f, 0.0f, 1.0f);
		logger::info("Degats recus : corps a corps et fleches x{}, magie x{}, poison et duree x{}, plafond par coup {:.0f} %",
			meleeDamage_.load(), magicDamage_.load(), overTimeDamage_.load(), hitCap_.load() * 100.0f);
		blockSkyrimAttacks_ = ::GetPrivateProfileIntA("Combat", "BloquerAttaquesSkyrim", 1, ini.c_str()) != 0;
		logger::info("Suivre le survivant = {}, vitesse x{}, degats x{}, degats recus x{}, attaques Skyrim bloquees = {}",
			followEnabled_.load() ? "oui" : "non", speedMultiplier_.load(), damageMultiplier_.load(),
			incomingDamageMultiplier_.load(), blockSkyrimAttacks_.load() ? "oui" : "non");

		// [Lancement] : absent des anciens fichiers .ini, d'ou des valeurs par defaut qui marchent toutes seules.
		autoLaunch_ = ::GetPrivateProfileIntA("Lancement", "LancerRoR2", 1, ini.c_str()) != 0;
		autoRun_ = ::GetPrivateProfileIntA("Lancement", "PartieAuto", 1, ini.c_str()) != 0;
		windowWidth_ = static_cast<int>(::GetPrivateProfileIntA("Lancement", "FenetreLargeur", 960, ini.c_str()));
		windowHeight_ = static_cast<int>(::GetPrivateProfileIntA("Lancement", "FenetreHauteur", 540, ini.c_str()));

		hideRoR2Window_ = ::GetPrivateProfileIntA("Lancement", "CacherFenetreRoR2", 1, ini.c_str()) != 0;
		hudEnabled_ = ::GetPrivateProfileIntA("HUD", "Afficher", 1, ini.c_str()) != 0;
		hudScale_ = ReadMultiplier(ini, "HUD", "Taille");
		hideSkyrimHealth_ = ::GetPrivateProfileIntA("HUD", "CacherBarreVieSkyrim", 1, ini.c_str()) != 0;

		viewmodelEnabled_ = ::GetPrivateProfileIntA("Mains", "Afficher", 1, ini.c_str()) != 0;
		viewmodelScale_ = std::clamp(ReadMultiplier(ini, "Mains", "Taille"), 0.2f, 2.0f);
		{
			char text[32]{};
			::GetPrivateProfileStringA("Mains", "Descendre", "0", text, static_cast<DWORD>(sizeof(text)), ini.c_str());
			for (char& c : text) {
				if (c == ',') {
					c = '.';
				}
			}
			viewmodelOffset_ = std::clamp(static_cast<float>(std::atof(text)), -0.5f, 0.8f);
		}
		hideSkyrimArms_ = ::GetPrivateProfileIntA("Mains", "CacherBrasSkyrim", 1, ini.c_str()) != 0;

		char survivor[64]{};
		::GetPrivateProfileStringA("Lancement", "Survivant", "Commando", survivor, static_cast<DWORD>(sizeof(survivor)), ini.c_str());
		char profile[MAX_PATH]{};
		::GetPrivateProfileStringA("Lancement", "ProfilR2modman", "", profile, static_cast<DWORD>(sizeof(profile)), ini.c_str());
		{
			std::scoped_lock lock(textLock_);
			survivor_ = Normalize(survivor).empty() ? "Commando" : std::string(survivor);
			profileDir_ = profile;
		}
		logger::info("Lancer RoR2 = {}, partie auto = {}, survivant = {}, profil = \"{}\"",
			autoLaunch_.load() ? "oui" : "non", autoRun_.load() ? "oui" : "non", survivor, profile);
	}

	std::string Config::Survivor() const
	{
		std::scoped_lock lock(textLock_);
		return survivor_;
	}

	std::string Config::KeyName(Action a_action) const
	{
		std::scoped_lock lock(textLock_);
		return keyNames_[static_cast<std::size_t>(a_action)];
	}

	std::string Config::ProfileDir() const
	{
		std::scoped_lock lock(textLock_);
		return profileDir_;
	}

	int Config::Key(Action a_action) const
	{
		return keys_[static_cast<std::size_t>(a_action)].load();
	}
}
