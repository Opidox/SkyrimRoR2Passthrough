#include "Hud.h"
#include "ThirdPerson.h"

#include "Bridge.h"
#include "Config.h"
#include "Coop.h"
#include "Altars.h"
#include "InputWatcher.h"
#include "Loot.h"
#include "Movement.h"

#include <d3d11.h>
#include <objbase.h>
#include <dxgi.h>

#include <WICTextureLoader.h>  // DirectXTK : lit les icones PNG
#include <fstream>
#include <imgui.h>
#include <imgui_impl_dx11.h>

// Le HUD de RoR2, dessine par-dessus l'image de Skyrim.
//
// RoR2 ecrit dans le pont (zone 7) la vie, le niveau, l'or, les competences et les objets,
// et exporte leurs icones en PNG. Ici, a chaque image, juste avant que Skyrim n'affiche
// son image, on dessine tout ca avec ImGui (une petite bibliotheque de dessin tres utilisee).
//
//   en haut a gauche : l'or, puis la ligne d'objets
//   en bas a gauche  : niveau, barre de vie (vert), bouclier (bleu), barriere (jaune), experience
//   en bas a droite  : les 4 competences (avec leur recharge) et l'equipement

namespace skyrimror2::Hud
{
	namespace
	{
		using Clock = std::chrono::steady_clock;

		ID3D11Device*        g_device = nullptr;
		ID3D11DeviceContext* g_context = nullptr;
		IDXGISwapChain*      g_swapChain = nullptr;
		bool                 g_ready = false;
		ImFont*              g_font = nullptr;
		Clock::time_point    g_lastFrame = Clock::now();

		// ---------- Les icones (fichiers PNG exportes par RoR2) ----------

		struct Icon
		{
			ID3D11ShaderResourceView* view = nullptr;
			Clock::time_point         nextTry{};
		};
		std::unordered_map<std::string, Icon> g_icons;

		std::filesystem::path IconFolder()
		{
			static const std::filesystem::path folder = []() {
				wchar_t local[MAX_PATH]{};
				if (::GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH) == 0) {
					return std::filesystem::path{};
				}
				return std::filesystem::path(local) / L"SkyrimRoR2" / L"icones";
			}();
			return folder;
		}

		// Donne l'image d'une icone, ou nullptr si elle n'est pas (encore) disponible.
		ID3D11ShaderResourceView* GetIcon(const char* a_name)
		{
			if (!a_name || !a_name[0]) {
				return nullptr;
			}
			Icon& icon = g_icons[a_name];
			if (icon.view) {
				return icon.view;
			}
			const auto now = Clock::now();
			if (now < icon.nextTry) {
				return nullptr;
			}
			icon.nextTry = now + 1s;  // fichier absent : RoR2 l'ecrira bientot, on reessaie dans 1 s

			const auto path = IconFolder() / (std::string(a_name) + ".png");
			std::error_code error;
			if (!std::filesystem::exists(path, error)) {
				return nullptr;
			}
			ID3D11ShaderResourceView* view = nullptr;
			if (SUCCEEDED(DirectX::CreateWICTextureFromFile(g_device, path.c_str(), nullptr, &view))) {
				icon.view = view;
			}
			return icon.view;
		}

		// ---------- Petits outils de dessin ----------

		ImU32 Color(int r, int g, int b, int a = 255) { return IM_COL32(r, g, b, a); }

		void Text(ImDrawList* a_draw, float a_size, ImVec2 a_pos, ImU32 a_color, const std::string& a_text, bool a_centered = false)
		{
			ImFont* font = g_font ? g_font : ImGui::GetFont();
			if (a_centered) {
				const ImVec2 size = font->CalcTextSizeA(a_size, FLT_MAX, 0.0f, a_text.c_str());
				a_pos.x -= size.x * 0.5f;
				a_pos.y -= size.y * 0.5f;
			}
			a_draw->AddText(font, a_size, ImVec2(a_pos.x + 1.5f, a_pos.y + 1.5f), Color(0, 0, 0, 200), a_text.c_str());  // ombre
			a_draw->AddText(font, a_size, a_pos, a_color, a_text.c_str());
		}

		void Image(ImDrawList* a_draw, ID3D11ShaderResourceView* a_view, ImVec2 a_min, ImVec2 a_max)
		{
			if (a_view) {
				a_draw->AddImage((ImTextureID)(std::uintptr_t)a_view, a_min, a_max);
			}
		}

		// Une case de competence : icone, voile sombre pendant la recharge, secondes restantes, charges, touche.
		void DrawSlot(ImDrawList* a_draw, const bridge::HudSlot& a_slot, ImVec2 a_min, float a_size, float a_scale, const std::string& a_key)
		{
			const ImVec2 max(a_min.x + a_size, a_min.y + a_size);
			a_draw->AddRectFilled(a_min, max, Color(20, 22, 28, 200), 4.0f * a_scale);

			char icon[bridge::kIconNameSize + 1]{};
			std::memcpy(icon, a_slot.icon, bridge::kIconNameSize);
			if (icon[0]) {
				Image(a_draw, GetIcon(icon), a_min, max);

				const bool waiting = a_slot.stock <= 0 && a_slot.cooldown > 0.05f;
				if (waiting) {
					const float part = a_slot.cooldownTotal > 0.0f ? std::clamp(a_slot.cooldown / a_slot.cooldownTotal, 0.0f, 1.0f) : 1.0f;
					a_draw->AddRectFilled(a_min, ImVec2(max.x, a_min.y + a_size * part), Color(0, 0, 0, 170));
					const float seconds = a_slot.cooldown;
					const std::string label = seconds >= 1.0f ? std::format("{}", static_cast<int>(std::ceil(seconds))) : std::format("{:.1f}", seconds);
					Text(a_draw, a_size * 0.42f, ImVec2(a_min.x + a_size * 0.5f, a_min.y + a_size * 0.5f), Color(255, 255, 255), label, true);
				}
				if (a_slot.maxStock > 1) {
					Text(a_draw, a_size * 0.30f, ImVec2(max.x - a_size * 0.30f, max.y - a_size * 0.36f), Color(255, 255, 255), std::format("{}", a_slot.stock));
				}
			}
			a_draw->AddRect(a_min, max, Color(200, 200, 210, 160), 4.0f * a_scale, 0, 1.5f * a_scale);

			if (!a_key.empty()) {
				Text(a_draw, 15.0f * a_scale, ImVec2(a_min.x + a_size * 0.5f, max.y + 11.0f * a_scale), Color(220, 220, 230), a_key, true);
			}
		}

		// Raccourcit les noms de touches du .ini pour l'affichage.
		std::string ShortKey(std::string a_name)
		{
			static const std::pair<const char*, const char*> kShort[]{
				{ "ClicGauche", "M1" }, { "ClicDroit", "M2" }, { "ClicMilieu", "M3" },
				{ "MajGauche", "Maj" }, { "MajDroit", "Maj" }, { "CtrlGauche", "Ctrl" }, { "CtrlDroit", "Ctrl" },
				{ "AltGauche", "Alt" }, { "AltDroit", "Alt" }, { "Espace", "Esp" },
			};
			for (const auto& [from, to] : kShort) {
				if (_stricmp(a_name.c_str(), from) == 0) {
					return to;
				}
			}
			return a_name;
		}

		// ---------- Le HUD complet ----------

		bridge::HudData   g_hud{};
		std::uint32_t     g_lastSequence = 0;
		Clock::time_point g_lastChange{};

		bool SkyrimMenuOpen();  // plus bas

		// ---------- Les mains du survivant (image filmee par RoR2) ----------

		HANDLE                    g_viewMapping = nullptr;
		const std::uint8_t*       g_viewData = nullptr;
		ID3D11Texture2D*          g_viewTexture = nullptr;
		ID3D11ShaderResourceView* g_viewSrv = nullptr;
		std::uint32_t             g_viewWidth = 0;
		std::uint32_t             g_viewHeight = 0;
		std::uint32_t             g_viewSequence = 0;
		Clock::time_point         g_viewLastFrame{};
		std::atomic_bool          g_viewShown{ false };  // lu par le fil principal pour cacher les bras de Skyrim

