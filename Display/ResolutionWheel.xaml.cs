using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace PatchyFlow;

/// <summary>
/// Radial resolution picker, esports chat-wheel style.
/// Hold the hotkey: aim at an option, release to apply. Quick-tap leaves the
/// wheel open in sticky mode (click to pick, Escape to close). Never takes focus.
/// </summary>
public partial class ResolutionWheel : Window
{
    private const double RingRadius = 150;
    private const double DeadZonePx = 45;

    private sealed record WheelItem(DisplayFeature.Mode Mode, Border Visual, double AngleRad, bool IsCurrent);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int VK_ESCAPE = 0x1B;

    private static readonly Brush ItemBg = new SolidColorBrush(Color.FromArgb(0xF0, 0x14, 0x14, 0x14));
    private static readonly Brush ItemBgHover = new SolidColorBrush(Color.FromArgb(0xF6, 0x26, 0x26, 0x26));
    private static readonly Brush ItemBorder = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush AccentBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0x4E, 0xC9, 0xB0));

    private readonly List<WheelItem> _items = new();
    private readonly int _targetVk;
    private readonly Action<DisplayFeature.Mode> _onCommit;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _openedAt = DateTime.UtcNow;

    private System.Drawing.Point _centerPx;
    private double _pxPerDip = 1.0;
    private int _hoverIndex = -1;
    private bool _sticky;

    internal ResolutionWheel(List<DisplayFeature.Mode> modes, DisplayFeature.Mode current, int targetVk,
        Action<DisplayFeature.Mode> onCommit)
    {
        InitializeComponent();
        _targetVk = targetVk;
        _onCommit = onCommit;

        BuildRing(modes, current);
        CenterLabel.Text = $"{current.Width} × {current.Height}";
        CenterHint.Text = "aim + release to apply";

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongW(hwnd, GWL_EXSTYLE,
                GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };

        PositionAtCursor();

        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        MouseWheel += (_, e) =>
        {
            int n = _items.Count;
            _hoverIndex = _hoverIndex < 0
                ? (e.Delta < 0 ? 0 : n - 1)
                : ((_hoverIndex + (e.Delta < 0 ? 1 : -1)) % n + n) % n;
            UpdateHoverVisuals();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void BuildRing(List<DisplayFeature.Mode> modes, DisplayFeature.Mode current)
    {
        double cx = Root.Width / 2, cy = Root.Height / 2;
        for (int i = 0; i < modes.Count; i++)
        {
            var mode = modes[i];
            bool isCurrent = mode.Width == current.Width && mode.Height == current.Height;
            double angle = -Math.PI / 2 + i * (2 * Math.PI / modes.Count);

            var label = new StackPanel();
            label.Children.Add(new TextBlock
            {
                Text = $"{mode.Width} × {mode.Height}",
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            });
            label.Children.Add(new TextBlock
            {
                Text = isCurrent ? $"{mode.Hz}Hz — current" : $"{mode.Hz}Hz",
                Foreground = new SolidColorBrush(Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF)),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 11,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            });

            var visual = new Border
            {
                Width = 148,
                Height = 54,
                CornerRadius = new CornerRadius(12),
                Background = ItemBg,
                BorderBrush = isCurrent ? AccentBorder : ItemBorder,
                BorderThickness = new Thickness(isCurrent ? 1.5 : 1),
                Child = label,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };
            label.VerticalAlignment = VerticalAlignment.Center;

            Canvas.SetLeft(visual, cx + RingRadius * Math.Cos(angle) - visual.Width / 2);
            Canvas.SetTop(visual, cy + RingRadius * Math.Sin(angle) - visual.Height / 2);
            Root.Children.Add(visual);

            int index = i;
            visual.MouseLeftButtonUp += (_, _) => Commit(index);
            visual.MouseEnter += (_, _) => { _hoverIndex = index; UpdateHoverVisuals(); };

            _items.Add(new WheelItem(mode, visual, angle, isCurrent));
        }
    }

    private void PositionAtCursor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);

        new WindowInteropHelper(this).EnsureHandle();
        var source = PresentationSource.FromVisual(this);
        _pxPerDip = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        double halfPx = Width / 2 * _pxPerDip;
        var wa = screen.WorkingArea;
        int cxPx = Math.Clamp(cursor.X, wa.Left + (int)halfPx, wa.Right - (int)halfPx);
        int cyPx = Math.Clamp(cursor.Y, wa.Top + (int)halfPx, wa.Bottom - (int)halfPx);

        _centerPx = new System.Drawing.Point(cxPx, cyPx);
        Left = (cxPx - halfPx) / _pxPerDip;
        Top = (cyPx - halfPx) / _pxPerDip;
    }

    private void Tick()
    {
        // Escape bails
        if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) { Close(); return; }

        // aim with the mouse: pick the item whose direction matches the cursor
        var cursor = System.Windows.Forms.Cursor.Position;
        double dx = cursor.X - _centerPx.X, dy = cursor.Y - _centerPx.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > DeadZonePx * _pxPerDip)
        {
            double angle = Math.Atan2(dy, dx);
            int best = -1;
            double bestDiff = double.MaxValue;
            for (int i = 0; i < _items.Count; i++)
            {
                double diff = Math.Abs(NormalizeAngle(angle - _items[i].AngleRad));
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            if (best != _hoverIndex) { _hoverIndex = best; UpdateHoverVisuals(); }
        }

        // hotkey released?
        bool keyDown = _targetVk != 0 && (GetAsyncKeyState(_targetVk) & 0x8000) != 0;
        if (!keyDown && !_sticky)
        {
            if (_hoverIndex >= 0)
            {
                Commit(_hoverIndex);
            }
            else if ((DateTime.UtcNow - _openedAt).TotalMilliseconds < 350 || _targetVk == 0)
            {
                // quick tap: stay open, let them click
                _sticky = true;
                CenterHint.Text = "click to apply — Esc to close";
            }
            else
            {
                Close(); // held, aimed at nothing, released = cancel
            }
        }
    }

    private static double NormalizeAngle(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }

    private void UpdateHoverVisuals()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            bool hover = i == _hoverIndex;
            item.Visual.Background = hover ? ItemBgHover : ItemBg;
            item.Visual.BorderBrush = hover || item.IsCurrent ? AccentBorder : ItemBorder;
            item.Visual.BorderThickness = new Thickness(hover ? 2 : item.IsCurrent ? 1.5 : 1);
            ((ScaleTransform)item.Visual.RenderTransform).ScaleX = hover ? 1.08 : 1.0;
            ((ScaleTransform)item.Visual.RenderTransform).ScaleY = hover ? 1.08 : 1.0;
        }
        if (_hoverIndex >= 0)
        {
            var m = _items[_hoverIndex].Mode;
            CenterLabel.Text = $"{m.Width} × {m.Height}";
        }
    }

    private void Commit(int index)
    {
        var mode = _items[index].Mode;
        Close();
        _onCommit(mode);
    }
}
