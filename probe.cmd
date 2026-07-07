@echo off
rem key-presence probe: reports LENGTH of the key only, never its value
set REPORT=C:\Users\Michael\Desktop\AI\Claude\Project\patchy-flow\fixup-report.txt
powershell -NoProfile -Command "$r = Get-Content \"$env:APPDATA\PatchyFlow\settings.json\" -Raw | ConvertFrom-Json; $s = Get-Content 'C:\Users\Michael\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\PatchyFlow\settings.json' -Raw | ConvertFrom-Json; $f = Get-Item \"$env:APPDATA\PatchyFlow\settings.json\"; \"real settings: keyLen=$($r.AnthropicApiKey.Length) cleanupEnabled=$($r.CleanupEnabled) lastWrite=$($f.LastWriteTime)\" ; \"shadow settings: keyLen=$($s.AnthropicApiKey.Length)\"" > "%REPORT%" 2>&1
