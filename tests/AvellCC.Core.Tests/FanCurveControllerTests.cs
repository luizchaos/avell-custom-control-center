using AvellCC.Core.Fan;

namespace AvellCC.Core.Tests;

public class FanCurveControllerTests
{
    private static readonly FanCurve Curve = new()
    {
        Name = "t", Hysteresis = 4, Shared = false,
        Cpu = [new(40, 25), new(60, 50), new(80, 90), new(85, 100)],
        Gpu = [new(40, 25), new(60, 40), new(80, 80), new(85, 100)],
    };

    private static FanSample S(int sec, int cpu, int gpu) => new(DateTimeOffset.UnixEpoch.AddSeconds(sec), cpu, gpu, 3000, 3000);

    [Fact]
    public void Maps_temperature_through_the_curve_per_fan()
    {
        var d = new FanCurveController(Curve).Step(S(0, 60, 60));
        Assert.Equal(50, d.Fan1);
        Assert.Equal(40, d.Fan2);
        Assert.True(d.ShouldWrite);
    }

    [Fact]
    public void Shared_mode_uses_the_hotter_curve_for_both_fans()
    {
        var d = new FanCurveController(Curve with { Shared = true }).Step(S(0, 45, 80));
        Assert.Equal(80, d.Fan1);
        Assert.Equal(80, d.Fan2);
    }

    [Fact]
    public void Never_goes_below_minimum_duty() =>
        Assert.Equal(25, new FanCurveController(Curve with { Cpu = [new(50, 0), new(70, 90)] }).Step(S(0, 30, 30)).Fan1);

    [Fact]
    public void Ramps_up_immediately()
    {
        var c = new FanCurveController(Curve);
        c.Step(S(0, 40, 40));
        Assert.Equal(90, c.Step(S(1, 80, 40)).Fan1);
    }

    [Fact]
    public void Ramps_down_slowly()
    {
        var c = new FanCurveController(Curve, new ControllerOptions { MaxDownPerSecond = 2 });
        c.Step(S(0, 80, 40));                       // 90 %
        var d = c.Step(S(5, 40, 40));               // queda de 40 °C, mas só -10 % em 5 s
        Assert.Equal(80, d.Fan1);
    }

    [Fact]
    public void Hysteresis_ignores_small_drops()
    {
        var c = new FanCurveController(Curve, new ControllerOptions { MaxDownPerSecond = 100 });
        c.Step(S(0, 60, 40));
        var small = c.Step(S(1, 57, 40));           // caiu 3 °C (< 4): continua efetiva 60
        Assert.Equal(60, small.CpuEffective);
        var big = c.Step(S(2, 50, 40));             // caiu 10 °C: efetiva = 50 + 4
        Assert.Equal(54, big.CpuEffective);
    }

    [Fact]
    public void Emergency_forces_full_speed_until_temperature_drops_by_release_margin()
    {
        var c = new FanCurveController(Curve, new ControllerOptions { CpuEmergency = 92, EmergencyRelease = 10 });
        var hot = c.Step(S(0, 92, 50));
        Assert.True(hot.Emergency);
        Assert.Equal(100, hot.Fan1);
        Assert.Equal(100, hot.Fan2);

        Assert.True(c.Step(S(1, 85, 50)).Emergency);   // ainda acima de 82
        var cool = c.Step(S(2, 81, 50));
        Assert.False(cool.Emergency);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(255, 50)]
    [InlineData(50, 255)]
    public void Invalid_sensor_forces_full_speed(int cpu, int gpu)
    {
        var d = new FanCurveController(Curve).Step(S(0, cpu, gpu));
        Assert.Equal(100, d.Fan1);
        Assert.NotNull(d.Reason);
    }

    [Fact]
    public void Writes_only_on_change_or_keep_alive()
    {
        var c = new FanCurveController(Curve, new ControllerOptions { KeepAlive = TimeSpan.FromSeconds(30) });
        Assert.True(c.Step(S(0, 60, 60)).ShouldWrite);
        Assert.False(c.Step(S(1, 60, 60)).ShouldWrite);
        Assert.False(c.Step(S(29, 60, 60)).ShouldWrite);
        Assert.True(c.Step(S(31, 60, 60)).ShouldWrite);
        Assert.True(c.Step(S(32, 70, 60)).ShouldWrite);
    }

    [Fact]
    public void Repository_balanced_curve_is_valid_and_reasonable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "curves", "balanced.json");
        var curve = FanCurve.Load(path);
        var c = new FanCurveController(curve);
        Assert.Equal(25, c.Step(S(0, 45, 45)).Fan1);          // ocioso: mínimo
        Assert.Equal(100, new FanCurveController(curve).Step(S(0, 85, 60)).Fan1);
    }
}
