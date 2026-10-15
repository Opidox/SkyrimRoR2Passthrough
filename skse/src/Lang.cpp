#include "Lang.h"

#include <cstring>
#include <fstream>
#include <mutex>
#include <unordered_map>

namespace skyrimror2::Lang
{
	namespace
	{
		std::mutex                                   g_lock;
		bool                                         g_loaded = false;
		std::string                                  g_code{ "english" };
		bool                                         g_plain = false;       // [Langue] SansAccents=1
		std::unordered_map<std::string, std::string> g_texts;              // langue choisie
		std::unordered_map<std::string, std::string> g_english;            // secours

		std::filesystem::path Folder() { return std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2_Lang"); }
		std::filesystem::path IniPath() { return std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini"); }

		std::string Lower(std::string a_text)
		{
			for (auto& c : a_text) {
				c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
			}
			return a_text;
		}

		std::string Trim(std::string_view a_text)
		{
			const auto first = a_text.find_first_not_of(" \t\r\n");
			if (first == std::string_view::npos) {
				return {};
			}
			const auto last = a_text.find_last_not_of(" \t\r\n");
			return std::string(a_text.substr(first, last - first + 1));
		}

		std::string Unescape(std::string_view a_text)
		{
			std::string out;
			out.reserve(a_text.size());
			for (std::size_t i = 0; i < a_text.size(); ++i) {
				if (a_text[i] == '\\' && i + 1 < a_text.size()) {
					const char next = a_text[i + 1];
					if (next == 'n') {
						out += '\n';
						++i;
						continue;
					}
					if (next == '\\') {
						out += '\\';
						++i;
						continue;
					}
				}
				out += a_text[i];
			}
			return out;
		}

		// Une ligne par texte :   cle = traduction   (# pour un commentaire)
		std::unordered_map<std::string, std::string> Read(const std::string& a_code)
		{
			std::unordered_map<std::string, std::string> texts;
			std::ifstream                                file(Folder() / (a_code + ".txt"), std::ios::binary);
			if (!file) {
				return texts;
			}
			std::string line;
			bool        first = true;
			while (std::getline(file, line)) {
				if (first && line.size() >= 3 && static_cast<unsigned char>(line[0]) == 0xEF) {
					line.erase(0, 3);  // marque UTF-8 au debut du fichier
				}
				first = false;
				if (!line.empty() && line.back() == '\r') {
					line.pop_back();
				}
				if (line.empty() || line[0] == '#') {
					continue;
				}
				const auto split = line.find(" = ");
				if (split == std::string::npos) {
					continue;
				}
				// Pas d'espaces enleves : certains textes commencent ou finissent par un espace (" (actif)").
				std::string key = Unescape(std::string_view(line).substr(0, split));
				std::string value = Unescape(std::string_view(line).substr(split + 3));
				if (!key.empty() && !value.empty()) {
					texts[std::move(key)] = std::move(value);
				}
			}
			return texts;
		}

		// Lettres accentuees (UTF-8) -> lettres simples, pour l'option SansAccents.
		std::string Plain(const std::string& a_text)
		{
			static const std::pair<const char*, const char*> kMap[] = {
				{ "\xC3\xA0", "a" }, { "\xC3\xA2", "a" }, { "\xC3\xA4", "a" }, { "\xC3\xA7", "c" }, { "\xC3\xA8", "e" },
				{ "\xC3\xA9", "e" }, { "\xC3\xAA", "e" }, { "\xC3\xAB", "e" }, { "\xC3\xAE", "i" }, { "\xC3\xAF", "i" },
				{ "\xC3\xB4", "o" }, { "\xC3\xB6", "o" }, { "\xC3\xB9", "u" }, { "\xC3\xBB", "u" }, { "\xC3\xBC", "u" },
				{ "\xC3\x80", "A" }, { "\xC3\x89", "E" }, { "\xC3\x88", "E" }, { "\xC3\x8A", "E" }, { "\xC3\x87", "C" },
				{ "\xC5\x93", "oe" }, { "\xC3\x9F", "ss" }, { "\xC3\xB1", "n" }, { "\xC3\xA1", "a" }, { "\xC3\xAD", "i" },
				{ "\xC3\xB3", "o" }, { "\xC3\xBA", "u" }, { "\xC3\x96", "O" }, { "\xC3\x9C", "U" }, { "\xC3\x84", "A" },
				{ "\xE2\x80\x99", "'" }, { "\xC2\xA0", " " }, { "\xE2\x80\xA6", "..." }, { "\xC2\xAB", "\"" }, { "\xC2\xBB", "\"" },
			};
			std::string out = a_text;
			for (const auto& [from, to] : kMap) {
				for (std::size_t at = out.find(from); at != std::string::npos; at = out.find(from, at)) {
					out.replace(at, std::strlen(from), to);
					at += std::strlen(to);
				}
			}
			return out;
		}

		std::string SkyrimLanguage(bool& a_found)
		{
			a_found = false;
			if (auto* collection = RE::INISettingCollection::GetSingleton()) {
				if (auto* setting = collection->GetSetting("sLanguage:General")) {
					if (const char* value = setting->GetString(); value && *value) {
						a_found = true;
						return Lower(value);
					}
				}
			}
			return "english";
		}

		// Pour RoR2 : il met les noms de ses objets et survivants dans la meme langue.
		void TellRoR2(const std::string& a_code)
		{
			wchar_t local[MAX_PATH]{};
			if (::GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH) == 0) {
				return;
			}
			std::error_code error;
			const auto      folder = std::filesystem::path(local) / L"SkyrimRoR2";
			std::filesystem::create_directories(folder, error);
			std::ofstream(folder / L"langue.txt", std::ios::binary | std::ios::trunc) << a_code << "\n";
		}

		void LoadLocked()
		{
			char text[64]{};
			::GetPrivateProfileStringA("Langue", "Langue", "auto", text, sizeof(text), IniPath().string().c_str());
			std::string wanted = Lower(Trim(text));
			g_plain = ::GetPrivateProfileIntA("Langue", "SansAccents", 0, IniPath().string().c_str()) != 0;
			bool found = true;
			if (wanted.empty() || wanted == "auto") {
				wanted = SkyrimLanguage(found);
			}
			if (!found) {
				logger::warn("Langue : sLanguage de Skyrim introuvable, anglais par defaut");
			}
			g_code = wanted;
			g_english = Read("english");
			g_texts = g_code == "english" ? g_english : Read(g_code);
			if (g_texts.empty() && g_code != "english") {
				logger::warn("Langue : pas de traduction pour {} ({} introuvable), textes en anglais", g_code, (Folder() / (g_code + ".txt")).string());
			}
			g_loaded = true;
			logger::info("Langue : {} ({} textes, {} en anglais){}", g_code, g_texts.size(), g_english.size(), g_plain ? ", sans accents" : "");
			TellRoR2(g_code);
		}

		void EnsureLoaded()
		{
			// Avant que Skyrim ait lu ses reglages, on relira la langue au prochain Init().
			if (!g_loaded) {
				LoadLocked();
			}
		}
	}

