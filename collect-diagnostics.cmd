@echo off
setlocal EnableDelayedExpansion
rem ============================================================
rem  BLT Deployment Crash Guard - diagnostics collector
rem  Bundles EVERYTHING a bug report needs into one .zip and
rem  uploads it:
rem    - CrashGuard.log + all rotated segments (.1 .. .6)
rem    - guardconfig.json, hero-identity.json, SubModule.xml
rem    - BannerlordTogether's bt-sync-*.txt (Desktop)
rem    - the game's own logs (rgl_log / rgl_log_errors / watchdog /
rem      launcher, newest few) from %ProgramData%
rem    - the newest game crash folder (text files only) and the
rem      newest crash report .html from Documents
rem  Override game detection with BANNERLORD_DIR.
rem  KEEP THE STEAM PATH LIST BELOW IDENTICAL in install.cmd,
rem  share-log.cmd and this file (tools/lint-scripts.sh checks it).
rem ============================================================

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
set "MOD=%GAME%\Modules\BLTDeploymentCrashGuard"
set "TWLOGS=%ProgramData%\Mount and Blade II Bannerlord\logs"
set "TWCRASH=%ProgramData%\Mount and Blade II Bannerlord\crashes"
set "STAGE=%TEMP%\bltguard-diag"
set "OUT=%TEMP%\bltguard-diagnostics.zip"
rmdir /s /q "%STAGE%" >nul 2>&1
mkdir "%STAGE%\mod" "%STAGE%\game-logs" "%STAGE%\bt-sync" >nul 2>&1
del "%OUT%" >nul 2>&1

echo Collecting diagnostics...
copy /y "%MOD%\CrashGuard.log" "%STAGE%\mod\" >nul 2>&1
for %%N in (1 2 3 4 5 6) do copy /y "%MOD%\CrashGuard.log.%%N" "%STAGE%\mod\" >nul 2>&1
copy /y "%MOD%\guardconfig.json" "%STAGE%\mod\" >nul 2>&1
copy /y "%MOD%\hero-identity.json" "%STAGE%\mod\" >nul 2>&1
copy /y "%MOD%\SubModule.xml" "%STAGE%\mod\" >nul 2>&1
for %%F in (bt-sync-host.txt bt-sync-client.txt bt-sync-solo.txt) do copy /y "%USERPROFILE%\Desktop\%%F" "%STAGE%\bt-sync\" >nul 2>&1

rem "rgl_log_*.txt" also matches "rgl_log_errors_*.txt" - exclude those here so the 3-file
rem cap is spent on the main game logs (the errors logs get their own call below).
call :newest "%TWLOGS%" "rgl_log_*.txt" 3 "rgl_log_errors_"
call :newest "%TWLOGS%" "rgl_log_errors_*.txt" 3
call :newest "%TWLOGS%" "watchdog_log_*.txt" 2
call :newest "%TWLOGS%" "launcher_log_*.txt" 1

rem newest TaleWorlds crash folder - text files only (dumps are huge)
for /f "delims=" %%D in ('dir /b /ad /o-d "%TWCRASH%" 2^>nul') do (
  mkdir "%STAGE%\game-crash" >nul 2>&1
  copy /y "%TWCRASH%\%%D\*.txt" "%STAGE%\game-crash\" >nul 2>&1
  goto :btcrash
)

:btcrash
rem newest ButterLib / BannerlordTogether crash report from Documents, if any
for /f "delims=" %%F in ('dir /b /o-d "%USERPROFILE%\Documents\*.html" 2^>nul') do (
  echo %%F | findstr /i "crash" >nul && ( copy /y "%USERPROFILE%\Documents\%%F" "%STAGE%\crashreport.html" >nul 2>&1 & goto :zipit )
)

:zipit
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%OUT%' -Force" >nul 2>&1
if not exist "%OUT%" ( echo ERROR: could not create the zip. Files are staged in "%STAGE%". & exit /b 1 )

echo Uploading bundle...
set "RESP=%TEMP%\bltguard-diag-resp.txt"
del "%RESP%" >nul 2>&1
curl -fsS -F "reqtype=fileupload" -F "time=72h" -F "fileToUpload=@%OUT%" https://litterbox.catbox.moe/resources/internals/api.php > "%RESP%" 2>nul
findstr /b "https://" "%RESP%" >nul 2>&1 || (
  curl -fsS -F "file=@%OUT%" https://0x0.st > "%RESP%" 2>nul
)
findstr /b "https://" "%RESP%" >nul 2>&1 || (
  echo Upload failed. Send this file directly: "%OUT%"
  exit /b 1
)
set /p URL=<"%RESP%"
echo %URL%| clip
echo.
echo ============================================================
echo  Diagnostics bundle uploaded (link on your clipboard):
echo    %URL%
echo  Contains: CrashGuard.log + rotated .1-.6, guardconfig.json,
echo  hero-identity.json, SubModule.xml, bt-sync-*.txt, the game's
echo  rgl/watchdog/launcher logs (newest), the newest game crash
echo  folder (text) and the newest crash report.
echo ============================================================
exit /b 0

:newest
rem %1 = folder, %2 = file pattern, %3 = how many newest files to copy,
rem %4 = optional substring to EXCLUDE from the matches (findstr /v /c:"" matches nothing)
set "NSRC=%~1"
set "NPAT=%~2"
set /a NLIMIT=%~3
set "NEXCL=%~4"
if "%NEXCL%"=="" set "NEXCL=__no_exclude__"
set /a NCOUNT=0
for /f "delims=" %%F in ('dir /b /o-d "%NSRC%\%NPAT%" 2^>nul ^| findstr /v /i /c:"%NEXCL%"') do (
  set /a NCOUNT+=1
  if !NCOUNT! leq !NLIMIT! copy /y "%NSRC%\%%F" "%STAGE%\game-logs\" >nul 2>&1
)
exit /b 0
