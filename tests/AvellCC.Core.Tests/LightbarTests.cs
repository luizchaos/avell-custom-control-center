using AvellCC.Core.Control;
using AvellCC.Core.Keyboard;

namespace AvellCC.Core.Tests;

public class LightbarTests
{
    private sealed class FakeTransport(bool output = true) : IFeatureTransport, IOutputTransport
    {
        public List<(string Kind, byte[] Data)> Log { get; } = [];
        public void SetFeature(byte[] report) => Log.Add(("feature", report));
        public void WriteOutput(byte[] report)
        {
            if (!output) throw new InvalidOperationException("no output reports");
            Log.Add(("output", report));
        }
        public byte[] GetFeature(byte reportId, int length) => new byte[length];
        public void Dispose() { }
    }

    private sealed class FeatureOnlyTransport : IFeatureTransport
    {
        public List<byte[]> Sent { get; } = [];
        public void SetFeature(byte[] report) => Sent.Add(report);
        public byte[] GetFeature(byte reportId, int length) => new byte[length];
        public void Dispose() { }
    }

    [Fact]
    public void Static_uses_the_vendor_user_mode_sequence()
    {
        var t = new FakeTransport();
        new Lightbar(t).Static(new Rgb(0x11, 0x22, 0x33), brightness: 4, save: false);

        Assert.Equal(1 + 2 * Lightbar.Rows, t.Log.Count);
        Assert.Equal("000802330032000000", Convert.ToHexString(t.Log[0].Data));       // user mode, brightness 50
        for (var row = 0; row < Lightbar.Rows; row++)
        {
            Assert.Equal(("feature", $"001600{row:X2}0000000000"), (t.Log[1 + 2 * row].Kind, Convert.ToHexString(t.Log[1 + 2 * row].Data)));
            Assert.Equal("output", t.Log[2 + 2 * row].Kind);
        }
    }

    [Fact]
    public void Row_report_is_blue_green_red_blocks_of_21()
    {
        var r = Lightbar.RowReport(new Rgb(0xAA, 0xBB, 0xCC));
        Assert.Equal(65, r.Length);
        Assert.Equal([0, 0], r[..2]);
        Assert.All(r[2..23], b => Assert.Equal(0xCC, b));    // B
        Assert.All(r[23..44], b => Assert.Equal(0xBB, b));   // G
        Assert.All(r[44..65], b => Assert.Equal(0xAA, b));   // R
    }

    [Fact]
    public void Without_output_reports_static_falls_back_to_a_firmware_static_effect()
    {
        var t = new FeatureOnlyTransport();
        new Lightbar(t).Static(new Rgb(255, 0, 0), 4, save: false);
        Assert.Equal(["00140001FF00000000", "000802010532010000"], t.Sent.Select(Convert.ToHexString));
    }

    [Fact]
    public void Breathing_uses_the_chosen_color_and_wave_the_firmware_rainbow()
    {
        var t = new FakeTransport();
        new LightbarSettings { Mode = "effect", Effect = "Breathing", Color = "00FF00" }.ApplyTo(new Lightbar(t), save: false);
        Assert.Equal("0014000100FF000000", Convert.ToHexString(t.Log[0].Data));
        Assert.Equal(1, t.Log[1].Data[6]);                                                      // color mode: one slot

        var w = new FakeTransport();
        new LightbarSettings { Mode = "effect", Effect = "Wave", Color = "00FF00" }.ApplyTo(new Lightbar(w), save: false);
        Assert.Single(w.Log);
        Assert.Equal(0, w.Log[0].Data[6]);                                                      // firmware rainbow
    }

    [Fact]
    public void Off_sends_the_off_control()
    {
        var t = new FakeTransport();
        new LightbarSettings { Mode = "off" }.ApplyTo(new Lightbar(t));
        Assert.Equal("000801000000000000", Convert.ToHexString(t.Log.Single().Data));
    }

    [Theory]
    [InlineData("zones", "Wave", "FFFFFF")]
    [InlineData("effect", "Ripple", "FFFFFF")]
    [InlineData("effect", "Static", "FFFFFF")]
    [InlineData("static", "Wave", "GGGGGG")]
    public void Invalid_settings_are_rejected(string mode, string effect, string color) =>
        Assert.Throws<ArgumentException>(() => new LightbarSettings { Mode = mode, Effect = effect, Color = color }.Validate());

    [Fact]
    public void Settings_round_trip_through_the_store()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avellcc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir);
            store.Save(new AppSettings { Lightbar = new LightbarSettings { Mode = "effect", Effect = "Mix", Color = "123456", Speed = 4 } });
            var lb = store.Load().Lightbar!;
            Assert.Equal(("effect", "Mix", "123456", 4), (lb.Mode, lb.Effect, lb.Color, lb.Speed));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
