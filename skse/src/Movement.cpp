#include "Movement.h"
#include "ThirdPerson.h"
#include "Loot.h"
#include "Altars.h"
#include "Items.h"

#include "Bridge.h"
#include "Combat.h"
#include "Config.h"
#include "Coop.h"
#include "Allies.h"
#include "Decor.h"
#include "Effects.h"
#include "Hud.h"
#include "Loadouts.h"
#include "Shrines.h"
#include "Solids.h"
#include "Terrain.h"

#include <mutex>

// Le joueur Skyrim suit le survivant RoR2.
//
// Premiere version : on ne reprend de RoR2 que la vitesse HORIZONTALE du survivant
// (sa direction et sa rapidite). A chaque image, on l'impose au corps physique du joueur
// Skyrim, en gardant sa vitesse verticale a lui. Resultat : le moteur physique de Skyrim
// continue de gerer la gravite, les pentes et les murs, mais on se deplace comme un survivant.
//
// Reperes : dans RoR2 (Unity), X = est, Z = nord, Y = hauteur.
//           dans Skyrim,       X = est, Y = nord, Z = hauteur.
// Les deux moteurs physiques comptent en metres : pas de conversion d'echelle.

namespace skyrimror2::Movement
{
	namespace
	{
		std::atomic_bool g_skyrimDriving{ false };
		std::atomic_bool g_linked{ false };
		std::atomic_bool g_sprintRequested{ false };  // touche de sprint enfoncee en se deplacant
		std::atomic_bool g_sprintAllowed{ true };     // faux quand l'endurance de Skyrim est vide
		std::atomic<float> g_groundGap{ -1.0f };      // distance pieds -> sol (m), envoyee a RoR2
		std::atomic<float> g_verticalSpeed{ 0.0f };   // vitesse verticale (m/s), envoyee a RoR2
		std::atomic<float> g_ceiling{ -1.0f };        // plafond au-dessus des pieds (m), -1 : aucun ; envoye a RoR2
		std::chrono::steady_clock::time_point g_landedAt{};  // dernier atterrissage du joueur Skyrim

		// Le sprint du survivant consomme l'endurance de Skyrim.
		// Endurance vide : plus de sprint jusqu'a ce qu'elle remonte a 25 %.
		constexpr float kSprintStaminaPerSecond = 10.0f;
		constexpr float kSprintResumeFraction = 0.25f;

		void UpdateStamina(RE::PlayerCharacter* a_player, float a_delta)
		{
			if (!g_linked || !Config::Get().SprintUsesStamina()) {
				g_sprintAllowed = true;
				return;
			}
			auto* values = a_player->AsActorValueOwner();
			if (!values) {
				return;
			}
			const float current = values->GetActorValue(RE::ActorValue::kStamina);
			const float maximum = std::max(1.0f, values->GetPermanentActorValue(RE::ActorValue::kStamina));

			if (g_sprintRequested && g_sprintAllowed) {
				const float cost = kSprintStaminaPerSecond * Config::Get().SprintStaminaCost() * std::clamp(a_delta, 0.0f, 0.25f);
				values->DamageActorValue(RE::ActorValue::kStamina, std::min(cost, std::max(0.0f, current)));
				if (current - cost <= 0.5f) {
					g_sprintAllowed = false;  // a bout de souffle
				}
			} else if (!g_sprintAllowed && current >= maximum * kSprintResumeFraction) {
				g_sprintAllowed = true;
			}
		}

		// Au-dela de ce delai sans nouvelle image de RoR2, on rend la main a Skyrim.
		// 0.74 : 1,5 s (au lieu de 0,5) : sur un PC plus lent, RoR2 a des a-coups (conversion des modeles au premier
		// lancement) et le lien se coupait / revenait en boucle.
		constexpr auto kTimeout = 1500ms;

		// 0.74 : la camera de Skyrim, lue sur le fil du jeu (a chaque image du joueur) et recopiee ici. Avant, le fil
		// qui ecrit au pont la lisait lui-meme pendant que le jeu la changeait : crash (Skyrim 1.7, chargements).
		struct CameraCopy
		{
			bool  valid = false;
			float x = 0.0f, y = 0.0f, z = 0.0f;
			float left = 0.0f, right = 0.0f, top = 0.0f, bottom = 0.0f, nearPlane = 0.0f;
			bool  ortho = false;
		};
		std::mutex g_cameraLock;
		CameraCopy g_camera;

