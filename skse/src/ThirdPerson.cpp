#include "ThirdPerson.h"

#include <atomic>
#include <chrono>
#include <cmath>
#include <mutex>

namespace skyrimror2::ThirdPerson
{
	namespace
	{
		using Clock = std::chrono::steady_clock;

		bool  g_enabled = true;    // [TroisiemePersonne] Activer
		bool  g_crosshair = true;  // Viseur
		float g_brightness = 1.0f; // Luminosite
		float g_minLight = 0.25f;  // LumiereMin : jamais plus sombre (donjons tres noirs)
		float g_interiorBoost = 2.0f;  // LumiereInterieurs : les interieurs sont surtout eclaires par des torches

		bool g_firstPerson = true;
		bool g_wanted = false;

		std::atomic<std::int64_t> g_filmShownAt{ 0 };  // horloge (ms) de la derniere image dessinee

		// Ce qu'on envoie a RoR2 (ecrit par le fil principal, lu par le fil des touches).
		std::mutex g_lock;
		struct Shared
		{
			bool  thirdPerson = false;
			float yaw = 0.0f;
			float pitch = 0.0f;
			float ambient[3]{ 0.5f, 0.5f, 0.5f };
			float sun[3]{ 0.8f, 0.8f, 0.8f };
			float sunDir[3]{ 0.3f, -0.4f, 0.85f };
			bool  interior = false;
			std::uint32_t placeId = 0;  // 0.70 (coop) : cellule (interieur) ou monde (exterieur)
		} g_shared;

		// La direction de la camera : 1re colonne de sa rotation (camera de Gamebryo) ou 1re ligne. On verifie
		// une fois en premiere personne, en comparant au regard du joueur, laquelle est la bonne.
		int g_axis = 0;     // 0 : colonne, 1 : ligne
		int g_votes[2]{};
		bool g_axisChosen = false;

		std::int64_t NowMs()
		{
			return std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count();
		}

		float AngleGap(float a_a, float a_b)
		{
			float d = a_a - a_b;
			while (d > 3.14159265f) d -= 6.2831853f;
			while (d < -3.14159265f) d += 6.2831853f;
			return std::abs(d);
		}

		RE::NiPoint3 Forward(const RE::NiCamera* a_camera, int a_axis)
		{
			const auto& m = a_camera->world.rotate.entry;
			return a_axis == 0 ? RE::NiPoint3(m[0][0], m[1][0], m[2][0]) : RE::NiPoint3(m[0][0], m[0][1], m[0][2]);
		}

		void YawPitch(const RE::NiPoint3& a_forward, float& a_yaw, float& a_pitch)
		{
			const float length = std::sqrt(a_forward.x * a_forward.x + a_forward.y * a_forward.y + a_forward.z * a_forward.z);
			if (length < 0.001f) {
				return;
			}
			a_yaw = std::atan2(a_forward.x, a_forward.y);  // 0 = nord (+y), sens des aiguilles d'une montre
			a_pitch = -std::asin(std::clamp(a_forward.z / length, -1.0f, 1.0f));
		}

		bool MenuOpen()
		{
			auto* ui = RE::UI::GetSingleton();
			if (!ui) {
				return false;
			}
			if (ui->GameIsPaused()) {
				return true;
			}
			// Tous les menus comptent, meme ceux qui ne mettent pas le jeu en pause (favoris, dialogue...).
			for (std::string_view menu : { RE::TweenMenu::MENU_NAME, RE::FavoritesMenu::MENU_NAME, RE::DialogueMenu::MENU_NAME,
					 RE::InventoryMenu::MENU_NAME, RE::MagicMenu::MENU_NAME, RE::StatsMenu::MENU_NAME, RE::MapMenu::MENU_NAME,
					 RE::JournalMenu::MENU_NAME, RE::Console::MENU_NAME, RE::ContainerMenu::MENU_NAME, RE::BarterMenu::MENU_NAME,
					 RE::LockpickingMenu::MENU_NAME, RE::BookMenu::MENU_NAME, RE::CraftingMenu::MENU_NAME, RE::LoadingMenu::MENU_NAME,
					 RE::SleepWaitMenu::MENU_NAME }) {
				if (ui->IsMenuOpen(menu)) {
					return true;
				}
			}
			return false;
		}

