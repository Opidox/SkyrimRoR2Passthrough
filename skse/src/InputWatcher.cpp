#include "InputWatcher.h"

namespace skyrimror2
{
	InputWatcher& InputWatcher::Get()
	{
		static InputWatcher watcher;
		return watcher;
	}

	void InputWatcher::Install()
	{
		if (auto* devices = RE::BSInputDeviceManager::GetSingleton()) {
			devices->AddEventSink(this);
			logger::info("Ecoute des touches de Skyrim installee");
		} else {
			logger::error("Impossible d'ecouter les touches de Skyrim");
		}
	}

	bool InputWatcher::IsDown(int a_virtualKey) const
	{
		// Boutons de la souris
		switch (a_virtualKey) {
		case VK_LBUTTON:
			return mouse_[0];
		case VK_RBUTTON:
			return mouse_[1];
		case VK_MBUTTON:
			return mouse_[2];
		case VK_XBUTTON1:
			return mouse_[3];
		case VK_XBUTTON2:
			return mouse_[4];
		default:
			break;
		}

		// Clavier : Skyrim numerote les touches par leur position (code "scan").
		UINT scan = ::MapVirtualKeyW(static_cast<UINT>(a_virtualKey), MAPVK_VK_TO_VSC_EX);
		if ((scan & 0xFF00) == 0xE000) {
			scan = (scan & 0xFF) | 0x80;  // touches "etendues" (Ctrl droit, Alt droit...)
		}
		return scan < keyboard_.size() && keyboard_[scan];
	}

	void InputWatcher::Clear()
	{
		for (auto& key : keyboard_) {
			key = false;
		}
		for (auto& button : mouse_) {
			button = false;
		}
	}

	void InputWatcher::Reconcile()
	{
		for (std::size_t scan = 1; scan < keyboard_.size(); ++scan) {
			if (!keyboard_[scan]) {
				stale_[scan] = 0;
				continue;
			}
			const UINT full = scan >= 0x80 ? (0xE000u | static_cast<UINT>(scan & 0x7F)) : static_cast<UINT>(scan);
			const UINT vk = ::MapVirtualKeyW(full, MAPVK_VSC_TO_VK_EX);
			if (vk == 0 || (::GetAsyncKeyState(static_cast<int>(vk)) & 0x8000) != 0) {
				stale_[scan] = 0;
				continue;
			}
			if (++stale_[scan] >= 12) {  // ~0,2 s
				keyboard_[scan] = false;
				stale_[scan] = 0;
			}
		}
	}

	RE::BSEventNotifyControl InputWatcher::ProcessEvent(RE::InputEvent* const* a_event,
		RE::BSTEventSource<RE::InputEvent*>*)
	{
		if (!a_event) {
			return RE::BSEventNotifyControl::kContinue;
		}

		for (auto* event = *a_event; event; event = event->next) {
			if (event->GetEventType() != RE::INPUT_EVENT_TYPE::kButton) {
				continue;
			}
			auto*      button = event->AsButtonEvent();
			const bool pressed = button->IsDown();
			const bool released = button->IsUp();
			if (!pressed && !released) {
				continue;  // touche tenue : rien de nouveau
			}

			const auto code = button->GetIDCode();
			if (button->GetDevice() == RE::INPUT_DEVICE::kKeyboard) {
				if (code < keyboard_.size()) {
					keyboard_[code] = pressed;
				}
			} else if (button->GetDevice() == RE::INPUT_DEVICE::kMouse) {
				if (code < mouse_.size()) {
					mouse_[code] = pressed;
				}
			}
		}
		return RE::BSEventNotifyControl::kContinue;
	}
}
