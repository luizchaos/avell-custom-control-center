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
    /// <summary>Named keyboard presets saved by the user.</summary>
    [JsonPropertyName("keyboardProfiles")] public KeyboardProfile[] KeyboardProfiles { get; init; } = [];
    /// <summary>Profile last applied; null after a manual change.</summary>
    [JsonPropertyName("keyboardProfile")] public string? KeyboardProfile { get; init; }
    /// <summary>In curve mode, the operational mode button switches to the next curve.</summary>
    [JsonPropertyName("modeButtonCyclesCurves")] public bool ModeButtonCyclesCurves { get; init; } = true;

    public const int MaxKeyboardProfiles = 50;

    public void Validate()
    {
        if (FanMode is not ("firmware" or "curve")) throw new ArgumentException($"Invalid fanMode: {FanMode}");
        SettingsStore.ValidateName(CurveName);
        Keyboard?.Validate();
        if (KeyboardProfiles.Length > MaxKeyboardProfiles) throw new ArgumentException($"At most {MaxKeyboardProfiles} keyboard profiles.");
        foreach (var p in KeyboardProfiles) p.Validate();
        var duplicate = KeyboardProfiles.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Duplicate keyboard profile: {duplicate.Key}");
    }

    /// <summary>Adds or replaces (same name, ignoring case) a profile.</summary>
    public AppSettings WithKeyboardProfile(KeyboardProfile profile)
    {
        profile.Validate();
        var others = KeyboardProfiles.Where(p => !p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        var next = this with { KeyboardProfiles = [.. others, profile] };
        next = next with { KeyboardProfiles = [.. next.KeyboardProfiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)] };
        next.Validate();
        return next;
    }

    public AppSettings WithoutKeyboardProfile(string name)
    {
        var profile = FindKeyboardProfile(name) ?? throw new ArgumentException($"Keyboard profile '{name}' not found.");
        return this with
        {
            KeyboardProfiles = [.. KeyboardProfiles.Where(p => p != profile)],
            KeyboardProfile = string.Equals(KeyboardProfile, profile.Name, StringComparison.OrdinalIgnoreCase) ? null : KeyboardProfile,
        };
    }

    public KeyboardProfile? FindKeyboardProfile(string name) =>
        KeyboardProfiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A named keyboard preset (colors, mode, effect, brightness).</summary>
public sealed record KeyboardProfile
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("keyboard")] public KeyboardSettings Keyboard { get; init; } = new();

    public void Validate()
    {
        var name = Name.Trim();
        if (name.Length is 0 or > 40 || name != Name || Name.Any(char.IsControl) || Name.IndexOfAny(['"', '\\', '/']) >= 0)
            throw new ArgumentException($"Invalid profile name: '{Name}' (1 to 40 characters, no leading/trailing spaces, no \" \\ /).");
        Keyboard.Validate();
    }
}

/// <summary>Picks the next item for the operational mode button (wraps around; unknown current → first).</summary>
public static class Cycle
{
    public static string? Next(IReadOnlyList<string> names, string? current)
    {
        if (names.Count == 0) return null;
        var index = -1;
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(names[i], current, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        return names[(index + 1) % names.Count];
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