	void Init()
	{
		std::lock_guard lock(g_lock);
		LoadLocked();
	}

	std::string Code()
	{
		std::lock_guard lock(g_lock);
		EnsureLoaded();
		return g_code;
	}

	Script Writing()
	{
		const std::string code = Code();
		if (code == "russian" || code == "ukrainian") {
			return Script::kCyrillic;
		}
		if (code == "japanese") {
			return Script::kJapanese;
		}
		if (code == "chinese" || code.starts_with("zh") || code == "korean") {
			return Script::kChinese;
		}
		return Script::kLatin;
	}

	std::string T(std::string_view a_key)
	{
		std::lock_guard lock(g_lock);
		EnsureLoaded();
		const std::string key(a_key);
		if (auto it = g_texts.find(key); it != g_texts.end()) {
			return g_plain ? Plain(it->second) : it->second;
		}
		if (auto it = g_english.find(key); it != g_english.end()) {
			return g_plain ? Plain(it->second) : it->second;
		}
		return key;
	}

	std::vector<std::string> AllTexts()
	{
		std::lock_guard lock(g_lock);
		EnsureLoaded();
		std::vector<std::string> all;
		all.reserve(g_texts.size());
		for (const auto& [key, value] : g_texts) {
			all.push_back(value);
		}
		return all;
	}

	std::string Packed(std::string_view a_text)
	{
		if (a_text.empty()) {
			return {};
		}
		std::vector<std::string> parts;
		std::size_t              start = 0;
		while (true) {
			const auto bar = a_text.find('|', start);
			parts.emplace_back(a_text.substr(start, bar == std::string_view::npos ? std::string_view::npos : bar - start));
			if (bar == std::string_view::npos) {
				break;
			}
			start = bar + 1;
		}
		switch (parts.size()) {
		case 1:
			return T(parts[0]);
		case 2:
			return F(parts[0], parts[1]);
		case 3:
			return F(parts[0], parts[1], parts[2]);
		default:
			return F(parts[0], parts[1], parts[2], parts[3]);
		}
	}
}
