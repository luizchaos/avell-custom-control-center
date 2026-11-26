using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvellCC.Core.Fan;

/// <summary>
/// Curva editável pelo usuário: pontos (°C, %) ligados por retas, um conjunto para a CPU e outro para a GPU.
/// O EC não entende retas: <see cref="FanTableEncoder"/> transforma a curva numa escada de 16 níveis
/// que fica SEMPRE igual ou acima da curva (nunca ventila menos do que o desenhado).
/// </summary>
public sealed record FanCurve
{
    public const int MinPoints = 2;
    public const int MaxPoints = 16;
    public const int MinTemp = 20;
    public const int MaxLastPointTemp = 90;     // a partir daqui o último nível vale para qualquer temperatura
    public const int MinLastPointDuty = 80;     // ... e precisa ventilar forte
    public const int MinHysteresis = 2;
    public const int MaxHysteresis = 10;

    [JsonPropertyName("name")] public string Name { get; init; } = "custom";
    [JsonPropertyName("hysteresis")] public int Hysteresis { get; init; } = 4;
    [JsonPropertyName("cpu")] public CurvePoint[] Cpu { get; init; } = [];
    [JsonPropertyName("gpu")] public CurvePoint[] Gpu { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static FanCurve Load(string path)
    {
        var curve = JsonSerializer.Deserialize<FanCurve>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("Curva vazia.");
        curve.Validate();
        return curve;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Lança <see cref="FanCurveException"/> com todos os problemas encontrados.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        if (Hysteresis is < MinHysteresis or > MaxHysteresis)
            errors.Add($"hysteresis deve estar entre {MinHysteresis} e {MaxHysteresis} °C (é {Hysteresis}).");
        ValidatePoints("cpu", Cpu, errors);
        ValidatePoints("gpu", Gpu, errors);
        if (errors.Count > 0) throw new FanCurveException(errors);
    }

    private void ValidatePoints(string name, CurvePoint[] points, List<string> errors)
    {
        if (points.Length is < MinPoints or > MaxPoints)
        {
            errors.Add($"{name}: precisa de {MinPoints} a {MaxPoints} pontos (tem {points.Length}).");
            return;
        }
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (p.Duty is < 0 or > 100) errors.Add($"{name}[{i}]: duty {p.Duty}% fora de 0–100.");
            if (p.Temp < MinTemp) errors.Add($"{name}[{i}]: temperatura {p.Temp} °C abaixo de {MinTemp}.");
            if (i == 0) continue;
            if (p.Temp <= points[i - 1].Temp) errors.Add($"{name}[{i}]: temperaturas precisam ser estritamente crescentes.");
            if (p.Duty < points[i - 1].Duty) errors.Add($"{name}[{i}]: duty não pode diminuir quando a temperatura sobe.");
        }
        var last = points[^1];
        if (last.Temp > MaxLastPointTemp) errors.Add($"{name}: o último ponto precisa estar em até {MaxLastPointTemp} °C (está em {last.Temp}).");
        if (last.Duty < MinLastPointDuty) errors.Add($"{name}: o último ponto precisa ter pelo menos {MinLastPointDuty}% (tem {last.Duty}%).");
        var span = last.Temp - points[0].Temp;
        if (span < FanTableEncoder.Levels - 1)
            errors.Add($"{name}: a curva precisa cobrir pelo menos {FanTableEncoder.Levels - 1} °C entre o primeiro e o último ponto (cobre {span}).");
        if (span / (FanTableEncoder.Levels - 1) < 1)
            errors.Add($"{name}: degraus menores que 1 °C.");
    }

    /// <summary>Duty (%) da curva numa temperatura, interpolando linearmente entre os pontos.</summary>
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
    : ArgumentException("Curva inválida:\n  - " + string.Join("\n  - ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
