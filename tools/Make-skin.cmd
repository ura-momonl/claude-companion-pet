@echo off
rem Drag one or more GIF / PNG files (named idle.gif, busy.gif, done.gif ...) or folders of PNG pictures onto this file to make a skin.
setlocal
if "%~1"=="" echo Drag your GIF or PNG files onto this file. See skins\README.md, "Make your own skin". & pause & exit /b 1
set /p NAME=Name for the new skin: 
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-skin.ps1" -Name "%NAME%" %*
pause