		void CopyCamera()
		{
			CameraCopy copy;
			auto*      main = RE::Main::GetSingleton();
			if (auto* camera = main ? RE::Main::WorldRootCamera() : nullptr) {
				const auto& frustum = camera->GetRuntimeData2().viewFrustum;
				copy.valid = true;
				copy.x = camera->world.translate.x;
				copy.y = camera->world.translate.y;
				copy.z = camera->world.translate.z;
				copy.left = frustum.fLeft;
				copy.right = frustum.fRight;
				copy.top = frustum.fTop;
				copy.bottom = frustum.fBottom;
				copy.nearPlane = frustum.fNear;
				copy.ortho = frustum.bOrtho;
			}
			std::scoped_lock lock(g_cameraLock);
			g_camera = copy;
		}

		// Escaliers et pentes : vitesse de montee maximale (m/s) quand le survivant ne saute pas.
		constexpr float kMaxClimbRatio = 0.7f;   // ~35 degres de pente
		constexpr float kMaxClimbSpeed = 5.0f;   // plafond absolu
		constexpr float kJumpThreshold = 2.0f;   // au-dessus, le survivant RoR2 est en train de sauter
		constexpr float kUnitsPerMeter = 70.0f;  // 1 metre = environ 70 unites Skyrim
		constexpr float kChestHeight = 1.0f;     // hauteur du rayon de verification (m)
		constexpr float kWallMargin = 0.6f;      // distance gardee devant un mur (m)
		constexpr float kGroundRange = 100.0f;   // distance au sol mesuree jusqu'a 100 m
		constexpr float kLandedGap = 0.3f;       // sol a moins de 30 cm sous les pieds : le joueur est soutenu
		constexpr float kStepDown = 0.7f;        // marche d'escalier : un vide plus petit n'est pas une chute
		constexpr float kBigFall = 2.0f;         // chute plus haute (m) : on laisse Skyrim jouer l'atterrissage
		constexpr auto  kLandingTime = 300ms;    // temps laisse a Skyrim pour l'atterrissage

		// Part libre (0 a 1) du trajet entre deux points, d'apres un rayon invisible de Skyrim.
		// Le rayon ignore le joueur lui-meme. 1 = rien sur le chemin.
		float FreePathFraction(RE::PlayerCharacter* a_player, const RE::NiPoint3& a_from, const RE::NiPoint3& a_to)
		{
			auto* cell = a_player->GetParentCell();
			auto* world = cell ? cell->GetbhkWorld() : nullptr;
			if (!world) {
				return 1.0f;
			}

			const float     scale = RE::bhkWorld::GetWorldScale();  // unites Skyrim -> metres physiques
			RE::bhkPickData pick{};
			pick.rayInput.from = RE::hkVector4(a_from.x * scale, a_from.y * scale, a_from.z * scale, 0.0f);
			pick.rayInput.to = RE::hkVector4(a_to.x * scale, a_to.y * scale, a_to.z * scale, 0.0f);

			RE::CFilter filter{};
			a_player->GetCollisionFilterInfo(filter);         // meme groupe que le joueur : il est ignore
			filter.SetCollisionLayer(RE::COL_LAYER::kLOS);     // comme une ligne de vue : murs, sol, decor
			pick.rayInput.filterInfo = filter;

			world->PickObject(pick);
			return pick.rayOutput.HasHit() ? std::clamp(pick.rayOutput.hitFraction, 0.0f, 1.0f) : 1.0f;
		}

		// Vrai s'il y a du sol a moins de a_gap metres sous les pieds du joueur.
		bool GroundBelow(RE::PlayerCharacter* a_player, float a_gap)
		{
			const auto   feet = a_player->GetPosition();
			const float  start = 0.3f;  // le rayon part un peu au-dessus des pieds
			RE::NiPoint3 from{ feet.x, feet.y, feet.z + start * kUnitsPerMeter };
			RE::NiPoint3 to{ feet.x, feet.y, feet.z - a_gap * kUnitsPerMeter };
			return FreePathFraction(a_player, from, to) < 1.0f;
		}

