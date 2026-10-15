#include "Combat.h"
#include "Lang.h"

#include "Allies.h"
#include "Bridge.h"

#include <random>
#include "Config.h"
#include "Experience.h"
#include "Items.h"
#include "Loot.h"
#include "Movement.h"

// Combat, cote Skyrim.
//
// 1. Chaque image, on liste les acteurs HOSTILES a moins de 50 m (pas les compagnons,
//    pas les passants) et on les envoie a RoR2 avec leur position par rapport au joueur.
//    RoR2 place un "double" de chacun autour du survivant.
// 2. Quand un ennemi qu'on suivait meurt, on le signale pendant 2 secondes avec le drapeau
//    kActorDead : RoR2 tue alors son double (objets "a la mort", or, experience).
// 3. On lit la file des degats ecrite par RoR2 et on les retire aux PV des ennemis Skyrim.
//
// Reperes : 1 metre = environ 70 unites Skyrim.
//           Skyrim : X = est, Y = nord, Z = hauteur.  RoR2 : X = est, Y = hauteur, Z = nord.

namespace skyrimror2::Combat
{
	namespace
	{
		using Clock = std::chrono::steady_clock;

		constexpr float kUnitsPerMeter = 70.0f;
		constexpr float kRangeMeters = 50.0f;
		constexpr auto  kDeadReportTime = 2s;

		std::uint32_t                                    g_tableSequence = 0;
		std::unordered_set<RE::FormID>                   g_aliveLastFrame;  // ennemis vivants envoyes a l'image precedente
		std::unordered_map<RE::FormID, Clock::time_point> g_deadUntil;      // morts a signaler, jusqu'a quand

		std::uint32_t g_damageRead = 0;
		bool          g_damageStarted = false;
		std::uint32_t g_damageApplied = 0;

		// Creatures et personnes NON hostiles ([Combat] du .ini) : elles ont aussi un double dans RoR2, que tes
		// drones et tourelles ignorent, mais que tu peux frapper toi-meme (comme dans Skyrim).
		bool g_passiveCreatures = true;
		bool g_passivePeople = true;

		bool IsPerson(RE::Actor* a_actor)
		{
			auto* race = a_actor->GetRace();
			return race && race->HasKeywordString("ActorTypeNPC");
		}

		// ---------- Elites ----------
		// Les ennemis hostiles peuvent etre des elites RoR2 (feu, glace, foudre), plus souvent quand la difficulte monte.
		// Tire une seule fois par ennemi ; son nom dans Skyrim le montre ("Draugr ardent"). RoR2 lui donne l'aspect
		// d'elite (effets, coups qui brulent, gelent ou foudroient) et le rend plus dur a tuer.
		std::unordered_map<RE::FormID, int> g_elites;
		constexpr const char*               kEliteSuffix[4]{ "", " ardent", " glacial", " survolte" };
		// 0.74 : le nom d'elite traduit ("{} ardent" -> "Blazing {}") ; l'ancien suffixe francais reste reconnu.
		constexpr const char* kEliteName[4]{ "{}", "{} ardent", "{} glacial", "{} survolte" };

		bool HasEliteName(std::string_view a_name, int a_type)
		{
			if (a_name.ends_with(kEliteSuffix[a_type])) {
				return true;
			}
			const std::string pattern = Lang::T(kEliteName[a_type]);
			const auto        hole = pattern.find("{}");
			if (hole == std::string::npos) {
				return false;
			}
			const std::string_view before = std::string_view(pattern).substr(0, hole);
			const std::string_view after = std::string_view(pattern).substr(hole + 2);
			return a_name.size() > before.size() + after.size() && a_name.starts_with(before) && a_name.ends_with(after);
		}

		int EliteOf(RE::Actor* a_actor, bool a_skip)
		{
			const RE::FormID id = a_actor->GetFormID();
			if (auto it = g_elites.find(id); it != g_elites.end()) {
				return it->second;
			}
			if (a_skip) {
				return 0;
			}
			const char*            shownName = a_actor->GetDisplayFullName();
			const std::string_view name = shownName ? shownName : "";
			// Deja elite (partie rechargee) : son nom le dit.
			for (int type = 1; type <= 3; ++type) {
				if (HasEliteName(name, type)) {
					g_elites[id] = type;
					return type;
				}
			}
			static std::mt19937 random{ std::random_device{}() };
			const float chance = std::clamp(2.0f + 3.0f * Loot::Difficulty(), 0.0f, 40.0f);
			int         type = 0;
			if (std::uniform_real_distribution<float>(0.0f, 100.0f)(random) < chance) {
				type = std::uniform_int_distribution<int>(1, 3)(random);
				if (!name.empty()) {
					a_actor->SetDisplayName(RE::BSFixedString(Lang::F(kEliteName[type], std::string(name))), true);
				}
				logger::info("Elite : {} {:08X}{}", std::string(name), id, kEliteSuffix[type]);
			}
			g_elites[id] = type;
			return type;
		}

		// ---------- 0.71 : les elites se voient (aura de couleur + couronne RoR2) ----------
		// Aura : le shader de la cape de flammes / de givre / de foudre de Skyrim (trouve dans les effets magiques
		// "cape"), pose sur l'ennemi tant qu'il vit. Couronne : le modele de l'aspect d'elite de RoR2 (converti par
		// RoR2 comme les autels : EliteCrown1_A, 2, 3), pose au-dessus de sa tete et qui le suit.
		std::array<RE::TESEffectShader*, 4> g_auras{};
		bool                                g_aurasSearched = false;
		bool                                g_eliteLook = true;    // [Elites] Apparence
		float                               g_crownSize = 0.35f;   // [Elites] TailleCouronne (m)
		constexpr std::uint32_t             kCrownIdBase = 0xE1170000;
		std::unordered_map<RE::FormID, std::uint32_t> g_crownIds;  // ennemi -> identifiant du modele pose
		std::unordered_set<std::uint32_t>             g_crownsShown;

		int ElementOf(RE::EffectSetting* a_effect)
		{
			if (!a_effect) {
				return 0;
			}
			const auto resist = a_effect->data.resistVariable;
			if (resist == RE::ActorValue::kResistFire || a_effect->HasKeywordString("MagicDamageFire")) {
				return 1;
			}
			if (resist == RE::ActorValue::kResistFrost || a_effect->HasKeywordString("MagicDamageFrost")) {
				return 2;
			}
			if (resist == RE::ActorValue::kResistShock || a_effect->HasKeywordString("MagicDamageShock")) {
				return 3;
			}
			return 0;
		}