		void UpdateCameraState()
		{
			auto*                    camera = RE::PlayerCamera::GetSingleton();
			static bool              stableFirstPerson = false;
			static Clock::time_point menuSeen{};
			static Clock::time_point firstPersonSeen{};
			const auto               now = Clock::now();
			if (MenuOpen()) {
				menuSeen = now;
			}
			const bool rawFirstPerson = camera && camera->IsInFirstPerson();
			// La camera doit etre VRAIMENT a la 3e personne (pas un etat de passage : menu Tab, transition, vue
			// d'attente...) pendant 0,4 s d'affilee.
			const auto stateId = camera && camera->currentState ? camera->currentState->id : RE::CameraState::kFirstPerson;
			const bool settledThird = stateId == RE::CameraState::kThirdPerson || stateId == RE::CameraState::kMount ||
			                          stateId == RE::CameraState::kFurniture || stateId == RE::CameraState::kBleedout ||
			                          stateId == RE::CameraState::kDragon;
			if (rawFirstPerson || !settledThird) {
				firstPersonSeen = now;
			}
			if (now - menuSeen <= std::chrono::seconds(3)) {
				g_firstPerson = stableFirstPerson || rawFirstPerson;
			} else {
				g_firstPerson = rawFirstPerson || now - firstPersonSeen < std::chrono::milliseconds(400);
				stableFirstPerson = g_firstPerson;
			}
		}

		// Lumiere de Skyrim autour du joueur : ciel (exterieur) ou eclairage de la cellule (interieur).
		void UpdateLight(RE::PlayerCharacter* a_player, Shared& a_out)
		{
			auto* cell = a_player->GetParentCell();
			const bool interior = cell && cell->IsInteriorCell();
			a_out.interior = interior;
			float ambient[3]{ 0.5f, 0.5f, 0.5f };
			float sun[3]{ 0.8f, 0.8f, 0.8f };
			float dir[3]{ 0.3f, -0.4f, 0.85f };
			if (interior) {
				if (auto* lighting = cell->GetLighting()) {
					ambient[0] = lighting->ambient.red / 255.0f * g_interiorBoost;
					ambient[1] = lighting->ambient.green / 255.0f * g_interiorBoost;
					ambient[2] = lighting->ambient.blue / 255.0f * g_interiorBoost;
					sun[0] = lighting->directional.red / 255.0f * g_interiorBoost;
					sun[1] = lighting->directional.green / 255.0f * g_interiorBoost;
					sun[2] = lighting->directional.blue / 255.0f * g_interiorBoost;
				}
			} else if (auto* sky = RE::Sky::GetSingleton()) {
				const auto& a = sky->skyColor[RE::TESWeather::ColorTypes::kAmbient];
				const auto& s = sky->skyColor[RE::TESWeather::ColorTypes::kSunlight];
				ambient[0] = a.red;
				ambient[1] = a.green;
				ambient[2] = a.blue;
				sun[0] = s.red;
				sun[1] = s.green;
				sun[2] = s.blue;
				// Le soleil se leve a l'est (6 h), passe au sud (midi), se couche a l'ouest (18 h). La nuit : la lune,
				// haute dans le ciel.
				const float hour = sky->currentGameHour;
				if (hour > 6.0f && hour < 18.0f) {
					const float angle = (hour - 6.0f) / 12.0f * 3.14159265f;
					dir[0] = std::cos(angle);
					dir[1] = -0.35f;
					dir[2] = std::max(0.15f, std::sin(angle));
				} else {
					dir[0] = 0.2f;
					dir[1] = -0.3f;
					dir[2] = 0.9f;
				}
			}
			const float floor = std::max(0.0f, g_minLight);
			for (int i = 0; i < 3; ++i) {
				a_out.ambient[i] = std::clamp(std::max(ambient[i], floor) * g_brightness, 0.0f, 3.0f);
				a_out.sun[i] = std::clamp(sun[i] * g_brightness, 0.0f, 3.0f);
			}
			const float length = std::sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
			for (int i = 0; i < 3; ++i) {
				a_out.sunDir[i] = dir[i] / length;
			}
		}
	}

