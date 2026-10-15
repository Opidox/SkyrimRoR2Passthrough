#pragma once

namespace skyrimror2::Launcher
{
	// Si [Lancement] LancerRoR2 = 1 et que RoR2 ne tourne pas encore, demarre RoR2
	// avec le profil r2modman (en arriere-plan, sans bloquer Skyrim).
	void StartRoR2IfNeeded();

	// Cache (ou remontre) la fenetre de RoR2 selon [Lancement] CacherFenetreRoR2. Appele regulierement.
	void UpdateRoR2Window();
}
