using AvellCC.Core.Ec;

namespace AvellCC.Core.Fan;

/// <summary>
/// Converts a <see cref="FanCurve"/> into the EC's 16-level table (0xF00–0xF5F). This EC ignores the table
/// (docs/HARDWARE.md); the encoder is kept for the experiments and for other Uniwill models with "universal" fan control.
///
/// EC semantics per level i (same as the vendor's FanTable_Manager.SetCpuFanTable):
///   EC[up   + i] = "end temp":   above it, go up to level i+1 (0xFF on the last level)
///   EC[down + i] = "start temp": below it, go down to level i-1 (0 on level 0)
///   EC[duty + i] = duty × 2 (0..200 = 0..100 %)
///
/// Staircase built from the curve (points T_first..T_last):
///   level k (k ≥ 1) starts at up_k = T_first + k·(T_last − T_first)/15 and level 15 starts at T_last;
///   duty_k = curve(up_{k+1}), i.e. the HIGHEST curve value within the step, since the curve never decreases;
///   start_k = up_k − hysteresis.
/// </summary>
public static class FanTableEncoder
{
    public const int Levels = EcAddresses.FanLevels;

    public static byte[] Encode(FanCurve curve)
    {
        curve.Validate();
        var table = new byte[EcAddresses.FanTableLength];
        Write(table, 0x00, BuildLevels(curve.Cpu, curve.Hysteresis));
        Write(table, 0x30, BuildLevels(curve.Gpu, curve.Hysteresis));
        return table;
    }

    /// <summary>Decoded levels of one half (CPU = 0x00, GPU = 0x30) of a raw table.</summary>
    public static EncodedLevel[] Decode(byte[] table, int blockOffset)
    {
        var levels = new EncodedLevel[Levels];
        for (var i = 0; i < Levels; i++)
            levels[i] = new EncodedLevel(i, table[blockOffset + 0x10 + i], table[blockOffset + i], table[blockOffset + 0x20 + i] / 2.0);
        return levels;
    }

    /// <summary>
    /// Expected duty at a temperature, climbing from level 0 (ignores hysteresis, which depends on history).
    /// Used by the monitor to compare with the actual RPM.
    /// </summary>
    public static double ExpectedDuty(EncodedLevel[] levels, int temp)
    {
        var level = 0;
        while (level < Levels - 1 && temp > levels[level].EndTemp) level++;
        return levels[level].DutyPercent;
    }

    internal static EncodedLevel[] BuildLevels(CurvePoint[] points, int hysteresis)
    {
        var first = points[0].Temp;
        var last = points[^1].Temp;
        var up = new int[Levels + 1];
        for (var k = 0; k < Levels; k++) up[k] = (int)Math.Round(first + k * (double)(last - first) / (Levels - 1));
        up[Levels] = 0xFF;

        var levels = new EncodedLevel[Levels];
        for (var k = 0; k < Levels; k++)
        {
            var start = k == 0 ? 0 : Math.Max(0, up[k] - hysteresis);
            var end = k == Levels - 1 ? 0xFF : up[k + 1];
            var duty = k == Levels - 1 ? points[^1].Duty : FanCurve.Evaluate(points, up[k + 1]);
            var dutyRaw = (int)Math.Ceiling(duty * 2);                 // round up: never less airflow
            levels[k] = new EncodedLevel(k, (byte)start, (byte)end, Math.Min(200, dutyRaw) / 2.0);
        }
        return levels;
    }

    private static void Write(byte[] table, int blockOffset, EncodedLevel[] levels)
    {
        foreach (var l in levels)
        {
            table[blockOffset + l.Level] = l.EndTemp;                        // 0xF00 / 0xF30
            table[blockOffset + 0x10 + l.Level] = l.StartTemp;               // 0xF10 / 0xF40
            table[blockOffset + 0x20 + l.Level] = (byte)(l.DutyPercent * 2); // 0xF20 / 0xF50
        }
    }
}

/// <summary>One step of the EC table.</summary>
public readonly record struct EncodedLevel(int Level, byte StartTemp, byte EndTemp, double DutyPercent);
