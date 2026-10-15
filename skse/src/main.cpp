#include "ThirdPerson.h"
#include "Allies.h"
#include "Bridge.h"
#include "Combat.h"
#include "Config.h"
#include "Hud.h"
#include "InputWatcher.h"
#include "Items.h"
#include "Launcher.h"
#include "Loadouts.h"
#include "Loot.h"
#include "Altars.h"
#include "Movement.h"
#include "Shrines.h"
#include "Solids.h"

// Plugin Skyrim de SkyrimRoR2, version 0.3 :
//  - a chaque image, le joueur Skyrim prend la vitesse du survivant RoR2 (Movement.cpp) ;
//  - toutes les 3 secondes, il affiche dans le coin de l'ecran ce que fait le survivant dans RoR2 ;
//  - environ 60 fois par seconde, il envoie a RoR2 les touches et la direction de la camera,
//    pour piloter le survivant depuis la fenetre de Skyrim.

namespace
{
	std::atomic_bool g_inGame{ false };  // vrai une fois une partie Skyrim chargee
	std::atomic_uint32_t g_requestId{ 0 };  // augmente a chaque chargement : RoR2 relance une partie si besoin
	std::uint32_t g_nextSurvivor = 0;      // compteurs d appuis sur F8 / F7, lus par RoR2
	std::uint32_t g_previousSurvivor = 0;

	void SetupLog()
	{
		// Le journal va dans Documents\My Games\Skyrim Special Edition\SKSE\SkyrimRoR2.log
		auto dir = SKSE::log::log_directory();
		if (!dir) {
			return;
		}
		auto path = *dir / "SkyrimRoR2.log";
		auto sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(path.string(), true);
		auto log = std::make_shared<spdlog::logger>("global", std::move(sink));
		log->set_level(spdlog::level::info);
		log->flush_on(spdlog::level::info);
		spdlog::set_default_logger(std::move(log));
		spdlog::set_pattern("[%H:%M:%S.%e] [%l] %v");
	}

	// Affiche un message dans le coin de l'ecran de Skyrim.
	// On passe par la file de taches de SKSE, car l'affichage doit se faire
	// depuis le fil principal du jeu, pas depuis nos fils d'arriere-plan.
	void Show(std::string a_text)
	{
		SKSE::GetTaskInterface()->AddTask([text = std::move(a_text)]() {
			RE::SendHUDMessage::ShowHUDMessage(text.c_str());
		});
	}

	// Vrai si la fenetre au premier plan est celle de Skyrim.
	bool SkyrimHasFocus()
	{
		HWND window = ::GetForegroundWindow();
		if (!window) {
			return false;
		}
		DWORD pid = 0;
		::GetWindowThreadProcessId(window, &pid);
		return pid == ::GetCurrentProcessId();
	}

	bool KeyDown(int a_virtualKey)
	{
		return (::GetAsyncKeyState(a_virtualKey) & 0x8000) != 0;
	}

