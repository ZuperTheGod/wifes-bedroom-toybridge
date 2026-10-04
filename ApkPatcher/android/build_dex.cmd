@echo off
rem Rebuilds ApkPatcher\android\phone_import.dex from PhoneImportActivity.java.
rem Needs a JDK on PATH and the Android SDK (platform android.jar + build-tools d8).
setlocal
set SDK=%LOCALAPPDATA%\Android\Sdk
for /f "delims=" %%p in ('dir /b /ad /o-n "%SDK%\platforms"') do if not defined PLATFORM set PLATFORM=%%p
for /f "delims=" %%b in ('dir /b /ad /o-n "%SDK%\build-tools"') do if not defined BT set BT=%%b
cd /d "%~dp0"
if exist classes rmdir /s /q classes
mkdir classes
javac --release 8 -Xlint:-options -cp "%SDK%\platforms\%PLATFORM%\android.jar" -d classes PhoneImportActivity.java || exit /b 1
call "%SDK%\build-tools\%BT%\d8.bat" --release --min-api 21 --lib "%SDK%\platforms\%PLATFORM%\android.jar" --output . classes\wbmods\PhoneImportActivity.class || exit /b 1
move /y classes.dex phone_import.dex >nul
rmdir /s /q classes
echo Built phone_import.dex
