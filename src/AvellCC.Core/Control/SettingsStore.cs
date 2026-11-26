using System.Text.Json;
using System.Text.Json.Serialization;
using AvellCC.Core.Fan;
using AvellCC.Core.Keyboard;

namespace AvellCC.Core.Control;

/// <summary>Estado desejado, persistido pelo serviço em %ProgramData%\AvellCC\settings.json.</summary>
public sealed record AppSettings
{
    /// <summary>"firmware" (curva interna do EC) ou "curve" (curva do AvellCC, controle direto).</summary>
    [JsonPropertyName("fanMode")] public string FanMode { get; init; } = "firmware";
    [JsonPropertyName("curveName")] public string CurveName { get; init; } = CurvePresets.Balanced.Name;
    /// <summary>null = o serviço não mexe no teclado.</summary>
    [JsonPropertyName("keyboard")] public KeyboardSettings? Keyboard { get; init; }

    public void Validate()
    {
        if (FanMode is not ("firmware" or "curve")) throw new ArgumentException($"fanMode inválido: {FanMode}");
        SettingsStore.ValidateName(CurveName);
        Keyboard?.Validate();
    }
}

/// <summary>Arquivos de configuração e curvas. Gravação atômica (arquivo temporário + troca).</summary>
public sealed class SettingsStore(string? root = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public string Root { get; } = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AvellCC");
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public string CurvesDirectory => Path.Combine(Root, "curves");

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
        settings.Validate();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.Validate();
        WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, Json));
    }

    /// <summary>Grava as curvas de fábrica que ainda não existirem (nunca sobrescreve uma curva editada).</summary>
    public void EnsurePresets()
    {
        Directory.CreateDirectory(CurvesDirectory);
        foreach (var preset in CurvePresets.All)
        {
            var path = CurvePath(preset.Name);
            if (!File.Exists(path)) WriteAtomic(path, preset.ToJson());
        }
    }

    public IReadOnlyList<FanCurve> ListCurves()
    {
        if (!Directory.Exists(CurvesDirectory)) return [];
        var curves = new List<FanCurve>();
        foreach (var file in Directory.GetFiles(CurvesDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try { curves.Add(FanCurve.Load(file)); }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidDataException) { /* curva inválida: ignorada na lista */ }
        }
        return curves;
    }

    public FanCurve LoadCurve(string name)
    {
        ValidateName(name);
        var path = CurvePath(name);
        if (!File.Exists(path)) throw new FileNotFoundException($"Curva '{name}' não encontrada.", path);
        return FanCurve.Load(path);
    }

    public void SaveCurve(FanCurve curve)
    {
        ValidateName(curve.Name);
        curve.Validate();
        Directory.CreateDirectory(CurvesDirectory);
        WriteAtomic(CurvePath(curve.Name), curve.ToJson());
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException($"Nome de curva inválido: '{name}' (use letras, números, - e _, até 40).");
    }

    private string CurvePath(string name) => Path.Combine(CurvesDirectory, $"{name}.json");

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
