namespace AvellCC.Core.Keyboard;

/// <summary>
/// 4-zone RGB keyboard (ITE 8291, 048D:CE00, usage page 0xFF12).
/// Follows the vendor's sequence (LM_ITE_RGB.Set_ITE_Effect_Type_FwMode):
///   one color         → 0x14 slot 1, then 0x08 with color = 1
///   several colors    → 0x14 slots 1..N, then 0x08 with color = 8
///   live zone color   → just 0x14 on the zone's slot (1..4)
/// </summary>
public sealed class FourZoneKeyboard(IFeatureTransport device, byte projectId, bool colorCorrection = true) : IDisposable
{
    /// <summary>This laptop's EC project ID (0x740 = 0x0F, read 2026-11-26).</summary>
    public const byte DefaultProjectId = 0x0F;

    private const byte ColorSingleSlot = 1;
    private const byte ColorCycleSlots = 8;
    private readonly object _lock = new();

    public void Static(Rgb color, int brightness, bool save) =>
        Send(Color(1, color), Ite8291.SetEffect(Ite8291.Control.Apply, Ite8291.Effect.Static, 2, brightness, ColorSingleSlot, Ite8291.Direction.LeftToRight, save));

    /// <summary>One color per zone: switches to Static, then writes each zone's color.</summary>
    public void Zones(IReadOnlyList<Rgb> colors, int brightness, bool save)
    {
        if (colors.Count != Ite8291.Zones) throw new ArgumentException($"There are {Ite8291.Zones} zones.", nameof(colors));
        var packets = new List<byte[]>
        {
            Color(1, colors[0]),
            Ite8291.SetEffect(Ite8291.Control.Apply, Ite8291.Effect.Static, 2, brightness, ColorSingleSlot, Ite8291.Direction.LeftToRight, save),
        };
        for (var zone = 1; zone <= Ite8291.Zones; zone++) packets.Add(Color(zone, colors[zone - 1]));
        Send([.. packets]);
    }

    /// <summary>Firmware effect. With colors it uses the cycling palette (color = 8); without, the firmware rainbow (color = 0).</summary>
    public void Effect(Ite8291.Effect effect, int speed, int brightness, Ite8291.Direction direction, IReadOnlyList<Rgb>? colors, bool save)
    {
        var packets = new List<byte[]>();
        byte colorMode = 0;
        if (colors is { Count: 1 }) { packets.Add(Color(1, colors[0])); colorMode = ColorSingleSlot; }
        else if (colors is { Count: > 1 })
        {
            if (colors.Count > 7) throw new ArgumentException("At most 7 colors.", nameof(colors));
            for (var i = 0; i < colors.Count; i++) packets.Add(Color(i + 1, colors[i]));
            colorMode = ColorCycleSlots;
        }
        packets.Add(Ite8291.SetEffect(Ite8291.Control.Apply, effect, speed, brightness, colorMode, direction, save));
        Send([.. packets]);
    }

    public void Brightness(int level) => Send(Ite8291.SetBrightness(level));

    public void Off() => Send(Ite8291.TurnOff());

    /// <summary>Firmware version (command 0x80). Bytes [2..5] of the response.</summary>
    public byte[] FirmwareVersion() => Query(Ite8291.QueryFirmware(), Ite8291.CmdGetFirmware)[2..6];

    /// <summary>
    /// Current effect (command 0x88). The response mirrors command 0x08, starting at byte 2:
    /// ctrl, effect, speed, brightness (firmware value), color, direction, save.
    /// Confirmed 2026-11-26: "02 03 01 24 08 01" = Apply, Wave, speed 1, brightness 36, cycling palette, right→left.
    /// </summary>
    public EffectState CurrentEffect()
    {
        var r = Query(Ite8291.QueryEffect(), Ite8291.CmdGetEffect);
        return new EffectState((Ite8291.Control)r[2], (Ite8291.Effect)r[3], r[4], r[5], r[6], (Ite8291.Direction)r[7], r[8]);
    }

    /// <summary>Sends a raw 9-byte report (development tool).</summary>
    public void Raw(byte[] report) => Send(report);

    private byte[] Color(int slot, Rgb color) =>
        Ite8291.SetSlotColor(slot, colorCorrection ? Ite8291.CorrectColor4Zone(projectId, color) : color);

    private byte[] Query(byte[] request, byte expectedCommand)
    {
        lock (_lock)
        {
            for (var attempt = 0; attempt < 10; attempt++)   // the vendor also retries up to 10 times
            {
                device.SetFeature(request);
                Thread.Sleep(2);
                var response = device.GetFeature(0, Ite8291.FeatureReportLength);
                if (response[1] == expectedCommand) return response;
                Thread.Sleep(20);
            }
            throw new InvalidOperationException($"No valid response to command 0x{expectedCommand:X2}.");
        }
    }

    private void Send(params byte[][] packets)
    {
        lock (_lock)
        {
            foreach (var packet in packets)
            {
                device.SetFeature(packet);
                Thread.Sleep(2); // the vendor waits 1 ms between commands
            }
        }
    }

    public void Dispose() => device.Dispose();
}

/// <summary>State reported by command 0x88 (raw firmware values for Speed/Brightness).</summary>
public readonly record struct EffectState(Ite8291.Control Control, Ite8291.Effect Effect, byte SpeedRaw, byte BrightnessRaw,
                                          byte ColorMode, Ite8291.Direction Direction, byte Save)
{
    public override string ToString() =>
        $"ctrl={Control} effect={Effect} speed={SpeedRaw} brightness={BrightnessRaw} color={ColorMode} dir={Direction} save={Save}";
}
