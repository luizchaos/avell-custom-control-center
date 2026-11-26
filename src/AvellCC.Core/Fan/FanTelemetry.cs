using AvellCC.Core.Ec;

namespace AvellCC.Core.Fan;

/// <summary>
/// One reading of temperatures, fans and the firmware curve level, straight from the EC (≈ 8 WMI accesses, ~1 s).
/// Level / LevelTemp = 0x460 / 0x461 (fan 1's current firmware level and its threshold).
/// </summary>
public readonly record struct FanSample(DateTimeOffset Time, int CpuTemp, int GpuTemp, int Fan1, int Fan2, int Level = 0, int LevelTemp = 0)
{
    public static FanSample Read(IEcAccess ec) => new(
        DateTimeOffset.Now,
        ec.Read(EcAddresses.CpuTemp),
        ec.Read(EcAddresses.GpuTemp),
        ec.ReadWord(EcAddresses.Fan1High, EcAddresses.Fan1Low),
        ec.ReadWord(EcAddresses.Fan2High, EcAddresses.Fan2Low),
        ec.Read(EcAddresses.Fan1Level),
        ec.Read(EcAddresses.Fan1LevelTemp));
}

/// <summary>
/// Safety rules while trialling a curve from the CLI. Any violation triggers an automatic restore of the original.
/// </summary>
public sealed class FanWatchdog(int cpuLimit = 92, int gpuLimit = 87, int stalledTemp = 65, TimeSpan? stalledFor = null)
{
    private readonly TimeSpan _stalledFor = stalledFor ?? TimeSpan.FromSeconds(15);
    private DateTimeOffset? _stalledSince;

    public int CpuLimit => cpuLimit;
    public int GpuLimit => gpuLimit;

    /// <summary>Returns the reason to abort, or null if everything is within limits.</summary>
    public string? Check(FanSample s, double expectedCpuDuty, double expectedGpuDuty)
    {
        if (s.CpuTemp >= cpuLimit) return $"CPU at {s.CpuTemp} °C (limit {cpuLimit} °C).";
        if (s.GpuTemp >= gpuLimit) return $"GPU at {s.GpuTemp} °C (limit {gpuLimit} °C).";

        // Absurd reading (0 or 255) = sensor/EC in a strange state
        if (s.CpuTemp is 0 or 255 || s.GpuTemp is 255) return $"Invalid temperature reading (CPU {s.CpuTemp}, GPU {s.GpuTemp}).";

        // Fan stopped while hot and the curve asks for rotation
        var hot = s.CpuTemp >= stalledTemp || s.GpuTemp >= stalledTemp;
        var shouldSpin = expectedCpuDuty > 0 || expectedGpuDuty > 0;
        var stalled = hot && shouldSpin && (s.Fan1 == 0 || s.Fan2 == 0);
        if (!stalled) { _stalledSince = null; return null; }
        _stalledSince ??= s.Time;
        return s.Time - _stalledSince >= _stalledFor
            ? $"Fan stopped for {_stalledFor.TotalSeconds:0} s with CPU {s.CpuTemp} °C / GPU {s.GpuTemp} °C."
            : null;
    }
}
