# Build me a local push-to-talk dictation app for Windows

Copy everything below this line into Claude (Fable/Opus) running in Claude Code on the Windows machine where you want the app.

---

I want you to build me a personal, local-first push-to-talk dictation app for Windows — a replacement for the core of Wispr Flow: hold a hotkey, talk, release, and polished text appears in whatever text field has focus. Transcription runs locally on my GPU (free, private, offline); an optional AI cleanup pass uses a Claude Haiku API call.

## How to run this project

**Interview me first, ONE question at a time.** Ask a question, wait for my answer, then ask the next. Do not present a wall of questions, and do not start building until the interview is done. After the interview, build the entire app end to end without stopping — compile it, launch it, and verify it with the smoke test described below before you hand it to me. You are on my machine: run the commands yourself, never tell me to run build steps manually.

## The interview

Adapt later questions based on earlier answers; skip questions you can answer yourself by checking my machine.

1. **"Do you currently use Wispr Flow on this PC?"** If yes: check `%APPDATA%\Wispr Flow\config.json` for my real push-to-talk binding (the `shortcuts` map — the entry whose value is `"ptt"`, keys are Windows virtual-key codes joined by `+`) and my preferences (`enableSounds`, mic device). Also open `%APPDATA%\Wispr Flow\flow.sqlite` read-only (Python sqlite3 with `?mode=ro`) and export the `Dictionary` table (`phrase`, `replacement`, `isDeleted=0`) to seed my personal dictionary. If no: default hotkey is hold Ctrl+Win, and start with an empty dictionary.
2. **"What GPU do you have?"** (Check yourself: `Get-CimInstance Win32_VideoController`.) Any modern discrete GPU or recent iGPU works via Vulkan. Weak/no GPU: use the `ggml-medium.bin` or `ggml-small.bin` Whisper model instead of large-v3-turbo, or offer the cleanup-only cloud path.
3. **"Do you want the AI cleanup pass?"** Explain it in one sentence: it deletes filler words and resolves self-corrections ("Tuesday... no wait, Thursday" → "Thursday") but never rephrases; costs roughly a dollar per year of heavy use via a Claude API key from console.anthropic.com. If yes and they have no key, build everything anyway — the pass silently disables until a key is pasted into settings.
4. **"Do you use a headset or a specific mic, and do you run virtual audio software (Voicemeeter, VB-Cable)?"** If virtual devices exist, default to the Windows *communications* capture device and expose a name-substring override in settings.
5. **"Where should the project folder live?"**

## The build spec

**Stack:** C# / .NET 8 / WPF tray app (install SDK via `winget install Microsoft.DotNet.SDK.8` if missing). NuGet: `NAudio`, `Whisper.net` + `Whisper.net.Runtime` + `Whisper.net.Runtime.Cuda` + `Whisper.net.Runtime.Vulkan`, and `Anthropic` (official SDK) if cleanup is wanted.

**Behavior:** hold the chord → small dark pill overlay appears bottom-center of the active monitor with live voice bars; release → transcribe → (optional cleanup) → paste into the focused field; Escape while talking cancels (short low tone); a start ping plays when the mic opens (settable). Tray icon menu: open dictionary, open settings, reload config, open log, start-with-Windows toggle (HKCU Run key), exit. Settings live in `%APPDATA%\<AppName>\settings.json`, dictionary in `dictionary.txt` (plain word = casing/vocabulary fix; `spoken => written` = rewrite rule), log in `log.txt` (never log transcript text, only lengths/timings). Single-instance mutex. First run downloads `ggml-large-v3-turbo.bin` (~1.6 GB) from `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/` into `%APPDATA%\<AppName>\models`.

