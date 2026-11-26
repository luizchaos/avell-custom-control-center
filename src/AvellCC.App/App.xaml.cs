using System.Threading;
using System.Windows;
using AvellCC.App.Localization;
using AvellCC.Ipc;

namespace AvellCC.App;

/// <summary>
/// AvellCC UI: window + tray icon. It never touches hardware: everything goes through the service (named pipe).
///   AvellCC.exe                       → connects to the installed service
///   AvellCC.exe --simulate            → connects to the simulation service (AvellCC.Service.exe --simulate)
///   AvellCC.exe --minimized           → starts in the tray only (used when starting with Windows)
///   AvellCC.exe --lang pt-BR          → forces a UI language for this run
///   AvellCC.exe --screenshot &lt;dir&gt;   → renders every tab to PNG and exits (development)
/// </summary>
public partial class App : Application
{
    private Mutex? _single;
    private TrayIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var lang = Array.IndexOf(e.Args, "--lang");
        if (lang >= 0 && lang + 1 < e.Args.Length) Loc.Instance.Load(e.Args[lang + 1]);
        else Loc.Instance.LoadPreferred();

        var simulate = e.Args.Contains("--simulate");
        _single = new Mutex(true, simulate ? "AvellCC.App.sim" : "AvellCC.App", out var first);
        if (!first)
        {
            MessageBox.Show(Loc.T("app.alreadyRunning"), "AvellCC", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var client = new AvellClient(simulate ? Protocol.PipeName + ".sim" : Protocol.PipeName);
        _window = new MainWindow(client, simulate);
        _tray = new TrayIcon(_window, client, ExitApp);
        _window.StatusUpdated += _tray.Update;

        var shot = Array.IndexOf(e.Args, "--screenshot");
        if (shot >= 0 && shot + 1 < e.Args.Length)
        {
            var dir = e.Args[shot + 1];
            _window.Loaded += async (_, _) =>      // subscribe BEFORE Show
            {
                try { await _window.CaptureTabsAsync(dir); }
                finally { ExitApp(); }
            };
        }

        if (!e.Args.Contains("--minimized")) _window.Show();
    }

    private void ExitApp()
    {
        _tray?.Dispose();
        _window?.CloseForReal();
        _single?.ReleaseMutex();
        Shutdown();
    }
}
