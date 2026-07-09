using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace PatchyFlow;

/// <summary>
/// The state machine that ties everything together:
/// hold chord -> record + waveform; release -> transcribe -> paste; Escape -> cancel.
/// </summary>
public sealed class FlowController : IDisposable
{
    private enum State { Idle, Recording, Processing }

    private AppSettings _settings = null!;
    private readonly UserDictionary _dictionary = new();
    private readonly Transcriber _transcriber = new();
    private AudioRecorder? _recorder;
    private KeyboardHook? _hook;
    private TrayIcon? _tray;
    private OverlayWindow? _overlay;
    private ScreenshotFeature? _screenshot;
    private DisplayFeature? _display;
    private RepasteFeature? _repaste;
    private DispatcherTimer? _maxDurationTimer;

    private State _state = State.Idle;
    private DateTime _recordingStartedAt;
    private ForegroundInfo? _dictationTarget;
    private CancellationTokenSource? _processingCts;

    public void Start()
    {
        Paths.EnsureCreated();
        Paths.MigrateFromLegacy();
        _settings = AppSettings.Load();
        _dictionary.Load();

        _overlay = new OverlayWindow();
        _tray = new TrayIcon(_settings.Hotkey.Describe());
        _tray.ReloadRequested += Reload;
        _tray.SettingsRequested += OpenAdminWindow;
        _tray.HistoryRequested += OpenHistoryWindow;
        _tray.ExitRequested += () => Application.Current.Shutdown();

        _hook = new KeyboardHook(_settings.Hotkey);
        _hook.ChordDown += () => Dispatch(OnChordDown);
        _hook.ChordUp += () => Dispatch(OnChordUp);
        _hook.Cancel += () => Dispatch(OnCancel);
        // Escape during transcription/paste aborts it. Runs on the hook thread;
        // return whether we consumed the key so it doesn't leak to the focused app.
        _hook.EscapeWhileIdle = () =>
        {
            if (_state != State.Processing) return false;
            _processingCts?.Cancel();
            return true;
        };
        _hook.Install();

        _screenshot = new ScreenshotFeature(() => _settings, (title, msg) => _tray?.Balloon(title, msg));
        _screenshot.ApplySettings();

        _display = new DisplayFeature(() => _settings, (title, msg) => _tray?.Balloon(title, msg));
        _display.ApplySettings();

        _repaste = new RepasteFeature(() => _settings, (title, msg) => _tray?.Balloon(title, msg));
        _repaste.ApplySettings();

        _maxDurationTimer = new DispatcherTimer();
        _maxDurationTimer.Tick += (_, _) =>
        {
            if (_state == State.Recording)
            {
                Log.Write("Max recording duration hit; force-stopping");
                OnChordUp();
            }
        };

        _ = InitializeModelAsync();
        Log.Write($"PatchyFlow started. Hotkey: {_settings.Hotkey.Describe()}");
    }

    private static void Dispatch(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);

    private async Task InitializeModelAsync()
    {
        try
        {
            if (!ModelDownloader.Exists(_settings.Model))
            {
                _tray?.Balloon("PatchyFlow", $"Downloading speech model ({_settings.Model}) — first run only.");
                var lastShown = 0;
                await ModelDownloader.DownloadAsync(_settings.Model, new Progress<double>(p =>
                {
                    int pct = (int)(p * 100);
                    if (pct >= lastShown + 25)
                    {
                        lastShown = pct;
                        _tray?.Balloon("PatchyFlow", $"Model download: {pct}%");
                    }
                }));
            }

            await _transcriber.InitializeAsync(ModelDownloader.ModelPath(_settings.Model));

            // warm-up on half a second of silence so the first real dictation
            // doesn't pay the GPU pipeline-compile cost
            try { await _transcriber.TranscribeAsync(new float[8000], _settings.Language, null); }
            catch (Exception ex) { Log.Write($"Warm-up inference failed (non-fatal): {ex.Message}"); }

            // pre-load + pin the local cleanup model so the first dictation doesn't
            // cold-load it inside the cleanup timeout. WarmAsync never throws.
            SyncLocalCleanupModel();

            _tray?.Balloon("PatchyFlow ready",
                $"Hold {_settings.Hotkey.Describe()} and talk. Runtime: {_transcriber.LoadedRuntime}");
        }
        catch (Exception ex)
        {
            Log.Write($"Model init failed: {ex}");
            _tray?.Balloon("PatchyFlow — model failed to load", "Check the log (tray menu).");
        }
    }

