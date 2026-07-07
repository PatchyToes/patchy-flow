using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace PatchyFlow;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showAdmin;
    private FlowController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // headless smoke tests:
        //   PatchyFlow.exe --transcribe file.wav  -> file.wav.txt
        //   PatchyFlow.exe --ocr file.png         -> file.png.txt
        var cmdArgs = Environment.GetCommandLineArgs();
        int ti = Array.IndexOf(cmdArgs, "--transcribe");
        if (ti >= 0 && ti + 1 < cmdArgs.Length)
        {
            RunTranscribeTest(cmdArgs[ti + 1]);
            return;
        }
        int oi = Array.IndexOf(cmdArgs, "--ocr");
        if (oi >= 0 && oi + 1 < cmdArgs.Length)
        {
            RunOcrTest(cmdArgs[oi + 1]);
            return;
        }
        int mi = Array.IndexOf(cmdArgs, "--makeicon");
        if (mi >= 0 && mi + 1 < cmdArgs.Length)
        {
            IconFactory.WriteIco(cmdArgs[mi + 1]);
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(true, "PatchyFlow_SingleInstance", out bool isNew);
        _showAdmin = new EventWaitHandle(false, EventResetMode.AutoReset, "PatchyFlow_ShowAdmin");
        if (!isNew)
        {
            // relaunch = "show me the app": tell the running instance to open Settings
            _showAdmin.Set();
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

        // wake-up listener: a second launch sets the event instead of starting
        var listener = new Thread(() =>
        {
            while (true)
            {
                _showAdmin!.WaitOne();
                Dispatcher.BeginInvoke(() => _controller?.OpenAdminWindow());
            }
        })
        { IsBackground = true, Name = "ShowAdminListener" };
        listener.Start();
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

    private async void RunOcrTest(string pngPath)
    {
        try
        {
            using var bitmap = new System.Drawing.Bitmap(pngPath);
            var text = await ScreenshotFeature.OcrAsync(bitmap);
            File.WriteAllText(pngPath + ".txt", text);
        }
        catch (Exception ex)
        {
            File.WriteAllText(pngPath + ".txt", $"ERROR: {ex}");
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
