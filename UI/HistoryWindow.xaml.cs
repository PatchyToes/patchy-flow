using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace PatchyFlow;

/// <summary>
/// Large, resizable view of everything PatchyFlow has transcribed, plus a
/// compact health/usage strip. List on the left, full text on the right,
/// copy anything. No junk — just the text and the high-level signals.
/// </summary>
public partial class HistoryWindow : Window
{
    private List<HistoryEntry> _entries = new();

    public HistoryWindow()
    {
        InitializeComponent();
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(ico))
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(ico));
        }
        catch { }
        Load();

        // Refresh live as new dictations land, so there's no manual Refresh to remember.
        HistoryStore.Changed += OnHistoryChanged;
        Closed += (_, _) => HistoryStore.Changed -= OnHistoryChanged;
    }

    // The Changed event may fire off the UI thread; marshal the reload.
    private void OnHistoryChanged() => Dispatcher.BeginInvoke(new Action(Load));

    private void Load()
    {
        RenderStats();

        // Follow-the-tail: if the user is sitting on the newest entry, keep them on the
        // newest after refresh; if they've scrolled to an older one, keep them on THAT
        // entry; if it was trimmed away, fall back to newest.
        int prevIndex = HList.SelectedIndex;
        var prevSelected = (prevIndex >= 0 && prevIndex < _entries.Count) ? _entries[prevIndex] : null;

        _entries = HistoryStore.ReadRecent(500);
        HList.Items.Clear();
        foreach (var e in _entries)
        {
            var preview = e.Text.Replace('\n', ' ');
            if (preview.Length > 70) preview = preview[..70] + "…";
            var app = string.IsNullOrEmpty(e.App) ? "" : $"[{e.App}] ";
            HList.Items.Add($"{e.Ts:MMM d  HH:mm}   {app}{preview}");
        }

        if (_entries.Count == 0)
        {
            HList.Items.Add("(no dictations yet)");
            return;
        }

        int idx = 0; // default: newest
        if (prevSelected != null && prevIndex != 0)
        {
            int found = _entries.FindIndex(e => e.Ts == prevSelected.Ts && e.Text == prevSelected.Text);
            if (found >= 0) idx = found;
        }
        HList.SelectedIndex = idx;
    }

    private void RenderStats()
    {
        var s = Stats.Load();
        StatsStrip.Children.Clear();

        AddStat("Dictations", $"{s.Dictations:N0}", null);
        AddStat("Words", $"{s.Words:N0}", null);
        AddStat("AI cleanups", $"{s.AiCalls:N0}", null);
        AddStat("AI failures", $"{s.AiFailures:N0}",
            s.AiFailures > 0 ? (Brush)FindResource("Warn") : null);
        AddStat("Est. AI cost", $"${s.EstimatedCostUsd:0.00}", null);
    }

    private void AddStat(string label, string value, Brush? valueBrush)
    {
        if (StatsStrip.Children.Count > 0)
            StatsStrip.Children.Add(new Border
            {
                Width = 1, Margin = new Thickness(18, 2, 18, 2),
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            });

        var col = new StackPanel();
        col.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = valueBrush ?? (Brush)FindResource("Fg"),
            FontSize = 20, FontWeight = FontWeights.SemiBold,
        });
        col.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("FgDim"),
            FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
        });
        StatsStrip.Children.Add(col);
    }

    private void HList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = HList.SelectedIndex;
        Detail.Text = (i >= 0 && i < _entries.Count) ? _entries[i].Text : "";
    }

    private void CopyCurrent()
    {
        int i = HList.SelectedIndex;
        if (i < 0 || i >= _entries.Count) return;
        try
        {
            System.Windows.Clipboard.SetDataObject(_entries[i].Text, true);
            CopiedNote.Visibility = Visibility.Visible;
            var hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            hide.Tick += (_, _) => { CopiedNote.Visibility = Visibility.Hidden; hide.Stop(); };
            hide.Start();
        }
        catch { /* clipboard briefly locked; user can retry */ }
    }

    private void HList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => CopyCurrent();
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyCurrent();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Load();
}
