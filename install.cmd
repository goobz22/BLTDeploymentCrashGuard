@echo off
setlocal EnableDelayedExpansion
rem ============================================================
rem  BLT Deployment Crash Guard - installer/updater
rem  Downloads the mod into <Bannerlord>\Modules\BLTDeploymentCrashGuard
rem  and verifies every file against the release manifest.
rem  Override auto-detection by setting BANNERLORD_DIR first.
rem  KEEP THE STEAM PATH LIST BELOW IDENTICAL in share-log.cmd,
rem  collect-diagnostics.cmd and this file (tools/lint-scripts.sh checks it).
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
set "GAME=%GAME:"=%"
if not exist "%GAME%\Modules" (
  echo ERROR: "%GAME%" does not look like a Bannerlord install ^(no Modules folder^).
  exit /b 1
)

set "MOD=%GAME%\Modules\BLTDeploymentCrashGuard"
set "DLLDIR=%MOD%\bin\Win64_Shipping_Client"
echo Installing into: "%MOD%"
mkdir "%DLLDIR%" 2>nul

rem The mod is TWO assemblies since v1.2.0: the harness (BLTDeploymentCrashGuard.dll, the
rem module Bannerlord loads) and the payload (BLTDeploymentCrashGuard.Payload.dll, every
rem guard/fix/tracer — the harness loads it). Both must be installed together, from the
rem SAME release: dist/manifest.txt (written by tools/release.sh from one build) carries
rem the SHA256 of each file and this script refuses a mismatched set.
rem If the game is running it locks the loaded DLLs; a rename is still allowed,
rem so move the old files aside and download the new ones next to them.
for %%F in (BLTDeploymentCrashGuard.dll BLTDeploymentCrashGuard.Payload.dll) do (
  if exist "%DLLDIR%\%%F" (
    del /f /q "%DLLDIR%\%%F.prev" >nul 2>&1
    ren "%DLLDIR%\%%F" "%%F.prev" >nul 2>&1
  )
)

curl -fsSL -o "%MOD%\SubModule.xml" "%REPO%/dist/SubModule.xml" || goto :fail
curl -fsSL -o "%DLLDIR%\BLTDeploymentCrashGuard.dll" "%REPO%/dist/BLTDeploymentCrashGuard.dll" || goto :fail
curl -fsSL -o "%DLLDIR%\BLTDeploymentCrashGuard.Payload.dll" "%REPO%/dist/BLTDeploymentCrashGuard.Payload.dll" || goto :fail

rem ---- integrity: every downloaded file must match the release manifest ----
set "MANIFEST=%TEMP%\bltguard-manifest.txt"
del "%MANIFEST%" >nul 2>&1
curl -fsSL -o "%MANIFEST%" "%REPO%/dist/manifest.txt" || goto :nomanifest
where certutil >nul 2>&1 || goto :nomanifest
set "BAD="
set "CHECKED=0"
for /f "usebackq tokens=1,2" %%A in ("%MANIFEST%") do (
  if /i "%%B"=="SubModule.xml" call :verify "%MOD%\SubModule.xml" %%A
  if /i "%%B"=="BLTDeploymentCrashGuard.dll" call :verify "%DLLDIR%\BLTDeploymentCrashGuard.dll" %%A
  if /i "%%B"=="BLTDeploymentCrashGuard.Payload.dll" call :verify "%DLLDIR%\BLTDeploymentCrashGuard.Payload.dll" %%A
)
if defined BAD (
  echo.
  echo ERROR: downloaded files do not match the release manifest:%BAD%
  echo The release may be mid-update on GitHub. Run this again in a minute; if it
  echo keeps failing, report it with the exact message above.
  goto :fail
)
echo Verified %CHECKED% file(s) against the release manifest.
goto :verified

:nomanifest
echo (no release manifest or certutil available - skipping the integrity check)

:verified
if not "%BLTGUARD_BIN%"=="" (
  echo %BLTGUARD_BIN%> "%MOD%\logstream.txt"
  echo Log streaming enabled ^(bin %BLTGUARD_BIN%^).
)

echo.
echo ============================================================
echo  Installed successfully.
echo  In the Bannerlord launcher, tick "BLT Deployment Crash Guard"
echo  in the Singleplayer mods list, AFTER BannerlordTogether.
echo  Log file: Modules\BLTDeploymentCrashGuard\CrashGuard.log
echo ============================================================
exit /b 0

:verify
rem %1 = file, %2 = expected SHA256 (lower/upper case both accepted)
set "VFILE=%~1"
set "VEXPECT=%~2"
set "VGOT="
for /f "skip=1 tokens=*" %%H in ('certutil -hashfile "%VFILE%" SHA256 2^>nul') do (
  if not defined VGOT set "VGOT=%%H"
)
set "VGOT=%VGOT: =%"
set /a CHECKED+=1
if /i not "%VGOT%"=="%VEXPECT%" set "BAD=%BAD% %~nx1"
exit /b 0

:fail
echo.
echo ERROR: install failed. Check your internet connection; if Bannerlord is
echo running, close it and run this again.
exit /b 1