		// Distance (m) entre les pieds du joueur et le sol en dessous, jusqu'a a_range metres.
		float GroundDistance(RE::PlayerCharacter* a_player, float a_range)
		{
			const auto   feet = a_player->GetPosition();
			const float  start = 0.3f;
			RE::NiPoint3 from{ feet.x, feet.y, feet.z + start * kUnitsPerMeter };
			RE::NiPoint3 to{ feet.x, feet.y, feet.z - a_range * kUnitsPerMeter };
			const float  fraction = FreePathFraction(a_player, from, to);
			return std::max(0.0f, fraction * (a_range + start) - start);
		}

		// Hauteur du plafond au-dessus des pieds (m), ou -1 s'il n'y en a pas dans les 6 m.
		// Le relief envoye a RoR2 n'a que des sols : sans ca, un survivant propulse vers le haut
		// (poing de la Chargeuse...) traversait les plafonds dans RoR2 et le joueur Skyrim restait colle dessous.
		float CeilingHeight(RE::PlayerCharacter* a_player)
		{
			constexpr float kStart = 1.0f;
			constexpr float kRange = 6.0f;
			const auto   feet = a_player->GetPosition();
			RE::NiPoint3 from{ feet.x, feet.y, feet.z + kStart * kUnitsPerMeter };
			RE::NiPoint3 to{ feet.x, feet.y, feet.z + kRange * kUnitsPerMeter };
			const float  fraction = FreePathFraction(a_player, from, to);
			return fraction < 1.0f ? kStart + fraction * (kRange - kStart) : -1.0f;
		}

		// Deplace le joueur d'un coup (en metres), en s'arretant devant le premier mur.
		// Renvoie la distance reellement parcourue, en metres.
		float SafeTeleport(RE::PlayerCharacter* a_player, float a_east, float a_north)
		{
			const float distance = std::sqrt(a_east * a_east + a_north * a_north);
			if (distance < 0.01f) {
				return 0.0f;
			}

			const auto  start = a_player->GetPosition();
			const float chest = kChestHeight * kUnitsPerMeter;
			RE::NiPoint3 from{ start.x, start.y, start.z + chest };
			RE::NiPoint3 to{ start.x + a_east * kUnitsPerMeter, start.y + a_north * kUnitsPerMeter, start.z + chest };

			const float free = FreePathFraction(a_player, from, to) * distance;
			const float travel = free >= distance ? distance : std::max(0.0f, free - kWallMargin);
			if (travel <= 0.0f) {
				return 0.0f;
			}

			auto destination = start;
			destination.x += a_east / distance * travel * kUnitsPerMeter;
			destination.y += a_north / distance * travel * kUnitsPerMeter;
			// 0.73.6 : on gardait la hauteur de depart. En arrivant sur une pente qui monte, une marche ou un pave plus haut,
			// le joueur etait pose SOUS le sol et passait a travers. On cherche le sol a l'arrivee (jusqu'a 1,2 m au-dessus
			// et 6 m au-dessous) et on pose les pieds dessus.
			{
				constexpr float kUp = 1.2f;  // pas plus haut : sous un plafond, on ne doit pas atterrir sur le toit
				constexpr float kDown = 6.0f;
				RE::NiPoint3 top{ destination.x, destination.y, start.z + kUp * kUnitsPerMeter };
				RE::NiPoint3 bottom{ destination.x, destination.y, start.z - kDown * kUnitsPerMeter };
				const float  fraction = FreePathFraction(a_player, top, bottom);
				if (fraction < 1.0f) {
					const float ground = top.z + (bottom.z - top.z) * fraction;
					destination.z = ground + 0.05f * kUnitsPerMeter;
				}
			}
			a_player->SetPosition(destination, true);
			return travel;
		}

