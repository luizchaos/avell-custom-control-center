using System.Text.Json;
using System.Text.Json.Serialization;
using AvellCC.Core.Ec;

namespace AvellCC.Core.Fan;

/// <summary>
/// "Office Mode Advanced" curve: 5 speeds (T1..T5, in %) at the firmware's fixed temperatures; the EC receives % × 2
/// at 0x743–0x747. This EC ignores it (docs/HARDWARE.md); kept for the experiments and for other models.
/// </summary>
public sealed record OfficeFanCurve
{
    public const int Levels = EcAddresses.OfficeAdvPwmCount;
    /// <summary>T5 never below the vendor preset's top (60 %): the curve must not cool less than the vendor at the top.</summary>
    public const int MinTopDuty = 60;

    [JsonPropertyName("name")] public string Name { get; init; } = "office";
    [JsonPropertyName("duties")] public int[] Duties { get; init; } = [];

    /// <summary>Also sets 0x727 bit 6 ("custom profile mode", tuxedo-drivers) together with Office mode.</summary>
    [JsonPropertyName("customProfile")] public bool CustomProfile { get; init; }

    private static readonly JsonSerializerOptions Json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static OfficeFanCurve Load(string path)
    {
        var curve = JsonSerializer.Deserialize<OfficeFanCurve>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("Empty curve.");
        curve.Validate();
        return curve;
    }

    public void Validate()
    {
        var errors = new List<string>();
        if (Duties.Length != Levels)
        {
            errors.Add($"duties must have exactly {Levels} values (T1..T5), has {Duties.Length}.");
        }
        else
        {
            for (var i = 0; i < Levels; i++)
            {
                if (Duties[i] is < 0 or > 100) errors.Add($"T{i + 1}: {Duties[i]}% is outside 0–100.");
                if (i > 0 && Duties[i] < Duties[i - 1]) errors.Add($"T{i + 1}: speed must not decrease from one level to the next.");
            }
            if (Duties[^1] < MinTopDuty) errors.Add($"T5 must be at least {MinTopDuty}% (is {Duties[^1]}%).");
        }
        if (errors.Count > 0) throw new FanCurveException(errors);
    }

    /// <summary>Bytes for 0x743–0x747 (% × 2), as in MyFanManager_Intel.SetFan_Optimized_PWM.</summary>
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
