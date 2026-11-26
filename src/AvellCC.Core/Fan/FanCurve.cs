using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvellCC.Core.Fan;

/// <summary>
/// User-editable curve: (°C, %) points joined by straight lines, one set for the CPU and one for the GPU.
/// Applied in software by <see cref="FanCurveController"/> (direct control). <see cref="FanTableEncoder"/> can also turn it
/// into the EC's 16-level table, but this EC ignores that table (see docs/HARDWARE.md).
/// </summary>
public sealed record FanCurve
{
    public const int MinPoints = 2;
    public const int MaxPoints = 16;
    public const int MinTemp = 20;
    public const int MaxLastPointTemp = 90;     // the last point applies to every temperature above it...
    public const int MinLastPointDuty = 80;     // ...so it must cool hard
    public const int MinHysteresis = 2;
    public const int MaxHysteresis = 10;

    [JsonPropertyName("name")] public string Name { get; init; } = "custom";
    [JsonPropertyName("hysteresis")] public int Hysteresis { get; init; } = 4;

    /// <summary>
    /// Only for the (ignored) 0xF00 table experiments: what to write to 0x751 when enabling the table.
    /// "keep" = leave it (0x00 in the factory state); "standard" = 0xA0 (vendor Standard mode; capped fans at ~4000 RPM
    /// in the 2026-11-26 test); "performance" = 0x40 (vendor Fan Boost).
    /// </summary>
    [JsonPropertyName("ecMode")] public string EcMode { get; init; } = "keep";

    /// <summary>
    /// true = both fans use max(CPU curve, GPU curve). Default, because the fans share heat pipes
    /// (under firmware control, fan 2 followed the CPU). false = fan 1 follows the CPU and fan 2 the GPU.
    /// </summary>
    [JsonPropertyName("shared")] public bool Shared { get; init; } = true;
    [JsonPropertyName("cpu")] public CurvePoint[] Cpu { get; init; } = [];
    [JsonPropertyName("gpu")] public CurvePoint[] Gpu { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static FanCurve Load(string path)
    {
        var curve = JsonSerializer.Deserialize<FanCurve>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("Empty curve.");
        curve.Validate();
        return curve;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Throws <see cref="FanCurveException"/> listing every problem found.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        if (Hysteresis is < MinHysteresis or > MaxHysteresis)
            errors.Add($"hysteresis must be between {MinHysteresis} and {MaxHysteresis} °C (got {Hysteresis}).");
        if (EcMode is not ("keep" or "standard" or "performance"))
            errors.Add($"ecMode must be keep, standard or performance (got '{EcMode}').");
        ValidatePoints("cpu", Cpu, errors);
        ValidatePoints("gpu", Gpu, errors);
        if (errors.Count > 0) throw new FanCurveException(errors);
    }

    private void ValidatePoints(string name, CurvePoint[] points, List<string> errors)
    {
        if (points.Length is < MinPoints or > MaxPoints)
        {
            errors.Add($"{name}: needs {MinPoints} to {MaxPoints} points (has {points.Length}).");
            return;
        }
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (p.Duty is < 0 or > 100) errors.Add($"{name}[{i}]: duty {p.Duty}% is outside 0–100.");
            if (p.Temp < MinTemp) errors.Add($"{name}[{i}]: temperature {p.Temp} °C is below {MinTemp}.");
            if (i == 0) continue;
            if (p.Temp <= points[i - 1].Temp) errors.Add($"{name}[{i}]: temperatures must be strictly increasing.");
            if (p.Duty < points[i - 1].Duty) errors.Add($"{name}[{i}]: duty must not decrease as temperature rises.");
        }
        var last = points[^1];
        if (last.Temp > MaxLastPointTemp) errors.Add($"{name}: the last point must be at or below {MaxLastPointTemp} °C (is at {last.Temp}).");
        if (last.Duty < MinLastPointDuty) errors.Add($"{name}: the last point must be at least {MinLastPointDuty}% (is {last.Duty}%).");
        var span = last.Temp - points[0].Temp;
        if (span < FanTableEncoder.Levels - 1)
            errors.Add($"{name}: the curve must span at least {FanTableEncoder.Levels - 1} °C between the first and last points (spans {span}).");
    }

    /// <summary>Curve duty (%) at a temperature, linearly interpolated between points.</summary>
    public static double Evaluate(IReadOnlyList<CurvePoint> points, double temp)
    {
        if (temp <= points[0].Temp) return points[0].Duty;
        for (var i = 1; i < points.Count; i++)
        {
            if (temp <= points[i].Temp)
            {
                var a = points[i - 1];
                var b = points[i];
                return a.Duty + (b.Duty - a.Duty) * (temp - a.Temp) / (b.Temp - a.Temp);
            }
        }
        return points[^1].Duty;
    }
}

public readonly record struct CurvePoint([property: JsonPropertyName("temp")] int Temp, [property: JsonPropertyName("duty")] int Duty);

public sealed class FanCurveException(IReadOnlyList<string> errors)
    : ArgumentException("Invalid curve:\n  - " + string.Join("\n  - ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
