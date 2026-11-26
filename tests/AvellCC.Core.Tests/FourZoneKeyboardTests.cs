using AvellCC.Core.Keyboard;

namespace AvellCC.Core.Tests;

public class FourZoneKeyboardTests
{
    private sealed class FakeTransport : IFeatureTransport
    {
        public List<byte[]> Sent { get; } = [];
        public Func<byte[]>? Response { get; set; }
        public void SetFeature(byte[] report) => Sent.Add(report);
        public byte[] GetFeature(byte reportId, int length) => Response?.Invoke() ?? new byte[length];
        public void Dispose() { }
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    [Fact]
    public void Static_sets_slot1_then_static_effect_with_single_color()
    {
        var t = new FakeTransport();
        new FourZoneKeyboard(t, FourZoneKeyboard.DefaultProjectId, colorCorrection: false).Static(new Rgb(255, 0, 0), 4, save: false);

        Assert.Equal(["001400 01 FF0000 0000".Replace(" ", ""), "0008 02 01 05 32 01 00 00".Replace(" ", "")], t.Sent.Select(Hex));
    }

    [Fact]
    public void Zones_sends_static_then_one_color_per_zone_with_correction()
    {
        var t = new FakeTransport();
        new FourZoneKeyboard(t, projectId: 0x0F).Zones(
            [new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255), new Rgb(255, 255, 255)], 4, save: true);

        Assert.Equal(6, t.Sent.Count);
        Assert.Equal(0x08, t.Sent[1][1]);
        Assert.Equal(1, t.Sent[1][8]);                               // save
        Assert.Equal("00140001FF0000" + "0000", Hex(t.Sent[2]));     // zone 1 red
        Assert.Equal("0014000200640000" + "00", Hex(t.Sent[3]));     // zone 2 green: 255*100/255 = 0x64
        Assert.Equal("001400030000C8" + "0000", Hex(t.Sent[4]));     // zone 3 blue:  255*200/255 = 0xC8
        Assert.Equal("00140004FF64C8" + "0000", Hex(t.Sent[5]));     // zone 4 corrected white
    }

    [Fact]
    public void Effect_with_palette_uses_cycle_mode_8()
    {
        var t = new FakeTransport();
        new FourZoneKeyboard(t, 0x0F, colorCorrection: false).Effect(
            Ite8291.Effect.Wave, speed: 4, brightness: 3, Ite8291.Direction.RightToLeft,
            [new Rgb(1, 2, 3), new Rgb(4, 5, 6)], save: false);

        Assert.Equal(3, t.Sent.Count);
        Assert.Equal("0008020301240801" + "00", Hex(t.Sent[2]));     // same state read from the hardware on 2026-11-26
    }

    [Fact]
    public void Current_effect_parses_hardware_response()
    {
        var t = new FakeTransport { Response = () => Convert.FromHexString("008802030124080100") };
        var state = new FourZoneKeyboard(t, 0x0F).CurrentEffect();

        Assert.Equal(Ite8291.Effect.Wave, state.Effect);
        Assert.Equal(36, state.BrightnessRaw);
        Assert.Equal(8, state.ColorMode);
        Assert.Equal(Ite8291.Direction.RightToLeft, state.Direction);
    }

    [Fact]
    public void Query_retries_until_command_echo_matches()
    {
        var calls = 0;
        var t = new FakeTransport { Response = () => ++calls < 3 ? new byte[9] : Convert.FromHexString("008011030000000000") };
        Assert.Equal(new byte[] { 0x11, 0x03, 0, 0 }, new FourZoneKeyboard(t, 0x0F).FirmwareVersion());
        Assert.Equal(3, calls);
    }
}