		void FindAuras()
		{
			g_aurasSearched = true;
			auto* data = RE::TESDataHandler::GetSingleton();
			if (!data) {
				return;
			}
			std::array<RE::TESEffectShader*, 4> fallback{};
			for (auto* effect : data->GetFormArray<RE::EffectSetting>()) {
				if (!effect || !effect->data.effectShader) {
					continue;
				}
				int element = ElementOf(effect);
				const bool cloak = effect->HasArchetype(RE::EffectSetting::Archetype::kCloak);
				if (cloak && element == 0) {
					// La cape elle-meme n'a pas d'element : celui du sort qu'elle lance autour d'elle.
					if (auto* spell = effect->data.associatedForm ? effect->data.associatedForm->As<RE::MagicItem>() : nullptr) {
						for (auto* entry : spell->effects) {
							element = element ? element : ElementOf(entry ? entry->baseEffect : nullptr);
						}
					}
				}
				if (element == 0) {
					continue;
				}
				if (cloak && !g_auras[element]) {
					g_auras[element] = effect->data.effectShader;
				} else if (!fallback[element]) {
					fallback[element] = effect->data.effectShader;
				}
			}
			for (int element = 1; element <= 3; ++element) {
				if (!g_auras[element]) {
					g_auras[element] = fallback[element];
				}
				logger::info("Elites : aura {} -> shader {:08X}", kEliteSuffix[element], g_auras[element] ? g_auras[element]->GetFormID() : 0);
			}
		}

		// Toutes les 2 s : chaque elite vivante et chargee a son aura (une seule), les mortes la perdent.
		void UpdateAuras(RE::PlayerCharacter* a_player, const std::unordered_map<RE::FormID, int>& a_alive)
		{
			static Clock::time_point next{};
			const auto               now = Clock::now();
			if (now < next) {
				return;
			}
			next = now + 2s;
			if (!g_aurasSearched) {
				FindAuras();
			}
			auto* lists = RE::ProcessLists::GetSingleton();
			if (!lists) {
				return;
			}
			std::unordered_set<RE::FormID> has;
			lists->ForEachShaderEffect([&](RE::ShaderReferenceEffect* a_effect) {
				if (!a_effect || a_effect->finished) {
					return RE::BSContainer::ForEachResult::kContinue;
				}
				const bool ours = a_effect->effectData && (a_effect->effectData == g_auras[1] || a_effect->effectData == g_auras[2] ||
				                                              a_effect->effectData == g_auras[3]);
				if (!ours) {
					return RE::BSContainer::ForEachResult::kContinue;
				}
				auto target = a_effect->target.get();
				const RE::FormID id = target ? target->GetFormID() : 0;
				const auto it = a_alive.find(id);
				if (it == a_alive.end() || !g_eliteLook || g_auras[it->second] != a_effect->effectData) {
					a_effect->finished = true;  // mort, plus elite, ou reglage coupe : l'aura s'en va
				} else {
					has.insert(id);
				}
				return RE::BSContainer::ForEachResult::kContinue;
			});
			if (!g_eliteLook) {
				return;
			}
			for (const auto& [id, type] : a_alive) {
				if (has.contains(id) || type < 1 || type > 3 || !g_auras[type]) {
					continue;
				}
				auto* actor = RE::TESForm::LookupByID<RE::Actor>(id);
				if (actor && actor->Get3D() && actor != a_player) {
					actor->ApplyEffectShader(g_auras[type], -1.0f);
				}
			}
		}

		// A chaque image : la couronne au-dessus de la tete de chaque elite vivante proche.
		void UpdateCrowns(const std::unordered_map<RE::FormID, int>& a_alive)
		{
			std::unordered_set<std::uint32_t> wanted;
			if (g_eliteLook && g_crownSize > 0.0f) {
				for (const auto& [id, type] : a_alive) {
					auto* actor = type >= 1 && type <= 3 ? RE::TESForm::LookupByID<RE::Actor>(id) : nullptr;
					auto* root = actor ? actor->Get3D() : nullptr;
					if (!root) {
						continue;
					}
					RE::NiPoint3 top = actor->GetPosition();
					top.z += actor->GetHeight() + 0.05f * kUnitsPerMeter;
					// Humains et la plupart des creatures : au-dessus de l'os de la tete.
					for (const char* bone : { "NPC Head [Head]", "NPC Head", "Head", "HEAD" }) {
						if (auto* head = root->GetObjectByName(RE::BSFixedString(bone))) {
							top = head->world.translate;
							top.z += 0.22f * kUnitsPerMeter * std::max(actor->GetScale(), 0.5f);
							break;
						}
					}
					auto [it, added] = g_crownIds.try_emplace(id, kCrownIdBase + static_cast<std::uint32_t>(g_crownIds.size()));
					wanted.insert(it->second);
					Allies::ShowStatic(it->second, std::format("EliteCrown{}_A", type), top, actor->data.angle.z, -g_crownSize);
					g_crownsShown.insert(it->second);
				}
			}
			for (auto it = g_crownsShown.begin(); it != g_crownsShown.end();) {
				if (!wanted.contains(*it)) {
					Allies::HideStatic(*it);
					it = g_crownsShown.erase(it);
				} else {
					++it;
				}
			}
		}

		// La boite de collision de Skyrim : largeur et longueur (bornes de l'objet, a son echelle), et orientation.
		// Sans bornes lisibles : on part de la sphere qui entoure son modele (dragons, mammouths...).
		void MeasureBox(RE::Actor* a_actor, bridge::ActorEntry& a_entry)
		{
			const float height = std::max(a_entry.height, 0.2f);
			float       width = 0.0f;
			float       length = 0.0f;
			const auto  low = a_actor->GetBoundMin();
			const auto  high = a_actor->GetBoundMax();
			const float scale = a_actor->GetScale() > 0.0f ? a_actor->GetScale() : 1.0f;
			width = (high.x - low.x) * scale / kUnitsPerMeter;
			length = (high.y - low.y) * scale / kUnitsPerMeter;
			const bool sane = width > 0.1f && length > 0.1f && width < 40.0f && length < 40.0f;
			if (!sane) {
				width = length = 0.0f;
				if (auto* model = a_actor->Get3D()) {
					const float diameter = model->worldBound.radius * 2.0f / kUnitsPerMeter;
					length = std::sqrt(std::max(diameter * diameter - height * height, 0.0f));
					width = std::min(length, height * 1.2f);
				}
				if (length < 0.2f) {
					width = length = std::max(height * 0.4f, 0.3f);
				}
			}
			a_entry.width = std::clamp(width, 0.2f, 30.0f);
			a_entry.length = std::clamp(length, 0.2f, 30.0f);
			a_entry.yaw = a_actor->data.angle.z;
		}

