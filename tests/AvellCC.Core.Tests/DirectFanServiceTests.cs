using AvellCC.Core.Ec;
using AvellCC.Core.Fan;
using AvellCC.Core.Safety;

namespace AvellCC.Core.Tests;

public sealed class DirectFanServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avellcc-direct-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEc _ec = new();
    private EcRegisterBackupStore Store => new(_dir, DirectFanService.BackupGroup);

    public DirectFanServiceTests() => _ec.Seed(EcAddresses.FanCtrlStatus, [0x0C]);   // valor real: sem controle universal

    private DirectFanService Service(Func<string[]>? vendor = null) =>
        new(_ec, Store, () => new MachineInfo(), vendor ?? (() => []));

    [Theory]
    [InlineData(25, 50)]
    [InlineData(70, 140)]
    [InlineData(100, 200)]
    public void Percent_maps_to_ec_scale(int percent, byte raw) => Assert.Equal(raw, DirectFanService.ToRaw(percent));

    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(101)]
    public void Never_writes_stop_or_out_of_range_values(int percent) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectFanService.ToRaw(percent));

    [Fact]
    public void Enable_writes_duty_before_full_fan_mode_and_backs_up_first()
    {
        Service().Enable(70, 60);

        Assert.True(Store.HasOriginal);
        Assert.Equal(0x00, Store.LoadOriginal().Values()[EcAddresses.FanControl]);
        Assert.Equal(EcAddresses.FullFanModeBit, _ec.Read(EcAddresses.FanControl));
        Assert.Equal(140, _ec.Read(EcAddresses.DirectFan1Duty));
        Assert.Equal(120, _ec.Read(EcAddresses.DirectFan2Duty));

        var firstDuty = _ec.Writes.FindIndex(w => w.Address == EcAddresses.DirectFan1Duty);
        var mode = _ec.Writes.FindIndex(w => w.Address == EcAddresses.FanControl);
        Assert.True(firstDuty < mode, "duty antes de ligar o full fan mode");
        Assert.True(_ec.Writes.FindLastIndex(w => w.Address == EcAddresses.DirectFan1Duty) > mode, "duty regravado depois");
    }

    [Fact]
    public void Fan_boost_only_sets_bit6_without_duty_and_restores()
    {
        var service = Service();
        service.EnableFanBoostOnly();
        Assert.Equal(EcAddresses.FullFanModeBit, _ec.Read(EcAddresses.FanControl));
        Assert.DoesNotContain(_ec.Writes, w => w.Address is EcAddresses.DirectFan1Duty or EcAddresses.DirectFan2Duty);
        service.Disable();
        Assert.Equal(0x00, _ec.Read(EcAddresses.FanControl));
    }

    [Fact]
    public void Max_preset_is_full_speed_everywhere()
    {
        var c = new FanCurveController(CurvePresets.Max);
        Assert.Equal(100, c.Step(new FanSample(DateTimeOffset.Now, 30, 30, 0, 0)).Fan1);
    }

    [Fact]
    public void Disable_returns_0x751_to_original()
    {
        var service = Service();
        service.Enable(70, 70);
        service.Disable();
        Assert.Equal(0x00, _ec.Read(EcAddresses.FanControl));
        Assert.False(service.Active);
    }

    [Fact]
    public void Refuses_when_vendor_is_running_or_full_fan_already_on()
    {
        Assert.Throws<VendorRunningException>(() => Service(() => ["GCUBridge"]).Enable(50, 50));
        Assert.DoesNotContain(_ec.Writes, w => w.Address == EcAddresses.FanControl);

        _ec.Seed(EcAddresses.FanControl, [0x40]);
        Assert.Throws<InvalidOperationException>(() => Service().Enable(50, 50));
    }

    [Fact]
    public void Office_custom_profile_bit_is_set_and_restored()
    {
        var store = new EcRegisterBackupStore(_dir, OfficeFanService.BackupGroup);
        var office = new OfficeFanService(_ec, store, () => new MachineInfo(), () => []);
        _ec.Seed(EcAddresses.OfficeAdvPwmStart, [0x3C, 0x46, 0x50, 0x64, 0x78]);

        office.Apply(new OfficeFanCurve { Name = "cp", Duties = [70, 75, 80, 90, 100], CustomProfile = true });
        Assert.Equal(EcAddresses.CustomProfileBit, _ec.Read(EcAddresses.CustomProfile));

        office.RestoreOriginal();
        Assert.Equal(0x00, _ec.Read(EcAddresses.CustomProfile));
        Assert.Equal(0x00, _ec.Read(EcAddresses.FanControl));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var f in Directory.GetFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }
}
