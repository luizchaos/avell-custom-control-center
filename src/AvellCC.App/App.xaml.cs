using System.Threading;
using System.Windows;
using AvellCC.Ipc;

namespace AvellCC.App;

/// <summary>
/// UI do AvellCC: janela + ícone na bandeja. Não acessa hardware: tudo passa pelo serviço (named pipe).
///   AvellCC.exe               → conecta no serviço instalado
///   AvellCC.exe --simulate    → conecta no serviço em modo simulação (AvellCC.Service.exe --simulate)
///   AvellCC.exe --minimized   → inicia só na bandeja (usado no início com o Windows)
/// </summary>
public partial class App : Application
{
    private Mutex? _single;
    private TrayIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var simulate = e.Args.Contains("--simulate");
        _single = new Mutex(true, simulate ? "AvellCC.App.sim" : "AvellCC.App", out var first);
        if (!first)
        {
            MessageBox.Show("O AvellCC já está aberto (veja o ícone na bandeja).", "AvellCC", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var client = new AvellClient(simulate ? Protocol.PipeName + ".sim" : Protocol.PipeName);
        _window = new MainWindow(client, simulate);
        _tray = new TrayIcon(_window, client, ExitApp);
        _window.StatusUpdated += _tray.Update;

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