		void PublishActors(RE::PlayerCharacter* a_player)
		{
			bridge::ActorTable table{};
			table.magic = bridge::kActorMagic;
			table.sequence = ++g_tableSequence;

			const auto  now = Clock::now();
			const auto  origin = a_player->GetPosition();
			auto*       lists = RE::ProcessLists::GetSingleton();
			std::unordered_set<RE::FormID> aliveNow;
			std::unordered_map<RE::FormID, int> eliteAlive;  // 0.71 : elites vivantes proches (aura, couronne)

			if (lists) {
				for (auto& handle : lists->highActorHandles) {
					if (table.count >= bridge::kMaxActors) {
						break;
					}
					auto  actorPtr = handle.get();
					auto* actor = actorPtr.get();
					if (!actor || actor == a_player) {
						continue;
					}

					const auto  position = actor->GetPosition();
					const float east = (position.x - origin.x) / kUnitsPerMeter;
					const float north = (position.y - origin.y) / kUnitsPerMeter;
					const float up = (position.z - origin.z) / kUnitsPerMeter;
					if (east * east + north * north + up * up > kRangeMeters * kRangeMeters) {
						continue;
					}

					const RE::FormID id = actor->GetFormID();
					const bool       dead = actor->IsDead();
					bool             passive = false;
					if (dead) {
						// On ne signale que la mort d'un ennemi qu'on suivait, et pendant 2 s seulement.
						if (g_aliveLastFrame.contains(id) && !g_deadUntil.contains(id)) {
							g_deadUntil[id] = now + kDeadReportTime;
						}
						const auto it = g_deadUntil.find(id);
						if (it == g_deadUntil.end() || now > it->second) {
							continue;
						}
					} else if (actor->IsPlayerTeammate() || actor->IsCommandedActor() || actor->IsAMount()) {
						continue;  // compagnons, invocations du joueur, montures : jamais de double
					} else if (!actor->IsHostileToActor(a_player)) {
						// Pas hostile : un double "paisible" seulement si le .ini le veut (animaux / personnes).
						const bool person = IsPerson(actor);
						if (actor->IsEssential() || (person ? !g_passivePeople : !g_passiveCreatures)) {
							continue;
						}
						passive = true;
					}

					auto& entry = table.actors[table.count++];
					entry.formId = id;
					entry.flags = (dead ? bridge::kActorDead : 0) | (passive ? bridge::kActorPassive : 0);
					entry.flags |= static_cast<std::uint32_t>(EliteOf(actor, dead || passive)) << bridge::kActorEliteShift;
					entry.relX = east;   // RoR2 X = est
					entry.relY = up;     // RoR2 Y = hauteur
					entry.relZ = north;  // RoR2 Z = nord
					entry.height = actor->GetHeight() / kUnitsPerMeter;
					MeasureBox(actor, entry);
					if (auto* values = actor->AsActorValueOwner()) {
						entry.health = values->GetActorValue(RE::ActorValue::kHealth);
						entry.maxHealth = values->GetPermanentActorValue(RE::ActorValue::kHealth);
					}
					if (!dead) {
						aliveNow.insert(id);
						if (const int elite = static_cast<int>((entry.flags >> bridge::kActorEliteShift) & bridge::kActorEliteMask); elite > 0 && !passive) {
							eliteAlive[id] = elite;
						}
					}
				}
			}

			// Oublie les morts signales depuis longtemps.
			std::erase_if(g_deadUntil, [&now](const auto& a_pair) { return now > a_pair.second + 10s; });
			g_aliveLastFrame = std::move(aliveNow);
			UpdateAuras(a_player, eliteAlive);
			UpdateCrowns(eliteAlive);

			Bridge::Get().WriteActors(table);
		}

		// ---------- Coups recus par le joueur ----------

		RE::FormID        g_lastAttacker = 0;  // dernier acteur qui a frappe le joueur
		Clock::time_point g_lastAttackerTime{};
		// 0.71 : le type du dernier coup (0 arme / fleche / poing, 1 magie, 2 poison), pour l'equilibrage.
		std::atomic_int   g_lastHitKind{ 0 };
		Clock::time_point g_lastHitTime{};

		int HitKindOf(RE::FormID a_source)
		{
			auto* form = a_source ? RE::TESForm::LookupByID(a_source) : nullptr;
			if (!form) {
				return 0;
			}
			switch (form->GetFormType()) {
			case RE::FormType::Spell:
			case RE::FormType::Scroll:
			case RE::FormType::Enchantment:
			case RE::FormType::Shout:
				return 1;
			case RE::FormType::AlchemyItem:
			case RE::FormType::Ingredient:
				return 2;
			default:
				return 0;
			}
		}

		// Skyrim nous previent a chaque coup porte : on note qui a frappe le joueur.
		class HitSink final : public RE::BSTEventSink<RE::TESHitEvent>
		{
		public:
			RE::BSEventNotifyControl ProcessEvent(const RE::TESHitEvent* a_event, RE::BSTEventSource<RE::TESHitEvent>*) override
			{
				if (a_event && a_event->target && a_event->cause &&
					a_event->target.get() == RE::PlayerCharacter::GetSingleton()) {
					g_lastAttacker = a_event->cause->GetFormID();
					g_lastAttackerTime = Clock::now();
					g_lastHitKind = HitKindOf(a_event->source);
					g_lastHitTime = g_lastAttackerTime;
				}
				return RE::BSEventNotifyControl::kContinue;
			}
		};

		// Tant que RoR2 est connecte, le joueur Skyrim est "essentiel" : un tres gros coup
		// (geant, dragon) le met a genoux au lieu de le tuer. Seule la mort du survivant le tue.
		void SetPlayerProtected(RE::PlayerCharacter* a_player, bool a_protect)
		{
			static bool applied = false;       // c'est nous qui avons mis le drapeau
			static bool wasEssential = false;  // le joueur etait-il deja essentiel avant nous ?

			auto* base = a_player->GetActorBase();
			if (!base) {
				return;
			}
			auto& flags = base->actorData.actorBaseFlags;
			if (a_protect && !applied) {
				wasEssential = flags.all(RE::ACTOR_BASE_DATA::Flag::kEssential);
				flags.set(RE::ACTOR_BASE_DATA::Flag::kEssential);
				applied = true;
				logger::info("Joueur protege : il ne peut mourir que si le survivant meurt");
			} else if (!a_protect && applied) {
				if (!wasEssential) {
					flags.reset(RE::ACTOR_BASE_DATA::Flag::kEssential);
				}
				applied = false;
				logger::info("Protection du joueur retiree");
			}
		}

		// Bloque les attaques normales de Skyrim (armes, arc, sorts, blocage) quand RoR2 pilote :
		// seules les competences RoR2 frappent. Le reste (parler, ouvrir, menus) n'est pas touche.
		struct AttackBlockHook
		{
			static bool thunk(RE::AttackBlockHandler* a_this, RE::InputEvent* a_event)
			{
				if (Config::Get().BlockSkyrimAttacks() && Movement::IsLinked()) {
					return false;
				}
				return func(a_this, a_event);
			}
			static inline REL::Relocation<decltype(thunk)> func;
		};

		// ---------- La vie maximum partagee ----------
		// Skyrim -> RoR2 : la vie max de Skyrim au-dela du depart (100) donne autant de PV au survivant :
		//   un point de niveau en Sante (+10), un enchantement ou une potion de sante comptent.
		// RoR2 -> Skyrim : la vie max du joueur Skyrim est ajustee pour valoir celle du survivant.
		// On ajuste avec le modificateur "temporaire" de Skyrim, et on retient notre part (g_healthDelta)
		// dans la sauvegarde, pour ne jamais la confondre avec la vraie vie de Skyrim.
		constexpr std::uint32_t kHealthRecord = 'HPMD';
		bool               g_shareHealth = true;
		float              g_startHealth = 100.0f;
		float              g_healthDelta = 0.0f;  // fil principal (et sauvegarde, sur le meme fil)
		std::atomic<float> g_bonusHealth{ 0.0f };
		// Les PV du joueur Skyrim ne sont qu'un reflet de ceux du survivant (40 % dans RoR2 = 40 % dans Skyrim).
		// Sans RoR2 (au chargement d'une partie, avant que RoR2 soit pret...), on lui rend une fois tous ses PV :
		// sinon il gardait le reflet de la derniere fois (10 PV...).
		bool g_healedWithoutRoR2 = false;

