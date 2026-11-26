namespace AvellCC.Core.Fan;

/// <summary>Parâmetros de segurança e suavização do controlador.</summary>
public sealed record ControllerOptions
{
    /// <summary>Duty mínimo com a ventoinha girando (tuxedo: 25 %). Nunca paramos as ventoinhas.</summary>
    public int MinDuty { get; init; } = DirectFanService.MinPercent;
    /// <summary>Quanto o duty pode cair por segundo (subir é imediato). Evita o "sobe e desce" audível.</summary>
    public double MaxDownPerSecond { get; init; } = 2.0;
    public int CpuEmergency { get; init; } = 92;
    public int GpuEmergency { get; init; } = 87;
    /// <summary>A emergência só termina quando a temperatura cai este tanto abaixo do limite.</summary>
    public int EmergencyRelease { get; init; } = 10;
    /// <summary>Regrava o duty mesmo sem mudança depois deste intervalo.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Decisão de um ciclo. Fan1/Fan2 em % (já com mínimo, rampa e emergência aplicados).</summary>
public readonly record struct FanDecision(int Fan1, int Fan2, double CpuEffective, double GpuEffective, bool Emergency, string? Reason, bool ShouldWrite);

/// <summary>
/// Controlador de curva aplicado pelo software (o EC deste notebook só aceita duty direto).
/// Lógica pura: recebe amostras e devolve o duty. Quem grava no EC é o chamador.
///
/// Histerese: a temperatura "efetiva" segue a real na subida; na descida, só cai quando a real fica
/// mais de <c>curve.Hysteresis</c> °C abaixo dela. Rampa: subir é imediato; descer é limitado a
/// <see cref="ControllerOptions.MaxDownPerSecond"/>. Emergência e leitura inválida → 100 %.
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
        if (invalid) reason = $"leitura inválida (CPU {s.CpuTemp}, GPU {s.GpuTemp})";

        if (!invalid && (s.CpuTemp >= _o.CpuEmergency || s.GpuTemp >= _o.GpuEmergency))
        {
            _emergency = true;
        }
        else if (_emergency && s.CpuTemp < _o.CpuEmergency - _o.EmergencyRelease && s.GpuTemp < _o.GpuEmergency - _o.EmergencyRelease)
        {
            _emergency = false;
        }
        if (_emergency) reason ??= $"emergência (CPU {s.CpuTemp} °C / GPU {s.GpuTemp} °C)";

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
