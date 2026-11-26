using System.Text.Json;
using System.Text.RegularExpressions;
using AvellCC.Core.Control;

namespace AvellCC.Core.Tests;

/// <summary>Keeps translations complete: every language has the same keys as en.json, and every key used in code exists.</summary>
public partial class LanguageFilesTests
{
    private static readonly string AppDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src", "AvellCC.App"));
    private static readonly string LanguagesDir = Path.Combine(AppDir, "Languages");

    private static Dictionary<string, string> Read(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file),
            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;

    public static IEnumerable<object[]> LanguageFiles() =>
        Directory.GetFiles(LanguagesDir, "*.json").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(LanguageFiles))]
    public void Every_language_has_exactly_the_english_keys(string file)
    {
        var english = Read(Path.Combine(LanguagesDir, "en.json")).Keys.ToHashSet();
        var keys = Read(Path.Combine(LanguagesDir, file)).Keys.ToHashSet();
        Assert.Empty(english.Except(keys));   // missing translations
        Assert.Empty(keys.Except(english));   // stale / misspelled keys
    }

    [Theory]
    [MemberData(nameof(LanguageFiles))]
    public void Placeholders_match_english(string file)
    {
        var english = Read(Path.Combine(LanguagesDir, "en.json"));
        foreach (var (key, value) in Read(Path.Combine(LanguagesDir, file)))
        {
            var expected = Placeholder().Matches(english[key]).Select(m => m.Value).Order();
            var actual = Placeholder().Matches(value).Select(m => m.Value).Order();
            Assert.True(expected.SequenceEqual(actual), $"{file}: '{key}' has different placeholders");
        }
    }

    [Fact]
    public void Every_key_used_in_code_exists_in_english()
    {
        var english = Read(Path.Combine(LanguagesDir, "en.json")).Keys.ToHashSet();
        var used = new HashSet<string>();
        foreach (var file in Directory.GetFiles(AppDir, "*.*", SearchOption.TopDirectoryOnly).Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in CodeKey().Matches(text)) used.Add(m.Groups[1].Value);
            foreach (Match m in XamlKey().Matches(text)) used.Add(m.Groups[1].Value);
        }
        // keys built at runtime
        foreach (var state in Enum.GetNames<LoopState>()) used.Add($"state.{state}");
        foreach (var mode in new[] { "static", "zones", "effect", "off" }) used.Add($"kb.hint.{mode}");
        foreach (var mode in Enum.GetNames<AvellCC.Core.Monitoring.PowerMode>().Where(m => m != "Unknown")) used.Add($"power.{mode}");

        Assert.NotEmpty(used);
        Assert.Empty(used.Where(k => !k.Contains('{')).Except(english));
    }

    [GeneratedRegex(@"\{\d+\}")] private static partial Regex Placeholder();
    [GeneratedRegex(@"Loc\.T\(""([\w.]+)""")] private static partial Regex CodeKey();
    [GeneratedRegex(@"\{l:Tr ([\w.]+)\}")] private static partial Regex XamlKey();
}
