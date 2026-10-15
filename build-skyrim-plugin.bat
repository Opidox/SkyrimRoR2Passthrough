@echo off
setlocal
cd /d "%~dp0"
echo.
echo ===== SkyrimRoR2 : compilation du plugin Skyrim =====
echo.

rem --- 1. Trouver Visual Studio et charger ses outils (compilateur C++, CMake) ---
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo [ERREUR] Visual Studio est introuvable.
  goto :fin
)
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH (
  echo [ERREUR] Les outils C++ de Visual Studio sont introuvables.
  goto :fin
)
call "%VSPATH%\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 >nul

where git >nul 2>nul
if errorlevel 1 (
  echo [ERREUR] Git est introuvable. Installe-le depuis https://git-scm.com/download/win
  echo          avec les options par defaut, puis relance ce fichier.
  goto :fin
)
where cmake >nul 2>nul
if errorlevel 1 (
  echo [ERREUR] CMake est introuvable. Dans Visual Studio Installer, clique sur Modifier,
  echo          puis coche "Outils CMake C++ pour Windows" dans Developpement Desktop en C++.
  goto :fin
)

rem --- 2. CommonLibSSE-NG, la meme version que SkyCraft (premiere fois seulement) ---
if not exist "skse\extern\CommonLibSSE-NG\CMakeLists.txt" (
  echo [1/4] Telechargement de CommonLibSSE-NG...
  git clone https://github.com/alandtse/CommonLibVR.git "skse\extern\CommonLibSSE-NG" || goto :erreur
  git -C "skse\extern\CommonLibSSE-NG" checkout d61bca4de789428aa7d98a770b1323ddf1bb855c || goto :erreur
  git -C "skse\extern\CommonLibSSE-NG" submodule update --init --recursive || goto :erreur
) else (
  echo [1/4] CommonLibSSE-NG deja present.
)

rem --- 3. vcpkg, qui telecharge les autres bibliotheques (premiere fois seulement) ---
if not exist ".tools\vcpkg\vcpkg.exe" (
  echo [2/4] Installation de vcpkg...
  if not exist ".tools\vcpkg\.git" git clone https://github.com/microsoft/vcpkg ".tools\vcpkg" || goto :erreur
  call ".tools\vcpkg\bootstrap-vcpkg.bat" -disableMetrics || goto :erreur
) else (
  echo [2/4] vcpkg deja present.
)

rem --- 4. Compilation ---
echo [3/4] Compilation. La premiere fois, compte 15 a 30 minutes : c'est normal.
pushd skse
cmake --preset default || (popd & goto :erreur)
cmake --build --preset release || (popd & goto :erreur)
popd

rem --- 5. Preparation d'un zip a installer avec ton gestionnaire de mods ---
echo [4/4] Preparation du zip du mod...
if exist "dist" rmdir /s /q "dist"
mkdir "dist\SkyrimRoR2-Skyrim\SKSE\Plugins"
copy /y "skse\build\RelWithDebInfo\SkyrimRoR2.dll" "dist\SkyrimRoR2-Skyrim\SKSE\Plugins\" >nul || goto :erreur
copy /y "skse\SkyrimRoR2.ini" "dist\SkyrimRoR2-Skyrim\SKSE\Plugins\" >nul || goto :erreur
copy /y "skse\SkyrimRoR2.esp" "dist\SkyrimRoR2-Skyrim\" >nul || goto :erreur
rem 0.74 : les textes du mod dans chaque langue de Skyrim
mkdir "dist\SkyrimRoR2-Skyrim\SKSE\Plugins\SkyrimRoR2_Lang"
copy /y "skse\lang\*.txt" "dist\SkyrimRoR2-Skyrim\SKSE\Plugins\SkyrimRoR2_Lang\" >nul || goto :erreur
powershell -NoProfile -Command "Compress-Archive -Path 'dist\SkyrimRoR2-Skyrim\*' -DestinationPath 'dist\SkyrimRoR2-Skyrim.zip' -Force" || goto :erreur

rem --- 6. Mise a jour directe dans MO2, si le mod y est deja installe ---
set "MO2MOD=%LOCALAPPDATA%\ModOrganizer\Skyrim Special Edition\mods\SkyrimRoR2-Skyrim"
set "MO2DONE=0"
if exist "%MO2MOD%\SKSE\Plugins" (
  copy /y "skse\build\RelWithDebInfo\SkyrimRoR2.dll" "%MO2MOD%\SKSE\Plugins\" >nul && set "MO2DONE=1"
  rem Les pouvoirs des variantes de competences (a cocher dans la liste des plugins de MO2).
  copy /y "skse\SkyrimRoR2.esp" "%MO2MOD%\" >nul
  if not exist "%MO2MOD%\SKSE\Plugins\SkyrimRoR2_Lang" mkdir "%MO2MOD%\SKSE\Plugins\SkyrimRoR2_Lang"
  copy /y "skse\lang\*.txt" "%MO2MOD%\SKSE\Plugins\SkyrimRoR2_Lang\" >nul
  rem Le fichier de reglages n'est copie que s'il manque, pour garder tes reglages.
  rem S'il existe, les reglages des nouvelles versions y sont ajoutes (tes valeurs ne bougent pas).
  if not exist "%MO2MOD%\SKSE\Plugins\SkyrimRoR2.ini" (
    copy /y "skse\SkyrimRoR2.ini" "%MO2MOD%\SKSE\Plugins\" >nul
  ) else (
    echo Mise a jour de ton SkyrimRoR2.ini dans MO2 :
    powershell -NoProfile -ExecutionPolicy Bypass -File "tools\merge-ini.ps1" -Modele "skse\SkyrimRoR2.ini" -Cible "%MO2MOD%\SKSE\Plugins\SkyrimRoR2.ini"
  )
)

echo.
echo ===== TERMINE =====
echo Le mod Skyrim est pret : dist\SkyrimRoR2-Skyrim.zip
if "%MO2DONE%"=="1" (
  echo Deja mis a jour dans MO2, rien a reinstaller.
) else (
  echo MO2 : mod SkyrimRoR2-Skyrim introuvable, ou Skyrim est encore ouvert.
  echo       Installe le zip a la main dans MO2, en choisissant Remplacer.
)
goto :fin

:erreur
echo.
echo ===== ECHEC =====
echo Quelque chose a bloque. Copie les dernieres lignes de cette fenetre et envoie-les a Claude.

:fin
echo.
pause
