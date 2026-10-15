#pragma once

namespace skyrimror2
{
	// Les actions qu'on peut associer a une touche.
	enum class Action : std::size_t
	{
		kForward,
		kBack,
		kLeft,
		kRight,
		kJump,
		kSprint,
		kSkill1,
		kSkill2,
		kSkill3,
		kSkill4,
		kNextSurvivor,
		kPreviousSurvivor,
		kEquipment,
		kUse,
		kItems,
		kJournal,  // 0.72 : le journal des objets RoR2
		kCount
	};

	// Reglages lus dans Data\SKSE\Plugins\SkyrimRoR2.ini
	class Config
	{
	public:
		static Config& Get();

		// (Re)lit le fichier. Appele au demarrage et a chaque chargement de sauvegarde.
		void Load();

		// Code Windows de la touche associee a une action (0 = aucune).
		int Key(Action a_action) const;

		// [Mouvement] Suivre : le joueur Skyrim prend la vitesse du survivant RoR2.
		bool FollowEnabled() const { return followEnabled_; }

		// [Mouvement] Vitesse : multiplie la vitesse du survivant (1.0 = vitesse RoR2).
		float SpeedMultiplier() const { return speedMultiplier_; }

		// [Mouvement] EnduranceSprint : le sprint consomme l'endurance de Skyrim.
		bool SprintUsesStamina() const { return sprintUsesStamina_; }

		// [Mouvement] CoutSprint : multiplie l'endurance consommee par seconde de sprint (1.0 = 10 par seconde).
		float SprintStaminaCost() const { return sprintStaminaCost_; }

		// [Combat] DegatsDonnes (0.71, avant : Degats) : multiplie les degats RoR2 appliques aux ennemis Skyrim.
		float DamageMultiplier() const { return damageMultiplier_; }

		// [Combat] DegatsRecus : multiplie les degats Skyrim subis par le survivant RoR2.
		float IncomingDamageMultiplier() const { return incomingDamageMultiplier_; }

		// 0.71 : [Combat] DegatsCorpsACorps / DegatsMagie / DegatsDuree, et PlafondCoup (part de la vie, 0 = sans plafond).
		float MeleeDamage() const { return meleeDamage_; }
		float MagicDamage() const { return magicDamage_; }
		float OverTimeDamage() const { return overTimeDamage_; }
		float HitCap() const { return hitCap_; }

		// [Combat] BloquerAttaquesSkyrim : seules les competences RoR2 attaquent quand RoR2 pilote.
		bool BlockSkyrimAttacks() const { return blockSkyrimAttacks_; }

		// [Lancement] LancerRoR2 : Skyrim demarre RoR2 tout seul (avec le profil r2modman).
		bool AutoLaunch() const { return autoLaunch_; }

		// [Lancement] PartieAuto : RoR2 passe les menus et lance une partie tout seul.
		bool AutoRun() const { return autoRun_; }

		// [Lancement] Survivant : le survivant choisi pour la partie automatique.
		std::string Survivor() const;

		// [Lancement] ProfilR2modman : dossier du profil r2modman (vide = profil Passthrough par defaut).
		std::string ProfileDir() const;

		// [Lancement] FenetreLargeur / FenetreHauteur : taille de la fenetre RoR2 (0 = ne pas changer).
		int WindowWidth() const { return windowWidth_; }
		int WindowHeight() const { return windowHeight_; }

		// [Lancement] CacherFenetreRoR2 : la fenetre RoR2 est cachee (tout se voit dans Skyrim).
		bool HideRoR2Window() const { return hideRoR2Window_; }

		// [HUD] Afficher : dessine le HUD de RoR2 dans Skyrim.
		bool HudEnabled() const { return hudEnabled_; }

		// [HUD] Taille : agrandit ou reduit le HUD (1.0 = normal).
		float HudScale() const { return hudScale_; }

		// [HUD] CacherBarreVieSkyrim : cache la barre de vie de Skyrim (remplacee par celle de RoR2).
		bool HideSkyrimHealth() const { return hideSkyrimHealth_; }

		// [Mains] Afficher : montre les bras du survivant (filmes par RoR2) en premiere personne.
		bool ViewmodelEnabled() const { return viewmodelEnabled_; }

		// [Mains] Taille : largeur de l'image des mains par rapport a l'ecran (1.0 = tout l'ecran).
		float ViewmodelScale() const { return viewmodelScale_; }

		// [Mains] Descendre : descend l'image des mains d'une part de la hauteur de l'ecran (0.15 = 15 %).
		float ViewmodelOffset() const { return viewmodelOffset_; }

		// [Mains] CacherBrasSkyrim : cache les bras et l'arme de Skyrim pendant ce temps.
		bool HideSkyrimArms() const { return hideSkyrimArms_; }

		// Nom de la touche tel qu'ecrit dans le .ini ("ClicGauche", "R"...), pour l'afficher dans le HUD.
		std::string KeyName(Action a_action) const;

	private:
		Config();

		std::array<std::atomic_int, static_cast<std::size_t>(Action::kCount)> keys_{};
		std::atomic_bool  followEnabled_{ true };
		std::atomic<float> speedMultiplier_{ 1.0f };
		std::atomic_bool   sprintUsesStamina_{ true };
		std::atomic<float> sprintStaminaCost_{ 1.0f };
		std::atomic<float> damageMultiplier_{ 1.0f };
		std::atomic<float> incomingDamageMultiplier_{ 1.0f };
		std::atomic<float> meleeDamage_{ 2.0f };
		std::atomic<float> magicDamage_{ 0.5f };
		std::atomic<float> overTimeDamage_{ 0.4f };
		std::atomic<float> hitCap_{ 0.4f };
		std::atomic_bool   blockSkyrimAttacks_{ true };
		std::atomic_bool   autoLaunch_{ true };
		std::atomic_bool   autoRun_{ true };
		std::atomic_int    windowWidth_{ 960 };
		std::atomic_int    windowHeight_{ 540 };
		std::atomic_bool   hideRoR2Window_{ true };
		std::atomic_bool   hudEnabled_{ true };
		std::atomic<float> hudScale_{ 1.0f };
		std::atomic_bool   hideSkyrimHealth_{ true };
		std::atomic_bool   viewmodelEnabled_{ true };
		std::atomic<float> viewmodelScale_{ 1.0f };
		std::atomic<float> viewmodelOffset_{ 0.0f };
		std::atomic_bool   hideSkyrimArms_{ true };

		mutable std::mutex textLock_;
		std::string        survivor_{ "Commando" };
		std::string        profileDir_;
		std::array<std::string, static_cast<std::size_t>(Action::kCount)> keyNames_{};
	};
}
