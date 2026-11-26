namespace AvellCC.Core.Fan;

/// <summary>Curvas que acompanham o app (gravadas em %ProgramData%\AvellCC\curves na primeira execução do serviço).</summary>
public static class CurvePresets
{
    private static CurvePoint[] P(params (int T, int D)[] points) => points.Select(p => new CurvePoint(p.T, p.D)).ToArray();

    /// <summary>Validada no hardware em 2026-11-26 (teste 7).</summary>
    public static readonly FanCurve Balanced = new()
    {
        Name = "balanced", Hysteresis = 4, Shared = true,
        Cpu = P((45, 25), (55, 32), (65, 45), (75, 70), (85, 100)),
        Gpu = P((45, 25), (55, 32), (65, 45), (72, 70), (82, 100)),
    };

    /// <summary>Mais silenciosa: espera mais para acelerar. Ainda chega a 100 % antes de 90 °C.</summary>
    public static readonly FanCurve Silent = new()
    {
        Name = "silent", Hysteresis = 5, Shared = true,
        Cpu = P((50, 25), (62, 30), (72, 45), (80, 75), (88, 100)),
        Gpu = P((50, 25), (62, 30), (70, 45), (78, 75), (85, 100)),
    };

    /// <summary>Agressiva: prioriza temperatura e desempenho sustentado.</summary>
    public static readonly FanCurve Performance = new()
    {
        Name = "performance", Hysteresis = 3, Shared = true,
        Cpu = P((40, 30), (50, 40), (60, 55), (70, 75), (80, 100)),
        Gpu = P((40, 30), (50, 40), (60, 55), (68, 75), (78, 100)),
    };

    /// <summary>
    /// 100 % sempre (duty 200, o máximo que o EC aceita; ≈ 5543/5590 RPM medidos). Equivale ao "Fan Boost" do vendor,
    /// a confirmar com `avellcc fan boost-test`.
    /// </summary>
    public static readonly FanCurve Max = new()
    {
        Name = "max", Hysteresis = 2, Shared = true,
        Cpu = P((20, 100), (40, 100)),
        Gpu = P((20, 100), (40, 100)),
    };

    public static IReadOnlyList<FanCurve> All { get; } = [Balanced, Silent, Performance, Max];
}