	// Fil 1 : lit les touches et la camera, et les envoie a RoR2 (~60 fois par seconde).
	void SendInputs()
	{
		std::uint32_t sequence = 0;
		std::int32_t  lastButtons[6]{};  // pour noter chaque nouvel appui dans le journal

		while (true) {
			std::this_thread::sleep_for(16ms);

			bridge::SkyrimInput input{};
			input.magic = bridge::kInputMagic;
			input.sequence = ++sequence;

			auto*      ui = RE::UI::GetSingleton();
			const bool paused = ui && ui->GameIsPaused();  // menu, inventaire, console...
			const bool focused = SkyrimHasFocus();
			if (!focused || paused) {
				// 0.72 : aussi pendant un menu (une touche relachee pendant le menu restait "enfoncee" : le survivant
				// continuait d'avancer). Une touche encore tenue au retour est vue par Windows.
				skyrimror2::InputWatcher::Get().Clear();
			} else {
				skyrimror2::InputWatcher::Get().Reconcile();
			}

			if (g_inGame && !paused && focused) {
				input.active = 1;

				// Les touches viennent de SkyrimRoR2.ini (voir Config.cpp).
				// Une touche compte si Skyrim OU Windows la voit enfoncee.
				const auto& config = skyrimror2::Config::Get();
				const auto& watcher = skyrimror2::InputWatcher::Get();
				const auto  down = [&config, &watcher](skyrimror2::Action a_action) {
					const int key = config.Key(a_action);
					return key != 0 && (watcher.IsDown(key) || KeyDown(key));
				};
				using skyrimror2::Action;

				input.moveForward = (down(Action::kForward) ? 1.0f : 0.0f) - (down(Action::kBack) ? 1.0f : 0.0f);
				input.moveRight = (down(Action::kRight) ? 1.0f : 0.0f) - (down(Action::kLeft) ? 1.0f : 0.0f);

				if (const auto* player = RE::PlayerCharacter::GetSingleton()) {
					input.yaw = player->data.angle.z;
					input.pitch = player->data.angle.x;
				}

				// Changer de survivant sans rien relancer (F8 / F7 par defaut).
				static bool nextWasDown = false, previousWasDown = false;
				const bool nextDown = down(Action::kNextSurvivor);
				const bool previousDown = down(Action::kPreviousSurvivor);
				// Avec les autels : seulement parmi les survivants debloques dans cette sauvegarde.
				// (Avec la triche ToucheTousLesSurvivants, Skyrim fait defiler tous les survivants lui-meme.)
				const bool unlockedOnly = skyrimror2::Shrines::Enabled();
				if (nextDown && !nextWasDown) {
					if (unlockedOnly) {
						skyrimror2::Shrines::Cycle(1);
					} else {
						++g_nextSurvivor;
					}
					logger::info("Survivant suivant demande");
				}
				if (previousDown && !previousWasDown) {
					if (unlockedOnly) {
						skyrimror2::Shrines::Cycle(-1);
					} else {
						++g_previousSurvivor;
					}
					logger::info("Survivant precedent demande");
				}
				nextWasDown = nextDown;
				previousWasDown = previousDown;

				input.jump = down(Action::kJump) ? 1 : 0;
				// Le sprint coute de l'endurance Skyrim : a bout de souffle, plus de sprint.
				const bool wantsSprint = down(Action::kSprint);
				const bool moving = input.moveForward != 0.0f || input.moveRight != 0.0f;
				skyrimror2::Movement::SetSprintRequested(wantsSprint && moving);
				input.sprint = wantsSprint && skyrimror2::Movement::SprintAllowed() ? 1 : 0;
				input.skill1 = down(Action::kSkill1) ? 1 : 0;
				input.skill2 = down(Action::kSkill2) ? 1 : 0;
				input.skill3 = down(Action::kSkill3) ? 1 : 0;
				input.skill4 = down(Action::kSkill4) ? 1 : 0;
				input.equipment = down(Action::kEquipment) ? 1 : 0;
			}

			// Journal : une ligne a chaque nouvel appui, pour verifier que Skyrim voit bien la touche.
			const std::int32_t   buttons[6]{ input.jump, input.sprint, input.skill1, input.skill2, input.skill3, input.skill4 };
			constexpr const char* kNames[6]{ "Saut", "Sprint", "Competence1", "Competence2", "Competence3", "Competence4" };
			static std::chrono::steady_clock::time_point pressedAt[6]{};
			for (int i = 0; i < 6; ++i) {
				if (buttons[i] && !lastButtons[i]) {
					logger::info("Skyrim envoie a RoR2 : {}", kNames[i]);
					pressedAt[i] = std::chrono::steady_clock::now();
				} else if (!buttons[i] && lastButtons[i]) {
					const auto held = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - pressedAt[i]).count();
					logger::info("  {} relachee apres {} ms", kNames[i], held);
				}
				lastButtons[i] = buttons[i];
			}

			if (input.active != 1) {
				skyrimror2::Movement::SetSprintRequested(false);
			}
			skyrimror2::Movement::SetSkyrimDriving(input.active == 1);
			skyrimror2::ThirdPerson::FillInput(input);  // camera et lumiere pour la vue a la 3e personne filmee
			skyrimror2::Bridge::Get().WriteInput(input);

			// Les ordres pour RoR2 : lancer une partie tout seul, avec quel survivant.
			bridge::SkyrimCommand command{};
			command.magic = bridge::kCommandMagic;
			command.sequence = sequence;
			command.requestId = g_requestId.load();
			command.autoStart = skyrimror2::Config::Get().AutoRun() ? 1 : 0;
			const std::string survivor = skyrimror2::Config::Get().Survivor();
			strncpy_s(command.survivor, survivor.c_str(), _TRUNCATE);
			command.nextSurvivor = g_nextSurvivor;
			command.previousSurvivor = g_previousSurvivor;
			// 0.66 : le niveau RoR2 suit le niveau du personnage Skyrim.
			if (auto* player = RE::PlayerCharacter::GetSingleton(); player && player->Is3DLoaded()) {
				command.skyrimLevel = static_cast<std::int32_t>(player->GetLevel());
			}
			skyrimror2::Shrines::FillCommand(command);
			skyrimror2::Loadouts::FillCommand(command);
			skyrimror2::Combat::FillCommand(command);
			skyrimror2::Movement::FillCommand(command);
			skyrimror2::Loot::FillCommand(command);
			skyrimror2::Items::FillCommand(command);  // apres Loot : les objets viennent de l'inventaire de Skyrim
			skyrimror2::Bridge::Get().WriteCommand(command);
		}
	}

	// Fil 2 : affiche l'etat de RoR2 dans Skyrim toutes les 3 secondes.
	void WatchBridge()
	{
		std::uint32_t lastSequence = 0;
		bool          connected = false;
		bool          toldWaiting = false;

		while (true) {
			std::this_thread::sleep_for(3s);
			skyrimror2::Launcher::UpdateRoR2Window();
			if (!g_inGame) {
				continue;
			}

			bridge::BridgeData data{};
			const bool valid = skyrimror2::Bridge::Get().Read(data);
			const bool alive = valid && data.sequence != lastSequence;
			if (valid) {
				lastSequence = data.sequence;
			}

			if (!alive) {
				if (connected) {
					logger::info("RoR2 ne repond plus");
					Show("SkyrimRoR2 : RoR2 ne repond plus");
					connected = false;
				} else if (!toldWaiting) {
					Show("SkyrimRoR2 : en attente de RoR2...");
					toldWaiting = true;
				}
				continue;
			}

			if (!connected) {
				logger::info("RoR2 connecte");
				Show("SkyrimRoR2 : RoR2 connecte");
				connected = true;
				toldWaiting = false;
			}

		}
	}

	// Par defaut, Skyrim se met en pause des qu'on clique sur une autre fenetre.
	// On active son reglage "bAlwaysActive" pour qu'il continue de tourner a cote de RoR2.
	void KeepRunningInBackground()
	{
		auto* collection = RE::INISettingCollection::GetSingleton();
		auto* setting = collection ? collection->GetSetting("bAlwaysActive:General") : nullptr;
		if (setting) {
			setting->data.b = true;
			logger::info("bAlwaysActive active : Skyrim continue sans le focus");
		} else {
			logger::warn("Reglage bAlwaysActive introuvable");
		}
	}

	void OnMessage(SKSE::MessagingInterface::Message* a_msg)
	{
		switch (a_msg->type) {
		case SKSE::MessagingInterface::kDataLoaded:
			KeepRunningInBackground();
			skyrimror2::Config::Get().Load();
			skyrimror2::InputWatcher::Get().Install();
			skyrimror2::Movement::Install();
			skyrimror2::Combat::Install();
			skyrimror2::Shrines::Install();
			skyrimror2::Loot::Install();
			skyrimror2::Items::Install();
			skyrimror2::ThirdPerson::LoadSettings();
			if (!skyrimror2::Bridge::Get().Open()) {
				logger::error("SkyrimRoR2 desactive : impossible d'ouvrir le pont");
				return;
			}
			std::thread(WatchBridge).detach();
			std::thread(SendInputs).detach();
			skyrimror2::Launcher::StartRoR2IfNeeded();  // RoR2 demarre pendant que tu choisis ta sauvegarde
			break;
		case SKSE::MessagingInterface::kPostLoadGame:
		case SKSE::MessagingInterface::kNewGame:
			skyrimror2::Config::Get().Load();  // relit les touches : pas besoin de redemarrer Skyrim
			g_inGame = true;
			++g_requestId;
			skyrimror2::Shrines::OnGameLoaded();
			skyrimror2::Loot::LoadSettings();
			skyrimror2::Altars::LoadSettings();
			skyrimror2::Loot::OnGameLoaded(a_msg->type == SKSE::MessagingInterface::kNewGame);
			skyrimror2::Solids::OnGameLoaded();
			break;
		default:
			break;
		}
	}
}

SKSEPluginLoad(const SKSE::LoadInterface* a_skse)
{
	SKSE::InitInfo info{};
	info.trampoline = true;
	info.trampolineSize = 64;  // place pour les 2 crochets du HUD
	SKSE::Init(a_skse, info);
	SetupLog();
	logger::info("SkyrimRoR2 0.73.9 charge (Skyrim {})", a_skse->RuntimeVersion().string());
	skyrimror2::Hud::Install();  // avant que Skyrim ne cree sa fenetre
	skyrimror2::Allies::PrepareFiles();  // les modeles des allies doivent exister des le lancement
	skyrimror2::Solids::PrepareFiles();  // 0.66 : les boites de collision (cube de detritus)
	skyrimror2::Shrines::RegisterSerialization();
	SKSE::GetMessagingInterface()->RegisterListener(OnMessage);
	return true;
}
