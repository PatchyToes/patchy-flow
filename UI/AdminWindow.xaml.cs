using System.Windows;
using System.Windows.Controls;

namespace PatchyFlow;

/// <summary>
/// The admin panel. One nav entry per feature; adding a feature =
/// one StackPanel in the XAML + a Load/Save pair here. Keep it that way.
/// </summary>
public partial class AdminWindow : Window
{
    private readonly Action _onSaved;

    public AdminWindow(Action onSaved)
    {
        InitializeComponent();
        _onSaved = onSaved;
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(ico))
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(ico));
        }
        catch { /* window works fine without an icon */ }
        LoadFromSettings(AppSettings.Load());
    }

    private void LoadFromSettings(AppSettings s)
    {
        GStartWithWindows.IsChecked = TrayIcon.IsAutoStartEnabled();
        GSounds.IsChecked = s.EnableSounds;

        DEnabled.IsChecked = s.DictationEnabled;
        DHotkey.Load(s.Hotkey);
        DLanguage.Text = s.Language;
        DMic.Text = s.MicDeviceName;
        DAppendSpace.IsChecked = s.AppendSpace;
        DRestoreClipboard.IsChecked = s.RestoreClipboard;

        CEnabled.IsChecked = s.CleanupEnabled;
        CContext.IsChecked = s.ContextAwareness;
        CApiKey.Text = s.AnthropicApiKey;
        CModel.Text = s.CleanupModel;

        SEnabled.IsChecked = s.Screenshot.Enabled;
        SHotkey.Load(s.Screenshot.Hotkey);
        SSmart.IsChecked = s.Screenshot.SmartDescribe;
        SSaveTo.Text = s.Screenshot.SavePngTo;

        REnabled.IsChecked = s.Display.Enabled;
        RHotkey.Load(s.Display.Hotkey);
        RPresets.Text = string.Join(Environment.NewLine, s.Display.Presets);

        HRepasteEnabled.IsChecked = s.History.RepasteEnabled;
        HHotkey.Load(s.History.RepasteHotkey);
        RefreshHistoryList();
    }

    private List<HistoryEntry> _historyEntries = new();

    private void RefreshHistoryList()
    {
        _historyEntries = HistoryStore.ReadRecent(50);
        HList.Items.Clear();
        foreach (var e in _historyEntries)
        {
            var preview = e.Text.Length > 90 ? e.Text[..90] + "…" : e.Text;
            HList.Items.Add($"{e.Ts:HH:mm}  [{e.App}]  {preview.Replace('\n', ' ')}");
        }
        if (_historyEntries.Count == 0)
            HList.Items.Add("(no dictations yet)");
    }

    private void CopySelectedHistory()
    {
        int i = HList.SelectedIndex;
        if (i < 0 || i >= _historyEntries.Count) return;
        try
        {
            System.Windows.Clipboard.SetDataObject(_historyEntries[i].Text, true);
            HCopiedNote.Visibility = Visibility.Visible;
            var hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            hide.Tick += (_, _) => { HCopiedNote.Visibility = Visibility.Hidden; hide.Stop(); };
            hide.Start();
        }
        catch { /* clipboard briefly locked; user can retry */ }
    }

    private void HistoryList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => CopySelectedHistory();
    private void HistoryCopy_Click(object sender, RoutedEventArgs e) => CopySelectedHistory();
    private void HistoryRefresh_Click(object sender, RoutedEventArgs e) => RefreshHistoryList();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Load(); // start from disk so fields this UI doesn't cover survive

        TrayIcon.SetAutoStart(GStartWithWindows.IsChecked == true);
        s.EnableSounds = GSounds.IsChecked == true;

        s.DictationEnabled = DEnabled.IsChecked == true;
        s.Hotkey = DHotkey.Value;
        s.Language = string.IsNullOrWhiteSpace(DLanguage.Text) ? "en" : DLanguage.Text.Trim();
        s.MicDeviceName = DMic.Text.Trim();
        s.AppendSpace = DAppendSpace.IsChecked == true;
        s.RestoreClipboard = DRestoreClipboard.IsChecked == true;

        s.CleanupEnabled = CEnabled.IsChecked == true;
        s.ContextAwareness = CContext.IsChecked == true;
        s.AnthropicApiKey = CApiKey.Text.Trim();
        s.CleanupModel = string.IsNullOrWhiteSpace(CModel.Text) ? "claude-haiku-4-5" : CModel.Text.Trim();

        s.Screenshot.Enabled = SEnabled.IsChecked == true;
        s.Screenshot.Hotkey = SHotkey.Value;
        s.Screenshot.SmartDescribe = SSmart.IsChecked == true;
        s.Screenshot.SavePngTo = SSaveTo.Text.Trim();

        s.Display.Enabled = REnabled.IsChecked == true;
        s.Display.Hotkey = RHotkey.Value;
        s.Display.Presets = RPresets.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => DisplayFeature.ParsePreset(line) != null)
            .ToList();

        s.History.RepasteEnabled = HRepasteEnabled.IsChecked == true;
        s.History.RepasteHotkey = HHotkey.Value;

        s.Save();
        _onSaved();

        SavedNote.Visibility = Visibility.Visible;
        var hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        hide.Tick += (_, _) => { SavedNote.Visibility = Visibility.Hidden; hide.Stop(); };
        hide.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GeneralSection == null) return; // fires during InitializeComponent
        var sections = new[] { GeneralSection, DictationSection, CleanupSection, ScreenshotSection, DisplaySection, HistorySection };
        for (int i = 0; i < sections.Length; i++)
            sections[i].Visibility = Nav.SelectedIndex == i ? Visibility.Visible : Visibility.Collapsed;
    }
}