    private void OnChordDown()
    {
        if (!_settings.DictationEnabled) return;
        if (_state != State.Idle) return;

        if (!_transcriber.IsReady)
        {
            _tray?.Balloon("PatchyFlow", "Speech model is still loading — give it a moment.");
            return;
        }

        _dictationTarget = _settings.ContextAwareness ? ForegroundContext.Capture() : null;

        try
        {
            _recorder = new AudioRecorder();
            _recorder.LevelChanged += level =>
                Application.Current.Dispatcher.BeginInvoke(() => _overlay?.PushLevel(level));
            _recorder.Start(_settings.MicDeviceName);

            _state = State.Recording;
            _recordingStartedAt = DateTime.UtcNow;
            _overlay?.ShowRecording();
            if (_settings.EnableSounds) SoundFx.PlayStart();

            _maxDurationTimer!.Interval = TimeSpan.FromSeconds(_settings.MaxRecordingSeconds);
            _maxDurationTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"Recording start failed: {ex}");
            _tray?.Balloon("PatchyFlow — mic error", ex.Message);
            _recorder?.Dispose();
            _recorder = null;
            _state = State.Idle;
        }
    }

    private void OnChordUp()
    {
        if (_state != State.Recording) return;
        _state = State.Processing;
        _maxDurationTimer?.Stop();
        if (_settings.EnableSounds) SoundFx.PlayStop(); // descending blip = "off"
        _ = FinishDictationAsync();
    }

    private async Task FinishDictationAsync()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder == null) { _state = State.Idle; return; }

        _processingCts = new CancellationTokenSource();
        var ct = _processingCts.Token;
        try
        {
            var heldMs = (DateTime.UtcNow - _recordingStartedAt).TotalMilliseconds;

            // capture a moment longer so the last word isn't clipped
            await Task.Delay(_settings.TailCaptureMs);
            var samples = await recorder.StopAsync();
            recorder.Dispose();

            if (heldMs < _settings.MinHoldMs || samples.Length < AudioRecorder.TargetRate / 4)
            {
                _overlay?.HideOverlay();
                _state = State.Idle;
                return;
            }

            _overlay?.ShowProcessing();

            // ONLY the dictionary glossary goes to Whisper. Do NOT add the app/window
            // title here — Whisper occasionally echoes its initial prompt at the start
            // of the transcript, and an arbitrary window title leaks as junk ("Claudius
            // S…"). App/window context still reaches the cleanup pass, which won't parrot it.
            var whisperPrompt = _dictionary.BuildWhisperPrompt();

            var text = await _transcriber.TranscribeAsync(samples, _settings.Language, whisperPrompt, ct);
            text = _dictionary.Apply(text);
            text = await CleanupPass.ApplyAsync(text, _dictationTarget, _settings, ct);
            text = StripEmDashes(text); // never paste an em/en dash, whatever the source

            _overlay?.HideOverlay();

            if (ct.IsCancellationRequested)
            {
                if (_settings.EnableSounds) SoundFx.PlayCancel();
                Log.Write("Dictation aborted by Escape during processing");
                return;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                HistoryStore.Append(text, _dictationTarget?.App ?? "", _settings.History.KeepLast);
                Stats.RecordDictation(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
                if (_settings.AppendSpace) text += " ";
                await TextInjector.PasteAsync(text, _settings.RestoreClipboard);
            }
        }
        catch (OperationCanceledException)
        {
            _overlay?.HideOverlay();
            if (_settings.EnableSounds) SoundFx.PlayCancel();
            Log.Write("Dictation aborted by Escape during processing");
        }
        catch (Exception ex)
        {
            Log.Write($"Dictation failed: {ex}");
            _overlay?.HideOverlay();
            _tray?.Balloon("PatchyFlow — dictation failed", "Check the log (tray menu).");
        }
        finally
        {
            _processingCts?.Dispose();
            _processingCts = null;
            _state = State.Idle;
            // Safety net: by now the chord is certainly released. Clear any chord key
            // the OS still thinks is held, even if the balancing key-up never reached
            // the hook (hook timeout / secure desktop) so nothing gets left stuck.
            _hook?.ReleaseStuckChordKeys();
        }
    }

    // Belt-and-suspenders: guarantees no em/en dash ever reaches the paste,
    // even when cleanup is off or the guard fell back to the raw transcript.
    private static string StripEmDashes(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s*[—–]\s*", ", ");

    private void OnCancel()
    {
        if (_state != State.Recording) return;
        _maxDurationTimer?.Stop();
        _recorder?.CancelAndDiscard();
        _recorder = null;
        _overlay?.HideOverlay();
        if (_settings.EnableSounds) SoundFx.PlayCancel();
        _state = State.Idle;
        _hook?.ReleaseStuckChordKeys();
        Log.Write("Dictation cancelled");
    }

    private AdminWindow? _admin;
    private HistoryWindow? _historyWindow;

    public void OpenAdminWindow()
    {
        if (_admin is { IsVisible: true })
        {
            _admin.Activate();
            return;
        }
        _admin = new AdminWindow(Reload);
        _admin.Show();
    }

    public void OpenHistoryWindow()
    {
        if (_historyWindow is { IsVisible: true })
        {
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new HistoryWindow();
        _historyWindow.Show();
    }

    public void Reload()
    {
        _settings = AppSettings.Load();
        _dictionary.Load();
        _hook?.Configure(_settings.Hotkey);
        _screenshot?.ApplySettings();
        _display?.ApplySettings();
        _repaste?.ApplySettings();
        _tray?.UpdateHotkeyDescription(_settings.Hotkey.Describe());
        // Warm the local model if they switched to Local, or release it if they
        // switched away — so choosing Haiku actually frees the VRAM again.
        SyncLocalCleanupModel();
        Log.Write("Config reloaded");
    }

    private bool _localWarmed;

    private void SyncLocalCleanupModel()
    {
        bool local = _settings.CleanupEnabled &&
            string.Equals(_settings.CleanupEngine, "Local", StringComparison.OrdinalIgnoreCase);

        if (local)
        {
            _localWarmed = true;
            _ = LocalCleanupProvider.WarmAsync(_settings); // idempotent; never throws
        }
        else if (_localWarmed)
        {
            // Switched off Local: unpin the model so the GPU is free for other work.
            _localWarmed = false;
            _ = LocalCleanupProvider.UnloadAsync(_settings);
        }
    }

    public void Dispose()
    {
        // Free the pinned cleanup model so games / other GPU apps get the VRAM back.
        // Only if we actually warmed one this session (skips a needless Ollama ping
        // on a pure-Haiku run, and avoids re-unloading after a switch-away already did).
        if (_localWarmed && _settings is not null)
        {
            try { LocalCleanupProvider.UnloadAsync(_settings).Wait(TimeSpan.FromSeconds(3)); }
            catch { /* exiting anyway */ }
        }

        _hook?.Dispose();
        _screenshot?.Dispose();
        _display?.Dispose();
        _repaste?.Dispose();
        _recorder?.Dispose();
        _transcriber.Dispose();
        _tray?.Dispose();
    }
}
