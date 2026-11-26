using AvellCC.Core.Ec;

namespace AvellCC.Core.Fan;

/// <summary>
/// Uma leitura de temperatura, ventoinhas e nível da curva do firmware direto do EC (≈ 8 acessos WMI, ~1 s).
/// Level / LevelTemp = 0x460 / 0x461 (nível atual da ventoinha 1 e seu limiar).
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
/// Regras de segurança durante o teste de uma curva. Qualquer violação leva à restauração automática do original.
/// </summary>
public sealed class FanWatchdog(int cpuLimit = 92, int gpuLimit = 87, int stalledTemp = 65, TimeSpan? stalledFor = null)
{
    private readonly TimeSpan _stalledFor = stalledFor ?? TimeSpan.FromSeconds(15);
    private DateTimeOffset? _stalledSince;

    public int CpuLimit => cpuLimit;
    public int GpuLimit => gpuLimit;

    /// <summary>Retorna o motivo para abortar, ou null se está tudo dentro dos limites.</summary>
    public string? Check(FanSample s, double expectedCpuDuty, double expectedGpuDuty)
    {
        if (s.CpuTemp >= cpuLimit) return $"CPU em {s.CpuTemp} °C (limite {cpuLimit} °C).";
        if (s.GpuTemp >= gpuLimit) return $"GPU em {s.GpuTemp} °C (limite {gpuLimit} °C).";

        // Leitura absurda (0 ou 255) = sensor/EC em estado estranho
        if (s.CpuTemp is 0 or 255 || s.GpuTemp is 255) return $"Leitura de temperatura inválida (CPU {s.CpuTemp}, GPU {s.GpuTemp}).";

        // Ventoinha parada com calor e com a curva pedindo rotação
        var hot = s.CpuTemp >= stalledTemp || s.GpuTemp >= stalledTemp;
        var shouldSpin = expectedCpuDuty > 0 || expectedGpuDuty > 0;
        var stalled = hot && shouldSpin && (s.Fan1 == 0 || s.Fan2 == 0);
        if (!stalled) { _stalledSince = null; return null; }
        _stalledSince ??= s.Time;
        return s.Time - _stalledSince >= _stalledFor
            ? $"Ventoinha parada por {_stalledFor.TotalSeconds:0} s com CPU {s.CpuTemp} °C / GPU {s.GpuTemp} °C."
            : null;
    }
}
