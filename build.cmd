@echo off
cd /d "%~dp0"
dotnet build -c Release -p:Game=KK
if errorlevel 1 exit /b 1
copy /Y bin\Release\KK_SceneExplorer.dll "..\..\BepInEx\plugins\"
echo Done.
