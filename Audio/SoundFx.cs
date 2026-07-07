using NAudio.Wave;

namespace PatchyFlow;

/// <summary>Tiny synthesized pings so you know the mic opened / closed / died.</summary>
public static class SoundFx
{
    // rising blip: recording started
    public static void PlayStart() => PlayGlide(660, 920, 70, 0.12);
    // descending blip: recording stopped ("it's off")
    public static void PlayStop() => PlayGlide(720, 420, 90, 0.12);
    // low thud: cancelled
    public static void PlayCancel() => PlayGlide(360, 300, 100, 0.11);

    private const int SampleRate = 44100;

    private static void PlayGlide(double startFreq, double endFreq, int ms, double gain)
    {
        Task.Run(() =>
        {
            try
            {
                int samples = SampleRate * ms / 1000;
                var buffer = new byte[samples * 2]; // 16-bit mono
                double phase = 0;
                for (int i = 0; i < samples; i++)
                {
                    double t = (double)i / samples;
                    double freq = startFreq + (endFreq - startFreq) * t;
                    phase += 2 * Math.PI * freq / SampleRate;

                    // quick attack + decay so it reads as a "blip", no click at edges
                    double env = Math.Min(1.0, Math.Min(i / (samples * 0.15), (samples - i) / (samples * 0.35)));
                    short s = (short)(Math.Sin(phase) * env * gain * short.MaxValue);
                    buffer[i * 2] = (byte)(s & 0xFF);
                    buffer[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }

                var provider = new RawSourceWaveStream(
                    new MemoryStream(buffer), new WaveFormat(SampleRate, 16, 1));
                using var output = new WaveOutEvent();
                output.Init(provider);
                output.Play();
                Thread.Sleep(ms + 120);
            }
            catch
            {
                // sounds are decoration; never let them break a dictation
            }
        });
    }
}
