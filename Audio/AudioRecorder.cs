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

    /// <summary>
    /// Friendly names of every active capture device, for the settings dropdown.
    /// Stored setting stays a substring filter, so a full name here still matches.
    /// </summary>
    public static IReadOnlyList<string> CaptureDeviceNames()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => d.FriendlyName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Write($"Enumerating capture devices failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public static MMDevice? PickDevice(string nameFilter)
    {
        var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

            foreach (var d in devices)
            {
                if (d.FriendlyName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    return d;
            }

            // Nothing matched outright. A headset that can run wired or wireless renames its
            // own endpoint when you plug or unplug it ("BlackShark V3 Pro USB - Chat" becomes
            // "BlackShark V3 Pro - Chat"), so a name saved on one side of that is wrong on the
            // other and we'd silently fall through to whatever Windows calls the default.
            // It is the same headset, so match on how much of the name agrees.
            var near = BestNameMatch(nameFilter, devices);
            if (near != null)
            {
                Log.Write($"Mic '{nameFilter}' isn't present; using '{near.FriendlyName}' (same device, renamed)");
                return near;
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

    // Only the endpoint-kind prefix Windows puts on every name. Deliberately short: words
    // like "in", "out" and "input" look generic but are exactly what tells one virtual audio
    // channel from another, and stripping them made unrelated channels look interchangeable.
    private static readonly HashSet<string> GenericNameWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "microphone", "mic", "headset", "headphones", "speakers",
    };

    private static HashSet<string> NameTokens(string name)
    {
        var all = System.Text.RegularExpressions.Regex
            .Matches(name.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value)
            .ToList();
        var meaningful = all.Where(t => !GenericNameWords.Contains(t)).ToHashSet();
        return meaningful.Count > 0 ? meaningful : all.ToHashSet();
    }

    /// <summary>
    /// Closest capture device to a saved name, or null if nothing is close enough.
    ///
    /// "The same device under a new name" means one name is the other with a qualifier added
    /// or dropped: "BlackShark V3 Pro USB - Chat" loses the "USB" when the headset goes back
    /// to wireless. Nothing is contradicted, only omitted. So one name's words must be wholly
    /// contained in the other's — a name that swaps a word for a different one (Chat for Game,
    /// Out A1 for Out B1) is a different endpoint, however similar it reads, and is refused.
    /// The overlap floor then stops a stray shared word from counting as containment.
    /// </summary>
    public static string? BestMatchingDeviceName(string nameFilter, IReadOnlyList<string> candidates)
    {
        const double MinimumOverlap = 0.65;

        var want = NameTokens(nameFilter);
        if (want.Count == 0) return null;

        string? best = null;
        double bestScore = 0;

        foreach (var name in candidates)
        {
            var have = NameTokens(name);
            if (have.Count == 0) continue;

            int shared = want.Count(have.Contains);
            bool oneContainsTheOther = shared == want.Count || shared == have.Count;
            if (!oneContainsTheOther) continue;

            double score = (double)shared / Math.Max(want.Count, have.Count);
            if (score > bestScore)
            {
                bestScore = score;
                best = name;
            }
        }

        return bestScore >= MinimumOverlap ? best : null;
    }

    private static MMDevice? BestNameMatch(string nameFilter, List<MMDevice> devices)
    {
        var name = BestMatchingDeviceName(nameFilter, devices.Select(d => d.FriendlyName).ToList());
        return name == null ? null : devices.FirstOrDefault(d => d.FriendlyName == name);
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