		// a_ror2Full <= 0 : RoR2 absent, on rend au joueur sa vraie vie max.
		// On ne se fie plus seulement a notre part retenue (g_healthDelta) : si elle ne correspondait plus a la
		// sauvegarde, l'ecart s'accumulait d'une partie a l'autre et la vie max tombait a 10. On regarde la
		// realite : la vie "permanente" du joueur (base + niveaux + enchantements) et le modificateur
		// "temporaire" reellement en place (le notre + potions).
		void AdjustMaxHealth(RE::PlayerCharacter* a_player, float a_ror2Full)
		{
			auto* values = a_player->AsActorValueOwner();
			if (!values) {
				return;
			}
			const float own = values->GetPermanentActorValue(RE::ActorValue::kHealth);  // sans aucun modificateur temporaire
			const float temporary = a_player->GetActorValueMax(RE::ActorValue::kHealth) - own;
			g_bonusHealth = g_shareHealth ? std::max(0.0f, own - g_startHealth) : 0.0f;

			if (!g_shareHealth || a_ror2Full <= 1.0f) {
				// RoR2 absent : on retire notre part. Un modificateur temporaire NEGATIF ne peut venir que de nous :
				// on l'efface entierement. Positif : on retire au plus ce qu'on avait ajoute (les potions restent).
				float remove = 0.0f;
				if (temporary < -0.5f) {
					remove = temporary;
				} else if (g_healthDelta > 0.5f) {
					remove = std::min(g_healthDelta, temporary);
				}
				if (std::abs(remove) > 0.5f) {
					values->ModActorValue(RE::ACTOR_VALUE_MODIFIER::kTemporary, RE::ActorValue::kHealth, -remove);
					logger::info("Vie max du joueur rendue : {:+.0f} PV (vie max {:.0f})", -remove, a_player->GetActorValueMax(RE::ActorValue::kHealth));
				}
				g_healthDelta = 0.0f;
				return;
			}
			// RoR2 present : la vie max du joueur = celle du survivant, exactement.
			const float wanted = a_ror2Full - own;
			if (std::abs(wanted - temporary) > 0.5f) {
				values->ModActorValue(RE::ACTOR_VALUE_MODIFIER::kTemporary, RE::ActorValue::kHealth, wanted - temporary);
			}
			g_healthDelta = wanted;
		}

		// Chaque image : les PV perdus par le joueur dans Skyrim (epee, fleche, sort, piege, chute)
		// partent vers RoR2, puis on lui rend ses PV. C'est RoR2 qui decide s'il survit.
		// Si le survivant meurt pour de bon dans RoR2, le joueur meurt dans Skyrim.
		void SyncPlayerHealth(RE::PlayerCharacter* a_player)
		{
			static std::uint32_t     lastSequence = 0;
			static Clock::time_point lastChange{};
			static bool              watching = false;  // on surveillait deja les PV a l'image precedente
			static bool              killed = false;    // le joueur a deja ete tue pour cette mort du survivant
			static float             expectedHealth = 0.0f;  // PV qu'on a nous-memes mis a l'image precedente
			static float             fullHealth = 0.0f;      // PV max du joueur dans Skyrim
			static std::uint32_t     hitsSent = 0;

			auto* values = a_player->AsActorValueOwner();
			if (!values || a_player->IsDead()) {
				SetPlayerProtected(a_player, false);
				watching = false;
				return;
			}

			bridge::BridgeData data{};
			const bool         valid = Bridge::Get().Read(data);
			const auto         now = Clock::now();
			if (valid && data.sequence != lastSequence) {
				lastSequence = data.sequence;
				lastChange = now;
			}
			if (!valid || now - lastChange > 500ms) {
				SetPlayerProtected(a_player, false);
				AdjustMaxHealth(a_player, 0.0f);  // RoR2 absent : le joueur retrouve sa vraie vie max
				if (!g_healedWithoutRoR2) {
					g_healedWithoutRoR2 = true;
					values->RestoreActorValue(RE::ActorValue::kHealth, 1.0e6f);
					logger::info("RoR2 absent : le joueur retrouve tous ses PV");
				}
				watching = false;  // RoR2 absent : Skyrim gere ses PV normalement
				return;
			}

			if (data.survivorDead == 1 && Items::WakeOnDeath()) {
				// 0.61 : pas de rechargement. Le joueur reste protege, se reveille dans son dernier lit sans ses objets
				// RoR2 portes, et RoR2 releve le survivant (voir Items::OnSurvivorDeath).
				SetPlayerProtected(a_player, true);
				if (!killed) {
					killed = true;
					logger::info("Le survivant est mort dans RoR2 : reveil sans rechargement");
					Items::OnSurvivorDeath(a_player);
				}
				watching = false;
				return;
			}
			if (data.survivorDead == 1) {
				static Clock::time_point killedAt{};
				// Plus du tout "essentiel" (ni le personnage, ni le joueur lui-meme) : sinon il reste a genoux.
				SetPlayerProtected(a_player, false);
				a_player->GetActorRuntimeData().boolFlags.reset(RE::Actor::BOOL_FLAGS::kEssential);
				if (!killed) {
					killed = true;
					killedAt = now;
					logger::info("Le survivant est mort dans RoR2 : le joueur meurt dans Skyrim");
					values->DamageActorValue(RE::ActorValue::kHealth, values->GetActorValue(RE::ActorValue::kHealth) + 10000.0f);
				} else if (now - killedAt > 400ms) {
					// Toujours debout (ou a genoux) : on le tue directement.
					killedAt = now + 5s;
					logger::info("Le joueur ne meurt pas par les degats : mort forcee");
					a_player->KillImmediate();
				}
				watching = false;
				return;
			}
			killed = false;
			g_healedWithoutRoR2 = false;
			SetPlayerProtected(a_player, true);  // RoR2 est la : le joueur ne meurt que par le survivant

			if (data.inRun != 1) {
				// Menu de RoR2, partie pas encore lancee, chargement d'etape : pas de survivant a refleter.
				AdjustMaxHealth(a_player, 0.0f);  // le joueur retrouve sa vraie vie max
				if (!g_healedWithoutRoR2) {
					g_healedWithoutRoR2 = true;
					values->RestoreActorValue(RE::ActorValue::kHealth, 1.0e6f);
				}
				static Clock::time_point nextLog{};
				if (now >= nextLog) {
					nextLog = now + 5s;
					logger::info("RoR2 pas en partie : PV du joueur {:.0f} / {:.0f} (notre part de la vie max {:+.0f})",
						values->GetActorValue(RE::ActorValue::kHealth), a_player->GetActorValueMax(RE::ActorValue::kHealth), g_healthDelta);
				}
				watching = false;  // menu, chargement d'etape...
				return;
			}

			static bool runLogged = false;
			if (!runLogged) {
				runLogged = true;
				logger::info("Partie RoR2 : PV du survivant {:.0f} / {:.0f}, PV du joueur {:.0f} / {:.0f}", data.health, data.maxHealth,
					values->GetActorValue(RE::ActorValue::kHealth), a_player->GetActorValueMax(RE::ActorValue::kHealth));
			}
			// Meme vie maximum que le survivant (avant de lire les PV, pour ne pas prendre le changement pour un coup).
			const bool maxChanged = std::abs(data.maxHealth - a_player->GetActorValueMax(RE::ActorValue::kHealth)) > 0.5f;
			AdjustMaxHealth(a_player, data.maxHealth);  // met aussi a jour les PV bonus envoyes a RoR2
			if (maxChanged) {
				watching = false;
			}

			const float current = values->GetActorValue(RE::ActorValue::kHealth);
			const bool skyrimFallDamage = Movement::JustLanded() && now - g_lastAttackerTime >= 1s;
			if (watching && current < expectedHealth - 0.01f && skyrimFallDamage) {
				logger::info("Degats de chute de Skyrim ignores ({:.1f} PV) : RoR2 compte deja la chute du survivant", expectedHealth - current);
			} else if (watching && current < expectedHealth - 0.01f) {
				// On envoie la PART des PV perdue (10 PV sur 100 = 0.1) : RoR2 retirera
				// la meme part des PV du survivant, avant son armure et ses objets.
				const float      lost = expectedHealth - current;
				const RE::FormID attacker = now - g_lastAttackerTime < 1s ? g_lastAttacker : 0;
				const float      fraction = fullHealth > 0.0f ? lost / fullHealth : 0.0f;
				// 0.71 : selon le type de coup. Pas de coup juste avant : poison ou degats sur la duree.
				const auto&      config = Config::Get();
				const int        kind = now - g_lastHitTime < 300ms ? g_lastHitKind.load() : 2;
				const float      byKind = kind == 0 ? config.MeleeDamage() : (kind == 1 ? config.MagicDamage() : config.OverTimeDamage());
				float            amount = fraction * config.IncomingDamageMultiplier() * byKind;
				const float      cap = config.HitCap();
				if (cap > 0.0f && amount > cap) {
					amount = cap;  // jamais plus de cette part de la vie en un coup
				}
				Bridge::Get().WriteHit(attacker, amount, bridge::kHitFraction);
				Experience::OnDamageTaken(lost);  // l'armure du survivant monte avec les coups recus

				++hitsSent;
				if (hitsSent <= 10 || hitsSent % 50 == 0) {
					logger::info("Coup recu dans Skyrim ({}) : {:.1f} PV sur {:.0f} ({:.0f} %, attaquant {:08X}), envoye a RoR2 (total {})",
						kind == 0 ? "corps a corps" : (kind == 1 ? "magie" : "poison/duree"), lost, fullHealth, amount * 100.0f, attacker, hitsSent);
				}
			}

			// La barre de vie Skyrim montre les PV du survivant : a 40 % dans RoR2, 40 % dans Skyrim.
			// (Sans danger : le joueur est essentiel, il ne meurt que si le survivant meurt.)
			values->RestoreActorValue(RE::ActorValue::kHealth, 1.0e6f);  // d'abord au maximum...
			const float full = values->GetActorValue(RE::ActorValue::kHealth);
			const float ratio = data.maxHealth > 0.0f ? std::clamp(data.health / data.maxHealth, 0.0f, 1.0f) : 1.0f;
			const float target = std::max(1.0f, full * ratio);
			if (full - target > 0.01f) {
				values->DamageActorValue(RE::ActorValue::kHealth, full - target);  // ...puis a la part du survivant
			}
			fullHealth = full;
			expectedHealth = values->GetActorValue(RE::ActorValue::kHealth);
			watching = true;
		}

