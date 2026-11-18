# PatchyFlow: handoff notes

## If you are the human

You don't need to read past this section. Open a coding agent that can **run commands and
edit files on your Windows PC** (Claude Code, Codex, Cursor's agent, GitHub Copilot's agent,
not a plain chat window), point it at this folder, and paste:

> Read HANDOFF.md in this folder and follow it. Interview me first, one question at a time.

Everything below is written for the agent.

---

## If you are the agent

You've been handed a working, personal push-to-talk dictation app. Your job is to install it
on this machine, tune it to this person, and keep it working, not to redesign it. The person
you're working with does not read code: talk in terms of what the app does, never file names or
mechanisms, and give one recommendation rather than a menu.

**Do not rebuild this from `ONESHOT-PROMPT.md`.** That's the original spec. The code has months
of fixes the spec never mentions (stuck Windows key, Whisper hallucinating "thank you" on
silence, lost dictation tails, headsets that rename themselves). `git log` explains why most
things are the way they are; read it before "simplifying" anything.

### What the app does

- Hold a hotkey, talk, release: transcribed text is pasted into the focused field.
  Escape cancels. A small waveform pill shows it's listening.
- Transcription is local (Whisper via Whisper.net). Optional **delete-only** cleanup pass
  removes filler and false starts via Claude Haiku (cloud, needs an API key) or Ollama (local).
  It never rephrases. A guard rejects any output that isn't a subset of what was said and
  falls back to the raw transcript. Under-correction is the product guarantee. Keep it that way.
- Extras, each independently switchable in settings: re-paste last dictation (Alt+Shift+Z),
  history window, screenshot snip with OCR (Ctrl+Shift+S), display-resolution wheel
  (Scroll Lock, cycles through preset resolutions; written for a 4K desktop monitor).
- Tray icon menu: settings window, dictionary, history, log, start with Windows, exit.

### Step 1: interview, ONE question at a time

Ask, wait for the answer, then ask the next. Check anything you can check yourself first.

1. **Hotkey.** Default is hold Ctrl+Win. If they use or used Wispr Flow, offer to import their
   binding and dictionary (`%APPDATA%\Wispr Flow\config.json` and `flow.sqlite`, read-only;
   details in `ONESHOT-PROMPT.md`).
2. **Hardware.** Run `Get-CimInstance Win32_VideoController` and check RAM. Then pick the
   Whisper model. This is the decision that matters most on a laptop:
   - Discrete GPU with 4 GB+ VRAM, or a recent iGPU with 16 GB+ system RAM:
     `ggml-large-v3-turbo.bin` (1.6 GB, best quality).
   - Thin or older laptop, or integrated graphics with 8 GB RAM: `ggml-small.bin`
     (~470 MB) or English-only `ggml-small.en.bin`. Noticeably less accurate on
     rare words but fast enough to feel instant. Start here if in doubt; moving up
     later is a one-line settings change.
   - No usable GPU at all (CPU fallback): `ggml-base.en.bin` (~150 MB). Tell them
     accuracy will be rough.
   Set it via `"Model"` in `settings.json`; the app downloads whatever is named there
   on next start (tray shows progress). Confirm which runtime loaded in `log.txt`
   ("runtime: Cuda/Vulkan/Cpu"). Vulkan needs current GPU drivers.
3. **Cleanup pass.** One sentence: it deletes "um", repeated words and the abandoned half of
   self-corrections, nothing else, and costs on the order of a dollar a year via an Anthropic
   API key. Options: cloud (needs a key from console.anthropic.com in `"AnthropicApiKey"`),
   local via Ollama (only on a machine with a real GPU; don't offer it on a low-end laptop),
   or off (`"CleanupEnabled": false`). Without a key the pass silently disables; the app
   still works.
4. **Microphone.** The settings window lists capture devices in a dropdown; or set
   `"MicDeviceName"` to a substring. Leave empty for the Windows default communications mic.
5. **Extras.** Ask which they want. On a laptop, turn the resolution wheel off
   (`"Display": { "Enabled": false }`); its presets are desktop resolutions.
6. **Start with Windows?** Tray menu toggle.
7. **Dashes.** The cleanup pass converts em/en dashes to commas because the original owner
   hates them. If this person doesn't mind dashes, remove that step (`StripDashes` in
   `Speech/CleanupPass.cs`).

### Step 2: install and build

- `winget install Microsoft.DotNet.SDK.8` if `dotnet --version` doesn't report 8.x.
- `dotnet build -c Release` in this folder. Exe lands at
  `bin\Release\net8.0-windows10.0.19041.0\win-x64\PatchyFlow.exe`.
- All app data (settings, dictionary, model, history, log) lives in `PatchyFlowData`
  **next to the exe**. Don't move the exe without it. Nothing goes to `%APPDATA%`.

### Step 3: launch (read this even if everything else seems obvious)

If you are running inside a packaged desktop app (Claude Desktop, some others), every process
you start inherits its sandbox and its view of `%APPDATA%` and the registry. Launch the app
via Explorer instead so it runs as the real user:

```
explorer.exe "C:\full\path\to\PatchyFlow.exe"
```

Then verify for real: open Notepad, hold the hotkey, say a sentence, release. Text should
appear. Check `PatchyFlowData\log.txt` for the model load line and the runtime that loaded.
`PatchyFlow.exe --transcribe clip.wav` writes `clip.wav.txt` without the UI if you need a
headless smoke test.

### Step 4: shipping a change

A commit or a Debug build changes nothing the person runs. To ship: `dotnet build -c Release`,
exit the running instance (tray, Exit), relaunch via `explorer.exe`. Verify by dictating.

### Things that will bite you (short version; long version in `ONESHOT-PROMPT.md`)

- The keyboard hook swallows the chord key so Win doesn't open Start. Only swallow a key-up if
  you swallowed its key-down, or Windows thinks Win is held forever. Already handled; don't
  "clean up" the hook.
- Paste is clipboard-swap plus synthetic Ctrl+V. The wait before restoring the clipboard scales
  with text length on purpose. The physical modifier keys must be released first or the paste
  becomes Ctrl+Win+V.
- Whisper's own no-speech score is useless for catching hallucinations (it's per 30 s window).
  The app measures actual audio energy under each segment instead. Don't swap it back.
- Never log transcript text. Lengths and timings only.
- Do not add streaming or chunked transcription. A 5-minute dictation transcribes in seconds on
  a GPU, and chunking makes quality worse.
- WPF and WinForms in one project: `Application`, `Clipboard`, `Color`, `Point` are ambiguous
  without the aliases already in place.

### Working style

Small changes, one at a time, verified by actually dictating. Read `git log` before touching
anything that looks odd; it usually has a reason. Conventional commits (`feat:`, `fix:`).
