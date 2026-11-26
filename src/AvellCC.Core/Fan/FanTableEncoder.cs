using AvellCC.Core.Ec;

namespace AvellCC.Core.Fan;

/// <summary>
/// Converte uma <see cref="FanCurve"/> na tabela de 16 níveis do EC (0xF00–0xF5F).
///
/// Semântica do EC por nível i (mesma do vendor, FanTable_Manager.SetCpuFanTable):
///   EC[up   + i] = "end temp":   acima disso, sobe para o nível i+1 (0xFF no último nível)
///   EC[down + i] = "start temp": abaixo disso, desce para o nível i-1 (0 no nível 0)
///   EC[duty + i] = duty × 2 (0..200 = 0..100 %)
///
/// Escada gerada a partir da curva (pontos T_first..T_last):
///   o nível k (k ≥ 1) começa em up_k = T_first + k·(T_last − T_first)/15 e o nível 15 começa em T_last;
///   duty_k = curva(up_{k+1}), ou seja, o MAIOR valor da curva dentro do degrau, porque a curva não decresce;
///   start_k = up_k − histerese.
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

    /// <summary>Níveis decodificados de uma das metades (CPU = 0x00, GPU = 0x30) de uma tabela crua.</summary>
    public static EncodedLevel[] Decode(byte[] table, int blockOffset)
    {
        var levels = new EncodedLevel[Levels];
        for (var i = 0; i < Levels; i++)
            levels[i] = new EncodedLevel(i, table[blockOffset + 0x10 + i], table[blockOffset + i], table[blockOffset + 0x20 + i] / 2.0);
        return levels;
    }

    /// <summary>
    /// Duty esperado numa temperatura, subindo a partir do nível 0 (ignora a histerese, que depende do histórico).
    /// Usado pelo monitor para comparar com o RPM real.
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
            var dutyRaw = (int)Math.Ceiling(duty * 2);                 // arredonda para cima: nunca menos ventilação
            levels[k] = new EncodedLevel(k, (byte)start, (byte)end, Math.Min(200, dutyRaw) / 2.0);
        }
        return levels;
    }

    private static void Write(byte[] table, int blockOffset, EncodedLevel[] levels)
    {
        foreach (var l in levels)
        {
            table[blockOffset + l.Level] = l.EndTemp;                       // 0xF00 / 0xF30
            table[blockOffset + 0x10 + l.Level] = l.StartTemp;              // 0xF10 / 0xF40
            table[blockOffset + 0x20 + l.Level] = (byte)(l.DutyPercent * 2); // 0xF20 / 0xF50
        }
    }
}

/// <summary>Um degrau da tabela do EC.</summary>
public readonly record struct EncodedLevel(int Level, byte StartTemp, byte EndTemp, double DutyPercent);
