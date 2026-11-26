using AvellCC.Core.Control;
using AvellCC.Core.Ec;
using AvellCC.Core.Fan;

namespace AvellCC.Core.Tests;

public sealed class FanControlLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avellcc-loop-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEc _ec = new();
    private string[] _vendor = [];
    private readonly DirectFanService _direct;
    private readonly FanControlLoop _loop;

    public FanControlLoopTests()
    {
        _direct = new DirectFanService(_ec, new EcRegisterBackupStore(_dir, DirectFanService.BackupGroup), () => new MachineInfo(), () => _vendor);
        _loop = new FanControlLoop(_ec, _direct, () => _vendor);
        SetTemps(60, 50);
    }

    private void SetTemps(byte cpu, byte gpu)
    {
        _ec.Seed(EcAddresses.CpuTemp, [cpu]);
        _ec.Seed(EcAddresses.GpuTemp, [gpu]);
    }

    private bool FullFan => (_ec.Read(EcAddresses.FanControl) & EcAddresses.FullFanModeBit) != 0;

    [Fact]
    public void Firmware_mode_never_enables_direct_control()
    {
        var s = _loop.Tick();
        Assert.Equal(LoopState.Firmware, s.State);
        Assert.False(FullFan);
    }

    [Fact]
    public void Curve_mode_enables_direct_control_and_follows_the_curve()
    {
        _loop.UseCurve(CurvePresets.Balanced);
        var s = _loop.Tick();
        Assert.Equal(LoopState.Curve, s.State);
        Assert.True(FullFan);
        Assert.True(s.DirectControl);
        Assert.Equal(78, _ec.Read(EcAddresses.DirectFan1Duty));   // balanced @ 60 °C ≈ 38,5 % → 39 % → 78
    }

    [Fact]
    public void Switching_back_to_firmware_releases_control()
    {
        _loop.UseCurve(CurvePresets.Balanced);
        _loop.Tick();
        _loop.UseFirmware();
        Assert.False(FullFan);
        Assert.Equal(LoopState.Firmware, _loop.Tick().State);
    }

    [Fact]
    public void Leftover_direct_control_from_a_crash_is_released_in_firmware_mode()
    {
        _ec.Seed(EcAddresses.FanControl, [EcAddresses.FullFanModeBit]);   // serviço anterior morreu com o bit ligado
        _loop.Tick();
        Assert.False(FullFan);
    }

    [Fact]
    public void Leftover_direct_control_is_taken_over_in_curve_mode()
    {
        _ec.Seed(EcAddresses.FanControl, [EcAddresses.FullFanModeBit]);
        _loop.UseCurve(CurvePresets.Balanced);
        Assert.Equal(LoopState.Curve, _loop.Tick().State);
        Assert.True(FullFan);
    }

    [Fact]
    public void Lost_full_fan_bit_is_reacquired()
    {
        _loop.UseCurve(CurvePresets.Balanced);
        _loop.Tick();
        _ec.Seed(EcAddresses.FanControl, [0x00]);     // tecla Fn / BIOS desligou
        var s = _loop.Tick();
        Assert.True(FullFan);
        Assert.Contains("reassumido", s.Message);
    }

    [Fact]
    public void Vendor_running_releases_control_and_blocks()
    {
        _loop.UseCurve(CurvePresets.Balanced);
        _loop.Tick();
        _vendor = ["GCUService"];
        var s = _loop.Tick();
        Assert.Equal(LoopState.BlockedByVendor, s.State);
        Assert.False(FullFan);
    }

    [Fact]
    public void Suspend_releases_and_resume_reacquires()
    {
        _loop.UseCurve(CurvePresets.Balanced);
        _loop.Tick();
        _loop.Suspend();
        Assert.False(FullFan);
        Assert.Equal(LoopState.Suspended, _loop.Tick().State);
        _loop.Resume();
        Assert.Equal(LoopState.Curve, _loop.Tick().State);
        Assert.True(FullFan);
    }

    [Fact]
    public void Emergency_is_reported_and_runs_fans_at_full_speed()
    {
        _loop.UseCurve(CurvePresets.Balanced);
        SetTemps(95, 50);
        var s = _loop.Tick();
        Assert.Equal(LoopState.Emergency, s.State);
        Assert.Equal(200, _ec.Read(EcAddresses.DirectFan1Duty));
    }

    private sealed class ThrowingEc(FakeEc inner) : IEcAccess
    {
        public bool Fail;
        public byte Read(ushort a) => Fail && a == EcAddresses.CpuTemp ? throw new IOException("WMI falhou") : inner.Read(a);
        public void Write(ushort a, byte v) => inner.Write(a, v);
    }

    [Fact]
    public void Errors_release_control_to_firmware()
    {
        var ec = new ThrowingEc(_ec);
        var direct = new DirectFanService(ec, new EcRegisterBackupStore(_dir, DirectFanService.BackupGroup), () => new MachineInfo(), () => []);
        var loop = new FanControlLoop(ec, direct, () => []);
        loop.UseCurve(CurvePresets.Balanced);
        loop.Tick();
        Assert.True(FullFan);

        ec.Fail = true;
        var s = loop.Tick();
        Assert.Equal(LoopState.Error, s.State);
        Assert.False(FullFan);
    }

    [Fact]
    public void Shutdown_releases_control()
    {
        _loop.UseCurve(CurvePresets.Balanced);
        _loop.Tick();
        _loop.Shutdown();
        Assert.False(FullFan);
    }

    [Fact]
    public void Presets_are_valid() => Assert.All(CurvePresets.All, c => c.Validate());

    [Fact]
    public void Settings_store_round_trips_and_never_overwrites_edited_presets()
    {
        var store = new SettingsStore(_dir);
        store.EnsurePresets();
        Assert.Equal(3, store.ListCurves().Count);

        var edited = CurvePresets.Balanced with { Hysteresis = 6 };
        store.SaveCurve(edited);
        store.EnsurePresets();
        Assert.Equal(6, store.LoadCurve("balanced").Hysteresis);

        store.Save(new AppSettings { FanMode = "curve", CurveName = "silent", Keyboard = new() { Mode = "zones", Colors = ["FF0000", "00FF00", "0000FF", "FFFFFF"] } });
        var loaded = store.Load();
        Assert.Equal("curve", loaded.FanMode);
        Assert.Equal("zones", loaded.Keyboard!.Mode);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a b")]
    [InlineData("")]
    public void Curve_names_cannot_escape_the_curves_folder(string name) =>
        Assert.Throws<ArgumentException>(() => SettingsStore.ValidateName(name));

    public void Dispose()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }
}
