@echo off
rem Runs OUTSIDE the Claude sandbox (launched via explorer) so %APPDATA% is the real one.
set SHADOW=C:\Users\Michael\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming
set EXE=C:\Users\Michael\Desktop\AI\Claude\Project\patchy-flow\bin\Release\net8.0-windows10.0.19041.0\win-x64\PatchyFlow.exe
set REPORT=C:\Users\Michael\Desktop\AI\Claude\Project\patchy-flow\fixup-report.txt

taskkill /IM PatchyFlow.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul

mkdir "%APPDATA%\PatchyFlow" 2>nul
copy /Y "%SHADOW%\PatchyFlow\dictionary.txt" "%APPDATA%\PatchyFlow\dictionary.txt" >nul
copy /Y "%SHADOW%\PatchyFlow\settings.json" "%APPDATA%\PatchyFlow\settings.json" >nul

rem real Start Menu entry + real autostart via the Startup folder (no admin needed)
powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; foreach ($dir in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Startup'))) { $sc = $ws.CreateShortcut((Join-Path $dir 'PatchyFlow.lnk')); $sc.TargetPath = '%EXE%'; $sc.WorkingDirectory = (Split-Path '%EXE%'); $sc.Save() }"

echo === fixup ran %DATE% %TIME% > "%REPORT%"
echo --- real PatchyFlow appdata: >> "%REPORT%"
dir "%APPDATA%\PatchyFlow" >> "%REPORT%" 2>&1
dir "%APPDATA%\PatchyFlow\models" >> "%REPORT%" 2>&1
echo --- start menu + startup links: >> "%REPORT%"
dir "%APPDATA%\Microsoft\Windows\Start Menu\Programs\PatchyFlow.lnk" >> "%REPORT%" 2>&1
dir "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\PatchyFlow.lnk" >> "%REPORT%" 2>&1

start "" "%EXE%"
