#include "Launcher.h"
#include "Lang.h"

#include "Config.h"

#include <TlHelp32.h>
#include <shellapi.h>

#include <fstream>

// Demarre RoR2 depuis Skyrim, exactement comme le bouton "Start modded" de r2modman :
// on demande a Steam de lancer RoR2 (n° 632360) en lui disant de charger BepInEx
// depuis le profil r2modman. Le mod SkyrimRoR2 de ce profil fait ensuite le reste
// (passer les menus et lancer une partie avec le bon survivant).

namespace skyrimror2::Launcher
{
	namespace
	{
		constexpr const wchar_t* kRoR2Process = L"Risk of Rain 2.exe";
		constexpr const wchar_t* kRoR2SteamId = L"632360";

		// Message dans le coin de l'ecran (depuis le fil principal du jeu, seul autorise a afficher).
		void Notify(const char* a_key)
		{
			SKSE::GetTaskInterface()->AddTask([text = Lang::T(a_key)]() { RE::SendHUDMessage::ShowHUDMessage(text.c_str()); });
		}

		std::wstring Widen(const std::string& a_text)
		{
			if (a_text.empty()) {
				return {};
			}
			const int size = ::MultiByteToWideChar(CP_ACP, 0, a_text.c_str(), -1, nullptr, 0);
			std::wstring out(static_cast<std::size_t>(size), L'\0');
			::MultiByteToWideChar(CP_ACP, 0, a_text.c_str(), -1, out.data(), size);
			out.resize(static_cast<std::size_t>(size) - 1);
			return out;
		}

		// Numero du processus "Risk of Rain 2.exe" (0 s'il ne tourne pas).
		DWORD RoR2ProcessId()
		{
			HANDLE snapshot = ::CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
			if (snapshot == INVALID_HANDLE_VALUE) {
				return 0;
			}
			PROCESSENTRY32W entry{};
			entry.dwSize = sizeof(entry);
			DWORD pid = 0;
			for (BOOL ok = ::Process32FirstW(snapshot, &entry); ok; ok = ::Process32NextW(snapshot, &entry)) {
				if (::_wcsicmp(entry.szExeFile, kRoR2Process) == 0) {
					pid = entry.th32ProcessID;
					break;
				}
			}
			::CloseHandle(snapshot);
			return pid;
		}


		// Vrai si "Risk of Rain 2.exe" tourne deja.
		bool RoR2Running()
		{
			HANDLE snapshot = ::CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
			if (snapshot == INVALID_HANDLE_VALUE) {
				return false;
			}
			PROCESSENTRY32W entry{};
			entry.dwSize = sizeof(entry);
			bool found = false;
			for (BOOL ok = ::Process32FirstW(snapshot, &entry); ok; ok = ::Process32NextW(snapshot, &entry)) {
				if (::_wcsicmp(entry.szExeFile, kRoR2Process) == 0) {
					found = true;
					break;
				}
			}
			::CloseHandle(snapshot);
			return found;
		}

