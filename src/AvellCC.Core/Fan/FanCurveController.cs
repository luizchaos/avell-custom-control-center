namespace AvellCC.Core.Fan;

/// <summary>Safety and smoothing parameters of the controller.</summary>
public sealed record ControllerOptions
{
    /// <summary>Minimum duty with the fan spinning (tuxedo: 25 %). Fans are never stopped.</summary>
    public int MinDuty { get; init; } = DirectFanService.MinPercent;
    /// <summary>How fast the duty may drop per second (rising is immediate). Avoids audible up-and-down.</summary>
    public double MaxDownPerSecond { get; init; } = 2.0;
    public int CpuEmergency { get; init; } = 92;
    public int GpuEmergency { get; init; } = 87;
    /// <summary>The emergency only ends when the temperature drops this much below the limit.</summary>
    public int EmergencyRelease { get; init; } = 10;
    /// <summary>Rewrites the duty after this interval even without changes.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Decision of one cycle. Fan1/Fan2 in % (minimum, ramp and emergency already applied).</summary>
public readonly record struct FanDecision(int Fan1, int Fan2, double CpuEffective, double GpuEffective, bool Emergency, string? Reason, bool ShouldWrite);

/// <summary>
/// Software fan-curve controller (this laptop's EC only accepts a direct duty).
/// Pure logic: takes samples and returns the duty. The caller writes to the EC.
///
/// Hysteresis: the "effective" temperature follows the real one going up; going down, it only drops once the real
/// temperature is more than <c>curve.Hysteresis</c> °C below it. Ramp: rising is immediate; falling is limited to
/// <see cref="ControllerOptions.MaxDownPerSecond"/>. Emergency and invalid readings → 100 %.
/// </summary>
public sealed class FanCurveController(FanCurve curve, ControllerOptions? options = null)
{
    private readonly ControllerOptions _o = options ?? new ControllerOptions();
    private double? _cpuEff, _gpuEff;
    private double _fan1, _fan2;
    private DateTimeOffset? _lastTime, _lastWrite;
    private int _lastWritten1 = -1, _lastWritten2 = -1;
    private bool _emergency;

    public FanCurve Curve => curve;

    public FanDecision Step(FanSample s)
    {
        var dt = _lastTime is { } t ? Math.Max(0, (s.Time - t).TotalSeconds) : 0;
        _lastTime = s.Time;

        string? reason = null;
        var invalid = s.CpuTemp is 0 or 255 || s.GpuTemp is 255;
        if (invalid) reason = $"invalid reading (CPU {s.CpuTemp}, GPU {s.GpuTemp})";

        if (!invalid && (s.CpuTemp >= _o.CpuEmergency || s.GpuTemp >= _o.GpuEmergency))
        {
            _emergency = true;
        }
        else if (_emergency && s.CpuTemp < _o.CpuEmergency - _o.EmergencyRelease && s.GpuTemp < _o.GpuEmergency - _o.EmergencyRelease)
        {
            _emergency = false;
        }
        if (_emergency) reason ??= $"emergency (CPU {s.CpuTemp} °C / GPU {s.GpuTemp} °C)";

        double target1, target2;
        if (invalid || _emergency)
        {
            target1 = target2 = 100;
        }
        else
        {
            _cpuEff = Effective(_cpuEff, s.CpuTemp);
            _gpuEff = Effective(_gpuEff, s.GpuTemp);
            var cpu = FanCurve.Evaluate(curve.Cpu, _cpuEff.Value);
            var gpu = FanCurve.Evaluate(curve.Gpu, _gpuEff.Value);
            (target1, target2) = curve.Shared ? (Math.Max(cpu, gpu), Math.Max(cpu, gpu)) : (cpu, gpu);
        }

        target1 = Math.Clamp(target1, _o.MinDuty, 100);
        target2 = Math.Clamp(target2, _o.MinDuty, 100);
        var first = _lastWrite is null;
        _fan1 = first || reason is not null ? target1 : Ramp(_fan1, target1, dt);
        _fan2 = first || reason is not null ? target2 : Ramp(_fan2, target2, dt);

        var fan1 = (int)Math.Ceiling(_fan1);
        var fan2 = (int)Math.Ceiling(_fan2);
        var changed = fan1 != _lastWritten1 || fan2 != _lastWritten2;
        var keepAlive = _lastWrite is { } w && s.Time - w >= _o.KeepAlive;
        var shouldWrite = first || changed || keepAlive;
        if (shouldWrite) { _lastWrite = s.Time; _lastWritten1 = fan1; _lastWritten2 = fan2; }

        return new FanDecision(fan1, fan2, _cpuEff ?? s.CpuTemp, _gpuEff ?? s.GpuTemp, _emergency || invalid, reason, shouldWrite);
    }

    private double Effective(double? previous, int temp)
    {
        if (previous is not { } p || temp >= p) return temp;
        return temp < p - curve.Hysteresis ? temp + curve.Hysteresis : p;
    }

    private double Ramp(double current, double target, double dt) =>
        target >= current ? target : Math.Max(target, current - _o.MaxDownPerSecond * dt);
}
