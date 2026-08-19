@echo off
setlocal EnableDelayedExpansion
rem ============================================================
rem  BLT Deployment Crash Guard - installer/updater
rem  Downloads the mod into <Bannerlord>\Modules\BLTDeploymentCrashGuard
rem  Override auto-detection by setting BANNERLORD_DIR first.
rem ============================================================

set "REPO=https://raw.githubusercontent.com/goobz22/BLTDeploymentCrashGuard/main"
set "GAME=%BANNERLORD_DIR%"

if not "%GAME%"=="" goto :found

for %%D in (
  "C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord"
  "C:\Program Files\Steam\steamapps\common\Mount & Blade II Bannerlord"
  "C:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
  "D:\Steam\steamapps\common\Mount & Blade II Bannerlord"
  "D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
  "E:\Steam\steamapps\common\Mount & Blade II Bannerlord"
  "E:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
  "F:\Steam\steamapps\common\Mount & Blade II Bannerlord"
  "F:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
  "G:\Steam\steamapps\common\Mount & Blade II Bannerlord"
  "G:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
) do (
  if "!GAME!"=="" if exist "%%~D\Modules" set "GAME=%%~D"
)

if not "%GAME%"=="" goto :found
echo Could not find Mount ^& Blade II Bannerlord automatically.
set /p GAME=Paste your Bannerlord install folder (contains bin\ and Modules\):

:found
if not exist "%GAME%\Modules" (
  echo ERROR: "%GAME%" does not look like a Bannerlord install ^(no Modules folder^).
  exit /b 1
)

set "MOD=%GAME%\Modules\BLTDeploymentCrashGuard"
echo Installing into: %MOD%
mkdir "%MOD%\bin\Win64_Shipping_Client" 2>nul

curl -fsSL -o "%MOD%\SubModule.xml" "%REPO%/dist/SubModule.xml" || goto :fail
curl -fsSL -o "%MOD%\bin\Win64_Shipping_Client\BLTDeploymentCrashGuard.dll" "%REPO%/dist/BLTDeploymentCrashGuard.dll" || goto :fail

echo.
echo ============================================================
echo  Installed successfully.
echo  In the Bannerlord launcher, tick "BLT Deployment Crash Guard"
echo  in the Singleplayer mods list, AFTER BannerlordTogether.
echo  Log file: Modules\BLTDeploymentCrashGuard\CrashGuard.log
echo ============================================================
exit /b 0

:fail
echo.
echo ERROR: download failed. Check your internet connection and try again.
exit /b 1
