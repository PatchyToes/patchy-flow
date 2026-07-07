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
    private DispatcherTimer? _maxDurationTimer;

    private State _state = State.Idle;
    private DateTime _recordingStartedAt;

    public void Start()
    {
        Paths.EnsureCreated();
        _settings = AppSettings.Load();
        _dictionary.Load();

        _overlay = new OverlayWindow();
        _tray = new TrayIcon(_settings.Hotkey.Describe());
        _tray.ReloadRequested += Reload;
        _tray.ExitRequested += () => Application.Current.Shutdown();

        _hook = new KeyboardHook(_settings.Hotkey);
        _hook.ChordDown += () => Dispatch(OnChordDown);
        _hook.ChordUp += () => Dispatch(OnChordUp);
        _hook.Cancel += () => Dispatch(OnCancel);
        _hook.Install();

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
        if (_state != State.Idle) return;

        if (!_transcriber.IsReady)
        {
            _tray?.Balloon("PatchyFlow", "Speech model is still loading — give it a moment.");
            return;
        }

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
        _ = FinishDictationAsync();
    }

    private async Task FinishDictationAsync()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder == null) { _state = State.Idle; return; }

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

            var text = await _transcriber.TranscribeAsync(samples, _settings.Language, _dictionary.BuildWhisperPrompt());
            text = _dictionary.Apply(text);

            _overlay?.HideOverlay();

            if (!string.IsNullOrWhiteSpace(text))
            {
                if (_settings.AppendSpace) text += " ";
                await TextInjector.PasteAsync(text, _settings.RestoreClipboard);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Dictation failed: {ex}");
            _overlay?.HideOverlay();
            _tray?.Balloon("PatchyFlow — dictation failed", "Check the log (tray menu).");
        }
        finally
        {
            _state = State.Idle;
        }
    }

    private void OnCancel()
    {
        if (_state != State.Recording) return;
        _maxDurationTimer?.Stop();
        _recorder?.CancelAndDiscard();
        _recorder = null;
        _overlay?.HideOverlay();
        if (_settings.EnableSounds) SoundFx.PlayCancel();
        _state = State.Idle;
        Log.Write("Dictation cancelled");
    }

    private void Reload()
    {
        _settings = AppSettings.Load();
        _dictionary.Load();
        _hook?.Configure(_settings.Hotkey);
        _tray?.UpdateHotkeyDescription(_settings.Hotkey.Describe());
        _tray?.Balloon("PatchyFlow", "Settings and dictionary reloaded.");
        Log.Write("Config reloaded");
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _recorder?.Dispose();
        _transcriber.Dispose();
        _tray?.Dispose();
    }
}