		// ---------- Degats envoyes par RoR2 aux ennemis ----------

		// ---------- Ennemis deplaces par RoR2 ----------
		// (tires par le grappin de la Chargeuse, portes par un drone, projetes par un coup...)
		// RoR2 ecrit a chaque image la position voulue de ces ennemis par rapport aux pieds du joueur :
		// on les y place. Quand RoR2 a fini (l'ennemi est retombe), il n'est plus dans la liste
		// et Skyrim le reprend normalement.

		HANDLE              g_pushMapping = nullptr;
		const std::uint8_t* g_pushView = nullptr;
		std::uint32_t       g_pushFrame = 0;
		Clock::time_point   g_pushFrameTime{};
		std::uint32_t       g_pushApplied = 0;
		Clock::time_point   g_lastPushCall{};

		// Ennemis relaches par RoR2 (lache par un drone...) : Skyrim ne les fait pas toujours tomber
		// apres les avoir places a la main, ils restaient alors "debout dans le vide". On les fait
		// tomber nous-memes jusqu'au sol.
		struct Fall
		{
			float             speed = 0.0f;  // m/s vers le bas
			Clock::time_point until{};
		};
		std::unordered_map<RE::FormID, Fall> g_falling;
		std::unordered_set<RE::FormID>       g_pushedLastFrame;

		// Distance (m) entre les pieds de l'acteur et le sol, jusqu'a a_range metres.
		float GroundUnder(RE::Actor* a_actor, float a_range)
		{
			auto* cell = a_actor->GetParentCell();
			auto* world = cell ? cell->GetbhkWorld() : nullptr;
			if (!world) {
				return 0.0f;
			}
			const float     scale = RE::bhkWorld::GetWorldScale();
			const auto      feet = a_actor->GetPosition();
			const float     start = 0.3f;
			RE::bhkPickData pick{};
			pick.rayInput.from = RE::hkVector4(feet.x * scale, feet.y * scale, (feet.z + start * kUnitsPerMeter) * scale, 0.0f);
			pick.rayInput.to = RE::hkVector4(feet.x * scale, feet.y * scale, (feet.z - a_range * kUnitsPerMeter) * scale, 0.0f);
			RE::CFilter filter{};
			a_actor->GetCollisionFilterInfo(filter);       // meme groupe que l'acteur : il s'ignore lui-meme
			filter.SetCollisionLayer(RE::COL_LAYER::kLOS);
			pick.rayInput.filterInfo = filter;
			world->PickObject(pick);
			const float fraction = pick.rayOutput.HasHit() ? std::clamp(pick.rayOutput.hitFraction, 0.0f, 1.0f) : 1.0f;
			return (std::max)(0.0f, fraction * (a_range + start) - start);
		}

		// ---------- Filet de securite : jamais d'ennemi sous le sol ([Sol] FiletEnnemis du .ini) ----------
		bool          g_enemyNet = true;
		std::uint32_t g_clampedPushes = 0;
		std::uint32_t g_rescuedEnemies = 0;

