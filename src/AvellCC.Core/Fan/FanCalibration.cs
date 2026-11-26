namespace AvellCC.Core.Fan;

/// <summary>
/// Duty → RPM relation measured on this laptop (fan 1, direct control, 2026-11-26, `fan curve run`).
/// Not linear: ~68 RPM/% near 30 % and ~45 RPM/% near 100 %. Maximum ≈ 5540 RPM (fan 2 ≈ 5590).
/// Only used for estimates in the UI/CLI; control is always by duty.
/// </summary>
public static class FanCalibration
{
    private static readonly (int Duty, int Rpm)[] Points =
    [
        (25, 1700), // extrapolated (direct-control minimum)
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
