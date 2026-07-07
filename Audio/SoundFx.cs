using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PatchyFlow;

/// <summary>Tiny synthesized pings so you know the mic opened / the dictation died.</summary>
public static class SoundFx
{
    public static void PlayStart() => PlayTone(880, 70, 0.12);
    public static void PlayCancel() => PlayTone(330, 90, 0.10);

    private static void PlayTone(double freq, int ms, double gain)
    {
        Task.Run(() =>
        {
            try
            {
                var tone = new SignalGenerator(44100, 1)
                {
                    Gain = gain,
                    Frequency = freq,
                    Type = SignalGeneratorType.Sin,
                }.Take(TimeSpan.FromMilliseconds(ms));

                using var output = new WaveOutEvent();
                output.Init(tone);
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
