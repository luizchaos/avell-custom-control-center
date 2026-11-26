using System.Management;
using System.Text.Json;
using AvellCC.Core.Control;
using AvellCC.Core.Ec;
using AvellCC.Core.Fan;
using AvellCC.Core.Keyboard;
using AvellCC.Core.Safety;
using AvellCC.Ipc;

namespace AvellCC.Service;

public sealed record HubOptions(bool Simulate)
{
    public string Root => Simulate
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvellCC-sim")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AvellCC");
    public string PipeName => Simulate ? Protocol.PipeName + ".sim" : Protocol.PipeName;
}

/// <summary>
/// Dono único do hardware dentro do serviço: loop das ventoinhas, teclado, configurações e comandos da UI.
/// </summary>
public sealed class ControlHub : IDisposable
{
    private readonly HubOptions _options;
    private readonly ILogger<ControlHub> _log;
    private readonly IEcAccess _ec;
    private readonly SettingsStore _store;
    private readonly FanControlLoop _loop;
    private readonly Func<IFeatureTransport> _keyboardTransport;
    private readonly string _machine;
    private readonly object _settingsLock = new();
    private AppSettings _settings = new();

    public ControlHub(HubOptions options, ILogger<ControlHub> log)
    {
        _options = options;
        _log = log;
        _store = new SettingsStore(options.Root);
        Func<string[]> vendor = options.Simulate ? () => [] : VendorGuard.RunningVendorProcesses;
        _ec = options.Simulate ? new SimulatedEc() : new WmiEc();
        var machine = ReadMachine();
        _machine = $"{machine.Manufacturer} {machine.Model} (BIOS {machine.Bios})";
        var direct = new DirectFanService(_ec, new EcRegisterBackupStore(Path.Combine(options.Root, "backup"), DirectFanService.BackupGroup),
                                          () => machine, vendor);
        _loop = new FanControlLoop(_ec, direct, vendor);
        _keyboardTransport = options.Simulate
            ? () => new LoggingTransport(log)
            : () => HidFeatureDevice.Open(Ite8291.VendorId, Ite8291.UsagePage4Zone);
    }

    public ControlStatus Status => _loop.Status;

    public void Initialize()
    {
        _store.EnsurePresets();
        try
        {
            _settings = _store.Load();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "settings.json inválido; usando o padrão (firmware).");
            _settings = new AppSettings();
        }
        ApplyFanMode(_settings);
        ApplyKeyboard(_settings.Keyboard);
        _log.LogInformation("AvellCC iniciado ({Machine}). Ventoinhas: {Mode} {Curve}. Simulação: {Sim}",
                            _machine, _settings.FanMode, _settings.CurveName, _options.Simulate);
    }

    public ControlStatus Tick()
    {
        var status = _loop.Tick();
        if (status.State is LoopState.Error or LoopState.Emergency)
            _log.LogWarning("Ventoinhas: {State} — {Message}", status.State, status.Message);
        return status;
    }

    public void Suspend()
    {
        _log.LogInformation("Suspensão: devolvendo as ventoinhas ao firmware.");
        _loop.Suspend();
    }

    public void Resume()
    {
        _log.LogInformation("Retomada: reassumindo configurações.");
        _loop.Resume();
        ApplyKeyboard(_settings.Keyboard);
    }

    public void Shutdown()
    {
        _log.LogInformation("Parando: devolvendo as ventoinhas ao firmware.");
        _loop.Shutdown();
    }

    /// <summary>Comandos da UI (pipe).</summary>
    public Task<object?> HandleAsync(string command, JsonElement? args, CancellationToken cancel)
    {
        object? result = command switch
        {
            Protocol.Commands.Info => new ServiceInfo(Protocol.Version, typeof(ControlHub).Assembly.GetName().Version?.ToString() ?? "?", _options.Simulate, _machine),
            Protocol.Commands.Status => _loop.Status,
            Protocol.Commands.GetSettings => _settings,
            Protocol.Commands.ListCurves => _store.ListCurves(),
            Protocol.Commands.SaveCurve => SaveCurve(Arg<FanCurve>(args)),
            Protocol.Commands.SetFanMode => SetFanMode(Arg<SetFanModeArgs>(args)),
            Protocol.Commands.SetKeyboard => SetKeyboard(Arg<KeyboardSettings>(args)),
            _ => throw new ArgumentException($"Comando desconhecido: {command}"),
        };
        return Task.FromResult(result);
    }

    private object? SaveCurve(FanCurve curve)
    {
        _store.SaveCurve(curve);
        _log.LogInformation("Curva '{Name}' salva.", curve.Name);
        lock (_settingsLock)
        {
            if (_settings.FanMode == "curve" && _settings.CurveName == curve.Name) _loop.UseCurve(curve);   // edição da curva ativa
        }
        return null;
    }

    private AppSettings SetFanMode(SetFanModeArgs a)
    {
        lock (_settingsLock)
        {
            var next = _settings with { FanMode = a.Mode, CurveName = a.CurveName ?? _settings.CurveName };
            next.Validate();
            ApplyFanMode(next);          // valida a curva (lança antes de salvar)
            _store.Save(next);
            _settings = next;
            _log.LogInformation("Ventoinhas: {Mode} {Curve}", next.FanMode, next.CurveName);
            return next;
        }
    }

    private AppSettings SetKeyboard(KeyboardSettings keyboard)
    {
        keyboard.Validate();
        lock (_settingsLock)
        {
            using (var kb = new FourZoneKeyboard(_keyboardTransport(), FourZoneKeyboard.DefaultProjectId))
                keyboard.ApplyTo(kb);
            var next = _settings with { Keyboard = keyboard };
            _store.Save(next);
            _settings = next;
            return next;
        }
    }

    private void ApplyFanMode(AppSettings settings)
    {
        if (settings.FanMode == "curve") _loop.UseCurve(_store.LoadCurve(settings.CurveName));
        else _loop.UseFirmware();
    }

    private void ApplyKeyboard(KeyboardSettings? keyboard)
    {
        if (keyboard is null) return;
        try
        {
            using var kb = new FourZoneKeyboard(_keyboardTransport(), FourZoneKeyboard.DefaultProjectId);
            keyboard.ApplyTo(kb);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao aplicar o teclado.");
        }
    }

    private static T Arg<T>(JsonElement? args) =>
        args is { } a ? a.Deserialize<T>(Protocol.Json) ?? throw new ArgumentException("Argumentos vazios.") : throw new ArgumentException("Faltam argumentos.");

    private static MachineInfo ReadMachine()
    {
        try
        {
            using var cs = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem").Get().Cast<ManagementObject>().First();
            using var bios = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS").Get().Cast<ManagementObject>().First();
            return new MachineInfo
            {
                Manufacturer = cs["Manufacturer"]?.ToString() ?? "",
                Model = cs["Model"]?.ToString() ?? "",
                Bios = bios["SMBIOSBIOSVersion"]?.ToString() ?? "",
            };
        }
        catch (ManagementException)
        {
            return new MachineInfo { Model = "?" };
        }
    }

    public void Dispose() => (_ec as IDisposable)?.Dispose();

    /// <summary>Teclado falso do modo simulação: só registra os pacotes.</summary>
    private sealed class LoggingTransport(ILogger log) : IFeatureTransport
    {
        public void SetFeature(byte[] report) => log.LogInformation("[sim] HID {Report}", Convert.ToHexString(report));
        public byte[] GetFeature(byte reportId, int length) => new byte[length];
        public void Dispose() { }
    }
}