	void LoadSettings()
	{
		const auto ini = std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini").string();
		auto       number = [&ini](const char* a_key, float a_default) {
            char text[32]{};
            ::GetPrivateProfileStringA("TroisiemePersonne", a_key, "", text, sizeof(text), ini.c_str());
            return text[0] != '\0' ? static_cast<float>(std::atof(text)) : a_default;
		};
		g_enabled = ::GetPrivateProfileIntA("TroisiemePersonne", "Activer", 1, ini.c_str()) != 0;
		g_crosshair = ::GetPrivateProfileIntA("TroisiemePersonne", "Viseur", 1, ini.c_str()) != 0;
		g_brightness = std::clamp(number("Luminosite", 1.0f), 0.1f, 3.0f);
		g_minLight = std::clamp(number("LumiereMin", 0.25f), 0.0f, 1.0f);
		g_interiorBoost = std::clamp(number("LumiereInterieurs", 2.0f), 0.5f, 5.0f);
		logger::info("3e personne filmee : {} (luminosite x{}, minimum {}, interieurs x{})", g_enabled ? "oui" : "non",
			g_brightness, g_minLight, g_interiorBoost);
	}

	bool Enabled()
	{
		return g_enabled;
	}

	bool Crosshair()
	{
		return g_crosshair;
	}

	void PerFrame(RE::PlayerCharacter* a_player, bool a_linked)
	{
		UpdateCameraState();
		g_wanted = g_enabled && a_linked && !g_firstPerson;
		if (!a_player) {
			return;
		}

		Shared next;
		next.thirdPerson = g_wanted;
		next.yaw = a_player->data.angle.z;
		next.pitch = a_player->data.angle.x;
		if (auto* camera = RE::Main::WorldRootCamera()) {
			// Une fois, en premiere personne : quel axe de la camera donne le regard du joueur ?
			auto* playerCamera = RE::PlayerCamera::GetSingleton();
			if (!g_axisChosen && playerCamera && playerCamera->IsInFirstPerson() && !MenuOpen()) {
				for (int axis = 0; axis < 2; ++axis) {
					float yaw = 0.0f, pitch = 0.0f;
					YawPitch(Forward(camera, axis), yaw, pitch);
					if (AngleGap(yaw, a_player->data.angle.z) < 0.15f) {
						++g_votes[axis];
					}
				}
				if (g_votes[0] + g_votes[1] >= 30) {
					g_axisChosen = true;
					g_axis = g_votes[1] > g_votes[0] ? 1 : 0;
					logger::info("3e personne : direction de la camera lue sur sa {} ({} / {})", g_axis == 0 ? "colonne" : "ligne",
						g_votes[0], g_votes[1]);
				}
			}
			YawPitch(Forward(camera, g_axis), next.yaw, next.pitch);
		}
		UpdateLight(a_player, next);
		// 0.70 (coop) : le lieu du joueur. Les joueurs d'un meme lieu partagent le meme calage dans RoR2.
		if (auto* cell = a_player->GetParentCell()) {
			if (cell->IsInteriorCell()) {
				next.placeId = cell->GetFormID();
			} else if (auto* world = a_player->GetWorldspace()) {
				next.placeId = world->GetFormID();
			}
		}

		std::lock_guard lock(g_lock);
		g_shared = next;
	}

	bool FirstPersonStable()
	{
		return g_firstPerson;
	}

	bool Wanted()
	{
		return g_wanted;
	}

	void NoteFilmShown()
	{
		g_filmShownAt = NowMs();
	}

	bool FilmShown()
	{
		return g_wanted && NowMs() - g_filmShownAt.load() < 1000;
	}

	void FillInput(bridge::SkyrimInput& a_input)
	{
		std::lock_guard lock(g_lock);
		a_input.thirdPerson = g_shared.thirdPerson ? 1 : 0;
		a_input.cameraYaw = g_shared.yaw;
		a_input.cameraPitch = g_shared.pitch;
		for (int i = 0; i < 3; ++i) {
			a_input.ambient[i] = g_shared.ambient[i];
			a_input.sun[i] = g_shared.sun[i];
			a_input.sunDir[i] = g_shared.sunDir[i];
		}
		a_input.interior = g_shared.interior ? 1 : 0;
		a_input.placeId = g_shared.placeId;
	}
}
