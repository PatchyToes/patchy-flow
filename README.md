# PatchyFlow

Personal push-to-talk dictation for Windows. A minimal, local-only replacement
for the parts of Wispr Flow Mike actually uses.

- Hold **Ctrl+Win**, talk, release → text lands in whatever field has focus
- **Escape** while talking cancels
- Small waveform pill bottom-center so you can see it's hearing you
- Transcription is 100% local: Whisper (large-v3-turbo) on the GPU via Whisper.net
- Personal dictionary at `%APPDATA%\PatchyFlow\dictionary.txt`
  (plain word = casing/bias fix, `spoken => written` = rewrite)
- Settings at `%APPDATA%\PatchyFlow\settings.json`, tray menu for everything

## Build

```
dotnet build -c Release
```

First run downloads the Whisper model (~1.6 GB) to `%APPDATA%\PatchyFlow\models`.

## Stack

.NET 8 WPF, NAudio (WASAPI capture), Whisper.net (CUDA → Vulkan → CPU fallback),
low-level keyboard hook for the chord, clipboard-swap paste for insertion.
