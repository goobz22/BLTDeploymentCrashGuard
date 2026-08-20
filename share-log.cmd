@echo off
setlocal EnableDelayedExpansion
rem ============================================================
rem  BLT Deployment Crash Guard - log sharer
rem  Uploads Modules\BLTDeploymentCrashGuard\CrashGuard.log to a
rem  24-hour file host and prints the URL to send to your co-op
rem  partner. Override game detection with BANNERLORD_DIR.
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
set "LOGFILE=%GAME%\Modules\BLTDeploymentCrashGuard\CrashGuard.log"
if not exist "%LOGFILE%" (
  echo ERROR: no log found at "%LOGFILE%" ^(has the mod run yet?^)
  exit /b 1
)

echo Uploading log ^(24-hour link^)...
set "URL="
for /f "usebackq delims=" %%U in (`curl -fsS -F "reqtype=fileupload" -F "time=24h" -F "fileToUpload=@%LOGFILE%" https://litterbox.catbox.moe/resources/internals/api.php`) do set "URL=%%U"

if "%URL%"=="" (
  echo ERROR: upload failed. Check your internet connection and try again,
  echo or just send the file directly: "%LOGFILE%"
  exit /b 1
)

echo %URL%| clip
echo.
echo ============================================================
echo  Log uploaded ^(link valid 24 hours^):
echo.
echo    %URL%
echo.
echo  The link is already on your clipboard - paste it to your
echo  co-op partner.
echo ============================================================
exit /b 0
