@echo off
cd /d "%~dp0"
dotnet build -c Release -p:Game=KK
if errorlevel 1 exit /b 1
copy /Y bin\Release\KK_SceneExplorer.dll "..\..\BepInEx\plugins\"
if not exist "..\..\BepInEx\plugins\KK_SceneExplorer\" mkdir "..\..\BepInEx\plugins\KK_SceneExplorer\"
copy /Y libwebp.dll "..\..\BepInEx\plugins\KK_SceneExplorer\"
echo Done.