**Architecture (one class each):** keyboard hook, audio recorder, overlay window, transcriber, model downloader, cleanup pass, text injector, dictionary, settings, tray icon, plus a controller state machine (Idle → Recording → Processing → Idle; ignore chord events while Processing; treat holds under ~350 ms as accidental; keep capturing ~200 ms after release so the last word isn't clipped; a max-duration timer force-stops runaway recordings).

**Landmines — these will each cost you an hour if you ignore them:**

- **WPF + WinForms in one project** (`UseWPF` + `UseWindowsForms`): `Application`, `MessageBox`, `Rectangle`, `Point`, `Color`, `Clipboard` become ambiguous — add `using X = System.Windows....X;` aliases. The Windows Desktop SDK also has a slimmer implicit-usings set: add a `GlobalUsings.cs` with `global using System.IO;` and `global using System.Net.Http;`.
- **Keyboard hook** (WH_KEYBOARD_LL): ignore events with the `LLKHF_INJECTED` flag (0x10) or your own synthetic Ctrl+V will confuse the state machine. Swallow the chord's non-modifier key (return 1) so it never reaches other apps — critical when the key is LWin, or the Start menu opens. **Only swallow the key-up if you swallowed its key-down**: if the chord was pressed in the other order (target key first, modifier second) the down already leaked to the OS, and eating the up leaves Windows thinking the key is held forever — a sticky Win key. Escape during recording cancels; suppress the target key's events until it's physically released after a cancel.
- **Audio:** `WasapiCapture` with a 30 ms buffer (`new WasapiCapture(device, true, 30)`) or the waveform updates at a choppy 10 Hz. Convert to 16 kHz mono float for Whisper: downmix channels, then linear-resample with the fractional position carried across buffers. Pick the mic by name-substring match, falling back to the default *communications* endpoint (never the default console device on machines with virtual audio).
- **Whisper.net:** set `RuntimeOptions.RuntimeLibraryOrder = [Cuda, Vulkan, Cpu]` before creating the factory. CUDA will silently fail to load without the CUDA toolkit installed — that's fine, Vulkan is nearly as fast on any modern GPU (measured ~60x realtime for large-v3-turbo on an RTX 4070 Super; a 5-minute dictation transcribes in ~5 s, so do NOT build chunked/streaming transcription). Run one warm-up inference on 0.5 s of silence at startup — the first GPU inference pays a pipeline-compile cost (~3 s) that's cached afterward. Pass the dictionary as the initial prompt ("Glossary: word1, word2...") and apply casing/rewrite fixes as a regex post-pass (use lookarounds, not `\b`, so entries with @ or : work). Strip `[BLANK_AUDIO]`-style bracket artifacts.
- **Overlay:** WPF window with `ShowActivated=False`, `Focusable=False`, Topmost, and `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` set in SourceInitialized — it must never steal focus from the field being dictated into. Keep it SMALL (~96×38) and render the bars as an **in-place spectrum** (every bar reacts to the live RMS level with a center-weighted bell and per-bar phase wobble), not a scrolling waveform — scrolling reads slower. Position on the monitor containing the foreground window.
- **Text injection:** save clipboard text → set transcript → synthetic Ctrl+V via SendInput → wait ~250 ms → restore clipboard. Before sending Ctrl+V, poll `GetAsyncKeyState` until the user's physical modifier keys are released (max 2 s), or the paste becomes Ctrl+Win+V. All clipboard calls on the UI thread with retries (clipboard is often briefly locked).
- **Cleanup pass (Claude `claude-haiku-4-5`, official Anthropic C# SDK — `new AnthropicClient { ApiKey = ... }`, `client.Messages.Create(new MessageCreateParams { ... Messages = [new MessageParam { Role = "user", Content = raw }] })`, extract text via `block.TryPickText(out var t)`):** system prompt allows **delete-only** editing — remove filler, false starts, and the abandoned half of self-corrections; never rephrase, reorder, or add words. Enforce it in code with a guard: ≥88% of cleaned tokens must exist in the raw transcript (multiset match) and length must not grow >15%; on guard failure, timeout (8 s), or any API error, paste the raw transcript. Undercorrection is the product guarantee. Skip the call under 4 words. Capture the foreground app name + window title at chord-down and add it to both the Whisper prompt and the cleanup prompt as context.
- **Testability:** add a hidden `--transcribe file.wav` mode that runs the full pipeline headless (16 kHz mono WAV in, `file.wav.txt` out with raw + cleaned text). You can generate test speech yourself with Windows SAPI (`System.Speech.Synthesis`, 16 kHz 16-bit mono output) — including deliberately messy speech with "um"s and a mid-sentence correction to prove the cleanup pass and its guard.

## Verification before handover (do this yourself)

1. Build clean, launch, confirm the tray icon and a log line showing the model loaded and which runtime (expect Vulkan).
2. SAPI-generate a sentence containing dictionary words spoken as lowercase ("patchy hub") → `--transcribe` → confirm casing was fixed and note the ms-per-second-of-audio.
3. If cleanup is enabled: SAPI-generate a messy sentence with fillers and a "no wait, actually X" correction → confirm the output is delete-only and the correction resolved.
4. Then give me these manual tests, one at a time: **(a)** quit Wispr Flow first if installed — both apps fight over the hotkey; **(b)** click into any text field, hold the hotkey, say a sentence, release — text should appear in about a second; **(c)** hold, start talking, press Escape — nothing should be typed; **(d)** say a dictionary word and confirm the casing; **(e)** deliberately change your mind mid-sentence and confirm only the final version survives; **(f)** check the overlay never steals your cursor from the field.

## After it works

Ask me: does the release-to-paste delay feel fine? Does the overlay read instantly? Did the cleanup ever touch something it shouldn't have (if so, the guard threshold and prompt are the tuning knobs)? Offer — but do not build unprompted — the natural v2 features: per-app context files that feed the cleanup prompt, auto-learning dictionary, and a GitHub remote for the repo.
