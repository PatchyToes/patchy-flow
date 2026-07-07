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

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
