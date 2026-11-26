namespace AvellCC.Core.Fan;

/// <summary>
/// Relação duty → RPM medida neste notebook (ventoinha 1, controle direto, 2026-11-26, `fan curve run`).
/// Não é linear: ~68 RPM/% perto de 30 % e ~45 RPM/% perto de 100 %. Máximo ≈ 5540 RPM (fan2 ≈ 5590).
/// Usada só para estimativas na UI/CLI; o controle é sempre por duty.
/// </summary>
public static class FanCalibration
{
    private static readonly (int Duty, int Rpm)[] Points =
    [
        (25, 1700), // extrapolado (mínimo do controle direto)
        (31, 2043), (36, 2433), (44, 2894), (50, 3240), (60, 3648), (70, 4138),
        (76, 4515), (85, 4900), (91, 5133), (94, 5265), (97, 5390), (100, 5543),
    ];

    public static int EstimateRpm(double duty)
    {
        if (duty <= Points[0].Duty) return Points[0].Rpm;
        for (var i = 1; i < Points.Length; i++)
        {
            if (duty <= Points[i].Duty)
            {
                var (d0, r0) = Points[i - 1];
                var (d1, r1) = Points[i];
                return (int)Math.Round(r0 + (r1 - r0) * (duty - d0) / (d1 - d0));
            }
        }
        return Points[^1].Rpm;
    }
}
