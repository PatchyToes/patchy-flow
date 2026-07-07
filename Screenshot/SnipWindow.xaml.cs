using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PatchyFlow;

/// <summary>
/// Full-virtual-screen rubber-band region picker over a frozen screenshot,
/// so the capture never includes our own dimming layer.
/// Returns the selected region in physical screen pixels.
/// </summary>
public partial class SnipWindow : Window
{
    private readonly double _scaleX;
    private readonly double _scaleY;
    private readonly int _virtualLeft;
    private readonly int _virtualTop;

    private Point _start;
    private bool _dragging;

    public System.Drawing.Rectangle? SelectedRegion { get; private set; }

    public SnipWindow(BitmapSource frozenScreen, int virtualLeft, int virtualTop, int virtualWidth, int virtualHeight)
    {
        InitializeComponent();

        _virtualLeft = virtualLeft;
        _virtualTop = virtualTop;

        // window is positioned/sized in DIPs; screenshot is physical pixels
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        _scaleX = dpi.DpiScaleX;
        _scaleY = dpi.DpiScaleY;

        Left = virtualLeft / _scaleX;
        Top = virtualTop / _scaleY;
        Width = virtualWidth / _scaleX;
        Height = virtualHeight / _scaleY;

        FrozenScreen.Source = frozenScreen;
        FrozenScreen.Width = Width;
        FrozenScreen.Height = Height;
        DimLayer.Width = Width;
        DimLayer.Height = Height;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { SelectedRegion = null; Close(); } };
        Loaded += (_, _) => { Activate(); Focus(); };
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(RootCanvas);
        _dragging = true;
        Selection.Visibility = Visibility.Visible;
        UpdateSelection(_start, _start);
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        UpdateSelection(_start, e.GetPosition(RootCanvas));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var end = e.GetPosition(RootCanvas);
        double x = Math.Min(_start.X, end.X), y = Math.Min(_start.Y, end.Y);
        double w = Math.Abs(_start.X - end.X), h = Math.Abs(_start.Y - end.Y);

        if (w < 4 || h < 4)
        {
            SelectedRegion = null; // accidental click
        }
        else
        {
            SelectedRegion = new System.Drawing.Rectangle(
                _virtualLeft + (int)(x * _scaleX),
                _virtualTop + (int)(y * _scaleY),
                Math.Max(1, (int)(w * _scaleX)),
                Math.Max(1, (int)(h * _scaleY)));
        }
        Close();
    }

    private void UpdateSelection(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        System.Windows.Controls.Canvas.SetLeft(Selection, x);
        System.Windows.Controls.Canvas.SetTop(Selection, y);
        Selection.Width = Math.Abs(a.X - b.X);
        Selection.Height = Math.Abs(a.Y - b.Y);
    }
}
