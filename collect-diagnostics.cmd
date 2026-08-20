@echo off
setlocal EnableDelayedExpansion
rem ============================================================
rem  BLT Deployment Crash Guard - diagnostics collector
rem  Bundles our log + BannerlordTogether's sync logs + the most
rem  recent crash report into one .zip and uploads it, so you can
rem  share EVERYTHING relevant in one link.
rem  Override game detection with BANNERLORD_DIR.
rem ============================================================

set "GAME=%BANNERLORD_DIR%"
if not "%GAME%"=="" goto :found
for %%D in (
  "C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord"
  "C:\Program Files\Steam\steamapps\common\Mount & Blade II Bannerlord"
  "C:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
  "D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
  "E:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
  "F:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
) do ( if "!GAME!"=="" if exist "%%~D\Modules" set "GAME=%%~D" )
if not "%GAME%"=="" goto :found
set /p GAME=Paste your Bannerlord install folder:

:found
set "GAME=%GAME:"=%"
set "STAGE=%TEMP%\bltguard-diag"
set "OUT=%TEMP%\bltguard-diagnostics.zip"
rmdir /s /q "%STAGE%" >nul 2>&1
mkdir "%STAGE%" >nul 2>&1
del "%OUT%" >nul 2>&1

echo Collecting diagnostics...
copy /y "%GAME%\Modules\BLTDeploymentCrashGuard\CrashGuard.log" "%STAGE%\" >nul 2>&1
copy /y "%GAME%\Modules\BLTDeploymentCrashGuard\CrashGuard.log.1" "%STAGE%\" >nul 2>&1
copy /y "%GAME%\Modules\BLTDeploymentCrashGuard\guardconfig.json" "%STAGE%\" >nul 2>&1
copy /y "%USERPROFILE%\Desktop\bt-sync-host.txt" "%STAGE%\" >nul 2>&1
copy /y "%USERPROFILE%\Desktop\bt-sync-client.txt" "%STAGE%\" >nul 2>&1
copy /y "%USERPROFILE%\Desktop\bt-sync-solo.txt" "%STAGE%\" >nul 2>&1

rem newest crash report from Documents, if any
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
echo  Contains: CrashGuard.log(+.1), guardconfig.json,
echo  bt-sync-*.txt, newest crash report.
echo ============================================================
exit /b 0