		bool OpenViewBridge()
		{
			if (g_viewData) {
				return true;
			}
			g_viewMapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kViewMapSize, bridge::kViewMapName);
			if (!g_viewMapping) {
				return false;
			}
			g_viewData = static_cast<const std::uint8_t*>(::MapViewOfFile(g_viewMapping, FILE_MAP_READ, 0, 0, bridge::kViewMapSize));
			return g_viewData != nullptr;
		}

		// Recopie la derniere image de RoR2 dans une texture DirectX. Renvoie false s'il n'y a rien a afficher.
		bool UpdateViewTexture()
		{
			if (!OpenViewBridge()) {
				return false;
			}
			bridge::ViewHeader header{};
			std::memcpy(&header, g_viewData, sizeof(header));
			if (header.magic != bridge::kViewMagic || header.active != 1 || header.width == 0 || header.height == 0 ||
				header.width > bridge::kViewMaxWidth || header.height > bridge::kViewMaxHeight || header.ready > 1) {
				return false;
			}

			const auto now = Clock::now();
			if (header.sequence == g_viewSequence) {
				return g_viewSrv && now - g_viewLastFrame < 500ms;  // pas de nouvelle image : on garde l'ancienne un moment
			}

			if (!g_viewTexture || header.width != g_viewWidth || header.height != g_viewHeight) {
				if (g_viewSrv) { g_viewSrv->Release(); g_viewSrv = nullptr; }
				if (g_viewTexture) { g_viewTexture->Release(); g_viewTexture = nullptr; }

				D3D11_TEXTURE2D_DESC desc{};
				desc.Width = header.width;
				desc.Height = header.height;
				desc.MipLevels = 1;
				desc.ArraySize = 1;
				desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
				desc.SampleDesc.Count = 1;
				desc.Usage = D3D11_USAGE_DYNAMIC;
				desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
				desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
				if (FAILED(g_device->CreateTexture2D(&desc, nullptr, &g_viewTexture)) ||
					FAILED(g_device->CreateShaderResourceView(g_viewTexture, nullptr, &g_viewSrv))) {
					logger::error("Mains : texture impossible a creer");
					return false;
				}
				g_viewWidth = header.width;
				g_viewHeight = header.height;
				logger::info("Mains : images de RoR2 recues ({} x {})", header.width, header.height);
			}

			D3D11_MAPPED_SUBRESOURCE mapped{};
			if (FAILED(g_context->Map(g_viewTexture, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
				return false;
			}
			const std::uint8_t* source = g_viewData + bridge::kViewHeaderSize + header.ready * bridge::kViewFrameSize;
			const std::size_t   row = static_cast<std::size_t>(header.width) * 4;
			for (std::uint32_t y = 0; y < header.height; ++y) {
				std::memcpy(static_cast<std::uint8_t*>(mapped.pData) + y * mapped.RowPitch, source + y * row, row);
			}
			g_context->Unmap(g_viewTexture, 0);

			g_viewSequence = header.sequence;
			g_viewLastFrame = now;
			return true;
		}

		bool InFirstPerson()
		{
			auto* camera = RE::PlayerCamera::GetSingleton();
			return camera && camera->IsInFirstPerson();
		}

		// Dessine les mains sur tout l'ecran (l'image de RoR2 a la meme forme que l'ecran : 16/9).
		// Lunette du Railgunner levee (0 aucune, 1 legere, 2 lourde) : mise a jour par UpdateScope.
		std::atomic<int> g_scope{ 0 };

		void DrawViewmodel(float a_width, float a_height)
		{
			// Pas de mains ni d'arme pendant la visee a la lunette (comme dans RoR2).
			const bool wanted = Config::Get().ViewmodelEnabled() && Movement::IsLinked() && !SkyrimMenuOpen() && InFirstPerson() && g_scope == 0;
			if (!wanted || !UpdateViewTexture()) {
				g_viewShown = false;
				return;
			}
			g_viewShown = true;

			// L'image garde ses proportions et touche le bas de l'ecran.
			const float scale = Config::Get().ViewmodelScale();
			const float imageAspect = static_cast<float>(g_viewWidth) / static_cast<float>(g_viewHeight);
			float width = a_width * scale;
			float height = width / imageAspect;
			// Descendue d'une partie de l'ecran (le bas de l'image sort de l'ecran) : armes plus basses.
			const float lower = a_height * Config::Get().ViewmodelOffset();
			const ImVec2 min((a_width - width) * 0.5f, a_height - height + lower);
			const ImVec2 max(min.x + width, a_height + lower);
			ImGui::GetBackgroundDrawList()->AddImage((ImTextureID)(std::uintptr_t)g_viewSrv, min, max);
		}

		// ---------- Ce que RoR2 voit depuis la camera de Skyrim (allies, tirs, effets) ----------
		// Meme format que les mains ; l'image couvre tout l'ecran, exactement (meme champ de vision que Skyrim).

		HANDLE                    g_worldMapping = nullptr;
		const std::uint8_t*       g_worldData = nullptr;
		ID3D11Texture2D*          g_worldTexture = nullptr;
		ID3D11ShaderResourceView* g_worldSrv = nullptr;
		std::uint32_t             g_worldWidth = 0;
		std::uint32_t             g_worldHeight = 0;
		std::uint32_t             g_worldSequence = 0;
		Clock::time_point         g_worldLastFrame{};

		bool UpdateWorldTexture()
		{
			if (!g_worldData) {
				g_worldMapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kViewMapSize, bridge::kWorldViewMapName);
				if (!g_worldMapping) {
					return false;
				}
				g_worldData = static_cast<const std::uint8_t*>(::MapViewOfFile(g_worldMapping, FILE_MAP_READ, 0, 0, bridge::kViewMapSize));
				if (!g_worldData) {
					return false;
				}
			}
			bridge::ViewHeader header{};
			std::memcpy(&header, g_worldData, sizeof(header));
			if (header.magic != bridge::kViewMagic || header.active != 1 || header.width == 0 || header.height == 0 ||
				header.width > bridge::kViewMaxWidth || header.height > bridge::kViewMaxHeight || header.ready > 1) {
				return false;
			}
			const auto now = Clock::now();
			if (header.sequence == g_worldSequence) {
				return g_worldSrv && now - g_worldLastFrame < 300ms;
			}
			if (!g_worldTexture || header.width != g_worldWidth || header.height != g_worldHeight) {
				if (g_worldSrv) { g_worldSrv->Release(); g_worldSrv = nullptr; }
				if (g_worldTexture) { g_worldTexture->Release(); g_worldTexture = nullptr; }
				D3D11_TEXTURE2D_DESC desc{};
				desc.Width = header.width;
				desc.Height = header.height;
				desc.MipLevels = 1;
				desc.ArraySize = 1;
				desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
				desc.SampleDesc.Count = 1;
				desc.Usage = D3D11_USAGE_DYNAMIC;
				desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
				desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
				if (FAILED(g_device->CreateTexture2D(&desc, nullptr, &g_worldTexture)) ||
					FAILED(g_device->CreateShaderResourceView(g_worldTexture, nullptr, &g_worldSrv))) {
					logger::error("Monde RoR2 : texture impossible a creer");
					return false;
				}
				g_worldWidth = header.width;
				g_worldHeight = header.height;
				logger::info("Monde RoR2 : images des allies et des tirs recues ({} x {})", header.width, header.height);
			}
			D3D11_MAPPED_SUBRESOURCE mapped{};
			if (FAILED(g_context->Map(g_worldTexture, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
				return false;
			}
			const std::uint8_t* source = g_worldData + bridge::kViewHeaderSize + header.ready * bridge::kViewFrameSize;
			const std::size_t   row = static_cast<std::size_t>(header.width) * 4;
			for (std::uint32_t y = 0; y < header.height; ++y) {
				std::memcpy(static_cast<std::uint8_t*>(mapped.pData) + y * mapped.RowPitch, source + y * row, row);
			}
			g_context->Unmap(g_worldTexture, 0);
			g_worldSequence = header.sequence;
			g_worldLastFrame = now;
			return true;
		}

		void DrawWorld(float a_width, float a_height)
		{
			const bool wanted = Movement::IsLinked() && !SkyrimMenuOpen() && InFirstPerson();
			if (!wanted || !UpdateWorldTexture()) {
				return;
			}
			// L'image a ete filmee un peu plus tot : si la camera a tourne depuis, on la decale d'autant,
			// comme si elle avait ete filmee maintenant (les allies restent a leur place quand on tourne la tete).
			float shiftX = 0.0f;
			float shiftY = 0.0f;
			auto* player = RE::PlayerCharacter::GetSingleton();
			auto* camera = RE::Main::WorldRootCamera();
			if (player && camera) {
				bridge::ViewHeader header{};
				std::memcpy(&header, g_worldData, sizeof(header));
				const auto& frustum = camera->GetRuntimeData2().viewFrustum;
				const float width = frustum.fRight - frustum.fLeft;
				const float height = frustum.fTop - frustum.fBottom;
				if (width > 0.0f && height > 0.0f && std::isfinite(header.yaw) && std::isfinite(header.pitch)) {
					float yawDelta = player->data.angle.z - header.yaw;
					while (yawDelta > 3.14159265f) yawDelta -= 6.2831853f;
					while (yawDelta < -3.14159265f) yawDelta += 6.2831853f;
					const float pitchDelta = player->data.angle.x - header.pitch;
					if (std::abs(yawDelta) < 0.6f && std::abs(pitchDelta) < 0.6f) {
						const float scale = std::abs(frustum.fRight) > 2.0f && frustum.fNear > 0.0f ? frustum.fNear : 1.0f;
						shiftX = -std::tan(yawDelta) * scale / width * a_width;
						shiftY = -std::tan(pitchDelta) * scale / height * a_height;
					}
				}
			}
			ImGui::GetBackgroundDrawList()->AddImage((ImTextureID)(std::uintptr_t)g_worldSrv, ImVec2(shiftX, shiftY), ImVec2(a_width + shiftX, a_height + shiftY));
		}

		// ---------- 0.65 : le survivant filme par RoR2, pour la vue a la 3e personne ----------
		// RoR2 le filme depuis la camera de Skyrim, sur fond transparent, et n'envoie que le rectangle de l'ecran
		// ou il se trouve. La camera de Skyrim tourne autour du joueur : le survivant reste au meme endroit de
		// l'ecran quand on tourne, pas besoin de decaler l'image comme pour le monde.

		// 0.70 (coop) : meme chose pour les coequipiers, un pont par coequipier (filmes eux aussi depuis notre camera).
		struct Film
		{
			const wchar_t*            name = nullptr;
			HANDLE                    mapping = nullptr;
			const std::uint8_t*       data = nullptr;
			ID3D11Texture2D*          texture = nullptr;
			ID3D11ShaderResourceView* srv = nullptr;
			std::uint32_t             width = 0;
			std::uint32_t             height = 0;
			std::uint32_t             sequence = 0;
			Clock::time_point         lastFrame{};
			float                     rect[4]{};
		};
		Film g_films[4]{ { bridge::kSurvivorViewMapName }, { bridge::kTeammateViewMapNames[0] }, { bridge::kTeammateViewMapNames[1] },
			{ bridge::kTeammateViewMapNames[2] } };

		bool UpdateFilm(Film& a_film)
		{
			if (!a_film.data) {
				a_film.mapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kSurvivorMapSize, a_film.name);
				if (!a_film.mapping) {
					return false;
				}
				a_film.data = static_cast<const std::uint8_t*>(::MapViewOfFile(a_film.mapping, FILE_MAP_READ, 0, 0, bridge::kSurvivorMapSize));
				if (!a_film.data) {
					return false;
				}
			}
			bridge::SurvivorViewHeader header{};
			std::memcpy(&header, a_film.data, sizeof(header));
			if (header.magic != bridge::kViewMagic || header.active != 1 || header.width == 0 || header.height == 0 ||
				header.width > bridge::kSurvivorMaxWidth || header.height > bridge::kSurvivorMaxHeight || header.ready > 1) {
				return false;
			}
			const auto now = Clock::now();
			if (header.sequence == a_film.sequence) {
				return a_film.srv && now - a_film.lastFrame < 300ms;
			}
			if (!a_film.texture || header.width != a_film.width || header.height != a_film.height) {
				if (a_film.srv) { a_film.srv->Release(); a_film.srv = nullptr; }
				if (a_film.texture) { a_film.texture->Release(); a_film.texture = nullptr; }
				D3D11_TEXTURE2D_DESC desc{};
				desc.Width = header.width;
				desc.Height = header.height;
				desc.MipLevels = 1;
				desc.ArraySize = 1;
				desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
				desc.SampleDesc.Count = 1;
				desc.Usage = D3D11_USAGE_DYNAMIC;
				desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
				desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
				if (FAILED(g_device->CreateTexture2D(&desc, nullptr, &a_film.texture)) ||
					FAILED(g_device->CreateShaderResourceView(a_film.texture, nullptr, &a_film.srv))) {
					logger::error("3e personne : texture impossible a creer");
					return false;
				}
				a_film.width = header.width;
				a_film.height = header.height;
				static int logged = 0;
				if (++logged <= 8) {
					logger::info("3e personne : images {} recues ({} x {})", &a_film == &g_films[0] ? "du survivant" : "d'un coequipier",
						header.width, header.height);
				}
			}
			D3D11_MAPPED_SUBRESOURCE mapped{};
			if (FAILED(g_context->Map(a_film.texture, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
				return false;
			}
			const std::uint8_t* source = a_film.data + bridge::kViewHeaderSize + header.ready * bridge::kSurvivorFrameSize;
			const std::size_t   row = static_cast<std::size_t>(header.width) * 4;
			for (std::uint32_t y = 0; y < header.height; ++y) {
				std::memcpy(static_cast<std::uint8_t*>(mapped.pData) + y * mapped.RowPitch, source + y * row, row);
			}
			g_context->Unmap(a_film.texture, 0);
			a_film.rect[0] = header.rectLeft;
			a_film.rect[1] = header.rectTop;
			a_film.rect[2] = header.rectRight;
			a_film.rect[3] = header.rectBottom;
			a_film.sequence = header.sequence;
			a_film.lastFrame = now;
			return true;
		}

		bool DrawFilm(Film& a_film, float a_width, float a_height)
		{
			if (!UpdateFilm(a_film)) {
				return false;
			}
			const float left = std::clamp(a_film.rect[0], -1.0f, 2.0f);
			const float top = std::clamp(a_film.rect[1], -1.0f, 2.0f);
			const float right = std::clamp(a_film.rect[2], -1.0f, 2.0f);
			const float bottom = std::clamp(a_film.rect[3], -1.0f, 2.0f);
			if (!(right > left) || !(bottom > top)) {
				return false;
			}
			ImGui::GetBackgroundDrawList()->AddImage((ImTextureID)(std::uintptr_t)a_film.srv,
				ImVec2(left * a_width, top * a_height), ImVec2(right * a_width, bottom * a_height));
			return true;
		}

		void DrawSurvivor(float a_width, float a_height)
		{
			if (!Movement::IsLinked() || SkyrimMenuOpen()) {
				return;
			}
			// Les coequipiers (en premiere comme en troisieme personne), seulement pendant une partie coop.
			if (Coop::Active()) {
				for (int i = 1; i < 4; ++i) {
					DrawFilm(g_films[i], a_width, a_height);
				}
			}
			if (!ThirdPerson::Wanted() || !DrawFilm(g_films[0], a_width, a_height)) {
				return;
			}
			ThirdPerson::NoteFilmShown();
			// Un viseur au centre de l'ecran (les tirs du survivant visent la ou regarde la camera).
			if (ThirdPerson::Crosshair()) {
				auto*        list = ImGui::GetBackgroundDrawList();
				const ImVec2 center(a_width * 0.5f, a_height * 0.5f);
				const float  radius = std::max(2.0f, a_height * 0.004f);
				list->AddCircleFilled(center, radius + 1.5f, IM_COL32(0, 0, 0, 140));
				list->AddCircleFilled(center, radius, IM_COL32(255, 255, 255, 220));
			}
		}

		// ---------- La lunette du Railgunner ----------
		// Quand le Railgunner vise avec sa lunette dans RoR2, Skyrim zoome (champ de vision reduit, en douceur)
		// et dessine une lunette : un cercle noir autour de l'image et une croix fine.
		// (g_scope : declare plus haut, avec les mains)
		float            g_zoom = 1.0f;           // 1 : normal ; plus petit : zoome
		float            g_savedWorldFov = 0.0f;  // champs de vision d'origine (0 : pas encore notes)
		float            g_savedFirstFov = 0.0f;

		void UpdateScope(bool a_linked)
		{
			bridge::BridgeData data{};
			int scope = 0;
			if (a_linked && Bridge::Get().Read(data)) {
				scope = data.scope;
			}
			g_scope = scope;
			const float target = scope == 2 ? 0.3f : scope == 1 ? 0.55f : 1.0f;
			auto* camera = RE::PlayerCamera::GetSingleton();
			if (!camera) {
				return;
			}
			if (g_zoom >= 0.999f && target >= 0.999f) {
				g_savedWorldFov = 0.0f;  // pas de zoom : on laisse Skyrim (et les reglages du joueur) tranquilles
				return;
			}
			if (g_savedWorldFov <= 0.0f) {
				g_savedWorldFov = camera->worldFOV;
				g_savedFirstFov = camera->firstPersonFOV;
			}
			g_zoom += (target - g_zoom) * 0.25f;  // en douceur
			if (std::abs(g_zoom - target) < 0.005f) {
				g_zoom = target;
			}
			camera->worldFOV = g_savedWorldFov * g_zoom;
			camera->firstPersonFOV = g_savedFirstFov * g_zoom;
			if (g_zoom >= 0.999f) {
				camera->worldFOV = g_savedWorldFov;  // revenu au champ de vision d'origine
				camera->firstPersonFOV = g_savedFirstFov;
			}
		}

		void DrawScope(float a_width, float a_height)
		{
			if (g_scope == 0 || !InFirstPerson()) {
				return;
			}
			auto*        draw = ImGui::GetBackgroundDrawList();
			const ImVec2 center(a_width * 0.5f, a_height * 0.5f);
			const float  radius = a_height * 0.46f;
			const float  thickness = a_width;  // un anneau noir tres epais : tout ce qui est hors du cercle
			draw->AddCircle(center, radius + thickness * 0.5f, IM_COL32(0, 0, 0, 255), 128, thickness);
			draw->AddCircle(center, radius, IM_COL32(20, 20, 20, 255), 128, 4.0f);
			const ImU32 line = IM_COL32(10, 10, 10, 220);
			draw->AddLine(ImVec2(center.x - radius, center.y), ImVec2(center.x - 8.0f, center.y), line, 2.0f);
			draw->AddLine(ImVec2(center.x + 8.0f, center.y), ImVec2(center.x + radius, center.y), line, 2.0f);
			draw->AddLine(ImVec2(center.x, center.y - radius), ImVec2(center.x, center.y - 8.0f), line, 2.0f);
			draw->AddLine(ImVec2(center.x, center.y + 8.0f), ImVec2(center.x, center.y + radius), line, 2.0f);
			draw->AddCircleFilled(center, 2.0f, IM_COL32(255, 60, 60, 255), 12);
		}

		// Vrai si un menu de Skyrim est ouvert (inventaire, carte, dialogue, console...) : on cache le HUD.
		bool SkyrimMenuOpen()
		{
			auto* ui = RE::UI::GetSingleton();
			if (!ui) {
				return true;
			}
			if (ui->GameIsPaused()) {
				return true;
			}
			for (std::string_view menu : { RE::DialogueMenu::MENU_NAME, RE::LoadingMenu::MENU_NAME, RE::MainMenu::MENU_NAME,
					 RE::Console::MENU_NAME, RE::LockpickingMenu::MENU_NAME, RE::BookMenu::MENU_NAME, RE::MapMenu::MENU_NAME,
					 RE::RaceSexMenu::MENU_NAME, RE::CraftingMenu::MENU_NAME, RE::BarterMenu::MENU_NAME, RE::ContainerMenu::MENU_NAME }) {
				if (ui->IsMenuOpen(menu)) {
					return true;
				}
			}
			return false;
		}

		void DrawSpecial(ImDrawList* a_draw, float a_width, float a_height, float a_scale);  // plus bas
		void DrawAllies(ImDrawList* a_draw, float a_height, float a_scale);                  // plus bas

		// ---------- La liste des objets (touche ListeObjets) ----------
		// RoR2 ecrit objets.txt (icone, nom, nombre, description) ; on l'affiche au milieu de l'ecran.
		struct ListedItem
		{
			std::string icon, name, description;
			int         count = 0;
		};
		std::vector<ListedItem>          g_listed;
		std::filesystem::file_time_type  g_listedTime{};
		Clock::time_point                g_listedCheck{};
		bool                             g_listOpen = false;
		bool                             g_listKeyWasDown = false;
		std::size_t                      g_listPage = 0;      // 0.72 : page affichee (chaque appui : page suivante)
		std::size_t                      g_listPages = 1;

		void RefreshList()
		{
			const auto now = Clock::now();
			if (now < g_listedCheck) {
				return;
			}
			g_listedCheck = now + 1s;
			char folder[MAX_PATH]{};
			if (::GetEnvironmentVariableA("LOCALAPPDATA", folder, MAX_PATH) == 0) {
				return;
			}
			const auto      path = std::filesystem::path(folder) / "SkyrimRoR2" / "objets.txt";
			std::error_code error;
			const auto      time = std::filesystem::last_write_time(path, error);
			if (error || time == g_listedTime) {
				return;
			}
			g_listedTime = time;
			g_listed.clear();
			std::ifstream file(path);
			std::string   line;
			while (std::getline(file, line)) {
				std::vector<std::string> parts;
				std::size_t              start = 0;
				for (std::size_t tab = line.find('\t'); tab != std::string::npos; tab = line.find('\t', start)) {
					parts.push_back(line.substr(start, tab - start));
					start = tab + 1;
				}
				parts.push_back(line.substr(start));
				if (parts.size() < 4) {
					continue;
				}
				g_listed.push_back(ListedItem{ parts[0], parts[1], parts[3], std::atoi(parts[2].c_str()) });
			}
		}

		void DrawItemList(ImDrawList* a_draw, float a_width, float a_height, float a_scale)
		{
			const int  key = Config::Get().Key(Action::kItems);
			const bool down = key != 0 && (InputWatcher::Get().IsDown(key) || (::GetAsyncKeyState(key) & 0x8000) != 0);
			// 0.72 : chaque appui montre la page suivante ; apres la derniere, la liste se ferme.
			if (down && !g_listKeyWasDown) {
				if (!g_listOpen) {
					g_listOpen = true;
					g_listPage = 0;
				} else if (g_listPage + 1 < g_listPages) {
					++g_listPage;
				} else {
					g_listOpen = false;
				}
			}
			g_listKeyWasDown = down;
			if (!g_listOpen) {
				return;
			}
			RefreshList();

			const float  s = a_scale;
			ImFont*      font = g_font ? g_font : ImGui::GetFont();
			const float  width = std::min(760.0f * s, a_width - 80.0f * s);
			const float  icon = 44.0f * s;
			const float  pad = 14.0f * s;
			const float  textWidth = width - icon - pad * 3.0f;
			const float  titleSize = 20.0f * s;
			const float  bodySize = 16.0f * s;
			const ImVec2 min((a_width - width) * 0.5f, a_height * 0.12f);
			const float  maxY = a_height * 0.88f;

			// 0.72 : les objets sont coupes en pages qui tiennent a l'ecran.
			const float               top = pad + 34.0f * s;
			const float               room = maxY - min.y - top - pad;
			std::vector<std::size_t>  starts{ 0 };
			float                     used = 0.0f;
			std::vector<float>        rows;
			for (const auto& item : g_listed) {
				const float text = titleSize + 4.0f * s + font->CalcTextSizeA(bodySize, FLT_MAX, textWidth, item.description.c_str()).y;
				rows.push_back(std::max(icon, text) + pad);
			}
			for (std::size_t i = 0; i < rows.size(); ++i) {
				if (used + rows[i] > room && i > starts.back()) {
					starts.push_back(i);
					used = 0.0f;
				}
				used += rows[i];
			}
			g_listPages = starts.size();
			if (g_listPage >= g_listPages) {
				g_listPage = g_listPages - 1;
			}
			const std::size_t first = starts[g_listPage];
			const std::size_t last = g_listPage + 1 < starts.size() ? starts[g_listPage + 1] : rows.size();
			float             height = top + pad;
			for (std::size_t i = first; i < last; ++i) {
				height += rows[i];
			}
			a_draw->AddRectFilled(min, ImVec2(min.x + width, min.y + height), Color(14, 16, 22, 215), 6.0f * s);
			const std::string pageText = g_listPages > 1 ? std::format("   page {}/{} ({} : suite)", g_listPage + 1, g_listPages, Config::Get().KeyName(Action::kItems)) : std::string();
			Text(a_draw, 22.0f * s, ImVec2(min.x + pad, min.y + pad), Color(240, 240, 245),
				g_listed.empty() ? std::string("Aucun objet RoR2") : std::format("Objets RoR2 ({}){}", g_listed.size(), pageText));

			float y = min.y + top;
			for (std::size_t i = first; i < last; ++i) {
				const auto& item = g_listed[i];
				const float row = rows[i] - pad;
				const ImVec2 iconMin(min.x + pad, y);
				a_draw->AddRectFilled(iconMin, ImVec2(iconMin.x + icon, iconMin.y + icon), Color(30, 32, 40, 200), 3.0f * s);
				Image(a_draw, GetIcon(item.icon.c_str()), iconMin, ImVec2(iconMin.x + icon, iconMin.y + icon));
				const float  textX = iconMin.x + icon + pad;
				const std::string title = item.count <= 0 ? item.name + "  (equipement)" : (item.count > 1 ? std::format("{}  x{}", item.name, item.count) : item.name);
				Text(a_draw, titleSize, ImVec2(textX, y), item.count <= 0 ? Color(255, 170, 60) : Color(250, 250, 250), title);
				a_draw->AddText(font, bodySize, ImVec2(textX, y + titleSize + 4.0f * s), Color(200, 205, 215), item.description.c_str(), nullptr, textWidth);
				y += row + pad;
			}
		}

		void DrawHud(float a_width, float a_height)
		{
			if (!Config::Get().HudEnabled() || !Movement::IsLinked() || SkyrimMenuOpen()) {
				return;
			}
			if (!Bridge::Get().ReadHud(g_hud) || g_hud.level <= 0) {
				return;
			}
			const auto now = Clock::now();
			if (g_hud.sequence != g_lastSequence) {
				g_lastSequence = g_hud.sequence;
				g_lastChange = now;
			}
			if (now - g_lastChange > 1s) {
				return;  // RoR2 ne met plus le HUD a jour
			}

			ImDrawList* draw = ImGui::GetForegroundDrawList();
			const float s = (a_height / 1080.0f) * Config::Get().HudScale();
			const float margin = 40.0f * s;

			// --- En haut a gauche : l'or ---
			Text(draw, 34.0f * s, ImVec2(margin, margin * 0.7f), Color(255, 214, 90), std::format("{} septims", Loot::Septims()));
			// A cote : la difficulte et les pieces lunaires.
			Text(draw, 20.0f * s, ImVec2(margin + 260.0f * s, margin * 0.7f + 10.0f * s), Color(210, 215, 225),
				std::format("Difficulte {:.1f}   Pieces lunaires {}   ({} : liste des objets, {} : journal)", Loot::Difficulty(), Loot::Coins(),
					Config::Get().KeyName(Action::kItems), Config::Get().KeyName(Action::kJournal)));

			// Sous la difficulte : l'autel RoR2 du lieu et sa distance.
			if (const std::string hint = Altars::Hint(); !hint.empty()) {
				Text(draw, 20.0f * s, ImVec2(margin + 260.0f * s, margin * 0.7f + 36.0f * s), Color(140, 220, 255), hint);
			}
			// Sous le viseur : le prix du contenant vise.
			if (const std::string hover = Loot::HoverText(); !hover.empty()) {
				Text(draw, 22.0f * s, ImVec2(a_width * 0.5f, a_height * 0.5f + 40.0f * s), Color(255, 214, 90), hover, true);
			}

			// 0.70 (coop) : a terre, ou un coequipier en train d'etre releve.
			if (Coop::Active()) {
				const float revive = Coop::Revive();
				const bool  downed = Coop::Downed();
				if (downed || revive >= 0.0f) {
					const float  barWidth = 420.0f * s;
					const float  barHeight = 16.0f * s;
					const ImVec2 barMin((a_width - barWidth) * 0.5f, a_height * 0.62f);
					const ImVec2 barMax(barMin.x + barWidth, barMin.y + barHeight);
					std::string  label = Coop::ReviveLabel();
					if (label.empty()) {
						label = downed ? "A terre : un coequipier peut te relever" : "Relevement";
					}
					Text(draw, 24.0f * s, ImVec2(a_width * 0.5f, barMin.y - 18.0f * s), downed ? Color(255, 120, 110) : Color(140, 230, 140), label, true);
					if (revive >= 0.0f) {
						draw->AddRectFilled(barMin, barMax, Color(20, 22, 28, 180), 3.0f * s);
						draw->AddRectFilled(barMin, ImVec2(barMin.x + barWidth * std::clamp(revive, 0.0f, 1.0f), barMax.y), Color(110, 200, 110, 235), 3.0f * s);
					}
				}
			}

			// En haut au centre : la charge du coffre du boss.
			if (const float charge = Loot::ChargeProgress(); charge >= 0.0f) {
				const float  barWidth = 360.0f * s;
				const float  barHeight = 14.0f * s;
				const ImVec2 barMin((a_width - barWidth) * 0.5f, a_height * 0.16f);  // sous la boussole de Skyrim
				const ImVec2 barMax(barMin.x + barWidth, barMin.y + barHeight);
				draw->AddRectFilled(barMin, barMax, Color(20, 22, 28, 170), 3.0f * s);
				draw->AddRectFilled(barMin, ImVec2(barMin.x + barWidth * std::clamp(charge, 0.0f, 1.0f), barMax.y), Color(200, 60, 60, 235), 3.0f * s);
				Text(draw, 20.0f * s, ImVec2(a_width * 0.5f, barMin.y - 14.0f * s), Color(240, 235, 235),
					std::format("Charge du coffre : {:.0f} %", charge * 100.0f), true);
			}

			// --- Sous l'or : les objets ---
			const float itemSize = 40.0f * s;
			const float itemGap = 4.0f * s;
			const float itemsRight = a_width * 0.55f;
			float x = margin;
			float y = margin * 0.7f + 50.0f * s;
			const int count = std::clamp(g_hud.itemCount, 0, static_cast<int>(bridge::kHudMaxItems));
			for (int i = 0; i < count; ++i) {
				const auto& item = g_hud.items[i];
				char icon[bridge::kIconNameSize + 1]{};
				std::memcpy(icon, item.icon, bridge::kIconNameSize);

				const ImVec2 min(x, y);
				const ImVec2 max(x + itemSize, y + itemSize);
				draw->AddRectFilled(min, max, Color(20, 22, 28, 140), 3.0f * s);
				Image(draw, GetIcon(icon), min, max);
				if (item.count > 1) {
					Text(draw, 15.0f * s, ImVec2(max.x - 18.0f * s, max.y - 17.0f * s), Color(255, 255, 255), std::format("x{}", item.count));
				}

				x += itemSize + itemGap;
				if (x + itemSize > itemsRight) {
					x = margin;
					y += itemSize + itemGap;
				}
			}

			// --- En bas a gauche : niveau, vie, experience ---
			const float barWidth = 440.0f * s;
			const float barHeight = 28.0f * s;
			const ImVec2 barMin((a_width - barWidth) * 0.5f, a_height - margin - barHeight - 14.0f * s);  // centree
			const ImVec2 barMax(barMin.x + barWidth, barMin.y + barHeight);

			// Nom du survivant et niveau, au-dessus de la barre.
			char name[bridge::kIconNameSize + 1]{};
			std::memcpy(name, g_hud.name, bridge::kIconNameSize);
			const std::string title = name[0] ? std::format("{}   NIV. {}", name, g_hud.level) : std::format("NIV. {}", g_hud.level);
			Text(draw, 22.0f * s, ImVec2(barMin.x, barMin.y - 30.0f * s), Color(235, 235, 240), title);

			// Le portrait du survivant, a gauche de la barre.
			char portrait[bridge::kIconNameSize + 1]{};
			std::memcpy(portrait, g_hud.portrait, bridge::kIconNameSize);
			if (portrait[0]) {
				const float size = 76.0f * s;
				const ImVec2 pMax(barMin.x - 12.0f * s, barMax.y + 13.0f * s);
				const ImVec2 pMin(pMax.x - size, pMax.y - size);
				draw->AddRectFilled(pMin, pMax, Color(20, 22, 28, 200), 4.0f * s);
				Image(draw, GetIcon(portrait), pMin, pMax);
				draw->AddRect(pMin, pMax, Color(200, 200, 210, 160), 4.0f * s, 0, 1.5f * s);
			}

			draw->AddRectFilled(barMin, barMax, Color(30, 15, 15, 220), 3.0f * s);
			const float total = std::max(1.0f, g_hud.fullHealth + g_hud.fullShield);
			const float healthPart = std::clamp(g_hud.health / total, 0.0f, 1.0f);
			const float shieldPart = std::clamp(g_hud.shield / total, 0.0f, 1.0f - healthPart);
			const float healthEnd = barMin.x + barWidth * healthPart;
			const float shieldEnd = healthEnd + barWidth * shieldPart;
			const bool  low = g_hud.health < g_hud.fullHealth * 0.25f;
			draw->AddRectFilled(barMin, ImVec2(healthEnd, barMax.y), low ? Color(214, 58, 48) : Color(110, 196, 74), 3.0f * s);
			if (shieldPart > 0.0f) {
				draw->AddRectFilled(ImVec2(healthEnd, barMin.y), ImVec2(shieldEnd, barMax.y), Color(64, 168, 245));
			}
			if (g_hud.barrier > 0.0f) {
				const float barrierPart = std::clamp(g_hud.barrier / total, 0.0f, 1.0f);
				draw->AddRectFilled(barMin, ImVec2(barMin.x + barWidth * barrierPart, barMin.y + barHeight * 0.35f), Color(240, 200, 60, 220));
			}
			draw->AddRect(barMin, barMax, Color(0, 0, 0, 220), 3.0f * s, 0, 2.0f * s);
			Text(draw, 20.0f * s, ImVec2((barMin.x + barMax.x) * 0.5f, (barMin.y + barMax.y) * 0.5f), Color(255, 255, 255),
				std::format("{:.0f} / {:.0f}", g_hud.health + g_hud.shield, total), true);

			const ImVec2 xpMin(barMin.x, barMax.y + 6.0f * s);
			const ImVec2 xpMax(barMax.x, xpMin.y + 7.0f * s);
			// 0.66 : barre cachee quand le niveau RoR2 suit le niveau Skyrim (RoR2 envoie alors une valeur negative).
			if (g_hud.xpFraction >= 0.0f) {
				draw->AddRectFilled(xpMin, xpMax, Color(20, 20, 30, 200), 2.0f * s);
				draw->AddRectFilled(xpMin, ImVec2(xpMin.x + barWidth * std::clamp(g_hud.xpFraction, 0.0f, 1.0f), xpMax.y), Color(90, 200, 255), 2.0f * s);
			}

			// --- En bas a droite : les 4 competences, puis l'equipement ---
			const float skillSize = 66.0f * s;
			const float skillGap = 10.0f * s;
			const float equipSize = 78.0f * s;
			const float bottom = a_height - margin - 70.0f * s;  // un peu plus haut (au-dessus de l'endurance de Skyrim)

			const float equipX = a_width - margin - equipSize;
			const float skillsRight = equipX - 26.0f * s;
			const auto& config = Config::Get();
			const Action keys[4]{ Action::kSkill1, Action::kSkill2, Action::kSkill3, Action::kSkill4 };
			for (int i = 0; i < 4; ++i) {
				const float sx = skillsRight - (4 - i) * skillSize - (3 - i) * skillGap;
				DrawSlot(draw, g_hud.slots[i], ImVec2(sx, bottom - skillSize), skillSize, s, ShortKey(config.KeyName(keys[i])));
			}
			if (g_hud.slots[4].icon[0]) {
				DrawSlot(draw, g_hud.slots[4], ImVec2(equipX, bottom - equipSize), equipSize, s, ShortKey(config.KeyName(Action::kEquipment)));
			}

			DrawSpecial(draw, a_width, a_height, s);
			DrawAllies(draw, a_height, s);
			DrawItemList(draw, a_width, a_height, s);
		}

		// 0.73 : la liste des allies (drones, tourelles...), a gauche, comme dans RoR2 : portrait, vie et nombre.
		HANDLE              g_allyHudMapping = nullptr;
		const std::uint8_t* g_allyHudView = nullptr;

		void DrawAllies(ImDrawList* a_draw, float a_height, float a_scale)
		{
			if (!g_allyHudView) {
				static bool tried = false;
				if (tried) {
					return;
				}
				tried = true;
				g_allyHudMapping = ::CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, bridge::kAllyHudMapSize, bridge::kAllyHudMapName);
				if (g_allyHudMapping) {
					g_allyHudView = static_cast<const std::uint8_t*>(::MapViewOfFile(g_allyHudMapping, FILE_MAP_READ, 0, 0, bridge::kAllyHudMapSize));
				}
				if (!g_allyHudView) {
					return;
				}
			}
			std::uint32_t magic = 0, count = 0;
			std::memcpy(&magic, g_allyHudView, 4);
			std::memcpy(&count, g_allyHudView + 8, 4);
			if (magic != bridge::kAllyHudMagic) {
				return;
			}
			count = std::min(count, bridge::kAllyHudMax);
			const float s = a_scale;
			const float icon = 40.0f * s;
			const float barWidth = 110.0f * s;
			const float x = 26.0f * s;
			float       y = a_height * 0.36f;
			for (std::uint32_t i = 0; i < count; ++i) {
				const std::uint8_t* entry = g_allyHudView + bridge::kAllyHudHeaderSize + i * bridge::kAllyHudEntrySize;
				char name[bridge::kIconNameSize + 1]{};
				std::memcpy(name, entry, bridge::kIconNameSize);
				float health = 0.0f, shield = 0.0f;
				std::int32_t number = 0;
				std::memcpy(&health, entry + 48, 4);
				std::memcpy(&shield, entry + 52, 4);
				std::memcpy(&number, entry + 56, 4);
				health = std::isfinite(health) ? std::clamp(health, 0.0f, 1.0f) : 0.0f;
				shield = std::isfinite(shield) ? std::clamp(shield, 0.0f, 1.0f - health) : 0.0f;
				const ImVec2 iMin(x, y);
				const ImVec2 iMax(x + icon, y + icon);
				a_draw->AddRectFilled(iMin, iMax, Color(20, 22, 28, 190), 3.0f * s);
				if (name[0]) {
					Image(a_draw, GetIcon(name), iMin, iMax);
				}
				a_draw->AddRect(iMin, iMax, Color(200, 200, 210, 140), 3.0f * s, 0, 1.0f * s);
				const ImVec2 bMin(iMax.x + 6.0f * s, y + icon - 12.0f * s);
				const ImVec2 bMax(bMin.x + barWidth, bMin.y + 8.0f * s);
				a_draw->AddRectFilled(bMin, bMax, Color(30, 15, 15, 210), 2.0f * s);
				a_draw->AddRectFilled(bMin, ImVec2(bMin.x + barWidth * health, bMax.y), health < 0.25f ? Color(214, 58, 48) : Color(110, 196, 74), 2.0f * s);
				if (shield > 0.0f) {
					a_draw->AddRectFilled(ImVec2(bMin.x + barWidth * health, bMin.y), ImVec2(bMin.x + barWidth * (health + shield), bMax.y), Color(64, 168, 245));
				}
				a_draw->AddRect(bMin, bMax, Color(0, 0, 0, 200), 2.0f * s, 0, 1.0f * s);
				if (number > 1) {
					Text(a_draw, 16.0f * s, ImVec2(bMin.x, y + 2.0f * s), Color(255, 255, 255), std::format("x{}", number));
				}
				y += icon + 8.0f * s;
			}
		}

		// La jauge propre au survivant, sous le viseur (comme dans RoR2) : corruption du Demon du vide,
		// recharge active du Railgunner (avec la fenetre a viser), tranquillite et Meditation de la Chercheuse.
		void DrawSpecial(ImDrawList* a_draw, float a_width, float a_height, float a_scale)
		{
			const auto& special = g_hud.special;
			if (special.kind <= 0 || special.kind > 5) {
				return;
			}
			const float  s = a_scale;
			const ImU32  color = IM_COL32((special.color >> 24) & 0xFF, (special.color >> 16) & 0xFF, (special.color >> 8) & 0xFF, 235);
			const float  width = 220.0f * s;
			const float  height = 12.0f * s;
			const ImVec2 min((a_width - width) * 0.5f, a_height * 0.5f + 70.0f * s);
			const ImVec2 max(min.x + width, min.y + height);
			char label[bridge::kIconNameSize + 1]{};
			std::memcpy(label, special.label, bridge::kIconNameSize);
			if (special.kind == 5) {  // 0.73 : Meditation de la Chercheuse, la suite de directions a taper
				const int   total = std::clamp(static_cast<int>(std::strlen(label)), 1, 12);
				const float box = 30.0f * s;
				const float gap = 8.0f * s;
				const float start = a_width * 0.5f - (total * box + (total - 1) * gap) * 0.5f;
				Text(a_draw, 17.0f * s, ImVec2(a_width * 0.5f, min.y - 13.0f * s), Color(235, 235, 240), "Meditation", true);
				for (int i = 0; i < total; ++i) {
					const ImVec2 p0(start + i * (box + gap), min.y);
					const ImVec2 p1(p0.x + box, p0.y + box);
					const bool   done = i < special.charges;
					const bool   current = i == special.charges;
					a_draw->AddRectFilled(p0, p1, done ? Color(60, 160, 120, 200) : current ? Color(40, 70, 80, 230) : Color(20, 22, 30, 200), 4.0f * s);
					a_draw->AddRect(p0, p1, current ? color : Color(0, 0, 0, 220), 4.0f * s, 0, (current ? 2.5f : 1.5f) * s);
					const ImVec2 c((p0.x + p1.x) * 0.5f, (p0.y + p1.y) * 0.5f);
					const float  r = box * 0.3f;
					ImVec2       a, b, d;
					switch (label[i]) {
					case 'H': a = ImVec2(c.x, c.y - r); b = ImVec2(c.x - r, c.y + r * 0.7f); d = ImVec2(c.x + r, c.y + r * 0.7f); break;
					case 'B': a = ImVec2(c.x, c.y + r); b = ImVec2(c.x + r, c.y - r * 0.7f); d = ImVec2(c.x - r, c.y - r * 0.7f); break;
					case 'G': a = ImVec2(c.x - r, c.y); b = ImVec2(c.x + r * 0.7f, c.y + r); d = ImVec2(c.x + r * 0.7f, c.y - r); break;
					case 'D': a = ImVec2(c.x + r, c.y); b = ImVec2(c.x - r * 0.7f, c.y - r); d = ImVec2(c.x - r * 0.7f, c.y + r); break;
					default: continue;
					}
					a_draw->AddTriangleFilled(a, b, d, done ? Color(200, 255, 220, 200) : Color(255, 255, 255, 240));
				}
				// Le temps qui reste, sous les fleches.
				const ImVec2 t0(start, min.y + box + 6.0f * s);
				const float  tw = total * box + (total - 1) * gap;
				a_draw->AddRectFilled(t0, ImVec2(t0.x + tw, t0.y + 5.0f * s), Color(20, 22, 30, 200), 2.0f * s);
				a_draw->AddRectFilled(t0, ImVec2(t0.x + tw * std::clamp(special.value, 0.0f, 1.0f), t0.y + 5.0f * s), color, 2.0f * s);
				return;
			}
			if (label[0]) {
				Text(a_draw, 17.0f * s, ImVec2(a_width * 0.5f, min.y - 13.0f * s), Color(235, 235, 240), label, true);
			}
			if (special.kind == 3) {  // des charges : une pastille par charge
				const int   total = std::clamp(special.maxCharges, 1, 12);
				const float pip = 14.0f * s;
				const float gap = 6.0f * s;
				const float start = a_width * 0.5f - (total * pip + (total - 1) * gap) * 0.5f;
				for (int i = 0; i < total; ++i) {
					const ImVec2 p0(start + i * (pip + gap), min.y);
					const ImVec2 p1(p0.x + pip, p0.y + pip);
					a_draw->AddRectFilled(p0, p1, i < special.charges ? color : Color(30, 32, 40, 180), 3.0f * s);
					a_draw->AddRect(p0, p1, Color(0, 0, 0, 200), 3.0f * s, 0, 1.5f * s);
				}
				return;
			}
			a_draw->AddRectFilled(min, max, Color(20, 22, 30, 200), 3.0f * s);
			const float value = std::clamp(special.value, 0.0f, 1.0f);
			if (special.kind == 2) {  // recharge active : la fenetre a viser, puis le curseur
				const float w0 = std::clamp(special.windowStart, 0.0f, 1.0f);
				const float w1 = std::clamp(special.windowEnd, w0, 1.0f);
				if (w1 > w0) {
					a_draw->AddRectFilled(ImVec2(min.x + width * w0, min.y), ImVec2(min.x + width * w1, max.y), Color(255, 210, 70, 230), 2.0f * s);
				}
				a_draw->AddRectFilled(min, ImVec2(min.x + width * value, max.y), static_cast<ImU32>((color & 0x00FFFFFFu) | (0x60u << 24)), 3.0f * s);
				const float x = min.x + width * value;
				a_draw->AddRectFilled(ImVec2(x - 2.0f * s, min.y - 4.0f * s), ImVec2(x + 2.0f * s, max.y + 4.0f * s), Color(255, 255, 255));
			} else {  // barre simple (corruption, progression)
				a_draw->AddRectFilled(min, ImVec2(min.x + width * value, max.y), color, 3.0f * s);
			}
			a_draw->AddRect(min, max, Color(0, 0, 0, 220), 3.0f * s, 0, 1.5f * s);
		}

		// ---------- Branchement sur le rendu de Skyrim ----------

		void Initialize()
		{
			auto* renderer = RE::BSGraphics::Renderer::GetSingleton();
			if (!renderer) {
				logger::error("HUD : moteur de rendu introuvable");
				return;
			}
			auto& data = renderer->GetRuntimeData();
			g_swapChain = reinterpret_cast<IDXGISwapChain*>(data.renderWindows[0].swapChain);
			g_device = reinterpret_cast<ID3D11Device*>(data.forwarder);
			g_context = reinterpret_cast<ID3D11DeviceContext*>(data.context);
			if (!g_swapChain || !g_device || !g_context) {
				logger::error("HUD : DirectX introuvable");
				return;
			}

			::CoInitializeEx(nullptr, COINIT_MULTITHREADED);  // necessaire pour lire les PNG (deja fait par Skyrim en general)

			IMGUI_CHECKVERSION();
			ImGui::CreateContext();
			ImGuiIO& io = ImGui::GetIO();
			io.IniFilename = nullptr;  // pas de fichier imgui.ini
			io.ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;

			// Une police lisible de Windows (sinon celle d'ImGui, plus petite).
			wchar_t windows[MAX_PATH]{};
			::GetWindowsDirectoryW(windows, MAX_PATH);
			for (const wchar_t* name : { L"segoeuib.ttf", L"arialbd.ttf", L"segoeui.ttf" }) {
				const auto path = std::filesystem::path(windows) / L"Fonts" / name;
				std::error_code error;
				if (std::filesystem::exists(path, error)) {
					g_font = io.Fonts->AddFontFromFileTTF(path.string().c_str(), 48.0f);
					if (g_font) {
						break;
					}
				}
			}

			if (!ImGui_ImplDX11_Init(g_device, g_context)) {
				logger::error("HUD : ImGui n'a pas pu demarrer");
				return;
			}
			g_ready = true;
			logger::info("HUD pret (icones dans {})", IconFolder().string());
		}

		// Taille de l'ecran (l'image que Skyrim affiche).
		bool ScreenSize(UINT& a_width, UINT& a_height)
		{
			DXGI_SWAP_CHAIN_DESC desc{};
			if (FAILED(g_swapChain->GetDesc(&desc)) || desc.BufferDesc.Width == 0 || desc.BufferDesc.Height == 0) {
				return false;
			}
			a_width = desc.BufferDesc.Width;
			a_height = desc.BufferDesc.Height;
			return true;
		}

		// Dessine les mains et le HUD RoR2 dans a_target, puis remet tout comme avant.
		void DrawFrame(ID3D11RenderTargetView* a_target, UINT a_width, UINT a_height)
		{
			const auto now = Clock::now();
			ImGuiIO&   io = ImGui::GetIO();
			io.DisplaySize = ImVec2(static_cast<float>(a_width), static_cast<float>(a_height));
			io.DeltaTime = std::clamp(std::chrono::duration<float>(now - g_lastFrame).count(), 0.0001f, 0.25f);
			g_lastFrame = now;

			ImGui_ImplDX11_NewFrame();
			ImGui::NewFrame();
			DrawWorld(io.DisplaySize.x, io.DisplaySize.y);      // allies et tirs de RoR2, sous les mains
			DrawSurvivor(io.DisplaySize.x, io.DisplaySize.y);   // le survivant filme, a la 3e personne
			DrawViewmodel(io.DisplaySize.x, io.DisplaySize.y);  // les mains, sous le HUD
			DrawScope(io.DisplaySize.x, io.DisplaySize.y);      // la lunette du Railgunner, par-dessus les mains
			DrawHud(io.DisplaySize.x, io.DisplaySize.y);
			ImGui::Render();

			ID3D11RenderTargetView* previousTarget = nullptr;
			ID3D11DepthStencilView* previousDepth = nullptr;
			g_context->OMGetRenderTargets(1, &previousTarget, &previousDepth);
			g_context->OMSetRenderTargets(1, &a_target, nullptr);
			ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
			g_context->OMSetRenderTargets(1, &previousTarget, previousDepth);
			if (previousTarget) {
				previousTarget->Release();
			}
			if (previousDepth) {
				previousDepth->Release();
			}
		}

		// Ecrit une seule fois dans le journal ou l'on dessine (utile si un probleme revient).
		void LogDrawPlace(bool a_underSkyrimHud)
		{
			static int last = -1;
			if (last != static_cast<int>(a_underSkyrimHud)) {
				last = static_cast<int>(a_underSkyrimHud);
				if (a_underSkyrimHud) {
					logger::info("Mains et HUD RoR2 dessines SOUS l'interface de Skyrim (sous-titres visibles)");
				} else {
					logger::info("Mains et HUD RoR2 dessines PAR-DESSUS l'interface de Skyrim");
				}
			}
		}

		// Juste avant que Skyrim dessine son HUD (sous-titres, boussole, notifications...).
		// On dessine dans l'image en cours, si elle a bien la taille de l'ecran. Renvoie false sinon.
		bool RenderUnderSkyrimHud()
		{
			UINT width = 0, height = 0;
			if (!g_ready || !ScreenSize(width, height)) {
				return false;
			}
			ID3D11RenderTargetView* bound = nullptr;
			ID3D11DepthStencilView* depth = nullptr;
			g_context->OMGetRenderTargets(1, &bound, &depth);
			if (depth) {
				depth->Release();
			}
			if (!bound) {
				return false;
			}
			bool sameSize = false;
			ID3D11Resource* resource = nullptr;
			bound->GetResource(&resource);
			if (resource) {
				ID3D11Texture2D* texture = nullptr;
				if (SUCCEEDED(resource->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&texture)))) {
					D3D11_TEXTURE2D_DESC desc{};
					texture->GetDesc(&desc);
					sameSize = desc.Width == width && desc.Height == height;
					texture->Release();
				}
				resource->Release();
			}
			if (sameSize) {
				DrawFrame(bound, width, height);
				LogDrawPlace(true);
			}
			bound->Release();
			return sameSize;
		}

		// Solution de secours : par-dessus toute l'image de Skyrim, juste avant qu'elle s'affiche.
		void RenderOnTop()
		{
			UINT width = 0, height = 0;
			if (!g_ready || !ScreenSize(width, height)) {
				return;
			}
			ID3D11Texture2D* backBuffer = nullptr;
			if (FAILED(g_swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer)))) {
				return;
			}
			ID3D11RenderTargetView* target = nullptr;
			g_device->CreateRenderTargetView(backBuffer, nullptr, &target);
			backBuffer->Release();
			if (!target) {
				return;
			}
			DrawFrame(target, width, height);
			target->Release();
			if (Movement::IsLinked()) {
				LogDrawPlace(false);
			}
		}

		bool g_drawnUnderSkyrimHud = false;  // deja dessine sous le HUD de Skyrim pour cette image

		// Juste apres que Skyrim a cree sa fenetre et DirectX.
		struct D3DInitHook
		{
			static void thunk()
			{
				func();
				Initialize();
			}
			static inline REL::Relocation<decltype(thunk)> func;
		};

		// Juste avant que Skyrim n'affiche son image.
		struct PresentHook
		{
			static void thunk(std::uint32_t a_timer)
			{
				func(a_timer);
				if (!g_drawnUnderSkyrimHud) {
					RenderOnTop();  // le HUD de Skyrim n'a pas ete dessine (ou pas au bon endroit)
				}
				g_drawnUnderSkyrimHud = false;
			}
			static inline REL::Relocation<decltype(thunk)> func;
		};

		// Le HUD de Skyrim se dessine : on passe juste avant lui, pour que les sous-titres restent par-dessus les mains.
		struct HudMenuDisplayHook
		{
			static void thunk(RE::IMenu* a_this)
			{
				if (!g_drawnUnderSkyrimHud) {
					g_drawnUnderSkyrimHud = RenderUnderSkyrimHud();
				}
				func(a_this);
			}
			static inline REL::Relocation<decltype(thunk)> func;
		};

		// ---------- La barre de vie de Skyrim ----------

		bool              g_skyrimHealthHidden = false;
		Clock::time_point g_nextHealthCheck{};

		void SetSkyrimHealthVisible(bool a_visible)
		{
			auto* ui = RE::UI::GetSingleton();
			if (!ui) {
				return;
			}
			auto hud = ui->GetMenu(RE::HUDMenu::MENU_NAME);
			if (!hud || !hud->uiMovie) {
				return;
			}
			hud->uiMovie->SetVariable("_root.HUDMovieBaseInstance.Health._alpha", RE::GFxValue(a_visible ? 100.0 : 0.0));
		}
	}

	void Install()
	{
		auto& trampoline = SKSE::GetTrampoline();

		REL::Relocation<std::uintptr_t> initTarget{ REL::RelocationID(75595, 77226), REL::VariantOffset(0x9, 0x275, 0x9) };
		D3DInitHook::func = trampoline.write_call<5>(initTarget.address(), D3DInitHook::thunk);

		REL::Relocation<std::uintptr_t> presentTarget{ REL::RelocationID(75461, 77246), REL::VariantOffset(0x9, 0x9, 0x15) };
		PresentHook::func = trampoline.write_call<5>(presentTarget.address(), PresentHook::thunk);

		// Fonction n°6 du menu HUD de Skyrim (PostDisplay) : c'est elle qui dessine son interface.
		REL::Relocation<std::uintptr_t> hudMenuVtable{ RE::VTABLE_HUDMenu[0] };
		HudMenuDisplayHook::func = hudMenuVtable.write_vfunc(0x6, HudMenuDisplayHook::thunk);

		logger::info("Crochets du HUD installes");
	}

	void PerFrame(bool a_linked)
	{
		UpdateScope(a_linked);

		// Les bras de Skyrim en premiere personne sont caches quand les mains du survivant s'affichent.
		static bool armsHidden = false;
		// Quelques secondes sans image des mains (mode transport du MUL-T, changement de forme...) : les bras de
		// Skyrim restent caches, sinon ils reapparaissaient le temps que RoR2 renvoie des images.
		static Clock::time_point lastViewShown{};
		const auto               frameTime = Clock::now();
		if (g_viewShown) {
			lastViewShown = frameTime;
		}
		const bool recentView = g_viewShown || frameTime - lastViewShown < std::chrono::seconds(5);
		const bool hideArms = a_linked && recentView && InFirstPerson() && Config::Get().HideSkyrimArms();
		if (hideArms != armsHidden && !hideArms && !InFirstPerson()) {
			// Passe en troisieme personne : Skyrim cache deja lui-meme les bras de premiere personne,
			// surtout ne pas les reafficher.
			armsHidden = false;
		} else if (hideArms != armsHidden) {
			if (auto* player = RE::PlayerCharacter::GetSingleton()) {
				if (auto* arms = player->Get3D(true)) {
					arms->CullNode(hideArms);
					armsHidden = hideArms;
				}
			}
		}

		const auto now = Clock::now();
		if (now < g_nextHealthCheck) {
			return;
		}
		g_nextHealthCheck = now + 1s;

		const bool hide = a_linked && Config::Get().HudEnabled() && Config::Get().HideSkyrimHealth();
		if (hide) {
			SetSkyrimHealthVisible(false);  // a chaque seconde : Skyrim la fait parfois reapparaitre
			g_skyrimHealthHidden = true;
		} else if (g_skyrimHealthHidden) {
			SetSkyrimHealthVisible(true);
			g_skyrimHealthHidden = false;
		}
	}
}