		// Hauteur (unites Skyrim) du premier sol sous le point (x, y, a_fromZ), cherche sur a_range metres.
		// Faux s'il n'y a rien en dessous.
		bool GroundAt(RE::Actor* a_actor, float a_x, float a_y, float a_fromZ, float a_range, float& a_groundZ)
		{
			auto* cell = a_actor->GetParentCell();
			auto* world = cell ? cell->GetbhkWorld() : nullptr;
			if (!world || !(a_range > 0.0f)) {
				return false;
			}
			const float     scale = RE::bhkWorld::GetWorldScale();
			const float     toZ = a_fromZ - a_range * kUnitsPerMeter;
			RE::bhkPickData pick{};
			pick.rayInput.from = RE::hkVector4(a_x * scale, a_y * scale, a_fromZ * scale, 0.0f);
			pick.rayInput.to = RE::hkVector4(a_x * scale, a_y * scale, toZ * scale, 0.0f);
			RE::CFilter filter{};
			a_actor->GetCollisionFilterInfo(filter);       // meme groupe que l'acteur : il s'ignore lui-meme
			filter.SetCollisionLayer(RE::COL_LAYER::kLOS);
			pick.rayInput.filterInfo = filter;
			world->PickObject(pick);
			if (!pick.rayOutput.HasHit()) {
				return false;
			}
			const float fraction = std::clamp(pick.rayOutput.hitFraction, 0.0f, 1.0f);
			a_groundZ = a_fromZ + (toZ - a_fromZ) * fraction;
			return true;
		}

		// Un ennemi deplace par RoR2 n'est jamais pose sous le sol de Skyrim : si la place voulue est sous un sol
		// (le double est descendu plus bas que le vrai sol), on le pose sur ce sol.
		void KeepAboveGround(RE::Actor* a_actor, const RE::NiPoint3& a_current, RE::NiPoint3& a_target)
		{
			if (!g_enemyNet) {
				return;
			}
			const float fromZ = (std::max)(a_current.z, a_target.z) + 0.5f * kUnitsPerMeter;
			const float range = (fromZ - a_target.z) / kUnitsPerMeter + 0.05f;
			float       groundZ = 0.0f;
			if (GroundAt(a_actor, a_target.x, a_target.y, fromZ, range, groundZ) && groundZ > a_target.z + 0.02f * kUnitsPerMeter) {
				if (++g_clampedPushes <= 10 || g_clampedPushes % 200 == 0) {
					logger::info("Filet : ennemi {:08X} pose sur le sol au lieu de {:.2f} m dessous (total {})", a_actor->GetFormID(),
						(groundZ - a_target.z) / kUnitsPerMeter, g_clampedPushes);
				}
				a_target.z = groundZ;
			}
		}

		// Un ennemi qui tombe dans le vide (plus aucun sol sous lui, sous l'endroit ou il etait pose) est ramene
		// a sa derniere place sur le sol. Les ennemis qui volent (dragons...) ne sont pas concernes.
		struct Guard
		{
			RE::NiPoint3      good{};
			bool              known = false;
			Clock::time_point voidSince{};
			bool              inVoid = false;
			std::uint32_t     seenIn = 0;
		};
		std::unordered_map<RE::FormID, Guard> g_guards;
		std::uint32_t                         g_guardPass = 0;
		Clock::time_point                     g_nextGuard{};

		void GuardEnemies(RE::PlayerCharacter* a_player, Clock::time_point a_now, const std::unordered_set<RE::FormID>& a_pushedNow)
		{
			if (!g_enemyNet || a_now < g_nextGuard) {
				return;
			}
			g_nextGuard = a_now + std::chrono::milliseconds(250);
			auto* lists = RE::ProcessLists::GetSingleton();
			if (!lists) {
				return;
			}
			++g_guardPass;
			const auto origin = a_player->GetPosition();
			for (auto& handle : lists->highActorHandles) {
				auto  actorPtr = handle.get();
				auto* actor = actorPtr.get();
				if (!actor || actor == a_player || actor->IsDead() || !actor->Is3DLoaded()) {
					continue;
				}
				// Les dragons volent tres haut, sans sol en dessous : ce n'est pas une chute.
				auto* race = actor->GetRace();
				if (race && race->HasKeywordString("ActorTypeDragon")) {
					continue;
				}
				const auto position = actor->GetPosition();
				if (position.GetDistance(origin) > 80.0f * kUnitsPerMeter) {
					continue;
				}
				auto& guard = g_guards[actor->GetFormID()];
				guard.seenIn = g_guardPass;
				if (a_pushedNow.contains(actor->GetFormID())) {
					guard.inVoid = false;  // RoR2 le deplace : on le laisse faire
					continue;
				}
				const float below = GroundUnder(actor, 200.0f);
				if (below < 0.3f) {
					guard.good = position;  // bien pose sur le sol
					guard.known = true;
					guard.inVoid = false;
					continue;
				}
				if (below < 199.0f || !guard.known) {
					guard.inVoid = false;  // il y a du sol plus bas : une chute normale
					continue;
				}
				// Plus aucun sol sous lui : il est passe a travers.
				if (!guard.inVoid) {
					guard.inVoid = true;
					guard.voidSince = a_now;
					continue;
				}
				if (a_now - guard.voidSince > std::chrono::milliseconds(400) && position.z < guard.good.z - 1.0f * kUnitsPerMeter) {
					actor->SetPosition(guard.good, true);
					guard.inVoid = false;
					if (++g_rescuedEnemies <= 10 || g_rescuedEnemies % 50 == 0) {
						logger::info("Filet : ennemi {:08X} tombe sous le sol, ramene a sa derniere place ({} fois)", actor->GetFormID(), g_rescuedEnemies);
					}
				}
			}
			std::erase_if(g_guards, [](const auto& a_pair) { return a_pair.second.seenIn != g_guardPass; });
		}

		void UpdateFalls(float a_delta, Clock::time_point a_now, const std::unordered_set<RE::FormID>& a_pushedNow)
		{
			for (const auto id : g_pushedLastFrame) {
				if (!a_pushedNow.contains(id)) {
					g_falling[id] = Fall{ 0.0f, a_now + 8s };  // vient d'etre relache
				}
			}
			g_pushedLastFrame = a_pushedNow;
			for (auto it = g_falling.begin(); it != g_falling.end();) {
				auto* actor = RE::TESForm::LookupByID<RE::Actor>(it->first);
				if (a_pushedNow.contains(it->first) || !actor || actor->IsDead() || !actor->Is3DLoaded() || a_now > it->second.until) {
					it = g_falling.erase(it);
					continue;
				}
				const float ground = GroundUnder(actor, 60.0f);
				if (ground < 0.05f) {
					it = g_falling.erase(it);  // pose au sol
					continue;
				}
				it->second.speed = (std::min)(it->second.speed + 9.8f * a_delta, 40.0f);
				const float drop = (std::min)(ground, it->second.speed * a_delta);
				auto        position = actor->GetPosition();
				position.z -= drop * kUnitsPerMeter;
				actor->SetPosition(position, true);
				++it;
			}
		}

