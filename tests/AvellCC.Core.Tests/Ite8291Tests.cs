using AvellCC.Core.Keyboard;

namespace AvellCC.Core.Tests;

public class Ite8291Tests
{
    [Fact]
    public void Packets_are_9_bytes_with_report_id_zero()
    {
        foreach (var p in new[] { Ite8291.TurnOff(), Ite8291.SetBrightness(4), Ite8291.QueryFirmware(), Ite8291.SetZoneColor(1, new Rgb(1, 2, 3), 0) })
        {
            Assert.Equal(Ite8291.FeatureReportLength, p.Length);
            Assert.Equal(0, p[0]);
        }
    }

    [Fact]
    public void Set_effect_layout_matches_vendor()
    {
        var p = Ite8291.SetEffect(Ite8291.Control.Apply, Ite8291.Effect.Wave, speedLevel: 4, brightnessLevel: 4, color: 0x08,
                                  Ite8291.Direction.RightToLeft, save: true);
        Assert.Equal(new byte[] { 0x00, 0x08, 0x02, 0x03, 1, 50, 0x08, 1, 1 }, p);
    }

    [Fact]
    public void Brightness_packet_matches_vendor() =>
        Assert.Equal(new byte[] { 0x00, 0x09, 0x02, 22, 0, 0, 0, 0, 0 }, Ite8291.SetBrightness(2));

    [Fact]
    public void Zone_color_applies_vendor_correction()
    {
        var p = Ite8291.SetZoneColor(3, new Rgb(255, 255, 255), projectId: 0);
        Assert.Equal(new byte[] { 0x00, 0x14, 0x00, 3, 255, 100, 200, 0, 0 }, p);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Zone_out_of_range_throws(int zone) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Ite8291.SetZoneColor(zone, new Rgb(), 0));
}
