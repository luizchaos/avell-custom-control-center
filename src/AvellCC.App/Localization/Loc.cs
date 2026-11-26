using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace AvellCC.App.Localization;

/// <summary>A language available in the Languages folder.</summary>
public sealed record LanguageInfo(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// UI strings loaded from JSON files in the <c>Languages</c> folder next to the executable
/// (<c>en.json</c>, <c>pt-BR.json</c>, ...). Adding a language = adding a file; no rebuild needed.
///
/// Lookup order: selected language → English → "[key]" (makes missing keys easy to spot).
/// XAML binds through the indexer (<see cref="TrExtension"/>), so switching languages updates the UI live.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string Fallback = "en";
    private const string RegistryKey = @"Software\AvellCC";
    private const string RegistryValue = "Language";

    public static Loc Instance { get; } = new();

    private Dictionary<string, string> _strings = [];
    private Dictionary<string, string> _fallback = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language { get; private set; } = Fallback;

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Languages");

    public string this[string key] =>
        _strings.TryGetValue(key, out var value) || _fallback.TryGetValue(key, out value) ? value : $"[{key}]";

    /// <summary>Translated and formatted string, for code-behind.</summary>
    public static string T(string key, params object?[] args)
    {
        var text = Instance[key];
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    public static IReadOnlyList<LanguageInfo> Available()
    {
        if (!System.IO.Directory.Exists(Directory)) return [new LanguageInfo(Fallback, "English")];
        return System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(path =>
            {
                var code = Path.GetFileNameWithoutExtension(path);
                var strings = Read(path);
                return new LanguageInfo(code, strings.GetValueOrDefault("_language", code));
            })
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Saved preference, else the Windows UI language if a file exists for it, else English.</summary>
    public void LoadPreferred()
    {
        var available = Available().Select(l => l.Code).ToList();
        var saved = Registry.CurrentUser.OpenSubKey(RegistryKey)?.GetValue(RegistryValue) as string;
        var ui = CultureInfo.CurrentUICulture;
        var code = new[] { saved, ui.Name, ui.TwoLetterISOLanguageName }
            .FirstOrDefault(c => c is not null && available.Contains(c, StringComparer.OrdinalIgnoreCase))
            ?? Fallback;
        Load(code);
    }

    public void Load(string code, bool remember = false)
    {
        _fallback = Read(Path.Combine(Directory, $"{Fallback}.json"));
        var path = Path.Combine(Directory, $"{code}.json");
        _strings = File.Exists(path) ? Read(path) : _fallback;
        Language = File.Exists(path) ? code : Fallback;

        if (remember)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
            key.SetValue(RegistryValue, Language);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    private static Dictionary<string, string> Read(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path),
                       new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   ?? [];
        }
        catch (JsonException)
        {
            return [];   // a broken translation file must not break the app: fall back to English
        }
    }
}
