using AvellCC.Core.Ec;
using AvellCC.Core.Fan;

namespace AvellCC.Core.Tests;

public sealed class FanCurveTests : IDisposable
{
    private static readonly string RepoCurve = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "curves", "safe-trial.json");

    private static FanCurve Curve(params (int T, int D)[] points) => new()
    {
        Name = "t",
        Hysteresis = 4,
        Cpu = points.Select(p => new CurvePoint(p.T, p.D)).ToArray(),
        Gpu = points.Select(p => new CurvePoint(p.T, p.D)).ToArray(),
    };

    private static readonly FanCurve Good = Curve((40, 30), (60, 50), (80, 80), (88, 100));

    [Fact]
    public void Repository_trial_curve_is_valid() => FanCurve.Load(RepoCurve);

    [Theory]
    [InlineData(new[] { 40, 60 }, new[] { 30, 20 }, "diminuir")]          // duty desce
    [InlineData(new[] { 40, 40 }, new[] { 30, 90 }, "crescentes")]         // temperatura repetida
    [InlineData(new[] { 40, 95 }, new[] { 30, 100 }, "até 90")]            // último ponto quente demais
    [InlineData(new[] { 40, 85 }, new[] { 30, 60 }, "pelo menos 80")]      // não ventila forte no fim
    [InlineData(new[] { 70, 80 }, new[] { 30, 100 }, "cobrir")]            // degraus < 1 °C
    [InlineData(new[] { 10, 80 }, new[] { 30, 100 }, "abaixo de 20")]
    public void Invalid_curves_are_rejected(int[] temps, int[] duties, string expected)
    {
        var curve = Curve(temps.Zip(duties).ToArray());
        var ex = Assert.Throws<FanCurveException>(curve.Validate);
        Assert.Contains(ex.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Staircase_is_never_below_the_curve()
    {
        var levels = FanTableEncoder.BuildLevels(Good.Cpu, Good.Hysteresis);
        for (var t = 20; t <= 110; t++)
            Assert.True(FanTableEncoder.ExpectedDuty(levels, t) >= FanCurve.Evaluate(Good.Cpu, t) - 1e-9, $"abaixo da curva em {t} °C");
    }

    [Fact]
    public void Staircase_structure_matches_ec_semantics()
    {
        var levels = FanTableEncoder.BuildLevels(Good.Cpu, Good.Hysteresis);
        Assert.Equal(16, levels.Length);
        Assert.Equal(0, levels[0].StartTemp);
        Assert.Equal(0xFF, levels[15].EndTemp);
        Assert.Equal(100, levels[15].DutyPercent);
        for (var k = 1; k < 16; k++)
        {
            Assert.True(levels[k].EndTemp > levels[k - 1].EndTemp, "end temps crescentes");
            Assert.Equal(levels[k - 1].EndTemp - Good.Hysteresis, levels[k].StartTemp);   // start = up - histerese
            Assert.True(levels[k].StartTemp < levels[k].EndTemp);
            Assert.True(levels[k].DutyPercent >= levels[k - 1].DutyPercent);
        }
    }

    [Fact]
    public void Encoded_table_places_blocks_at_ec_offsets()
    {
        var table = FanTableEncoder.Encode(Good);
        Assert.Equal(EcAddresses.FanTableLength, table.Length);
        var cpu = FanTableEncoder.Decode(table, 0x00);
        Assert.Equal(cpu[3].EndTemp, table[0x03]);                       // 0xF03
        Assert.Equal(cpu[3].StartTemp, table[0x13]);                     // 0xF13
        Assert.Equal((byte)(cpu[3].DutyPercent * 2), table[0x23]);       // 0xF23
        Assert.Equal(200, table[0x2F]);                                  // último nível CPU = 100 %
        Assert.Equal(200, table[0x5F]);                                  // último nível GPU = 100 %
    }

    // ---- aplicação no EC (FakeEc) ----

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avellcc-curve-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEc _ec = new();

    private FanTableService Service(Func<string[]>? vendor = null) =>
        new(_ec, new FanTableBackupStore(_dir), () => new MachineInfo(), vendor ?? (() => []));

    [Fact]
    public void Apply_curve_writes_table_before_enabling_user_table()
    {
        _ec.Seed(EcAddresses.ApOemByte3, [0x04]);   // quiet ligado
        var table = Service().ApplyCurve(Good);

        Assert.Equal(table, _ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength));
        Assert.Equal(0x08, _ec.Read(EcAddresses.ApOemByte3));           // bit 3 ligado, bit 2 desligado
        Assert.Equal(0xA0, _ec.Read(EcAddresses.FanControl));

        var lastTableWrite = _ec.Writes.FindLastIndex(w => w.Address is >= EcAddresses.FanTableStart and < EcAddresses.FanTableStart + EcAddresses.FanTableLength);
        var enableWrite = _ec.Writes.FindIndex(w => w.Address == EcAddresses.ApOemByte3);
        Assert.True(enableWrite > lastTableWrite, "a tabela do usuário só pode ser ligada depois da tabela gravada");
    }

    [Fact]
    public void Restore_after_curve_returns_to_firmware_curve_and_disables_user_table_first()
    {
        var service = Service();
        service.EnsureOriginalBackup();                 // original: tudo zero
        service.ApplyCurve(Good);
        _ec.Writes.Clear();

        service.RestoreOriginal();

        Assert.All(_ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength), b => Assert.Equal(0, b));
        Assert.False(service.IsUserTableActive());
        var disable = _ec.Writes.FindIndex(w => w.Address == EcAddresses.ApOemByte3);
        var firstTableWrite = _ec.Writes.FindIndex(w => w.Address >= EcAddresses.FanTableStart);
        Assert.True(disable < firstTableWrite, "desligar a tabela do usuário antes de zerá-la");
    }

    private sealed class FlakyEc(FakeEc inner, ushort failAt) : IEcAccess
    {
        public bool Armed = true;
        public byte Read(ushort a) => inner.Read(a);
        public void Write(ushort a, byte v)
        {
            if (Armed && a == failAt) { Armed = false; inner.Write(a, (byte)(v ^ 0xFF)); return; }  // grava errado uma vez
            inner.Write(a, v);
        }
    }

    [Fact]
    public void Readback_mismatch_rolls_back_to_previous_state()
    {
        var flaky = new FlakyEc(_ec, (ushort)(EcAddresses.FanTableStart + 5));
        var service = new FanTableService(flaky, new FanTableBackupStore(_dir), () => new MachineInfo(), () => []);

        Assert.Throws<InvalidOperationException>(() => service.ApplyCurve(Good));

        Assert.All(_ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength), b => Assert.Equal(0, b));
        Assert.Equal(0, _ec.Read(EcAddresses.ApOemByte3));
        Assert.Equal(0, _ec.Read(EcAddresses.FanControl));
    }

    // ---- watchdog ----

    private static FanSample S(int cpu, int gpu, int f1, int f2, int sec = 0) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(sec), cpu, gpu, f1, f2);

    [Fact]
    public void Watchdog_aborts_on_overtemperature() =>
        Assert.NotNull(new FanWatchdog(cpuLimit: 92).Check(S(92, 60, 4000, 4000), 50, 50));

    [Fact]
    public void Watchdog_aborts_only_after_fan_is_stalled_for_the_grace_period()
    {
        var w = new FanWatchdog(stalledFor: TimeSpan.FromSeconds(15));
        Assert.Null(w.Check(S(70, 60, 0, 4000, 0), 50, 50));
        Assert.Null(w.Check(S(70, 60, 0, 4000, 10), 50, 50));
        Assert.NotNull(w.Check(S(70, 60, 0, 4000, 16), 50, 50));
    }

    [Fact]
    public void Watchdog_resets_stall_timer_when_fan_spins_again()
    {
        var w = new FanWatchdog(stalledFor: TimeSpan.FromSeconds(15));
        Assert.Null(w.Check(S(70, 60, 0, 4000, 0), 50, 50));
        Assert.Null(w.Check(S(70, 60, 3000, 4000, 10), 50, 50));
        Assert.Null(w.Check(S(70, 60, 0, 4000, 20), 50, 50));
    }

    [Fact]
    public void Watchdog_ignores_stopped_fans_when_cool() =>
        Assert.Null(new FanWatchdog(stalledFor: TimeSpan.Zero).Check(S(45, 40, 0, 0), 30, 30));

    public void Dispose()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var f in Directory.GetFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }
}
