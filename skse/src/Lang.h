#pragma once

// 0.74 : les textes du mod dans la langue de Skyrim.
//
// Chaque texte du code est ecrit en francais sans accents ("Deja vendu") : c'est sa CLE. Les fichiers
// Data\SKSE\Plugins\SkyrimRoR2_Lang\<langue>.txt (english.txt, french.txt, german.txt...) donnent sa traduction,
// une ligne par texte :   Deja vendu = Already sold
// Un texte absent de la langue choisie est pris en anglais, sinon la cle elle-meme est affichee.
// La langue est celle de Skyrim (sLanguage), sauf si [Langue] Langue= du .ini en impose une autre.

#include <format>
#include <string>
#include <string_view>
#include <vector>

namespace skyrimror2::Lang
{
	enum class Script
	{
		kLatin,
		kCyrillic,
		kJapanese,
		kChinese
	};

	// Lit la langue et les traductions (appele au demarrage ; relu sans danger).
	void Init();

	// "english", "french", "german"...
	std::string Code();
	Script      Writing();

	// Le texte traduit.
	std::string T(std::string_view a_key);

	// Tous les textes de la langue choisie (pour preparer les lettres de la police du HUD).
	std::vector<std::string> AllTexts();

	// Un texte venu de RoR2 : "cle|valeur|valeur" -> la cle traduite, ses {} remplis par les valeurs.
	std::string Packed(std::string_view a_text);

	// Le texte traduit, avec ses {} remplis. Si une traduction a des {} incorrects, on retombe sur la cle.
	template <class... Args>
	std::string F(std::string_view a_key, const Args&... a_args)
	{
		const std::string text = T(a_key);
		try {
			return std::vformat(text, std::make_format_args(a_args...));
		} catch (...) {
		}
		try {
			return std::vformat(a_key, std::make_format_args(a_args...));
		} catch (...) {
		}
		return std::string(a_key);
	}
}