		void ApplyPushes(RE::PlayerCharacter* a_player)
		{
			if (!g_pushView) {
				g_pushMapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kPushMapSize, bridge::kPushMapName);
				if (!g_pushMapping) {
					return;
				}
				g_pushView = static_cast<const std::uint8_t*>(::MapViewOfFile(g_pushMapping, FILE_MAP_READ, 0, 0, bridge::kPushMapSize));
				if (!g_pushView) {
					return;
				}
			}
			std::uint32_t magic = 0, count = 0, frame = 0;
			std::memcpy(&magic, g_pushView, 4);
			std::memcpy(&count, g_pushView + 4, 4);
			std::memcpy(&frame, g_pushView + 8, 4);
			if (magic != bridge::kPushMagic) {
				return;
			}
			const auto now = Clock::now();
			if (frame != g_pushFrame) {
				g_pushFrame = frame;
				g_pushFrameTime = now;
			}
			// Le temps depuis la derniere image, pour un deplacement fluide.
			const float delta = std::clamp(std::chrono::duration<float>(now - g_lastPushCall).count(), 0.0f, 0.1f);
			g_lastPushCall = now;
			std::unordered_set<RE::FormID> pushedNow;
			if (now - g_pushFrameTime > 300ms) {
				UpdateFalls(delta, now, pushedNow);  // RoR2 ne tourne plus : liste perimee
				GuardEnemies(a_player, now, pushedNow);
				return;
			}
			const auto origin = a_player->GetPosition();
			count = (std::min)(count, bridge::kPushMax);
			for (std::uint32_t i = 0; i < count; ++i) {
				const std::uint8_t* entry = g_pushView + 16 + i * 16;
				RE::FormID id = 0;
				float      rel[3];
				std::memcpy(&id, entry, 4);
				std::memcpy(rel, entry + 4, 12);
				auto* actor = RE::TESForm::LookupByID<RE::Actor>(id);
				if (!actor || actor == a_player || actor->IsDead() || !actor->Is3DLoaded()) {
					continue;
				}
				const RE::NiPoint3 wanted(origin.x + rel[0] * kUnitsPerMeter, origin.y + rel[1] * kUnitsPerMeter, origin.z + rel[2] * kUnitsPerMeter);
				// Proche (moins de 0,75 m) : il colle exactement au double, sans retard.
				// Un grand ecart d'un coup (le drone l'attrape) : il glisse vite vers sa place au lieu d'apparaitre
				// en l'air. Tres loin (plus de 25 m) : on le pose directement.
				const RE::NiPoint3 current = actor->GetPosition();
				RE::NiPoint3       target = wanted;
				KeepAboveGround(actor, current, target);
				const RE::NiPoint3 gap = target - current;
				const float        distance = gap.Length();
				RE::NiPoint3       next = target;
				if (distance > 0.75f * kUnitsPerMeter && distance < 25.0f * kUnitsPerMeter) {
					// Vitesse de la montee : 4 m/s, plus 1,2 m/s par metre de retard (pour rattraper le drone
					// s'il s'eloigne). Une prise a 5 m de haut se voit monter pendant environ une demi-seconde.
					const float speed = (4.0f + 1.2f * distance / kUnitsPerMeter) * kUnitsPerMeter;
					const float step = (std::min)(distance, speed * delta);
					next = current + gap * (step / distance);
				}
				actor->SetPosition(next, true);
				pushedNow.insert(id);
				if (++g_pushApplied <= 5 || g_pushApplied % 500 == 0) {
					logger::info("Ennemi {:08X} deplace par RoR2 (total {})", id, g_pushApplied);
				}
			}
			UpdateFalls(delta, now, pushedNow);
			GuardEnemies(a_player, now, pushedNow);
		}

