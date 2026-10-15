#pragma once

namespace skyrimror2
{
	// Ecoute les touches et les boutons de la souris DIRECTEMENT dans le moteur de Skyrim
	// (comme SkyCraft). Plus fiable que de demander a Windows, surtout pour la souris,
	// que Skyrim garde pour lui.
	class InputWatcher final : public RE::BSTEventSink<RE::InputEvent*>
	{
	public:
		static InputWatcher& Get();

		// Branche l'ecoute sur Skyrim (a appeler une fois, au chargement des donnees).
		void Install();

		// Vrai si la touche (code Windows, voir Config) est enfoncee selon Skyrim.
		bool IsDown(int a_virtualKey) const;

		// Oublie tout (quand Skyrim perd le focus, il ne nous dit pas qu'on a relache).
		void Clear();

		// 0.72 : une touche du clavier que Skyrim croit enfoncee mais que Windows voit relachee depuis un moment
		// (relachee pendant un menu, une fenetre, un chargement...) est oubliee. A appeler ~60 fois par seconde.
		void Reconcile();

		RE::BSEventNotifyControl ProcessEvent(RE::InputEvent* const* a_event,
			RE::BSTEventSource<RE::InputEvent*>* a_source) override;

	private:
		InputWatcher() = default;

		std::array<std::atomic_bool, 256> keyboard_{};  // par code de touche Skyrim
		std::array<std::atomic_bool, 8>   mouse_{};     // 0 gauche, 1 droit, 2 milieu, 3-4 lateraux
		std::array<int, 256>              stale_{};     // images de suite ou Windows la voit relachee
	};
}