		void Follow(RE::PlayerCharacter* a_player)
		{
			static std::uint32_t lastSequence = 0;
			static auto          lastChange = std::chrono::steady_clock::now();
			static bool          following = false;

			bridge::BridgeData data{};
			const bool         valid = Bridge::Get().Read(data);
			const auto         now = std::chrono::steady_clock::now();
			if (valid && data.sequence != lastSequence) {
				lastSequence = data.sequence;
				lastChange = now;
			}
			const bool fresh = valid && now - lastChange < kTimeout;
			const bool linked = fresh && data.inRun == 1 && g_skyrimDriving;
			g_linked = linked;
			const bool active = Config::Get().FollowEnabled() && linked;

			// Petites teleportations (blink de la Huntress...) : RoR2 envoie un total cumule,
			// on applique ce qui a change depuis la derniere image.
			static bool  blinkKnown = false;
			static float lastBlinkX = 0.0f;
			static float lastBlinkZ = 0.0f;
			const float  blinkX = valid ? data.blinkTotalX : lastBlinkX;
			const float  blinkZ = valid ? data.blinkTotalZ : lastBlinkZ;
			const float  blinkEast = blinkX - lastBlinkX;
			const float  blinkNorth = blinkZ - lastBlinkZ;
			const bool   blinked = blinkKnown && (blinkEast != 0.0f || blinkNorth != 0.0f);
			blinkKnown = valid;
			lastBlinkX = blinkX;
			lastBlinkZ = blinkZ;

			if (active != following) {
				following = active;
				if (active) {
					logger::info("Le joueur Skyrim suit le survivant");
				} else {
					logger::info("Le joueur Skyrim se deplace seul");
				}
			}
			if (!active) {
				g_groundGap = -1.0f;  // RoR2 garde son sol tel quel
				g_ceiling = -1.0f;
				return;
			}

			auto* controller = a_player->GetCharController();
			if (!controller) {
				return;
			}

			const float multiplier = Config::Get().SpeedMultiplier();

			// Petite teleportation (blink...) : on te deplace d'un coup, mais apres avoir verifie
			// le trajet avec un rayon invisible. S'il touche un mur, on s'arrete juste devant.
			if (blinked) {
				const float wantedEast = blinkEast * multiplier;
				const float wantedNorth = blinkNorth * multiplier;
				const float travelled = SafeTeleport(a_player, wantedEast, wantedNorth);
				logger::info("Teleportation courte : {:.1f} m demandes, {:.1f} m parcourus",
					std::sqrt(wantedEast * wantedEast + wantedNorth * wantedNorth), travelled);
			}

			const float east = data.velX * multiplier;
			const float north = data.velZ * multiplier;
			const bool moving = east * east + north * north > 0.01f;

			const float  z = a_player->GetPosition().z;

			// Pour RoR2 : la distance au sol sous le joueur Skyrim. Son sol invisible se place pareil sous le
			// survivant, qui tombe et atterrit donc en meme temps que le joueur Skyrim (falaise, toit...).
			{
				RE::hkVector4 speed;
				controller->GetLinearVelocityImpl(speed);
				g_verticalSpeed = speed.quad.m128_f32[2];
				const auto state = controller->context.currentState;
				const bool walkingOrFalling = state == RE::hkpCharacterStateType::kOnGround || state == RE::hkpCharacterStateType::kInAir;
				g_groundGap = walkingOrFalling ? GroundDistance(a_player, kGroundRange) : 0.0f;  // nage, echelle... : au sol
				g_ceiling = Terrain::Enabled() ? CeilingHeight(a_player) : -1.0f;
			}
			const auto   state = controller->context.currentState;
			const bool   onGround = state == RE::hkpCharacterStateType::kOnGround;
			const bool   inAir = state == RE::hkpCharacterStateType::kInAir;  // pas en nageant, ni sur une echelle

			// Un saut commence quand le survivant decolle vers le haut dans RoR2, et dure tant qu'il est en l'air.
			// Mais le sol de Skyrim n'est pas celui de RoR2 : si le joueur Skyrim retouche le sol en
			// redescendant, son saut est fini, meme si le survivant est encore en l'air dans RoR2.
			static bool rorAirborne = false;   // le survivant est en l'air dans RoR2, apres un saut
			static bool jumpLanded = false;    // ...mais le joueur Skyrim a deja retouche le sol
			static auto jumpLandedAt = now;
			// Avec le relief recopie dans RoR2, les deux jeux ont le meme sol : le joueur Skyrim suit alors
			// le survivant pendant TOUT son temps en l'air (saut, chute, grappin...), et ils atterrissent ensemble.
			const bool sameGround = Terrain::Enabled();
			// Sur un escalier, le survivant de RoR2 "decolle" un instant a chaque marche : on n'en tient compte que si
			// ca dure (0,15 s), ou si c'est un vrai saut. Sinon le joueur Skyrim sautillait et atterrissait (bruit) a
			// chaque marche, en montant comme en descendant.
			static bool wasAirborne = false;
			static auto airborneSince = now;
			if (data.survivorAirborne == 1 && !wasAirborne) {
				airborneSince = now;
			}
			wasAirborne = data.survivorAirborne == 1;
			const bool airborneLong = now - airborneSince > 150ms;
			if (data.survivorAirborne != 1) {
				if (rorAirborne && !jumpLanded) {
					g_landedAt = now;          // le survivant a atterri : le joueur Skyrim aussi
				}
				rorAirborne = false;
				jumpLanded = false;
			} else if (data.velY > kJumpThreshold || (sameGround && !rorAirborne && airborneLong)) {
				rorAirborne = true;            // saut, double saut (ou, avec le relief, toute mise en l'air)
				jumpLanded = false;
			} else if (rorAirborne && !jumpLanded && data.velY < 0.0f && (onGround || GroundBelow(a_player, kLandedGap))) {
				jumpLanded = true;             // redescente : sol de Skyrim atteint
				g_landedAt = now;
				jumpLandedAt = now;
			}
			const bool inJump = rorAirborne && !jumpLanded;

			// On garde la vitesse verticale de Skyrim (gravite, sauts)...
			RE::hkVector4 current;
			controller->GetLinearVelocityImpl(current);
			float vertical = current.quad.m128_f32[2];

			// ---- Les vraies chutes (falaise, toit...) ----
			// D'habitude on force le mode "en l'air" et on remet le compteur de chute a zero (voir plus bas).
			// Mais pendant une vraie chute, ca empechait Skyrim de voir l'atterrissage : le joueur restait
			// en animation de chute, et les degats arrivaient en retard. Pendant une chute, et juste apres
			// l'atterrissage, on laisse donc Skyrim tout gerer : gravite, atterrissage, degats de chute.
			// On ne se fie pas a la vitesse : en remettant le compteur de chute a zero, on empechait justement
			// Skyrim d'accelerer la chute. On regarde donc s'il y a du sol sous les pieds, avec un rayon invisible.
			// Des 30 cm de vide, la gravite de Skyrim reprend la main (sinon le joueur flottait jusqu'au sol).
			static bool  falling = false;
			static float supportedZ = 0.0f;               // derniere hauteur ou le joueur etait soutenu
			static auto  landingUntil = now;              // fin du petit temps laisse a l'atterrissage
			if (inJump || (!inAir && !onGround)) {
				falling = false;  // saut RoR2 (c'est RoR2 qui gere la hauteur), nage...
			} else if (falling) {
				if (onGround || GroundBelow(a_player, kLandedGap)) {
					falling = false;
					g_landedAt = now;
					const float height = (supportedZ - z) / kUnitsPerMeter;
					if (height >= kBigFall) {
						landingUntil = now + kLandingTime;  // vraie chute : Skyrim joue l'atterrissage et compte les degats
						logger::info("Atterrissage apres une chute de {:.1f} m", height);
					}
				}
			} else if (inAir && !GroundBelow(a_player, kStepDown)) {
				// Plus de sol sous les pieds (bord, fin de saut au-dessus du sol...) : on tombe.
				// Une simple marche (moins de 70 cm) n'est pas une chute : sinon, en descendant un escalier, Skyrim
				// jouait un atterrissage (et son bruit) a chaque marche.
				falling = true;
				controller->fallStartHeight = supportedZ;  // Skyrim compte la chute depuis le bord
			}
			if (!falling) {
				supportedZ = z;  // le joueur est encore soutenu (ou saute dans RoR2)
			}
			const bool skyrimHandlesFall = falling || now < landingUntil;

			// Le moteur physique de Skyrim a deux modes : "au sol", il recalcule sa propre vitesse
			// et efface la notre ; "en l'air", il garde la vitesse qu'on lui donne.
			// Quand le survivant bouge, on le passe donc en mode "en l'air", comme SkyCraft.
			// La gravite le plaque toujours au sol et les murs le bloquent toujours.
			if (moving && !skyrimHandlesFall) {
				if (onGround) {
					controller->context.currentState = RE::hkpCharacterStateType::kInAir;
				}
				// Pas de degats ni d'animation de chute en marchant : on remet le compteur de chute a zero.
				controller->fallStartHeight = z;
				controller->fallTime = 0.0f;
			}

			// ...mais en mode "en l'air", chaque rebord de marche pousse le joueur vers le haut,
			// et ca s'accumule. Tant que le survivant ne saute pas dans RoR2, on plafonne la montee :
			// assez pour gravir escaliers et pentes, pas assez pour s'envoler.
			// Le survivant saute (ou est propulse vers le haut) dans RoR2 : le joueur Skyrim monte
			// a la meme vitesse. Skyrim ne peut pas sauter tout seul ici, car on l'a mis en mode
			// "en l'air" pour garder notre vitesse. Doubles sauts compris.
			// Pendant tout le saut, Skyrim suit la vitesse verticale de RoR2, en montee comme en
			// descente : la gravite de RoR2 etant plus forte, c'est ce qui donne la bonne hauteur.
			if (inJump) {
				vertical = data.velY;
				if (controller->context.currentState == RE::hkpCharacterStateType::kOnGround) {
					controller->context.currentState = RE::hkpCharacterStateType::kInAir;  // decolle
				}
				controller->fallStartHeight = a_player->GetPosition().z;
				controller->fallTime = 0.0f;
			} else if (moving && !skyrimHandlesFall) {
				const float horizontal = std::sqrt(east * east + north * north);
				const float maxRise = std::min(horizontal * kMaxClimbRatio, kMaxClimbSpeed);
				vertical = std::min(vertical, maxRise);
				const float gap = GroundDistance(a_player, kStepDown + 0.1f);
				// Le moteur de Skyrim "monte" sur un caillou en poussant le joueur vers le haut ; au sommet, cet elan le
				// lancait en l'air comme un tremplin (RoR2, lui, reste au sol). Monter n'est permis qu'au contact du sol :
				// des que le joueur s'en ecarte en montant depuis plus de 0,1 s, l'elan vers le haut est coupe.
				static auto risingFreeSince = now;
				const bool  risingFree = vertical > 0.2f && gap > 0.08f;
				if (!risingFree) {
					risingFreeSince = now;
				} else if (now - risingFreeSince > 100ms) {
					vertical = 0.0f;
				}
				// En descente (escalier, pente) : le joueur est pose directement sur la marche d'en dessous (sans vitesse
				// vers le bas : une grande vitesse faisait jouer a Skyrim un atterrissage, et son bruit, a chaque marche).
				if (vertical <= 0.2f && data.velY <= 0.2f && gap > 0.05f && gap <= kStepDown) {
					auto position = a_player->GetPosition();
					position.z -= (gap - 0.02f) * kUnitsPerMeter;
					a_player->SetPosition(position, true);
					vertical = std::min(vertical, 0.0f);
				}
				// Pas de bruit ni d'animation d'atterrissage en marchant (escaliers compris).
				controller->fallStartHeight = a_player->GetPosition().z;
				controller->fallTime = 0.0f;
			}

			// Le joueur Skyrim a deja atterri mais le survivant est encore en l'air (les deux jeux se sont
			// decales) : on ne garde pas son elan, sinon le joueur Skyrim glisse sur le sol. RoR2 ramene
			// ensuite le survivant a sa place.
			float moveEast = east;
			float moveNorth = north;
			if (sameGround && jumpLanded && data.survivorAirborne == 1 && now - jumpLandedAt > 250ms) {
				moveEast = 0.0f;
				moveNorth = 0.0f;
			}
			controller->SetLinearVelocityImpl(RE::hkVector4(moveEast, moveNorth, vertical, 0.0f));
		}

