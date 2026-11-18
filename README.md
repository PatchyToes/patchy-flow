# PatchyFlow

Personal push-to-talk dictation for Windows. A minimal, local-first replacement
for the parts of Wispr Flow that actually get used.

- Hold the hotkey (default **Ctrl+Win**), talk, release → text lands in whatever field has focus
- **Escape** while talking cancels
- Small waveform pill bottom-center so you can see it's hearing you
- Transcription is 100% local: Whisper on the GPU via Whisper.net (CUDA → Vulkan → CPU)
- Optional delete-only AI cleanup (filler words, false starts) via Claude Haiku or a local Ollama model
- Extras, each with its own hotkey and off switch: re-paste last dictation, dictation history
  window, screenshot snip with OCR, display-resolution wheel

**Taking this over from someone else, or pointing an AI agent at it? Read
[HANDOFF.md](HANDOFF.md) first.** `ONESHOT-PROMPT.md` is the original build spec,
kept for its landmine list. Do not rebuild from it: the code has fixes the spec lacks.

## Build

```
dotnet build -c Release
```

Exe: `bin\Release\net8.0-windows10.0.19041.0\win-x64\PatchyFlow.exe`. Needs the .NET 8 SDK.

## Where things live

Everything the app writes sits in a `PatchyFlowData` folder **next to the exe**, not in
`%APPDATA%` (which gets silently redirected when launched from a packaged app):

- `settings.json`: every setting; defaults documented in `Core/AppSettings.cs`
- `dictionary.txt`: personal vocabulary. Plain word = casing/bias fix, `spoken => written` = rewrite
- `models/`: Whisper model, downloaded on first run (default `ggml-large-v3-turbo.bin`, ~1.6 GB)
- `history.jsonl`, `stats.json`, `log.txt`: the log never contains transcript text

The tray menu opens all of these.

## Stack

.NET 8 WPF (+ WinForms for the tray), NAudio (WASAPI capture), Whisper.net, Anthropic C# SDK,
low-level keyboard hook for the chord, clipboard-swap paste for insertion.
