using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PatchyFlow;

/// <summary>
/// Captures the microphone via WASAPI, downmixes to mono, resamples to 16 kHz
/// (what Whisper expects), and reports live levels for the overlay waveform.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    public event Action<float>? LevelChanged;

    private WasapiCapture? _capture;
    private readonly List<float> _samples = new(16000 * 60);
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _stopped;

    private int _srcRate;
    private int _srcChannels;
    private bool _srcIsFloat;
    private double _resamplePos;
    private float _carrySample;
    private bool _haveCarry;

    public const int TargetRate = 16000;

    public bool IsRecording { get; private set; }

    public static MMDevice? PickDevice(string nameFilter)
    {
        var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (d.FriendlyName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            Log.Write($"Mic filter '{nameFilter}' matched nothing; using default communications mic");
        }
        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch
        {
            try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
            catch { return null; }
        }
    }

    public void Start(string micNameFilter)
    {
        if (IsRecording) return;

        var device = PickDevice(micNameFilter)
            ?? throw new InvalidOperationException("No microphone found.");

        Log.Write($"Recording from: {device.FriendlyName}");

        // 30ms buffers so the overlay waveform updates ~33x/sec instead of 10
        _capture = new WasapiCapture(device, true, 30);
        var wf = _capture.WaveFormat;
        _srcRate = wf.SampleRate;
        _srcChannels = wf.Channels;
        _srcIsFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat;
        if (!_srcIsFloat && wf.BitsPerSample != 16)
            throw new InvalidOperationException($"Unsupported capture format: {wf}");

        lock (_gate) _samples.Clear();
        _resamplePos = 0;
        _haveCarry = false;
        _stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, _) => _stopped?.TrySetResult(true);
        _capture.StartRecording();
        IsRecording = true;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int bytesPerSample = _srcIsFloat ? 4 : 2;
        int frameSize = bytesPerSample * _srcChannels;
        int frames = e.BytesRecorded / frameSize;
        if (frames == 0) return;

        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < _srcChannels; c++)
            {
                int offset = f * frameSize + c * bytesPerSample;
                float v = _srcIsFloat
                    ? BitConverter.ToSingle(e.Buffer, offset)
                    : BitConverter.ToInt16(e.Buffer, offset) / 32768f;
                sum += v;
            }
            mono[f] = sum / _srcChannels;
        }

        // live level for the waveform (RMS, roughly normalized for speech)
        double sq = 0;
        for (int i = 0; i < mono.Length; i++) sq += mono[i] * mono[i];
        float rms = (float)Math.Sqrt(sq / mono.Length);
        LevelChanged?.Invoke(Math.Clamp(rms * 9f, 0f, 1f));

        // linear resample srcRate -> 16k, carrying the fractional position across buffers
        double step = (double)_srcRate / TargetRate;
        lock (_gate)
        {
            if (!_haveCarry)
            {
                _carrySample = mono[0];
                _haveCarry = true;
            }

            // virtual buffer: [carry][mono...], position 0 == carry sample
            while (_resamplePos < mono.Length)
            {
                int i = (int)_resamplePos;
                double frac = _resamplePos - i;
                float a = i == 0 ? _carrySample : mono[i - 1];
                float b = mono[i];
                _samples.Add((float)(a * (1 - frac) + b * frac));
                _resamplePos += step;
            }
            _resamplePos -= mono.Length;
            _carrySample = mono[^1];
        }
    }

    /// <summary>Stop capturing and return everything recorded at 16 kHz mono.</summary>
    public async Task<float[]> StopAsync()
    {
        if (!IsRecording || _capture == null) return Array.Empty<float>();
        IsRecording = false;

        try { _capture.StopRecording(); } catch { /* already stopped */ }
        if (_stopped != null)
            await Task.WhenAny(_stopped.Task, Task.Delay(1000));

        _capture.Dispose();
        _capture = null;

        lock (_gate) return _samples.ToArray();
    }

    public async void CancelAndDiscard()
    {
        try
        {
            await StopAsync();
            lock (_gate) _samples.Clear();
        }
        catch (Exception ex)
        {
            Log.Write($"Cancel failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
    }
}