		// Crochet : appele a chaque image, juste apres la mise a jour normale du joueur.
		struct PlayerUpdateHook
		{
			static void thunk(RE::PlayerCharacter* a_this, float a_delta)
			{
				func(a_this, a_delta);
				try {
					CopyCamera();
				} catch (...) {
				}
				try {
					Follow(a_this);
				} catch (const std::exception& e) {
					logger::error("Suivi du survivant : {}", e.what());
				}
				try {
					Combat::PerFrame(a_this);
				} catch (const std::exception& e) {
					logger::error("Combat : {}", e.what());
				}
				UpdateStamina(a_this, a_delta);
				Shrines::PerFrame();
				Loadouts::PerFrame();
				Terrain::PerFrame(a_this, g_linked);
				try {
					Effects::PerFrame(a_this, g_linked);
				} catch (const std::exception& e) {
					logger::error("Effets : {}", e.what());
				}
				ThirdPerson::PerFrame(a_this, g_linked);  // avant les allies : etat de la camera
				try {
					Allies::PerFrame(a_this, g_linked);
				} catch (const std::exception& e) {
					logger::error("Allies : {}", e.what());
				}
				try {
					Solids::PerFrame(a_this, g_linked);  // 0.66 : cube de detritus solide
				} catch (const std::exception& e) {
					logger::error("Solides : {}", e.what());
				}
				try {
					Decor::PerFrame(a_this, g_linked);
				} catch (const std::exception& e) {
					logger::error("Decor : {}", e.what());
				}
				try {
					Loot::HoldUntilReady();
					Coop::PerFrame(a_this, g_linked);  // 0.70 : avant Loot (difficulte commune, contenants des coequipiers)
					Loot::PerFrame(a_this, g_linked);
					Altars::PerFrame(a_this, g_linked);
					Items::PerFrame(a_this, g_linked);
				} catch (const std::exception& e) {
					logger::error("Objets : {}", e.what());
				}
				Hud::PerFrame(g_linked);
			}
			static inline REL::Relocation<decltype(thunk)> func;
		};
	}

