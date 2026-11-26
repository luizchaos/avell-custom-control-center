using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using AvellCC.App.Localization;
using AvellCC.Core.Control;
using AvellCC.Ipc;
using Forms = System.Windows.Forms;

namespace AvellCC.App;

/// <summary>Tray icon: status tooltip, quick fan-mode switch, open and exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MainWindow _window;
    private readonly AvellClient _client;
    private readonly Forms.ToolStripMenuItem _open = new();
    private readonly Forms.ToolStripMenuItem _fanMenu = new();
    private readonly Forms.ToolStripMenuItem _exit = new();
    private LoopState? _lastState;
    private ControlStatus? _lastStatus;

    public TrayIcon(MainWindow window, AvellClient client, Action exit)
    {
        _window = window;
        _client = client;
        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon.Create(LoopState.Firmware, Math.Clamp(Forms.SystemInformation.SmallIconSize.Width, 16, 64)),
            Text = "AvellCC",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _open.Click += (_, _) => ShowWindow();
        _exit.Click += (_, _) => exit();
        _icon.ContextMenuStrip.Items.Add(_open);
        _icon.ContextMenuStrip.Items.Add(_fanMenu);
        _icon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add(_exit);
        _icon.ContextMenuStrip.Opening += async (_, _) => await RebuildFanMenuAsync();
        _icon.DoubleClick += (_, _) => ShowWindow();
        Loc.Instance.PropertyChanged += (_, _) => RefreshTexts();
        RefreshTexts();
    }

    private void RefreshTexts()
    {
        _open.Text = Loc.T("tray.open");
        _fanMenu.Text = Loc.T("tray.fans");
        _exit.Text = Loc.T("tray.exit");
        Update(_lastStatus);
    }

    private void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async Task RebuildFanMenuAsync()
    {
        _fanMenu.DropDownItems.Clear();
        try
        {
            var settings = await _client.GetSettingsAsync();
            var curves = await _client.ListCurvesAsync();
            AddItem(Loc.T("tray.firmware"), settings.FanMode == "firmware", () => _client.SetFanModeAsync("firmware", null));
            foreach (var c in curves)
            {
                var name = c.Name;
                AddItem(Loc.T("tray.curve", name), settings.FanMode == "curve" && settings.CurveName == name, () => _client.SetFanModeAsync("curve", name));
            }
        }
        catch (Exception ex)
        {
            _fanMenu.DropDownItems.Add(new Forms.ToolStripMenuItem(ex.Message) { Enabled = false });
        }

        void AddItem(string text, bool check, Func<Task> action)
        {
            var item = new Forms.ToolStripMenuItem(text) { Checked = check };
            item.Click += async (_, _) =>
            {
                try { await action(); await _window.ReloadSettingsAsync(); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "AvellCC", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            _fanMenu.DropDownItems.Add(item);
        }
    }

    public void Update(ControlStatus? s)
    {
        _lastStatus = s;
        var text = s is null
            ? Loc.T("tray.noService")
            : Loc.T("tray.tooltip", MainWindow.StateText(s.State), s.CpuTemp, s.GpuTemp, s.Fan1Rpm, s.Fan2Rpm);
        _icon.Text = text.Length > 127 ? text[..127] : text;     // NotifyIcon limit

        var state = s?.State ?? LoopState.Error;
        if (state != _lastState)
        {
            var old = _icon.Icon;
            _icon.Icon = AppIcon.Create(state, Math.Clamp(Forms.SystemInformation.SmallIconSize.Width, 16, 64));
            old?.Dispose();
            _lastState = state;
            if (state is LoopState.Emergency)
                _icon.ShowBalloonTip(5000, "AvellCC", Loc.T("tray.emergency", s?.Message), Forms.ToolTipIcon.Warning);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
