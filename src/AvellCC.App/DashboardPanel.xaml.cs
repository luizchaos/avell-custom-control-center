using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AvellCC.App.Localization;
using AvellCC.Core.Control;
using AvellCC.Core.Monitoring;
using AvellCC.Ipc;

namespace AvellCC.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One usage ring (CPU or a GPU).</summary>
public sealed class GaugeItem(string key) : Observable
{
    private string _title = "", _subtitle = "", _footer = "", _footerCaption = "";
    private double _value;
    private bool _isAvailable;
    private Brush _footerBrush = Brushes.White;

    public string Key { get; } = key;
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    public double Value { get => _value; set => Set(ref _value, value); }
    public bool IsAvailable { get => _isAvailable; set => Set(ref _isAvailable, value); }
    public string Footer { get => _footer; set => Set(ref _footer, value); }
    public string FooterCaption { get => _footerCaption; set => Set(ref _footerCaption, value); }
    public Brush FooterBrush { get => _footerBrush; set => Set(ref _footerBrush, value); }
}

/// <summary>One line of the "System" list; the bar is shown when <see cref="Percent"/> is set.</summary>
public sealed class InfoRow(string key) : Observable
{
    private string _label = "", _value = "";
    private double? _percent;

    public string Key { get; } = key;
    public string Label { get => _label; set => Set(ref _label, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    public double? Percent
    {
        get => _percent;
        set { Set(ref _percent, value); Raise(nameof(MeterVisibility)); }
    }
    public Visibility MeterVisibility => _percent is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Dashboard tab: system usage (CPU, GPUs, memory, disks, network, power — read locally, no admin) plus
/// temperatures and fans from the service. Sampling runs only while the tab is visible.
/// </summary>
public partial class DashboardPanel : UserControl
{
    private readonly ObservableCollection<GaugeItem> _gauges = [];
    private readonly ObservableCollection<InfoRow> _rows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Task<SystemMonitor>? _monitor;
    private SystemSnapshot? _snapshot;
    private ControlStatus? _status;
    private bool _sampling;
    private bool _freeingMemory;
    private DateTime _memoryResultUntil;

    public DashboardPanel()
    {
        InitializeComponent();
        Gauges.ItemsSource = _gauges;
        Rows.ItemsSource = _rows;
        _timer.Tick += async (_, _) => await SampleAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { _timer.Start(); await SampleAsync(); }
            else _timer.Stop();
        };
        Loc.Instance.PropertyChanged += (_, _) => Render();
        Render();
    }

    /// <summary>Service client, for the actions that need administrator rights (free up memory).</summary>
    public AvellClient? Client { get; set; }

    /// <summary>Latest status from the service (null = service unavailable).</summary>
    public void UpdateStatus(ControlStatus? status)
    {
        _status = status;
        Render();
    }

    // ------------------------------------------------------------------ actions

    private async void FreeMemory_Click(object sender, RoutedEventArgs e)
    {
        if (Client is null) return;
        _freeingMemory = true;
        Render();
        try
        {
            var r = await Client.FreeMemoryAsync();
            ShowMemoryResult(Loc.T("dash.freedMemory", MonitorMath.Bytes(r.Freed), MonitorMath.Bytes(r.InUseBefore), MonitorMath.Bytes(r.InUseAfter)));
            await SampleAsync();
        }
        catch (Exception ex)
        {
            ShowMemoryResult(ex.Message, error: true);
        }
        finally
        {
            _freeingMemory = false;
            Render();
        }
    }

    private void ShowMemoryResult(string text, bool error = false)
    {
        MemoryResult.Text = text;
        MemoryResult.Foreground = Brush(error ? "Warn" : "Ok");
        MemoryResult.Visibility = Visibility.Visible;
        _memoryResultUntil = DateTime.UtcNow.AddSeconds(15);
    }

    /// <summary>Windows Settings → Storage (cleanup recommendations, temporary files, Storage Sense).</summary>
    private void CleanDisk_Click(object sender, RoutedEventArgs e)
    {
        if (!Launch("ms-settings:storagesense")) CleanDiskClassic_Click(sender, e);
    }

    /// <summary>The classic Disk Cleanup tool.</summary>
    private void CleanDiskClassic_Click(object sender, RoutedEventArgs e)
    {
        if (!Launch("cleanmgr.exe"))
            MessageBox.Show(Window.GetWindow(this)!, Loc.T("dash.cleanDiskFailed"), "AvellCC", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool Launch(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            _monitor ??= Task.Run(() => new SystemMonitor());   // DXGI and PDH setup off the UI thread
            var monitor = await _monitor;
            _snapshot = await Task.Run(monitor.Sample);
            Render();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"system monitor: {ex}");
        }
        finally
        {
            _sampling = false;
        }
    }

    // ------------------------------------------------------------------ rendering

    private void Render()
    {
        var info = _monitor is { IsCompletedSuccessfully: true } m ? m.Result.Info : null;
        var s = _snapshot;
        var status = _status;

        // ---- rings
        var gauges = new List<GaugeItem>();
        var cpu = Gauge(gauges, "cpu");
        cpu.Title = "CPU";
        cpu.Subtitle = info is null ? "" : ShortName(info.CpuName);
        cpu.Value = s?.CpuPercent ?? 0;
        cpu.IsAvailable = s is not null;
        SetTemperature(cpu, status?.CpuTemp, Loc.T("dash.temperature"));

        var gpus = s?.Gpus.ToList() ?? info?.Gpus.Select(a => new GpuLoad(a, 0, null)).ToList() ?? [];
        var discrete = gpus.FirstOrDefault(g => g.Adapter.IsDiscrete) ?? gpus.LastOrDefault();
        if (gpus.Count == 0)
        {
            var gpu = Gauge(gauges, "gpu");
            gpu.Title = "GPU";
            gpu.IsAvailable = false;
            SetTemperature(gpu, status?.GpuTemp, Loc.T("dash.temperature"));
        }
        foreach (var g in gpus)
        {
            var gauge = Gauge(gauges, $"gpu-{g.Adapter.Luid}");
            gauge.Title = gpus.Count > 1 ? $"GPU {gpus.IndexOf(g) + 1}" : "GPU";
            gauge.Subtitle = ShortName(g.Adapter.Name);
            gauge.Value = g.Percent;
            gauge.IsAvailable = s is not null;
            if (ReferenceEquals(g, discrete))
            {
                var vram = g.VramUsed is { } used && g.Adapter.DedicatedMemory > 0
                    ? Loc.T("dash.vram", MonitorMath.Bytes(used), MonitorMath.Bytes(g.Adapter.DedicatedMemory))
                    : Loc.T("dash.temperature");
                SetTemperature(gauge, status?.GpuTemp, vram);
            }
            else
            {
                gauge.Footer = "—";
                gauge.FooterBrush = Brush("Muted");
                gauge.FooterCaption = Loc.T("dash.integrated");
            }
        }
        Sync(_gauges, gauges);

        // ---- memory and disk tiles
        if (s is not null)
        {
            MemoryText.Text = $"{s.Memory.Percent:0} %";
            MemoryBar.Value = s.Memory.Percent;
            MemoryDetail.Text = Loc.T("dash.memoryDetail", MonitorMath.Bytes(s.Memory.Used), MonitorMath.Bytes(s.Memory.Total));
            DiskText.Text = s.DiskActivePercent is { } active ? $"{active:0} %" : "—";
            DiskBar.Value = s.DiskActivePercent ?? 0;
            var system = s.Drives.FirstOrDefault(d => d.Name.Equals(System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                         ?? s.Drives.FirstOrDefault();
            DiskDetail.Text = system is null ? "" : Loc.T("dash.diskDetail", system.Name, MonitorMath.Bytes(system.Free), MonitorMath.Bytes(system.Total));
        }

        FreeMemoryButton.IsEnabled = status is not null && Client is not null && !_freeingMemory;
        FreeMemoryButton.Content = Loc.T(_freeingMemory ? "dash.freeingMemory" : "dash.freeMemory");
        if (MemoryResult.Visibility == Visibility.Visible && DateTime.UtcNow > _memoryResultUntil) MemoryResult.Visibility = Visibility.Collapsed;

        // ---- fans (service)
        if (status is not null)
        {
            Fan1Text.Text = $"{status.Fan1Rpm} RPM";
            Fan2Text.Text = $"{status.Fan2Rpm} RPM";
            Fan1Bar.Value = status.Fan1Duty;
            Fan2Bar.Value = status.Fan2Duty;
            Fan1Duty.Text = Loc.T("dash.duty", status.Fan1Duty.ToString("0.#"));
            Fan2Duty.Text = Loc.T("dash.duty", status.Fan2Duty.ToString("0.#"));
            ModeText.Text = status.State switch
            {
                LoopState.Curve or LoopState.Emergency => Loc.T("dash.mode.curve", status.CurveName),
                LoopState.Firmware => Loc.T("dash.mode.firmware"),
                _ => MainWindow.StateText(status.State),
            };
            MessageText.Text = status.Message ?? "";
        }
        else
        {
            Fan1Text.Text = Fan2Text.Text = "— RPM";
            Fan1Bar.Value = Fan2Bar.Value = 0;
            Fan1Duty.Text = Fan2Duty.Text = " ";
            ModeText.Text = Loc.T("state.noService");
            MessageText.Text = "";
        }

        // ---- system list
        var rows = new List<InfoRow>();
        if (info is not null)
        {
            Row(rows, "cpu", Loc.T("dash.row.cpu"), ShortName(info.CpuName));
            foreach (var g in info.Gpus)
                Row(rows, $"gpu-{g.Luid}", Loc.T("dash.row.gpu"), g.IsDiscrete ? $"{ShortName(g.Name)} · {MonitorMath.Bytes(g.DedicatedMemory)}" : ShortName(g.Name));
            Row(rows, "ram", Loc.T("dash.row.ram"), Loc.T("dash.row.ramValue", MonitorMath.Bytes(info.InstalledMemory)));
        }
        if (s is not null)
        {
            foreach (var d in s.Drives)
                Row(rows, $"drive-{d.Name}", string.IsNullOrEmpty(d.Label) ? d.Name : $"{d.Name} {d.Label}",
                    Loc.T("dash.row.driveValue", MonitorMath.Bytes(d.Free), MonitorMath.Bytes(d.Total)), d.Percent);
            foreach (var n in s.Networks)
                Row(rows, $"net-{n.Id}", n.Name,
                    n.IsUp ? Loc.T("dash.row.netValue", MonitorMath.Rate(n.DownBytesPerSecond), MonitorMath.Rate(n.UpBytesPerSecond)) : Loc.T("dash.row.disconnected"));
            if (s.Power.PlanName is { } plan) Row(rows, "plan", Loc.T("dash.row.plan"), plan);
            if (s.Power.Mode != PowerMode.Unknown) Row(rows, "mode", Loc.T("dash.row.mode"), Loc.T($"power.{s.Power.Mode}"));
            var source = (s.Power.OnAc, s.Power.BatteryPercent) switch
            {
                (true, { } b) => Loc.T("dash.source.acBattery", b),
                (true, null) => Loc.T("dash.source.ac"),
                (false, { } b) => Loc.T("dash.source.battery", b),
                _ => null,
            };
            if (source is not null) Row(rows, "source", Loc.T("dash.row.source"), source);
        }
        Sync(_rows, rows);
    }

    private GaugeItem Gauge(List<GaugeItem> list, string key)
    {
        var item = _gauges.FirstOrDefault(g => g.Key == key) ?? new GaugeItem(key);
        list.Add(item);
        return item;
    }

    private void Row(List<InfoRow> list, string key, string label, string value, double? percent = null)
    {
        var row = _rows.FirstOrDefault(r => r.Key == key) ?? new InfoRow(key);
        row.Label = label;
        row.Value = value;
        row.Percent = percent;
        list.Add(row);
    }

    /// <summary>Updates the collection only where the set of items changed, so bound visuals are reused.</summary>
    private static void Sync<T>(ObservableCollection<T> target, List<T> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private static void SetTemperature(GaugeItem gauge, int? celsius, string caption)
    {
        gauge.Footer = celsius is { } t ? $"{t} °C" : "— °C";
        gauge.FooterBrush = celsius switch
        {
            >= 90 => Brush("Bad"),
            >= 80 => Brush("Warn"),
            null => Brush("Muted"),
            _ => Brush("Fg"),
        };
        gauge.FooterCaption = caption;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>"Intel(R) Core(TM) i9-10980HK CPU @ 2.40GHz" → "Intel Core i9-10980HK @ 2.40GHz".</summary>
    internal static string ShortName(string name) =>
        string.Join(' ', name.Replace("(R)", "").Replace("(TM)", "").Replace(" CPU ", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
