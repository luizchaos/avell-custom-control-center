using System.Text.Json;
using System.Text.Json.Serialization;
using AvellCC.Core.Fan;
using AvellCC.Core.Keyboard;

namespace AvellCC.Core.Control;

/// <summary>Desired state, persisted by the service in %ProgramData%\AvellCC\settings.json.</summary>
public sealed record AppSettings
{
    /// <summary>"firmware" (EC's internal curve) or "curve" (AvellCC curve, direct control).</summary>
    [JsonPropertyName("fanMode")] public string FanMode { get; init; } = "firmware";
    [JsonPropertyName("curveName")] public string CurveName { get; init; } = CurvePresets.Balanced.Name;
    /// <summary>null = the service leaves the keyboard alone.</summary>
    [JsonPropertyName("keyboard")] public KeyboardSettings? Keyboard { get; init; }

    public void Validate()
    {
        if (FanMode is not ("firmware" or "curve")) throw new ArgumentException($"Invalid fanMode: {FanMode}");
        SettingsStore.ValidateName(CurveName);
        Keyboard?.Validate();
    }
}

/// <summary>Settings and curve files. Atomic writes (temporary file + replace).</summary>
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

    /// <summary>Writes the built-in curves that do not exist yet (never overwrites an edited curve).</summary>
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
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidDataException) { /* invalid curve: left out of the list */ }
        }
        return curves;
    }

    public FanCurve LoadCurve(string name)
    {
        ValidateName(name);
        var path = CurvePath(name);
        if (!File.Exists(path)) throw new FileNotFoundException($"Curve '{name}' not found.", path);
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
            throw new ArgumentException($"Invalid curve name: '{name}' (use letters, digits, - and _, up to 40).");
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