	void Install()
	{
		REL::Relocation<std::uintptr_t> playerVtbl{ RE::VTABLE_PlayerCharacter[0] };
		PlayerUpdateHook::func = playerVtbl.write_vfunc(0xAD, PlayerUpdateHook::thunk);
		logger::info("Crochet de deplacement installe");
	}

	void SetSkyrimDriving(bool a_driving)
	{
		g_skyrimDriving = a_driving;
	}

	bool IsLinked()
	{
		return g_linked;
	}

	void SetSprintRequested(bool a_requested)
	{
		g_sprintRequested = a_requested;
	}

	void FillCommand(bridge::SkyrimCommand& a_command)
	{
		a_command.groundGap = g_groundGap;
		a_command.verticalSpeed = g_verticalSpeed;
		a_command.ceilingHeight = g_ceiling;

		// La camera de Skyrim (position et champ de vision) : RoR2 filme ses allies et ses tirs exactement d'ici.
		CameraCopy camera;
		{
			std::scoped_lock lock(g_cameraLock);
			camera = g_camera;
		}
		if (camera.valid) {
			float scale = 1.0f;
			if (!camera.ortho && camera.nearPlane > 0.0f && std::abs(camera.right) > 2.0f) {
				scale = 1.0f / camera.nearPlane;  // bords donnes au plan proche : ramenes a 1 m
			}
			a_command.cameraX = camera.x / kUnitsPerMeter;
			a_command.cameraY = camera.y / kUnitsPerMeter;
			a_command.cameraZ = camera.z / kUnitsPerMeter;
			a_command.frustumLeft = camera.left * scale;
			a_command.frustumRight = camera.right * scale;
			a_command.frustumTop = camera.top * scale;
			a_command.frustumBottom = camera.bottom * scale;
			static bool logged = false;
			if (!logged && camera.right != 0.0f) {
				logged = true;
				logger::info("Camera Skyrim : champ de vision gauche {:.3f} droite {:.3f} haut {:.3f} bas {:.3f} (proche {:.1f})",
					camera.left, camera.right, camera.top, camera.bottom, camera.nearPlane);
			}
		}
	}

	bool JustLanded()
	{
		return std::chrono::steady_clock::now() - g_landedAt < 500ms;
	}

	bool SprintAllowed()
	{
		return g_sprintAllowed;
	}
}
