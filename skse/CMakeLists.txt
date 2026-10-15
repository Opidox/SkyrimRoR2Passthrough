cmake_minimum_required(VERSION 3.25)

project(SkyrimRoR2 VERSION 0.74.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")

# CommonLibSSE-NG : la bibliotheque qui permet de parler au moteur de Skyrim.
# Memes reglages que SkyCraft (SE + AE, sans VR).
set(BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(ENABLE_SKYRIM_VR OFF CACHE BOOL "" FORCE)
set(SKSE_SUPPORT_XBYAK ON CACHE BOOL "" FORCE)
add_subdirectory(extern/CommonLibSSE-NG CommonLibSSE EXCLUDE_FROM_ALL)
if(NOT COMMAND add_commonlibsse_plugin)
	include(extern/CommonLibSSE-NG/cmake/CommonLibSSE.cmake)
endif()

add_commonlibsse_plugin(
	SkyrimRoR2
	NAME "SkyrimRoR2"
	AUTHOR "Opidox & Claude"
	USE_ADDRESS_LIBRARY
	SOURCES
		src/main.cpp
		src/Bridge.cpp
		src/Bridge.h
		src/Challenges.cpp
		src/Challenges.h
		src/Combat.cpp
		src/Combat.h
		src/Config.cpp
		src/Config.h
		src/Experience.cpp
		src/Experience.h
		src/Hud.cpp
		src/Hud.h
		src/InputWatcher.cpp
		src/InputWatcher.h
		src/Lang.cpp
		src/Lang.h
		src/Launcher.cpp
		src/Launcher.h
		src/Loadouts.cpp
		src/Loadouts.h
		src/Loot.cpp
		src/Loot.h
		src/Items.cpp
		src/Items.h
		src/Movement.cpp
		src/Movement.h
		src/Shrines.cpp
		src/Shrines.h
		src/Allies.cpp
		src/Altars.cpp
		src/Altars.h
		src/Allies.h
		src/Decor.cpp
		src/Effects.cpp
		src/Effects.h
		src/Decor.h
		src/Terrain.cpp
		src/Solids.cpp
		src/Coop.cpp
		src/Coop.h
		src/Solids.h
		src/SolidNif.h
		src/Terrain.h
		src/ThirdPerson.cpp
		src/ThirdPerson.h
		src/PCH.h
		../Shared/bridge_protocol.h
)

# Dessin du HUD RoR2 dans Skyrim : ImGui (dessin) et DirectXTK (lecture des icones PNG).
find_package(imgui CONFIG REQUIRED)
find_package(directxtk CONFIG REQUIRED)
target_link_libraries(SkyrimRoR2 PRIVATE imgui::imgui Microsoft::DirectXTK d3d11 dxgi d3dcompiler)

target_include_directories(SkyrimRoR2 PRIVATE src ${CMAKE_CURRENT_SOURCE_DIR}/../Shared)
target_precompile_headers(SkyrimRoR2 PRIVATE src/PCH.h)
target_compile_definitions(SkyrimRoR2 PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
if(MSVC)
	target_compile_options(SkyrimRoR2 PRIVATE /W4 /permissive- /Zc:preprocessor /utf-8 /MP)
endif()
