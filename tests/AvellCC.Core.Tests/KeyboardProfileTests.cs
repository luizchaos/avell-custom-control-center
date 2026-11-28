using AvellCC.Core.Control;
using AvellCC.Core.Keyboard;

namespace AvellCC.Core.Tests;

public class KeyboardProfileTests
{
    private static KeyboardProfile Profile(string name, string color = "FF0000") =>
        new() { Name = name, Keyboard = new KeyboardSettings { Mode = "static", Colors = [color] } };

    [Fact]
    public void Saving_adds_sorted_and_replaces_same_name_ignoring_case()
    {
        var s = new AppSettings()
            .WithKeyboardProfile(Profile("Night"))
            .WithKeyboardProfile(Profile("Gaming RGB"))
            .WithKeyboardProfile(Profile("night", "0000FF"));
        Assert.Equal(["Gaming RGB", "night"], s.KeyboardProfiles.Select(p => p.Name));
        Assert.Equal("0000FF", s.FindKeyboardProfile("NIGHT")!.Keyboard.Colors[0]);
    }

    [Fact]
    public void Deleting_the_active_profile_clears_it()
    {
        var s = new AppSettings().WithKeyboardProfile(Profile("A")).WithKeyboardProfile(Profile("B")) with { KeyboardProfile = "A" };
        var after = s.WithoutKeyboardProfile("a");
        Assert.Equal(["B"], after.KeyboardProfiles.Select(p => p.Name));
        Assert.Null(after.KeyboardProfile);
        Assert.Throws<ArgumentException>(() => after.WithoutKeyboardProfile("missing"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" padded ")]
    [InlineData("a/b")]
    [InlineData("12345678901234567890123456789012345678901")]
    public void Invalid_names_are_rejected(string name) =>
        Assert.Throws<ArgumentException>(() => new AppSettings().WithKeyboardProfile(Profile(name)));

    [Fact]
    public void Invalid_keyboard_settings_are_rejected() =>
        Assert.Throws<ArgumentException>(() => new AppSettings().WithKeyboardProfile(
            new KeyboardProfile { Name = "x", Keyboard = new KeyboardSettings { Mode = "zones", Colors = ["FF0000"] } }));

    [Fact]
    public void Settings_round_trip_through_the_store()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avellcc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir);
            store.Save(new AppSettings().WithKeyboardProfile(Profile("Ação")) with { KeyboardProfile = "Ação" });
            var loaded = store.Load();
            Assert.Equal("Ação", loaded.KeyboardProfile);
            Assert.Equal("FF0000", loaded.FindKeyboardProfile("ação")!.Keyboard.Colors[0]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("balanced", "max")]
    [InlineData("silent", "balanced")]     // wraps around
    [InlineData("SILENT", "balanced")]     // ignores case
    [InlineData("deleted-curve", "balanced")]
    [InlineData(null, "balanced")]
    public void Mode_button_cycles_through_curves(string? current, string expected) =>
        Assert.Equal(expected, Cycle.Next(["balanced", "max", "performance", "silent"], current));

    [Fact]
    public void Cycle_with_no_curves_returns_null() => Assert.Null(Cycle.Next([], "x"));
}
