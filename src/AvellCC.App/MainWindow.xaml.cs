using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AvellCC.App.Localization;
using AvellCC.Core.Control;
using AvellCC.Core.Fan;
using AvellCC.Core.Keyboard;
using AvellCC.Ipc;
using Forms = System.Windows.Forms;

namespace AvellCC.App;

/// <summary>Editable row of the curve editor grids.</summary>
public sealed class PointRow
{
    public int Temp { get; set; }
    public int Duty { get; set; }
}

public partial class MainWindow : Window
{
    private static readonly Brush CpuBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush GpuBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x32, 0x3B));
    private static readonly Brush AxisText = new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xAD));

    private readonly AvellClient _client;
    private readonly bool _simulate;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ObservableCollection<PointRow> _cpu = [];
    private readonly ObservableCollection<PointRow> _gpu = [];
    private readonly string[] _kbColors = ["FFFFFF", "FFFFFF", "FFFFFF", "FFFFFF"];
    private List<FanCurve> _curves = [];
    private AppSettings? _settings;
    private ControlStatus? _status;
    private ServiceInfo? _info;
    private bool _reallyClose;
    private bool _polling;
    private bool _loadingUi;
    private int? _lastModeSwitch;

    public event Action<ControlStatus?>? StatusUpdated;

    public MainWindow(AvellClient client, bool simulate)
    {
        _client = client;
        _simulate = simulate;
        InitializeComponent();
        Dashboard.Client = client;

        CpuGrid.ItemsSource = _cpu;
        GpuGrid.ItemsSource = _gpu;
        _cpu.CollectionChanged += (_, _) => DrawChart();
        _gpu.CollectionChanged += (_, _) => DrawChart();
        KbEffectCombo.ItemsSource = Ite8291.FourZoneEffects.Select(e => e.ToString()).ToArray();   // only effects that work on 4 zones
        KbEffectCombo.SelectedItem = nameof(Ite8291.Effect.Wave);
        KbModeCombo.SelectedIndex = 0;
        KbDirectionCombo.SelectedIndex = 0;
        StartupCheck.IsChecked = StartupRegistration.IsEnabled;

        _loadingUi = true;
        LanguageCombo.ItemsSource = Loc.Available();
        LanguageCombo.SelectedItem = Loc.Available().FirstOrDefault(l => l.Code == Loc.Instance.Language);
        _loadingUi = false;
        Loc.Instance.PropertyChanged += (_, _) => RefreshTexts();
        RefreshTexts();

        _timer.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) => await InitAsync();
        _timer.Start();
        _ = PollAsync();
    }

    // ------------------------------------------------------------------ lifecycle

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            e.Cancel = true;   // closing the window = go to the tray
            Hide();
        }
        base.OnClosing(e);
    }

    public void CloseForReal()
    {
        _reallyClose = true;
        _timer.Stop();
        Close();
    }

    private async Task InitAsync()
    {
        try
        {
            _info = await _client.InfoAsync();
            RefreshTexts();
            await ReloadSettingsAsync();
        }
        catch (Exception ex)
        {
            MachineText.Text = ex.Message;
        }
    }

    /// <summary>Texts set from code (not bound in XAML). Called on startup, on language change and when data arrives.</summary>
    private void RefreshTexts()
    {
        Title = _simulate ? $"AvellCC — {Loc.T("app.simulation")}" : "AvellCC";
        if (_info is { } info)
        {
            MachineText.Text = Loc.T("header.service", info.Machine, info.Version) + (info.Simulated ? $" · {Loc.T("app.simulation")}" : "");
            AboutText.Text = Loc.T("settings.aboutText", info.Version, info.ProtocolVersion, info.Machine);
        }
        var buttons = new[] { Zone1Button, Zone2Button, Zone3Button, Zone4Button };
        for (var i = 0; i < buttons.Length; i++) buttons[i].ToolTip = Loc.T("kb.zone", i + 1);
        UpdateModeHelp();
        UpdateKbButtons();
        if (_status is not null) ShowStatus(_status);
        else if (Banner.Visibility == Visibility.Visible) ShowNoService();
    }

    public async Task ReloadSettingsAsync()
    {
        _settings = await _client.GetSettingsAsync();
        _curves = [.. await _client.ListCurvesAsync()];

        _loadingUi = true;
        try
        {
            FirmwareRadio.IsChecked = _settings.FanMode == "firmware";
            CurveRadio.IsChecked = _settings.FanMode == "curve";
            CurveCombo.ItemsSource = _curves;
            CurveCombo.SelectedItem = _curves.FirstOrDefault(c => c.Name == _settings.CurveName) ?? _curves.FirstOrDefault();
            LoadEditor(CurveCombo.SelectedItem as FanCurve);
            if (_settings.Keyboard is { } kb) LoadKeyboard(kb);
            LoadKeyboardProfiles();
            ModeButtonCheck.IsChecked = _settings.ModeButtonCyclesCurves;
            UpdateModeHelp();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void LoadKeyboardProfiles()
    {
        if (_settings is null) return;
        KbProfileCombo.ItemsSource = _settings.KeyboardProfiles.Select(p => p.Name).ToList();
        KbProfileCombo.SelectedItem = _settings.KeyboardProfile;
        if (_settings.KeyboardProfile is null) KbProfileCombo.Text = "";
    }

    // ------------------------------------------------------------------ status

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            _status = await _client.StatusAsync();
            ShowStatus(_status);
            StatusUpdated?.Invoke(_status);
            if (_settings is null) await InitAsync();
            else if (_lastModeSwitch is { } seen && _status.ModeSwitchCount != seen) await ReloadSettingsAsync();   // mode button changed the curve
            _lastModeSwitch = _status.ModeSwitchCount;
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or ServiceException)
        {
            _status = null;
            ShowNoService();
            StatusUpdated?.Invoke(null);
        }
        finally
        {
            _polling = false;
        }
    }

    private void ShowNoService()
    {
        ShowBanner(Loc.T(_simulate ? "banner.noServiceSim" : "banner.noService"), "Bad");
        StatusText.Text = Loc.T("state.noService");
        StatusDot.Fill = (Brush)FindResource("Bad");
        Dashboard.UpdateStatus(null);
    }

    private void ShowStatus(ControlStatus s)
    {
        Dashboard.UpdateStatus(s);
        StatusText.Text = StateText(s.State);
        StatusDot.Fill = (Brush)FindResource(s.State switch
        {
            LoopState.Curve => "Ok",
            LoopState.Firmware => "Muted",
            LoopState.Emergency or LoopState.Error => "Bad",
            _ => "Warn",
        });

        switch (s.State)
        {
            case LoopState.Emergency: ShowBanner(Loc.T("banner.emergency", s.Message), "Bad"); break;
            case LoopState.BlockedByVendor: ShowBanner(Loc.T("banner.vendor", s.Message), "Warn"); break;
            case LoopState.Error: ShowBanner(Loc.T("banner.error", s.Message), "Bad"); break;
            case LoopState.Suspended: ShowBanner(Loc.T("banner.suspended"), "Warn"); break;
            default: Banner.Visibility = Visibility.Collapsed; break;
        }
        DrawChart();
    }

    private void ShowBanner(string text, string brush)
    {
        BannerText.Text = text;
        Banner.Background = (Brush)FindResource(brush);
        Banner.Visibility = Visibility.Visible;
    }

    public static string StateText(LoopState state) => Loc.T($"state.{state}");

    // ------------------------------------------------------------------ fans

    private void ModeRadio_Click(object sender, RoutedEventArgs e) => UpdateModeHelp();

    /// <summary>Shows the explanation of the selected mode (firmware vs. AvellCC curve).</summary>
    private void UpdateModeHelp()
    {
        var curve = CurveRadio.IsChecked == true;
        ModeHelpFirmware.Visibility = curve ? Visibility.Collapsed : Visibility.Visible;
        ModeHelpCurve.Visibility = curve ? Visibility.Visible : Visibility.Collapsed;
        CurveCombo.IsEnabled = curve;
    }

    private async void ApplyMode_Click(object sender, RoutedEventArgs e)
    {
        var mode = CurveRadio.IsChecked == true ? "curve" : "firmware";
        var name = (CurveCombo.SelectedItem as FanCurve)?.Name;
        await Run(async () =>
        {
            await _client.SetFanModeAsync(mode, name);
            await ReloadSettingsAsync();
            await PollAsync();
        });
    }

    private void CurveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingUi) LoadEditor(CurveCombo.SelectedItem as FanCurve);
    }

    private void LoadEditor(FanCurve? curve)
    {
        _cpu.Clear();
        _gpu.Clear();
        if (curve is null) return;
        NameBox.Text = curve.Name;
        HysteresisBox.Text = curve.Hysteresis.ToString(CultureInfo.InvariantCulture);
        SharedCheck.IsChecked = curve.Shared;
        foreach (var p in curve.Cpu) _cpu.Add(new PointRow { Temp = p.Temp, Duty = p.Duty });
        foreach (var p in curve.Gpu) _gpu.Add(new PointRow { Temp = p.Temp, Duty = p.Duty });
    }

    private FanCurve BuildCurveFromEditor()
    {
        CpuGrid.CommitEdit(DataGridEditingUnit.Row, true);
        GpuGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!int.TryParse(HysteresisBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hysteresis))
            throw new ArgumentException(Loc.T("fans.errHysteresis"));
        return new FanCurve
        {
            Name = NameBox.Text.Trim(),
            Hysteresis = hysteresis,
            Shared = SharedCheck.IsChecked == true,
            Cpu = _cpu.OrderBy(p => p.Temp).Select(p => new CurvePoint(p.Temp, p.Duty)).ToArray(),
            Gpu = _gpu.OrderBy(p => p.Temp).Select(p => new CurvePoint(p.Temp, p.Duty)).ToArray(),
        };
    }

    private async void SaveCurve_Click(object sender, RoutedEventArgs e)
    {
        await Run(async () =>
        {
            var curve = BuildCurveFromEditor();
            curve.Validate();                       // clear error before reaching the service
            await _client.SaveCurveAsync(curve);
            await ReloadSettingsAsync();
            CurveCombo.SelectedItem = _curves.FirstOrDefault(c => c.Name == curve.Name);
            var active = _settings?.FanMode == "curve" && _settings.CurveName == curve.Name;
            MessageBox.Show(this, Loc.T("fans.saved", curve.Name) + Loc.T(active ? "fans.savedActive" : "fans.savedInactive"),
                            "AvellCC", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void ReloadCurve_Click(object sender, RoutedEventArgs e) => LoadEditor(CurveCombo.SelectedItem as FanCurve);

    private void AddCpuPoint_Click(object sender, RoutedEventArgs e) => AddPoint(_cpu);
    private void AddGpuPoint_Click(object sender, RoutedEventArgs e) => AddPoint(_gpu);

    private static void AddPoint(ObservableCollection<PointRow> rows)
    {
        var last = rows.LastOrDefault();
        rows.Add(new PointRow { Temp = Math.Min(90, (last?.Temp ?? 40) + 5), Duty = Math.Min(100, (last?.Duty ?? 25) + 10) });
    }

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(DrawChart, DispatcherPriority.Background);

    private void Editor_Changed(object sender, RoutedEventArgs e) => DrawChart();

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    // ------------------------------------------------------------------ chart

    private void DrawChart()
    {
        var c = ChartCanvas;
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 50 || h < 50) return;
        const double left = 36, bottom = 24, top = 8, right = 8;
        const double tMin = 20, tMax = 100;
        double X(double t) => left + (t - tMin) / (tMax - tMin) * (w - left - right);
        double Y(double d) => top + (1 - d / 100) * (h - top - bottom);

        for (var t = 20; t <= 100; t += 10)
        {
            c.Children.Add(new Line { X1 = X(t), X2 = X(t), Y1 = Y(0), Y2 = Y(100), Stroke = GridBrush, StrokeThickness = 1 });
            AddText($"{t}°", X(t) - 10, h - bottom + 4);
        }
        for (var d = 0; d <= 100; d += 25)
        {
            c.Children.Add(new Line { X1 = X(tMin), X2 = X(tMax), Y1 = Y(d), Y2 = Y(d), Stroke = GridBrush, StrokeThickness = 1 });
            AddText($"{d}%", 0, Y(d) - 8);
        }
        // effective minimum band (25 %)
        c.Children.Add(new Rectangle
        {
            Width = X(tMax) - X(tMin), Height = Y(0) - Y(DirectFanService.MinPercent),
            Fill = new SolidColorBrush(Color.FromArgb(40, 239, 68, 68)),
        });
        Canvas.SetLeft(c.Children[^1], X(tMin));
        Canvas.SetTop(c.Children[^1], Y(DirectFanService.MinPercent));

        DrawSeries(_gpu, GpuBrush);
        DrawSeries(_cpu, CpuBrush);

        if (_status is { } s)
        {
            Marker(s.CpuTemp, CpuBrush);
            Marker(s.GpuTemp, GpuBrush);
            var dot = new Ellipse { Width = 10, Height = 10, Fill = Brushes.White };
            c.Children.Add(dot);
            Canvas.SetLeft(dot, X(Math.Max(s.CpuTemp, s.GpuTemp)) - 5);
            Canvas.SetTop(dot, Y(Math.Max(s.Fan1Duty, s.Fan2Duty)) - 5);
        }

        void DrawSeries(IEnumerable<PointRow> rows, Brush brush)
        {
            var pts = rows.OrderBy(p => p.Temp).ToList();
            if (pts.Count == 0) return;
            var line = new Polyline { Stroke = brush, StrokeThickness = 2.5 };
            line.Points.Add(new Point(X(tMin), Y(Math.Max(DirectFanService.MinPercent, pts[0].Duty))));
            foreach (var p in pts) line.Points.Add(new Point(X(p.Temp), Y(Math.Max(DirectFanService.MinPercent, p.Duty))));
            line.Points.Add(new Point(X(tMax), Y(pts[^1].Duty)));
            c.Children.Add(line);
            foreach (var p in pts)
            {
                var e = new Ellipse { Width = 8, Height = 8, Fill = brush };
                c.Children.Add(e);
                Canvas.SetLeft(e, X(p.Temp) - 4);
                Canvas.SetTop(e, Y(p.Duty) - 4);
            }
        }

        void Marker(int temp, Brush brush) =>
            c.Children.Add(new Line { X1 = X(temp), X2 = X(temp), Y1 = Y(0), Y2 = Y(100), Stroke = brush, StrokeThickness = 1, StrokeDashArray = [4, 4], Opacity = 0.8 });

        void AddText(string text, double x, double y)
        {
            var tb = new TextBlock { Text = text, FontSize = 10, Foreground = AxisText };
            c.Children.Add(tb);
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
        }
    }

    // ------------------------------------------------------------------ keyboard

    private void LoadKeyboard(KeyboardSettings kb)
    {
        foreach (ComboBoxItem item in KbModeCombo.Items) if ((string)item.Tag == kb.Mode) KbModeCombo.SelectedItem = item;
        for (var i = 0; i < _kbColors.Length; i++) _kbColors[i] = i < kb.Colors.Length ? kb.Colors[i] : kb.Colors.LastOrDefault() ?? "FFFFFF";
        KbBrightness.Value = kb.Brightness;
        KbSpeed.Value = kb.Speed;
        KbEffectCombo.SelectedItem = kb.EffectiveEffect.ToString();   // removed effects show as Wave
        foreach (ComboBoxItem item in KbDirectionCombo.Items) if ((string)item.Tag == kb.Direction) KbDirectionCombo.SelectedItem = item;
        UpdateKbButtons();
    }

    private string KbMode => (string)((ComboBoxItem)KbModeCombo.SelectedItem).Tag;

    private void KbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KbHint is null || KbModeCombo.SelectedItem is null) return;
        UpdateKbButtons();
    }

    private void UpdateKbButtons()
    {
        if (KbModeCombo.SelectedItem is null) return;
        var buttons = new[] { Zone1Button, Zone2Button, Zone3Button, Zone4Button };
        var visible = KbMode switch { "static" => 1, "zones" => 4, "effect" => 4, _ => 0 };
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + _kbColors[i]));
            buttons[i].Visibility = i < visible ? Visibility.Visible : Visibility.Collapsed;
        }
        KbEffectCombo.IsEnabled = KbSpeed.IsEnabled = KbDirectionCombo.IsEnabled = KbMode == "effect";
        KbHint.Text = Loc.T($"kb.hint.{KbMode}");
    }

    private void Zone_Click(object sender, RoutedEventArgs e)
    {
        var index = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
        var current = KeyboardSettings.ParseColor(_kbColors[index]);
        using var dialog = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B) };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _kbColors[index] = $"{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        UpdateKbButtons();
    }

    private KeyboardSettings BuildKeyboardSettings()
    {
        var mode = KbMode;
        var count = mode switch { "static" => 1, "zones" => 4, "effect" => 4, _ => 1 };
        return new KeyboardSettings
        {
            Mode = mode,
            Colors = _kbColors.Take(count).ToArray(),
            Brightness = (int)KbBrightness.Value,
            Speed = (int)KbSpeed.Value,
            Effect = (string?)KbEffectCombo.SelectedItem ?? nameof(Ite8291.Effect.Wave),
            Direction = (string)((ComboBoxItem)KbDirectionCombo.SelectedItem).Tag,
        };
    }

    private async void ApplyKeyboard_Click(object sender, RoutedEventArgs e)
    {
        var settings = BuildKeyboardSettings();
        await Run(async () =>
        {
            _settings = await _client.SetKeyboardAsync(settings);   // manual change: no profile selected anymore
            _loadingUi = true;
            try { LoadKeyboardProfiles(); }
            finally { _loadingUi = false; }
        });
    }

    // ------------------------------------------------------------------ keyboard profiles

    /// <summary>Picking a profile from the list applies it right away.</summary>
    private async void KbProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || KbProfileCombo.SelectedItem is not string name) return;
        await Run(async () =>
        {
            _settings = await _client.ApplyKeyboardProfileAsync(name);
            if (_settings.Keyboard is { } kb) LoadKeyboard(kb);
        });
    }

    private async void SaveKbProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = KbProfileCombo.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, Loc.T("kb.profileNameMissing"), "AvellCC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_settings?.FindKeyboardProfile(name) is not null &&
            MessageBox.Show(this, Loc.T("kb.profileOverwrite", name), "AvellCC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var profile = new KeyboardProfile { Name = name, Keyboard = BuildKeyboardSettings() };
        await Run(async () =>
        {
            profile.Validate();   // clear error before reaching the service
            _settings = await _client.SaveKeyboardProfileAsync(profile);
            _settings = await _client.ApplyKeyboardProfileAsync(profile.Name);   // what you save is what you see
            _loadingUi = true;
            try { LoadKeyboardProfiles(); }
            finally { _loadingUi = false; }
        });
    }

    private async void DeleteKbProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = (KbProfileCombo.SelectedItem as string) ?? KbProfileCombo.Text.Trim();
        if (_settings?.FindKeyboardProfile(name) is not { } profile) return;
        if (MessageBox.Show(this, Loc.T("kb.profileDeleteConfirm", profile.Name), "AvellCC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await Run(async () =>
        {
            _settings = await _client.DeleteKeyboardProfileAsync(profile.Name);
            _loadingUi = true;
            try { LoadKeyboardProfiles(); }
            finally { _loadingUi = false; }
        });
    }

    // ------------------------------------------------------------------ settings

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || LanguageCombo.SelectedItem is not LanguageInfo language) return;
        Loc.Instance.Load(language.Code, remember: true);
    }

    private async void ModeButtonCheck_Click(object sender, RoutedEventArgs e)
    {
        var enabled = ModeButtonCheck.IsChecked == true;
        await Run(async () => { _settings = await _client.SetModeButtonAsync(enabled); });
        ModeButtonCheck.IsChecked = _settings?.ModeButtonCyclesCurves ?? enabled;
    }

    private void StartupCheck_Click(object sender, RoutedEventArgs e)
    {
        try { StartupRegistration.Set(StartupCheck.IsChecked == true); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "AvellCC", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ------------------------------------------------------------------ development

    /// <summary>--screenshot &lt;folder&gt;: renders every tab to PNG (automated visual check).</summary>
    public async Task CaptureTabsAsync(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        await Task.Delay(4000);      // give the first status and settings time to arrive
        for (var i = 0; i < Tabs.Items.Count; i++)
        {
            Tabs.SelectedIndex = i;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            DrawChart();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var root = (FrameworkElement)Content;
            var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            var tag = ((TabItem)Tabs.Items[i]).Tag?.ToString() ?? i.ToString(CultureInfo.InvariantCulture);
            await using var file = System.IO.File.Create(System.IO.Path.Combine(directory, $"{i}-{tag}-{Loc.Instance.Language}.png"));
            encoder.Save(file);
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "AvellCC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
