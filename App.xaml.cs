using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace PatchyFlow;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private FlowController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // headless smoke test: PatchyFlow.exe --transcribe file.wav writes file.wav.txt and exits
        var cmdArgs = Environment.GetCommandLineArgs();
        int ti = Array.IndexOf(cmdArgs, "--transcribe");
        if (ti >= 0 && ti + 1 < cmdArgs.Length)
        {
            RunTranscribeTest(cmdArgs[ti + 1]);
            return;
        }

        _singleInstanceMutex = new Mutex(true, "PatchyFlow_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("PatchyFlow is already running (check the system tray).",
                "PatchyFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write($"FATAL: {args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"UI EXCEPTION: {args.Exception}");
            args.Handled = true;
        };

        _controller = new FlowController();
        _controller.Start();
    }

    private async void RunTranscribeTest(string wavPath)
    {
        try
        {
            using var reader = new NAudio.Wave.WaveFileReader(wavPath);
            if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1)
                throw new InvalidOperationException($"Test wav must be 16kHz mono, got {reader.WaveFormat}");

            var bytes = new byte[reader.Length];
            int read = reader.Read(bytes, 0, bytes.Length);
            var samples = new float[read / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;

            var settings = AppSettings.Load();
            var dictionary = new UserDictionary();
            dictionary.Load();

            using var transcriber = new Transcriber();
            await transcriber.InitializeAsync(ModelDownloader.ModelPath(settings.Model));
            // run twice: first pass pays the GPU pipeline warm-up, second shows steady-state speed
            await transcriber.TranscribeAsync(samples, settings.Language, dictionary.BuildWhisperPrompt());
            var text = await transcriber.TranscribeAsync(samples, settings.Language, dictionary.BuildWhisperPrompt());
            text = dictionary.Apply(text);
            var cleaned = await CleanupPass.ApplyAsync(text, null, settings);

            File.WriteAllText(wavPath + ".txt",
                $"runtime={transcriber.LoadedRuntime}\nraw: {text}\ncleaned: {cleaned}");
        }
        catch (Exception ex)
        {
            File.WriteAllText(wavPath + ".txt", $"ERROR: {ex}");
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
