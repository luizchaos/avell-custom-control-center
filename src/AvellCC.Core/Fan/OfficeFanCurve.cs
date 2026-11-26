using System.Text.Json;
using System.Text.Json.Serialization;
using AvellCC.Core.Ec;

namespace AvellCC.Core.Fan;

/// <summary>
/// Curva do "Office Mode Advanced": 5 velocidades (T1..T5, em %) nas temperaturas fixas do firmware.
/// É o mecanismo que este EC obedece (docs/HARDWARE.md). O EC recebe % × 2 em 0x743–0x747.
/// </summary>
public sealed record OfficeFanCurve
{
    public const int Levels = EcAddresses.OfficeAdvPwmCount;
    /// <summary>T5 nunca abaixo do máximo do preset da Avell (60 %): a curva não pode resfriar menos que o vendor no topo.</summary>
    public const int MinTopDuty = 60;

    [JsonPropertyName("name")] public string Name { get; init; } = "office";
    [JsonPropertyName("duties")] public int[] Duties { get; init; } = [];

    /// <summary>Liga 0x727 bit 6 ("custom profile mode", tuxedo-drivers) junto com o modo Office.</summary>
    [JsonPropertyName("customProfile")] public bool CustomProfile { get; init; }

    private static readonly JsonSerializerOptions Json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static OfficeFanCurve Load(string path)
    {
        var curve = JsonSerializer.Deserialize<OfficeFanCurve>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("Curva vazia.");
        curve.Validate();
        return curve;
    }

    public void Validate()
    {
        var errors = new List<string>();
        if (Duties.Length != Levels)
        {
            errors.Add($"duties precisa ter exatamente {Levels} valores (T1..T5), tem {Duties.Length}.");
        }
        else
        {
            for (var i = 0; i < Levels; i++)
            {
                if (Duties[i] is < 0 or > 100) errors.Add($"T{i + 1}: {Duties[i]}% fora de 0–100.");
                if (i > 0 && Duties[i] < Duties[i - 1]) errors.Add($"T{i + 1}: a velocidade não pode diminuir de um nível para o seguinte.");
            }
            if (Duties[^1] < MinTopDuty) errors.Add($"T5 precisa ter pelo menos {MinTopDuty}% (tem {Duties[^1]}%).");
        }
        if (errors.Count > 0) throw new FanCurveException(errors);
    }

    /// <summary>Bytes para 0x743–0x747 (% × 2), como em MyFanManager_Intel.SetFan_Optimized_PWM.</summary>
    public byte[] ToRaw()
    {
        Validate();
        return Duties.Select(d => (byte)(d * 2)).ToArray();
    }

    public static int[] FromRaw(ReadOnlySpan<byte> raw)
    {
        var duties = new int[raw.Length];
        for (var i = 0; i < raw.Length; i++) duties[i] = raw[i] / 2;
        return duties;
    }
}
