using System.Text.Json.Serialization;

namespace AvellCC.Core.Keyboard;

/// <summary>
/// Front LED light bar: a second ITE controller (048D:6005, usage page 0xFF03, vendor "MEZone_Lighbar", light bar
/// type "3"). Same 9-byte feature commands as the keyboard; for a fixed color the vendor uses ITE user mode
/// (LM_ITE_RGB.Set_ITE_Effect_Type_UserMode_Lightbar):
///   0x08 [2, 0x33 user mode, 0, brightness, 0, 0, save]
///   8 × ( 0x16 [0, row]  +  65-byte output report [0, 0, B×21, G×21, R×21] )
///
/// EXPERIMENTAL / UNTESTED: on the A65 LIV every command is accepted but the bar never lit, not even with the vendor
/// app (docs/HARDWARE.md §3.1). The vendor's per-model LED map (ConvertToLightBarMap) and color correction
/// (WKDColor.cheatRGB_HIDLightbar) are not reproduced: every LED gets the same, uncorrected color.
/// </summary>
public sealed class Lightbar(IFeatureTransport device) : IDisposable
{
    public const byte UserModeEffect = 0x33;
    public const byte CmdRowIndex = 0x16;
    public const int Rows = 8;
    public const int LedsPerRow = 21;
    public const int OutputReportLength = 65;

    /// <summary>Firmware effects offered for the light bar (same list as the 4-zone keyboard minus Static/Reactive; untested).</summary>
    public static readonly Ite8291.Effect[] Effects =
        [Ite8291.Effect.Breathing, Ite8291.Effect.Wave, Ite8291.Effect.Rainbow, Ite8291.Effect.Flash, Ite8291.Effect.Mix];

    private readonly object _lock = new();

    /// <summary>Fixed color on every LED (user mode). Falls back to a firmware Static effect without output reports.</summary>
    public void Static(Rgb color, int brightness, bool save)
    {
        lock (_lock)
        {
            if (device is not IOutputTransport output)
            {
                Send(Ite8291.SetSlotColor(1, color), Ite8291.SetEffect(Ite8291.Control.Apply, Ite8291.Effect.Static, 2, brightness, 1, Ite8291.Direction.LeftToRight, save));
                return;
            }
            device.SetFeature(UserModePacket(brightness, save));
            Thread.Sleep(20);   // the vendor waits 20 ms before the rows
            var row = RowReport(color);
            for (byte r = 0; r < Rows; r++)
            {
                device.SetFeature(RowIndexPacket(r));
                output.WriteOutput(row);
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>Firmware effect; one color for Breathing/Flash, the firmware rainbow for the others.</summary>
    public void Effect(Ite8291.Effect effect, int speed, int brightness, Ite8291.Direction direction, Rgb? color, bool save)
    {
        var packets = new List<byte[]>();
        byte colorMode = 0;
        if (color is { } c) { packets.Add(Ite8291.SetSlotColor(1, c)); colorMode = 1; }
        packets.Add(Ite8291.SetEffect(Ite8291.Control.Apply, effect, speed, brightness, colorMode, direction, save));
        lock (_lock) Send([.. packets]);
    }

    public void Off()
    {
        lock (_lock) Send(Ite8291.TurnOff());
    }

    public static byte[] UserModePacket(int brightness, bool save) =>
        [0x00, Ite8291.CmdSetEffect, (byte)Ite8291.Control.Apply, UserModeEffect, 0x00,
         Ite8291.BrightnessSteps[Math.Clamp(brightness, 0, 4)], 0x00, 0x00, save ? (byte)1 : (byte)0];

    public static byte[] RowIndexPacket(byte row) => [0x00, CmdRowIndex, 0x00, row, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>One user-mode row: [0, 0, B×21, G×21, R×21].</summary>
    public static byte[] RowReport(Rgb color)
    {
        var data = new byte[OutputReportLength];
        for (var i = 0; i < LedsPerRow; i++)
        {
            data[2 + i] = color.B;
            data[2 + LedsPerRow + i] = color.G;
            data[2 + 2 * LedsPerRow + i] = color.R;
        }
        return data;
    }

    private void Send(params byte[][] packets)
    {
        foreach (var p in packets)
        {
            device.SetFeature(p);
            Thread.Sleep(2);
        }
    }

    public void Dispose() => device.Dispose();
}

/// <summary>Persisted light bar configuration (experimental).</summary>
public sealed record LightbarSettings
{
    /// <summary>"static" (one color), "effect" (firmware effect) or "off".</summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = "static";
    [JsonPropertyName("color")] public string Color { get; init; } = "FFFFFF";
    [JsonPropertyName("brightness")] public int Brightness { get; init; } = 3;
    [JsonPropertyName("effect")] public string Effect { get; init; } = nameof(Ite8291.Effect.Wave);
    [JsonPropertyName("speed")] public int Speed { get; init; } = 2;

    public void Validate()
    {
        if (Mode is not ("static" or "effect" or "off")) throw new ArgumentException($"Invalid light bar mode: {Mode}");
        if (Brightness is < 0 or > 4) throw new ArgumentException("Brightness must be between 0 and 4.");
        if (Speed is < 0 or > 4) throw new ArgumentException("Speed must be between 0 and 4.");
        KeyboardSettings.ParseColor(Color);
        if (!Enum.TryParse<Ite8291.Effect>(Effect, true, out var e) || !Lightbar.Effects.Contains(e))
            throw new ArgumentException($"Light bar effect not supported: {Effect}");
    }

    public void ApplyTo(Lightbar bar, bool save = true)
    {
        Validate();
        var color = KeyboardSettings.ParseColor(Color);
        switch (Mode)
        {
            case "off": bar.Off(); break;
            case "static": bar.Static(color, Brightness, save); break;
            case "effect":
                var effect = Enum.Parse<Ite8291.Effect>(Effect, true);
                var single = effect is Ite8291.Effect.Breathing or Ite8291.Effect.Flash;
                bar.Effect(effect, Speed, Brightness, Ite8291.Direction.LeftToRight, single ? color : null, save);
                break;
        }
    }
}