		void ApplyDamage()
		{
			const auto* ring = Bridge::Get().DamageQueue();
			if (!ring || ring->magic != bridge::kDamageMagic) {
				return;
			}

			// Combien de degats RoR2 a ecrits au total (lu une seule fois, puis on lit les evenements).
			const std::uint32_t written = *reinterpret_cast<const volatile std::uint32_t*>(&ring->writeCount);
			std::atomic_thread_fence(std::memory_order_acquire);

			if (!g_damageStarted) {
				g_damageRead = written;  // on ignore les degats d'avant notre arrivee
				g_damageStarted = true;
				return;
			}
			if (written - g_damageRead > bridge::kDamageSlots) {
				g_damageRead = written - bridge::kDamageSlots;  // trop de retard : on garde les plus recents
			}

			const float multiplier = Config::Get().DamageMultiplier();
			while (g_damageRead != written) {
				const bridge::DamageEvent event = ring->events[g_damageRead % bridge::kDamageSlots];
				++g_damageRead;

				const int code = static_cast<int>((event.reserved >> 16) & 0xFF);  // competence d'un objet / equipement
				if (event.formId == 0) {
					// Utilisation d'un equipement : seulement de l'experience.
					Experience::OnSkillUse(code, event.amount);
					continue;
				}
				auto* actor = RE::TESForm::LookupByID<RE::Actor>(event.formId);
				if (!actor || actor->IsDead() || !(event.amount > 0.0f)) {
					continue;
				}
				auto* values = actor->AsActorValueOwner();
				if (!values) {
					continue;
				}
				// Les atouts Skyrim du joueur comptent aussi (Archerie +20 %... voir Experience::PerkMultiplier).
				// La case de l'attaque (M1, M2, Maj, R, allies...) et sa variante, envoyees par RoR2.
				const int   slot = (event.reserved & 0xFF) != 0 ? static_cast<int>(event.reserved & 0xFF) - 1 : 5;
				const int   variant = static_cast<int>((event.reserved >> 8) & 0xFF);
				// 0.70 (coop) : le coup d'un coequipier : memes degats, mais ni nos atouts ni notre experience.
				const bool  remote = (event.flags & bridge::kDamageRemote) != 0;
				const float damage = event.amount * multiplier * (remote ? 1.0f : Experience::PerkMultiplier(actor, slot, variant, code));
				const float health = values->GetActorValue(RE::ActorValue::kHealth);
				auto*       player = RE::PlayerCharacter::GetSingleton();
				if (player && damage >= health && !actor->IsEssential()) {  // un essentiel tombe a genoux, comme dans Skyrim
					// Coup fatal : on tue l'ennemi AU NOM DU JOUEUR. Sans ca, Skyrim ne savait pas qui l'avait
					// tue (quetes "tuer X", statistiques, ames de dragon, primes...).
					actor->KillImpl(player, damage, true, false);
					logger::info("Ennemi {:08X} tue par le joueur (coup RoR2 de {:.1f})", event.formId, damage);
				} else {
					values->DamageActorValue(RE::ActorValue::kHealth, damage);
				}
				// Les competences Skyrim montent aussi avec les coups RoR2 (la Chasseuse fait monter l'Archerie...).
				if (!remote) {
					Experience::OnDamageDealt(std::min(damage, std::max(health, 0.0f)), slot, variant, code);
				}
				// Feu, gel, foudre et atouts de fin d'arbre de Destruction (voir [EffetsDestruction]).
				if (!remote && !actor->IsDead()) {
					Experience::ApplyDestruction(actor, damage, slot, variant, code);
				}

				++g_damageApplied;
				if (g_damageApplied <= 10 || g_damageApplied % 100 == 0) {
					logger::info("Degats RoR2 appliques : {:.1f} sur {:08X} (total {})",
						event.amount * multiplier, event.formId, g_damageApplied);
				}
			}
		}
	}

	void Install()
	{
		REL::Relocation<std::uintptr_t> attackVtbl{ RE::VTABLE_AttackBlockHandler[0] };
		AttackBlockHook::func = attackVtbl.write_vfunc(0x1, AttackBlockHook::thunk);
		logger::info("Crochet sur les attaques de Skyrim installe");

		LoadSettings();
		static HitSink sink;
		if (auto* events = RE::ScriptEventSourceHolder::GetSingleton()) {
			events->AddEventSink<RE::TESHitEvent>(&sink);
			logger::info("Ecoute des coups recus installee");
		}
	}

	void LoadSettings()
	{
		const auto ini = std::filesystem::absolute("Data/SKSE/Plugins/SkyrimRoR2.ini").string();
		g_shareHealth = ::GetPrivateProfileIntA("PV", "Partager", 1, ini.c_str()) != 0;
		char text[32]{};
		::GetPrivateProfileStringA("PV", "Depart", "100", text, sizeof(text), ini.c_str());
		g_startHealth = static_cast<float>(std::atof(text));
		logger::info("PV partages = {}, vie de depart {}", g_shareHealth ? "oui" : "non", g_startHealth);
		g_passiveCreatures = ::GetPrivateProfileIntA("Combat", "CreaturesPaisibles", 1, ini.c_str()) != 0;
		g_passivePeople = ::GetPrivateProfileIntA("Combat", "PersonnesPaisibles", 1, ini.c_str()) != 0;
		logger::info("Doubles paisibles : creatures = {}, personnes = {}", g_passiveCreatures ? "oui" : "non", g_passivePeople ? "oui" : "non");
		g_enemyNet = ::GetPrivateProfileIntA("Sol", "FiletEnnemis", 1, ini.c_str()) != 0;
		logger::info("Filet de securite des ennemis (jamais sous le sol) : {}", g_enemyNet ? "oui" : "non");
		// 0.71 : l'apparence des elites.
		g_eliteLook = ::GetPrivateProfileIntA("Elites", "Apparence", 1, ini.c_str()) != 0;
		::GetPrivateProfileStringA("Elites", "TailleCouronne", "0.35", text, sizeof(text), ini.c_str());
		for (char& c : text) {
			c = c == ',' ? '.' : c;
		}
		g_crownSize = std::clamp(static_cast<float>(std::atof(text)), 0.0f, 3.0f);
		logger::info("Elites : aura et couronne = {}, couronne de {:.2f} m", g_eliteLook ? "oui" : "non", g_crownSize);
	}

	int EliteType(RE::FormID a_actor)
	{
		const auto it = g_elites.find(a_actor);
		return it != g_elites.end() ? it->second : 0;
	}

	void FillCommand(bridge::SkyrimCommand& a_command)
	{
		a_command.bonusHealth = g_bonusHealth;
	}

	void Save(SKSE::SerializationInterface* a_intfc)
	{
		if (a_intfc->OpenRecord(kHealthRecord, 1)) {
			a_intfc->WriteRecordData(&g_healthDelta, sizeof(g_healthDelta));
		}
	}

	bool Load(SKSE::SerializationInterface* a_intfc, std::uint32_t a_type, std::uint32_t a_version)
	{
		if (a_type != kHealthRecord) {
			return false;
		}
		float delta = 0.0f;
		if (a_version == 1 && a_intfc->ReadRecordData(&delta, sizeof(delta)) == sizeof(delta) && std::isfinite(delta)) {
			g_healthDelta = delta;  // la sauvegarde Skyrim contient deja ce modificateur : on reprend notre part
		}
		return true;
	}

	void Revert()
	{
		g_healthDelta = 0.0f;
		g_healedWithoutRoR2 = false;
	}

	namespace
	{
		// ---------- Invisibilite (fumigene du Bandit...) ----------
		// Tant que le survivant est invisible dans RoR2, le joueur l'est aussi dans Skyrim : on lui lance le sort
		// Invisibilite du jeu (les ennemis le perdent de vue), et on le retire quand l'invisibilite de RoR2 finit.
		// En secours (sort introuvable), les ennemis proches arretent simplement le combat.
		constexpr RE::FormID kInvisibilitySpell = 0x00027EB6;  // sort "Invisibilite" de Skyrim.esm

		void SyncCloak(RE::PlayerCharacter* a_player)
		{
			static bool           searched = false;
			static RE::SpellItem* spell = nullptr;
			static bool           active = false;
			static Clock::time_point nextCalm{};

			bridge::BridgeData data{};
			const bool cloaked = Bridge::Get().Read(data) && data.inRun == 1 && data.cloaked == 1;
			if (!searched) {
				searched = true;
				spell = RE::TESForm::LookupByID<RE::SpellItem>(kInvisibilitySpell);
				const char* name = spell ? spell->GetName() : nullptr;
				if (!spell || !name || (std::string(name).find("nvisib") == std::string::npos)) {
					logger::warn("Invisibilite : sort {:08X} introuvable ou inattendu ({}), les ennemis arreteront juste le combat",
						kInvisibilitySpell, name ? name : "aucun");
					spell = nullptr;
				}
			}
			if (cloaked && !active) {
				active = true;
				if (spell) {
					if (auto* caster = a_player->GetMagicCaster(RE::MagicSystem::CastingSource::kInstant)) {
						caster->CastSpellImmediate(spell, false, a_player, 1.0f, false, 0.0f, a_player);
					}
				}
				logger::info("Invisibilite de RoR2 : le joueur devient invisible dans Skyrim");
			} else if (!cloaked && active) {
				active = false;
				if (spell) {
					if (auto* target = a_player->AsMagicTarget()) {
						if (auto* effects = target->GetActiveEffectList()) {
							for (auto* effect : *effects) {
								if (effect && effect->spell == spell) {
									effect->Dispel(true);
								}
							}
						}
					}
				}
				logger::info("Invisibilite de RoR2 terminee");
			}
			// Secours : les ennemis proches oublient le combat tant que dure l'invisibilite.
			if (active && !spell && Clock::now() >= nextCalm) {
				nextCalm = Clock::now() + std::chrono::milliseconds(500);
				if (auto* lists = RE::ProcessLists::GetSingleton()) {
					for (auto& handle : lists->highActorHandles) {
						auto actorPtr = handle.get();
						auto* actor = actorPtr.get();
						if (actor && actor != a_player && !actor->IsDead() && actor->IsHostileToActor(a_player)) {
							actor->StopCombat();
						}
					}
				}
			}
		}
	}

	void PerFrame(RE::PlayerCharacter* a_player)
	{
		PublishActors(a_player);
		ApplyDamage();
		ApplyPushes(a_player);
		SyncPlayerHealth(a_player);
		SyncCloak(a_player);
	}
}
