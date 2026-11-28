using System.Management;
using System.Text.Json;
using AvellCC.Core.Control;
using AvellCC.Core.Ec;
using AvellCC.Core.Fan;
using AvellCC.Core.Keyboard;
using AvellCC.Core.Maintenance;
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
/// Single owner of the hardware inside the service: fan loop, keyboard, settings and UI commands.
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
    private static readonly TimeSpan MemoryCooldown = TimeSpan.FromSeconds(20);
    private readonly IMemoryCleaner _memoryCleaner;
    private readonly object _memoryLock = new();
    private DateTime _lastMemoryClean = DateTime.MinValue;
    private static readonly TimeSpan ModeButtonDebounce = TimeSpan.FromMilliseconds(400);
    private EcEventWatcher? _events;
    private DateTime _lastModeButton = DateTime.MinValue;
    private int _modeSwitchCount;
    private string? _modeSwitchCurve;

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
        _memoryCleaner = options.Simulate ? new SimulatedMemoryCleaner() : new MemoryCleaner();
        _keyboardTransport = options.Simulate
            ? () => new LoggingTransport(log)
            : () => HidFeatureDevice.Open(Ite8291.VendorId, Ite8291.UsagePage4Zone);
    }

    /// <summary>Loop status plus the mode button counter (the UI notifies when it changes).</summary>
    public ControlStatus Status => _loop.Status with { ModeSwitchCount = _modeSwitchCount, ModeSwitchCurve = _modeSwitchCurve };

    public void Initialize()
    {
        StartEcEvents();
        _store.EnsurePresets();
        try
        {
            _settings = _store.Load();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Invalid settings.json; using defaults (firmware).");
            _settings = new AppSettings();
        }
        ApplyFanMode(_settings);
        ApplyKeyboard(_settings.Keyboard);
        _log.LogInformation("AvellCC started ({Machine}). Fans: {Mode} {Curve}. Simulation: {Sim}",
                            _machine, _settings.FanMode, _settings.CurveName, _options.Simulate);
    }

    public ControlStatus Tick()
    {
        var status = _loop.Tick();
        if (status.State is LoopState.Error or LoopState.Emergency)
            _log.LogWarning("Fans: {State} — {Message}", status.State, status.Message);
        return status;
    }

    public void Suspend()
    {
        _log.LogInformation("Suspend: handing the fans back to the firmware.");
        _loop.Suspend();
    }

    public void Resume()
    {
        _log.LogInformation("Resume: re-applying settings.");
        _loop.Resume();
        ApplyKeyboard(_settings.Keyboard);
    }

    public void Shutdown()
    {
        _log.LogInformation("Stopping: handing the fans back to the firmware.");
        _events?.Dispose();
        _events = null;
        _loop.Shutdown();
    }

    // ------------------------------------------------------------------ operational mode button

    private void StartEcEvents()
    {
        if (_options.Simulate) return;   // simulation: pressModeButton command instead
        try
        {
            _events = new EcEventWatcher();
            _events.EventReceived += code =>
            {
                if (code == EcEvents.ModeButton) OnModeButton();
            };
            _events.Start();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _log.LogWarning("EC events unavailable ({Message}); the mode button will not switch curves.", ex.Message);
            _events?.Dispose();
            _events = null;
        }
    }

    /// <summary>
    /// Operational mode button. In curve mode: next curve (alphabetical, wraps) + notification for the UI.
    /// In firmware mode AvellCC does nothing (the EC switches its own operational mode, as without AvellCC).
    /// </summary>
    public ControlStatus OnModeButton()
    {
        try
        {
            lock (_settingsLock)
            {
                var now = DateTime.UtcNow;
                if (now - _lastModeButton < ModeButtonDebounce) return Status;
                _lastModeButton = now;
                if (_settings.FanMode != "curve")
                {
                    _log.LogInformation("Mode button: firmware mode, ignored.");
                    return Status;
                }
                var next = Cycle.Next(_store.ListCurves().Select(c => c.Name).ToList(), _settings.CurveName);
                if (next is null) return Status;
                SetFanMode(new SetFanModeArgs("curve", next));
                _modeSwitchCurve = next;
                _modeSwitchCount++;
                _log.LogInformation("Mode button: curve {Curve}.", next);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Mode button: failed to switch the curve.");
        }
        return Status;
    }

    // ------------------------------------------------------------------ keyboard profiles

    private AppSettings SaveKeyboardProfile(KeyboardProfile profile)
    {
        lock (_settingsLock)
        {
            var next = _settings.WithKeyboardProfile(profile);
            _store.Save(next);
            _settings = next;
            _log.LogInformation("Keyboard profile '{Name}' saved.", profile.Name);
            return next;
        }
    }

    private AppSettings DeleteKeyboardProfile(NameArgs a)
    {
        lock (_settingsLock)
        {
            var next = _settings.WithoutKeyboardProfile(a.Name);
            _store.Save(next);
            _settings = next;
            return next;
        }
    }

    private AppSettings ApplyKeyboardProfile(NameArgs a)
    {
        lock (_settingsLock)
        {
            var profile = _settings.FindKeyboardProfile(a.Name) ?? throw new ArgumentException($"Keyboard profile '{a.Name}' not found.");
            return SetKeyboard(profile.Keyboard, profile.Name);
        }
    }

    /// <summary>UI commands (pipe).</summary>
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
            Protocol.Commands.SaveKeyboardProfile => SaveKeyboardProfile(Arg<KeyboardProfile>(args)),
            Protocol.Commands.DeleteKeyboardProfile => DeleteKeyboardProfile(Arg<NameArgs>(args)),
            Protocol.Commands.ApplyKeyboardProfile => ApplyKeyboardProfile(Arg<NameArgs>(args)),
            Protocol.Commands.PressModeButton when _options.Simulate => OnModeButton(),
            Protocol.Commands.FreeMemory => FreeMemory(),
            _ => throw new ArgumentException($"Unknown command: {command}"),
        };
        return Task.FromResult(result);
    }

    /// <summary>"Free up memory" button. Rate-limited: repeating it only makes apps fault their pages back in.</summary>
    private MemoryCleanResult FreeMemory()
    {
        lock (_memoryLock)
        {
            var since = DateTime.UtcNow - _lastMemoryClean;
            if (since < MemoryCooldown)
                throw new ArgumentException($"Memory was freed {since.TotalSeconds:0} s ago; wait {(MemoryCooldown - since).TotalSeconds:0} s.");
            var result = _memoryCleaner.FreeUpMemory();
            _lastMemoryClean = DateTime.UtcNow;
            _log.LogInformation("Free up memory ({Method}): in use {Before} MB → {After} MB.",
                                result.Method, result.InUseBefore >> 20, result.InUseAfter >> 20);
            return result;
        }
    }

    private object? SaveCurve(FanCurve curve)
    {
        _store.SaveCurve(curve);
        _log.LogInformation("Curve '{Name}' saved.", curve.Name);
        lock (_settingsLock)
        {
            if (_settings.FanMode == "curve" && _settings.CurveName == curve.Name) _loop.UseCurve(curve);   // editing the active curve
        }
        return null;
    }

    private AppSettings SetFanMode(SetFanModeArgs a)
    {
        lock (_settingsLock)
        {
            var next = _settings with { FanMode = a.Mode, CurveName = a.CurveName ?? _settings.CurveName };
            next.Validate();
            ApplyFanMode(next);          // validates the curve (throws before saving)
            _store.Save(next);
            _settings = next;
            _log.LogInformation("Fans: {Mode} {Curve}", next.FanMode, next.CurveName);
            return next;
        }
    }

    /// <summary>Applies keyboard settings; <paramref name="profile"/> = name when they come from a saved profile.</summary>
    private AppSettings SetKeyboard(KeyboardSettings keyboard, string? profile = null)
    {
        keyboard.Validate();
        lock (_settingsLock)
        {
            using (var kb = new FourZoneKeyboard(_keyboardTransport(), FourZoneKeyboard.DefaultProjectId))
                keyboard.ApplyTo(kb);
            var next = _settings with { Keyboard = keyboard, KeyboardProfile = profile };
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
            _log.LogError(ex, "Failed to apply keyboard settings.");
        }
    }

    private static T Arg<T>(JsonElement? args) =>
        args is { } a ? a.Deserialize<T>(Protocol.Json) ?? throw new ArgumentException("Empty arguments.") : throw new ArgumentException("Missing arguments.");

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

    public void Dispose()
    {
        _events?.Dispose();
        (_ec as IDisposable)?.Dispose();
    }

    /// <summary>Fake keyboard for simulation mode: only logs the packets.</summary>
    private sealed class LoggingTransport(ILogger log) : IFeatureTransport
    {
        public void SetFeature(byte[] report) => log.LogInformation("[sim] HID {Report}", Convert.ToHexString(report));
        public byte[] GetFeature(byte reportId, int length) => new byte[length];
        public void Dispose() { }
    }
}
