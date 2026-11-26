using AvellCC.Core.Ec;
using AvellCC.Core.Fan;
using AvellCC.Core.Safety;

namespace AvellCC.Core.Tests;

public sealed class OfficeFanServiceTests : IDisposable
{
    private static readonly string CurvesDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "curves");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avellcc-office-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEc _ec = new();
    private readonly EcRegisterBackupStore _store;

    private static readonly OfficeFanCurve Strong = new() { Name = "strong", Duties = [40, 50, 60, 80, 100] };

    public OfficeFanServiceTests()
    {
        _store = new EcRegisterBackupStore(_dir);
        // Estado lido do notebook em 2026-11-26
        _ec.Seed(EcAddresses.OfficeAdvPwmStart, [0x3C, 0x46, 0x50, 0x64, 0x78]);
        _ec.Seed(EcAddresses.OfficeAdvPwmDefaultStart, [0x3C, 0x46, 0x50, 0x64, 0x78]);
        _ec.Seed(EcAddresses.BiosOemByte2, [0x5D]);
    }

    private OfficeFanService Service(Func<string[]>? vendor = null) =>
        new(_ec, _store, () => new MachineInfo { Model = "A65 LIV" }, vendor ?? (() => []));

    [Theory]
    [InlineData("office-probe.json")]
    [InlineData("office-performance.json")]
    public void Repository_curves_are_valid(string file) => OfficeFanCurve.Load(Path.Combine(CurvesDir, file));

    [Theory]
    [InlineData(new[] { 30, 35, 40, 50 }, "exactly 5")]
    [InlineData(new[] { 30, 35, 40, 50, 110 }, "outside 0–100")]
    [InlineData(new[] { 30, 50, 40, 60, 80 }, "must not decrease")]
    [InlineData(new[] { 30, 35, 40, 50, 55 }, "at least 60")]
    public void Invalid_curves_are_rejected(int[] duties, string expected)
    {
        var ex = Assert.Throws<FanCurveException>(new OfficeFanCurve { Duties = duties }.Validate);
        Assert.Contains(ex.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Raw_is_percent_times_two_like_vendor() =>
        Assert.Equal(new byte[] { 0x3C, 0x46, 0x50, 0x64, 0x78 }, new OfficeFanCurve { Duties = [30, 35, 40, 50, 60] }.ToRaw());

    [Fact]
    public void Reads_current_state()
    {
        var s = Service().ReadState();
        Assert.Equal([30, 35, 40, 50, 60], s.Duties);
        Assert.True(s.SupportsMyFan3);
        Assert.False(s.OfficeAdvancedActive);
    }

    [Fact]
    public void Apply_backs_up_then_writes_speeds_before_enabling_mode()
    {
        Service().Apply(Strong);

        var original = _store.LoadOriginal().Values();
        Assert.Equal(0x3C, original[EcAddresses.OfficeAdvPwmStart]);               // original salvo ANTES de gravar
        Assert.Equal(0x00, original[EcAddresses.FanControl]);
        Assert.True(File.GetAttributes(_store.OriginalPath).HasFlag(FileAttributes.ReadOnly));

        Assert.Equal(new byte[] { 80, 100, 120, 160, 200 }, _ec.ReadRange(EcAddresses.OfficeAdvPwmStart, 5));
        Assert.Equal(OfficeFanService.OfficeAdvancedMode, _ec.Read(EcAddresses.FanControl));

        var modeWrite = _ec.Writes.FindIndex(w => w.Address == EcAddresses.FanControl);
        var lastPwmWrite = _ec.Writes.FindLastIndex(w => w.Address is >= EcAddresses.OfficeAdvPwmStart and < EcAddresses.OfficeAdvPwmStart + 5);
        Assert.True(modeWrite > lastPwmWrite, "o modo só pode ser ligado depois das velocidades");
    }

    [Fact]
    public void Restore_leaves_office_mode_first_and_restores_exact_values()
    {
        var service = Service();
        service.Apply(Strong);
        _ec.Writes.Clear();

        service.RestoreOriginal();

        Assert.Equal(0x00, _ec.Read(EcAddresses.FanControl));
        Assert.Equal(new byte[] { 0x3C, 0x46, 0x50, 0x64, 0x78 }, _ec.ReadRange(EcAddresses.OfficeAdvPwmStart, 5));
        Assert.Equal(EcAddresses.FanControl, _ec.Writes[0].Address);
    }

    [Fact]
    public void Original_backup_is_never_overwritten()
    {
        var service = Service();
        service.Apply(Strong);                                   // cria o original com 30..60 %
        service.Apply(Strong with { Duties = [50, 60, 70, 80, 100] });

        Assert.Equal(0x3C, _store.LoadOriginal().Values()[EcAddresses.OfficeAdvPwmStart]);
    }

    [Fact]
    public void Refuses_to_write_while_vendor_is_running()
    {
        Assert.Throws<VendorRunningException>(() => Service(() => ["GCUService"]).Apply(Strong));
        Assert.Empty(_ec.Writes);
    }

    private sealed class FailingModeEc(FakeEc inner) : IEcAccess
    {
        public byte Read(ushort a) => inner.Read(a);
        public void Write(ushort a, byte v) => inner.Write(a, a == EcAddresses.FanControl && v == OfficeFanService.OfficeAdvancedMode ? (byte)0x55 : v);
    }

    [Fact]
    public void Failed_mode_write_rolls_back_speeds_and_mode()
    {
        var service = new OfficeFanService(new FailingModeEc(_ec), _store, () => new MachineInfo(), () => []);

        Assert.Throws<InvalidOperationException>(() => service.Apply(Strong));

        Assert.Equal(0x00, _ec.Read(EcAddresses.FanControl));
        Assert.Equal(new byte[] { 0x3C, 0x46, 0x50, 0x64, 0x78 }, _ec.ReadRange(EcAddresses.OfficeAdvPwmStart, 5));
    }

    [Fact]
    public void Tampered_register_backup_is_rejected()
    {
        Service().EnsureOriginalBackup();
        File.SetAttributes(_store.OriginalPath, FileAttributes.Normal);
        File.WriteAllText(_store.OriginalPath, File.ReadAllText(_store.OriginalPath).Replace("\"0x3C\"", "\"0x00\""));
        Assert.Throws<InvalidDataException>(() => _store.LoadOriginal());
    }

    public void Dispose()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var f in Directory.GetFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }
}
