using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using AvellCC.Core.Control;
using AvellCC.Ipc;
using Forms = System.Windows.Forms;

namespace AvellCC.App;

/// <summary>Ícone na bandeja: status no tooltip, troca rápida de modo das ventoinhas, abrir e sair.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MainWindow _window;
    private readonly AvellClient _client;
    private readonly Forms.ToolStripMenuItem _fanMenu = new("Ventoinhas");
    private LoopState? _lastState;

    public TrayIcon(MainWindow window, AvellClient client, Action exit)
    {
        _window = window;
        _client = client;
        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon.Create(LoopState.Firmware),
            Text = "AvellCC",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Items.Add("Abrir", null, (_, _) => ShowWindow());
        _icon.ContextMenuStrip.Items.Add(_fanMenu);
        _icon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add("Sair", null, (_, _) => exit());
        _icon.ContextMenuStrip.Opening += async (_, _) => await RebuildFanMenuAsync();
        _icon.DoubleClick += (_, _) => ShowWindow();
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
            AddItem("Firmware (padrão do notebook)", settings.FanMode == "firmware", () => _client.SetFanModeAsync("firmware", null));
            foreach (var c in curves)
            {
                var name = c.Name;
                AddItem($"Curva: {name}", settings.FanMode == "curve" && settings.CurveName == name, () => _client.SetFanModeAsync("curve", name));
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
        var text = s is null
            ? "AvellCC — serviço indisponível"
            : $"AvellCC — {MainWindow.StateText(s.State)}\nCPU {s.CpuTemp} °C · GPU {s.GpuTemp} °C\n{s.Fan1Rpm} / {s.Fan2Rpm} RPM";
        _icon.Text = text.Length > 127 ? text[..127] : text;     // limite do NotifyIcon

        var state = s?.State ?? LoopState.Error;
        if (state != _lastState)
        {
            var old = _icon.Icon;
            _icon.Icon = AppIcon.Create(state);
            old?.Dispose();
            _lastState = state;
            if (state is LoopState.Emergency)
                _icon.ShowBalloonTip(5000, "AvellCC", $"Temperatura alta: ventoinhas a 100 %. {s?.Message}", Forms.ToolTipIcon.Warning);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Ícone desenhado em código (sem arquivo binário no repositório): ventoinha com cor do estado.</summary>
public static class AppIcon
{
    public static Icon Create(LoopState state)
    {
        var color = state switch
        {
            LoopState.Curve => Color.FromArgb(59, 130, 246),
            LoopState.Firmware => Color.FromArgb(154, 161, 173),
            LoopState.Emergency or LoopState.Error => Color.FromArgb(239, 68, 68),
            _ => Color.FromArgb(245, 158, 11),
        };
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            for (var i = 0; i < 4; i++)
            {
                var state2 = g.Save();
                g.TranslateTransform(16, 16);
                g.RotateTransform(i * 90 + 20);
                g.FillEllipse(brush, -4, -15, 9, 14);   // pá
                g.Restore(state2);
            }
            using var hub = new SolidBrush(Color.FromArgb(22, 24, 29));
            g.FillEllipse(brush, 10, 10, 12, 12);
            g.FillEllipse(hub, 13, 13, 6, 6);
        }
        var handle = bmp.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var icon = (Icon)temp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
