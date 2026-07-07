using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Rectangle = System.Windows.Shapes.Rectangle;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Matrix = System.Windows.Media.Matrix;

namespace PatchyFlow;

/// <summary>
/// The little bottom-center pill: live waveform while recording,
/// a gentle traveling wave while transcribing.
/// Never takes focus — the target text field keeps the caret.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int BarCount = 12;
    private const double BarWidth = 3;
    private const double BarGap = 2;
    private const double MaxBarHeight = 18;
    private const double MinBarHeight = 2.5;

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _levels = new double[BarCount];
    private readonly double[] _barPhase = new double[BarCount];
    private readonly DispatcherTimer _timer;
    private float _currentLevel;
    private double _phase;

    private enum Mode { Hidden, Recording, Processing }
    private Mode _mode = Mode.Hidden;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    public OverlayWindow()
    {
        InitializeComponent();

        double totalWidth = BarCount * BarWidth + (BarCount - 1) * BarGap;
        BarsCanvas.Width = totalWidth;

        var brush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        for (int i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = BarWidth,
                Height = MinBarHeight,
                RadiusX = 2,
                RadiusY = 2,
                Fill = brush,
            };
            System.Windows.Controls.Canvas.SetLeft(bar, i * (BarWidth + BarGap));
            System.Windows.Controls.Canvas.SetTop(bar, (BarsCanvas.Height - MinBarHeight) / 2);
            BarsCanvas.Children.Add(bar);
            _bars[i] = bar;
            // fixed per-bar phase offsets so the in-place wobble looks organic, not synchronized
            _barPhase[i] = i * 1.7;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Render();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
            SetWindowLongW(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    public void PushLevel(float level)
    {
        // fast attack, slow decay — reads as "alive" instead of jittery
        if (level > _currentLevel) _currentLevel = _currentLevel * 0.4f + level * 0.6f;
        else _currentLevel = _currentLevel * 0.82f + level * 0.18f;
    }

    public void ShowRecording()
    {
        _mode = Mode.Recording;
        Array.Clear(_levels);
        _currentLevel = 0;
        StatusText.Visibility = Visibility.Collapsed;
        BarsCanvas.Visibility = Visibility.Visible;
        Pill.Opacity = 1;
        PositionOnActiveScreen();
        Show();
        _timer.Start();
    }

    public void ShowProcessing()
    {
        _mode = Mode.Processing;
        _phase = 0;
    }

    public void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
        BarsCanvas.Visibility = Visibility.Collapsed;
        PositionOnActiveScreen();
        Show();
    }

    public void HideOverlay()
    {
        _mode = Mode.Hidden;
        _timer.Stop();
        Hide();
    }

    private void Render()
    {
        if (_mode == Mode.Recording)
        {
            // in-place spectrum: every bar reacts to the live level instantly,
            // with a center-weighted bell and per-bar wobble so it reads as voice
            _phase += 0.45;
            for (int i = 0; i < BarCount; i++)
            {
                double bell = 0.35 + 0.65 * Math.Sin(Math.PI * (i + 0.5) / BarCount);
                double wobble = 0.55 + 0.45 * Math.Sin(_phase + _barPhase[i]);
                double target = Math.Min(1.0, _currentLevel * 1.6) * bell * wobble;
                _levels[i] += (target - _levels[i]) * 0.6; // fast attack, minimal lag
            }
        }
        else if (_mode == Mode.Processing)
        {
            // gentle traveling wave so "thinking" still looks alive
            _phase += 0.25;
            for (int i = 0; i < BarCount; i++)
                _levels[i] = 0.12 + 0.10 * (1 + Math.Sin(_phase - i * 0.55));
        }

        for (int i = 0; i < BarCount; i++)
        {
            double h = MinBarHeight + _levels[i] * (MaxBarHeight - MinBarHeight);
            _bars[i].Height = h;
            System.Windows.Controls.Canvas.SetTop(_bars[i], (BarsCanvas.Height - h) / 2);
        }
    }

    private void PositionOnActiveScreen()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            var screen = hwnd != IntPtr.Zero
                ? System.Windows.Forms.Screen.FromHandle(hwnd)
                : System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return;

            new WindowInteropHelper(this).EnsureHandle();
            var source = PresentationSource.FromVisual(this);
            var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

            var wa = screen.WorkingArea;
            var topLeft = transform.Transform(new Point(wa.Left, wa.Top));
            var bottomRight = transform.Transform(new Point(wa.Right, wa.Bottom));

            Left = topLeft.X + ((bottomRight.X - topLeft.X) - Width) / 2;
            Top = bottomRight.Y - Height - 12;
        }
        catch (Exception ex)
        {
            Log.Write($"Overlay positioning failed: {ex.Message}");
        }
    }
}