		// Ou est installe Steam ? (Steam l'ecrit lui-meme dans le registre de Windows.)
		std::wstring FindSteam()
		{
			wchar_t value[MAX_PATH]{};
			DWORD   size = sizeof(value);
			if (::RegGetValueW(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamExe", RRF_RT_REG_SZ, nullptr, value, &size) == ERROR_SUCCESS) {
				return value;
			}
			size = sizeof(value);
			if (::RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Valve\\Steam", L"InstallPath", RRF_RT_REG_SZ, nullptr, value, &size) == ERROR_SUCCESS) {
				return std::wstring(value) + L"\\steam.exe";
			}
			return {};
		}

		// Le profil r2modman : celui du .ini, sinon %APPDATA%\r2modmanPlus-local\RiskOfRain2\profiles\Passthrough
		std::filesystem::path ProfileDir()
		{
			const std::string fromIni = Config::Get().ProfileDir();
			if (!fromIni.empty()) {
				return std::filesystem::path(Widen(fromIni));
			}
			wchar_t appData[MAX_PATH]{};
			if (::GetEnvironmentVariableW(L"APPDATA", appData, MAX_PATH) == 0) {
				return {};
			}
			return std::filesystem::path(appData) / L"r2modmanPlus-local" / L"RiskOfRain2" / L"profiles" / L"Passthrough";
		}

		// ---- Taille de la fenetre de RoR2 ----
		// Unity retient la taille de sa fenetre dans le registre de Windows (pas dans le profil r2modman) : apres
		// une partie avec le mod, le RoR2 normal se relancait en vignette minuscule. Avant de lancer RoR2, on note
		// les reglages d'ecran normaux du joueur ; une petite tache en arriere-plan attend la fermeture de RoR2
		// et les remet.
		constexpr const wchar_t* kRoR2Key = L"Software\\Hopoo Games, LLC\\Risk of Rain 2";

		std::filesystem::path ScreenRestoreScript()
		{
			wchar_t folder[MAX_PATH]{};
			if (::GetEnvironmentVariableW(L"LOCALAPPDATA", folder, MAX_PATH) == 0) {
				return {};
			}
			return std::filesystem::path(folder) / L"SkyrimRoR2" / L"ecran_ror2.ps1";
		}

		// Ecrit le script qui remet les reglages d'ecran actuels, s'ils sont "normaux" (pas ceux du mod).
		// (Unity range ces nombres en DWORD ou en QWORD selon les versions : on accepte les deux.)
		// Le nombre de reglages "Screen Manager..." d'une cle du registre (0 si elle n'existe pas).
		int CountScreenValues(const std::wstring& a_path)
		{
			HKEY key = nullptr;
			if (::RegOpenKeyExW(HKEY_CURRENT_USER, a_path.c_str(), 0, KEY_READ, &key) != ERROR_SUCCESS) {
				return 0;
			}
			int count = 0;
			for (DWORD i = 0; i < 4096; ++i) {
				wchar_t name[512]{};
				DWORD   nameSize = 512;
				const LONG result = ::RegEnumValueW(key, i, name, &nameSize, nullptr, nullptr, nullptr, nullptr);
				if (result == ERROR_NO_MORE_ITEMS) {
					break;
				}
				if (result == ERROR_SUCCESS && std::wstring(name, nameSize).rfind(L"Screen Manager", 0) == 0) {
					++count;
				}
			}
			::RegCloseKey(key);
			return count;
		}

		// Ou Unity range les reglages de RoR2 : HKCU\Software\<editeur>\Risk of Rain 2. Le nom de l'editeur
		// n'est pas toujours celui qu'on croit : on cherche parmi tous les editeurs celui qui a ces reglages.
		std::wstring FindRoR2Key()
		{
			if (CountScreenValues(kRoR2Key) > 0) {
				return kRoR2Key;
			}
			HKEY software = nullptr;
			if (::RegOpenKeyExW(HKEY_CURRENT_USER, L"Software", 0, KEY_READ, &software) != ERROR_SUCCESS) {
				return {};
			}
			std::wstring found;
			for (DWORD i = 0; i < 4096 && found.empty(); ++i) {
				wchar_t company[256]{};
				DWORD   size = 256;
				if (::RegEnumKeyExW(software, i, company, &size, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS) {
					break;
				}
				for (const wchar_t* product : { L"Risk of Rain 2", L"RiskOfRain2", L"Risk of Rain 2 (Modded)" }) {
					const std::wstring path = std::wstring(L"Software\\") + company + L"\\" + product;
					if (CountScreenValues(path) > 0) {
						found = path;
						break;
					}
				}
			}
			::RegCloseKey(software);
			return found;
		}

		std::wstring g_ror2Key;  // cle trouvee (pour le script de remise en place)

		void SaveScreenSettings()
		{
			// Le script est refait a chaque lancement (il ne doit pas grossir a chaque partie).
			if (const auto script = ScreenRestoreScript(); !script.empty()) {
				std::error_code removeError;
				std::filesystem::remove(script, removeError);
			}
			g_ror2Key = FindRoR2Key();
			if (g_ror2Key.empty()) {
				logger::info("Ecran de RoR2 : rien dans le registre (normal : RoR2 garde sa taille d'ecran dans config.cfg)");
				return;
			}
			HKEY key = nullptr;
			const LONG opened = ::RegOpenKeyExW(HKEY_CURRENT_USER, g_ror2Key.c_str(), 0, KEY_READ, &key);
			if (opened != ERROR_SUCCESS) {
				logger::info("Ecran de RoR2 : cle du registre introuvable (code {})", opened);
				return;
			}
			struct Value
			{
				std::wstring  name;
				DWORD         type;
				std::uint64_t data;
			};
			std::vector<Value> values;
			std::uint64_t      width = 0;
			int                seen = 0;
			for (DWORD i = 0; i < 4096; ++i) {
				wchar_t       name[512]{};
				DWORD         nameSize = 512;
				DWORD         type = 0;
				std::uint64_t data = 0;
				DWORD         dataSize = sizeof(data);
				const LONG result = ::RegEnumValueW(key, i, name, &nameSize, nullptr, &type, reinterpret_cast<LPBYTE>(&data), &dataSize);
				if (result == ERROR_NO_MORE_ITEMS) {
					break;
				}
				const std::wstring text(name, nameSize);
				if (text.rfind(L"Screen Manager", 0) != 0) {
					continue;
				}
				++seen;
				if (result != ERROR_SUCCESS || (type != REG_DWORD && type != REG_QWORD)) {
					continue;
				}
				if (type == REG_DWORD) {
					data &= 0xFFFFFFFFull;
				}
				if (text.find(L"Resolution Width") != std::wstring::npos) {
					width = data;
				}
				values.push_back({ text, type, data });
			}
			::RegCloseKey(key);
			if (values.empty() || width < 800) {
				logger::info("Ecran de RoR2 : reglages actuels non gardes ({} valeurs \"Screen Manager\", {} lisibles, largeur {}) ; on garde ceux notes avant",
					seen, values.size(), width);
				return;
			}
			const auto      path = ScreenRestoreScript();
			std::error_code error;
			std::filesystem::create_directories(path.parent_path(), error);
			std::wofstream file(path, std::ios::trunc);
			file << L"# Ecrit par SkyrimRoR2 : remet la taille d'ecran normale de RoR2 apres une partie avec le mod.\n";
			file << L"$key = 'HKCU:\\" << g_ror2Key << L"'\n";
			for (const auto& value : values) {
				file << L"New-ItemProperty -Path $key -Name '" << value.name << L"' -Value " << value.data
				     << (value.type == REG_QWORD ? L" -PropertyType QWord" : L" -PropertyType DWord") << L" -Force | Out-Null\n";
			}
			logger::info("Ecran de RoR2 : reglages normaux notes ({} valeurs, largeur {})", values.size(), width);
		}

		// Attend que RoR2 soit lance (2 minutes au plus), puis confie a PowerShell, cache, l'attente de sa fermeture
		// et la remise des reglages d'ecran.
		// Le script qui remet les reglages d'ecran dans le fichier de reglages de RoR2 (config.cfg), d'apres
		// ecran_ror2.txt (ecrit par le mod RoR2 au lancement : chemin du fichier, puis les lignes a remettre).
		void WriteConfigRestoreScript(const std::filesystem::path& a_script)
		{
			std::error_code error;
			std::filesystem::create_directories(a_script.parent_path(), error);
			std::ofstream file(a_script, std::ios::app);
			file << "\n# Reglages d'ecran de RoR2 dans son fichier config.cfg\n";
			file << "$info = Join-Path $env:LOCALAPPDATA 'SkyrimRoR2\\ecran_ror2.txt'\n";
			file << "if (Test-Path $info) {\n";
			file << "  $lines = @(Get-Content $info)\n";
			file << "  if ($lines.Count -ge 2 -and (Test-Path $lines[0])) {\n";
			file << "    $content = @(Get-Content $lines[0])\n";
			file << "    foreach ($l in $lines[1..($lines.Count - 1)]) {\n";
			file << "      $name = ($l -split ' ')[0]\n";
			file << "      $content = @($content | Where-Object { -not $_.Trim().StartsWith($name + ' ') })\n";
			file << "      $content += $l\n";
			file << "    }\n";
			file << "    Set-Content -Path $lines[0] -Value $content\n";
			file << "  }\n";
			file << "}\n";
		}

		void RestoreScreenWhenRoR2Closes()
		{
			const auto script = ScreenRestoreScript();
			std::error_code error;
			if (script.empty()) {
				return;
			}
			// Toujours un script : au moins la partie "config.cfg" (la partie registre n'existe que si on l'a notee).
			if (!std::filesystem::exists(script, error)) {
				std::ofstream(script) << "# Ecrit par SkyrimRoR2 : remet la taille d'ecran normale de RoR2 apres une partie avec le mod.\n";
			}
			WriteConfigRestoreScript(script);
			DWORD pid = 0;
			for (int i = 0; i < 120 && pid == 0; ++i) {
				std::this_thread::sleep_for(std::chrono::seconds(1));
				pid = RoR2ProcessId();
			}
			if (pid == 0) {
				return;
			}
			std::wstring command = std::format(
				L"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Wait-Process -Id {}; Start-Sleep -Seconds 2; & '{}'\"",
				pid, script.wstring());
			STARTUPINFOW        startup{};
			PROCESS_INFORMATION process{};
			startup.cb = sizeof(startup);
			if (::CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) {
				::CloseHandle(process.hThread);
				::CloseHandle(process.hProcess);
				logger::info("Ecran de RoR2 : sera remis a la normale a la fermeture de RoR2");
			} else {
				logger::warn("Ecran de RoR2 : tache de remise en place impossible (code {})", ::GetLastError());
			}
		}

		void Launch()
		{
			if (RoR2Running()) {
				logger::info("RoR2 tourne deja : pas besoin de le lancer");
				return;
			}

			const auto profile = ProfileDir();
			const auto preloader = profile / L"BepInEx" / L"core" / L"BepInEx.Preloader.dll";
			if (!std::filesystem::exists(preloader)) {
				logger::error("Profil r2modman introuvable ({}) : RoR2 non lance. Regle [Lancement] ProfilR2modman dans SkyrimRoR2.ini",
					preloader.string());
				Notify("SkyrimRoR2 : profil r2modman introuvable, lance RoR2 a la main");
				return;
			}

			const std::wstring steam = FindSteam();
			if (steam.empty() || !std::filesystem::exists(steam)) {
				logger::error("Steam introuvable : RoR2 non lance");
				Notify("SkyrimRoR2 : Steam introuvable, lance RoR2 a la main");
				return;
			}

			// Les deux ecritures (ancienne et nouvelle) des reglages de BepInEx : RoR2 ignore celle qu'il ne connait pas.
			SaveScreenSettings();
			const std::wstring target = L"\"" + preloader.wstring() + L"\"";
			std::wstring args = std::wstring(L"-applaunch ") + kRoR2SteamId +
			                    L" --doorstop-enable true --doorstop-target " + target +
			                    L" --doorstop-enabled true --doorstop-target-assembly " + target;

			const int width = Config::Get().WindowWidth();
			const int height = Config::Get().WindowHeight();
			if (width > 0 && height > 0) {
				args += std::format(L" -screen-fullscreen 0 -screen-width {} -screen-height {}", width, height);
			}

			const auto result = reinterpret_cast<std::intptr_t>(::ShellExecuteW(nullptr, L"open", steam.c_str(), args.c_str(), nullptr, SW_SHOWMINNOACTIVE));
			if (result <= 32) {
				logger::error("Le lancement de RoR2 a echoue (code {})", result);
				Notify("SkyrimRoR2 : impossible de lancer RoR2");
				return;
			}
			logger::info("RoR2 lance via Steam avec le profil {}", profile.string());
			Notify("SkyrimRoR2 : lancement de RoR2...");
			RestoreScreenWhenRoR2Closes();
		}
	}

	void StartRoR2IfNeeded()
	{
		if (!Config::Get().AutoLaunch()) {
			logger::info("Lancement automatique de RoR2 desactive");
			return;
		}
		std::thread([]() {
			try {
				Launch();
			} catch (const std::exception& e) {
				logger::error("Erreur pendant le lancement de RoR2 : {}", e.what());
			}
		}).detach();
	}

	// "Cacher" la fenetre RoR2 = la reduire a une vignette minuscule dans le coin de l'ecran, tout au fond
	// (derriere Skyrim), et l'enlever de la barre des taches. Si on la masquait vraiment ou qu'on
	// l'envoyait hors de l'ecran, Windows et Unity arretaient de la dessiner, et certains survivants
	// (Bandit, Vagabonde...) devenaient invisibles dans l'image des mains.
	void UpdateRoR2Window()
	{
		constexpr int kTinyWidth = 160;
		constexpr int kTinyHeight = 90;
		const bool    hide = Config::Get().HideRoR2Window();
		const DWORD   pid = RoR2ProcessId();

		// 0.74 : RoR2 tourne derriere Skyrim. Windows 11 ralentit les programmes en arriere-plan (mode "efficacite"),
		// surtout sur un PC portable : RoR2 mettait plus de 90 s a lancer sa partie. On le lui interdit, et on lui
		// donne une priorite un peu plus haute (une fois par processus).
		static DWORD boosted = 0;
		if (pid != 0 && pid != boosted) {
			boosted = pid;
			if (HANDLE process = ::OpenProcess(PROCESS_SET_INFORMATION, FALSE, pid)) {
				// SetProcessInformation et sa structure, decrits ici (absents des en-tetes avec nos reglages de compilation).
				struct PowerThrottling
				{
					ULONG version = 1;        // PROCESS_POWER_THROTTLING_CURRENT_VERSION
					ULONG controlMask = 0x1;  // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
					ULONG stateMask = 0;      // jamais ralenti
				} state;
				constexpr int kProcessPowerThrottling = 4;
				using SetInfo = BOOL(WINAPI*)(HANDLE, int, LPVOID, DWORD);
				const auto setInfo = reinterpret_cast<SetInfo>(::GetProcAddress(::GetModuleHandleW(L"kernel32.dll"), "SetProcessInformation"));
				const bool throttleOff = setInfo && setInfo(process, kProcessPowerThrottling, &state, sizeof(state)) != FALSE;
				const bool priority = ::SetPriorityClass(process, ABOVE_NORMAL_PRIORITY_CLASS) != FALSE;
				::CloseHandle(process);
				logger::info("RoR2 en arriere-plan : ralentissement de Windows {}, priorite haute {}", throttleOff ? "coupe" : "non coupe",
					priority ? "oui" : "non");
			} else {
				logger::warn("RoR2 en arriere-plan : processus inaccessible (erreur {})", ::GetLastError());
			}
		}

		struct Search
		{
			DWORD             pid;
			std::vector<HWND> windows;
		} search{ pid, {} };

		if (pid != 0) {
			::EnumWindows([](HWND a_window, LPARAM a_param) -> BOOL {
				auto* search = reinterpret_cast<Search*>(a_param);
				DWORD owner = 0;
				::GetWindowThreadProcessId(a_window, &owner);
				if (owner == search->pid && ::GetWindow(a_window, GW_OWNER) == nullptr) {
					wchar_t className[64]{};
					::GetClassNameW(a_window, className, 64);
					if (std::wstring_view(className) == L"UnityWndClass") {  // la fenetre du jeu
						search->windows.push_back(a_window);
					}
				}
				return TRUE;
			}, reinterpret_cast<LPARAM>(&search));
		}

		for (HWND window : search.windows) {
			RECT rect{};
			::GetWindowRect(window, &rect);
			const bool tiny = (rect.right - rect.left) <= kTinyWidth + 40 && rect.left > -10000;
			const LONG exStyle = ::GetWindowLongW(window, GWL_EXSTYLE);

			if (hide) {
				if (!::IsWindowVisible(window)) {
					::ShowWindow(window, SW_SHOWNOACTIVATE);  // ancienne version : elle avait ete masquee
				}
				if (!tiny) {
					::SetWindowLongW(window, GWL_EXSTYLE, (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
					::SetWindowPos(window, HWND_BOTTOM, 0, 0, kTinyWidth, kTinyHeight, SWP_NOACTIVATE | SWP_FRAMECHANGED);
					logger::info("Fenetre RoR2 reduite en vignette derriere Skyrim");
				}
			} else if (tiny || rect.left <= -10000 || !::IsWindowVisible(window)) {
				const int width = Config::Get().WindowWidth() > 0 ? Config::Get().WindowWidth() : 960;
				const int height = Config::Get().WindowHeight() > 0 ? Config::Get().WindowHeight() : 540;
				::SetWindowLongW(window, GWL_EXSTYLE, exStyle & ~WS_EX_TOOLWINDOW);
				::ShowWindow(window, SW_SHOWNOACTIVATE);
				::SetWindowPos(window, nullptr, 100, 100, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
				logger::info("Fenetre RoR2 remise a l'ecran");
			}
		}
	}
}
