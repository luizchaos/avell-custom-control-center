namespace AvellCC.Core.Keyboard;

/// <summary>
/// ITE 8291 controller protocol (048D:CE00, interface MI_01, usage page 0xFF12 = 4 zones).
/// 9-byte feature report: [0] = report ID 0x00, [1] = command, [2..8] = arguments.
/// Source: LightingModel.LM_ITE_RGB / ITE_SPEC in the vendor's GCUService.exe. See docs/HARDWARE.md §3.
/// This class only builds packets (pure, testable). Sending is done by a separate HID device.
/// </summary>
public static class Ite8291
{
    public const ushort VendorId = 0x048D;
    public const ushort ProductId4Zone = 0xCE00;
    public const ushort UsagePage4Zone = 0xFF12;
    /// <summary>Front light bar: a second ITE controller (048D:6005, vendor name "MEZone_Lighbar") with the same commands.</summary>
    public const ushort ProductIdLightbar = 0x6005;
    public const ushort UsagePageLightbar = 0xFF03;
    public const int FeatureReportLength = 9;
    public const int Zones = 4;

    public const byte CmdSetEffect = 0x08;
    public const byte CmdSetBrightness = 0x09;
    public const byte CmdSetColor = 0x14;
    public const byte CmdGetFirmware = 0x80;
    public const byte CmdGetEffect = 0x88;

    /// <summary>"ctrl" byte of command 0x08.</summary>
    public enum Control : byte { Off = 1, Apply = 2, Welcome = 3, Night = 4 }

    public enum Effect : byte
    {
        Static = 0x01, Breathing = 0x02, Wave = 0x03, Reactive = 0x04, Rainbow = 0x05, Ripple = 0x06,
        Marquee = 0x09, Raindrop = 0x0A, Stack = 0x0C, Impact = 0x0D, Aurora = 0x0E, Neon = 0x0F,
        Spark = 0x11, Flash = 0x12, Mix = 0x13, Gaming = 0x15, RippleO = 0x16, Alphabet = 0x17,
        StarSpark = 0x18, StarHitting = 0x19, Thinking = 0x21, Music = 0x22, UserMode = 0x33,
    }

    public enum Direction : byte { LeftToRight = 0, RightToLeft = 1, BottomToTop = 2, TopToBottom = 3, OnKeyPress = 4 }

    /// <summary>UI level 0..4 → firmware value (max 0x32).</summary>
    public static readonly byte[] BrightnessSteps = [0, 8, 22, 36, 50];

    /// <summary>UI level 0..4 (slow → fast) → firmware value.</summary>
    public static readonly byte[] SpeedSteps = [10, 7, 5, 3, 1];

    public static byte[] SetEffect(Control control, Effect effect, int speedLevel, int brightnessLevel, byte color, Direction direction, bool save) =>
        Packet(CmdSetEffect, (byte)control, (byte)effect, SpeedSteps[Level(speedLevel)], BrightnessSteps[Level(brightnessLevel)],
               color, (byte)direction, save ? (byte)1 : (byte)0);

    public static byte[] TurnOff() => Packet(CmdSetEffect, (byte)Control.Off);

    public static byte[] SetBrightness(int brightnessLevel) => Packet(CmdSetBrightness, 0x02, BrightnessSteps[Level(brightnessLevel)]);

    /// <summary>Color of a zone (1..4), with the vendor's color correction applied.</summary>
    public static byte[] SetZoneColor(int zone, Rgb color, byte projectId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(zone, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zone, Zones);
        return SetSlotColor(zone, CorrectColor4Zone(projectId, color));
    }

    /// <summary>Raw color of a slot (1..7), uncorrected. Slots 1..4 = zones; 1..N = effect palette.</summary>
    public static byte[] SetSlotColor(int slot, Rgb color)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slot, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, MaxColorSlots);
        return Packet(CmdSetColor, 0x00, (byte)slot, color.R, color.G, color.B);
    }

    public const int MaxColorSlots = 7;

    public static byte[] QueryFirmware() => Packet(CmdGetFirmware);
    public static byte[] QueryEffect() => Packet(CmdGetEffect);

    /// <summary>
    /// WKDColor.cheatRGB_4Zone: the green and blue LEDs are stronger, so the vendor attenuates G and B,
    /// with special cases for orange and yellow. Confirmed visually on project ID 0x0F (white looks white).
    /// </summary>
    public static Rgb CorrectColor4Zone(byte projectId, Rgb c)
    {
        if (projectId is 6 or 7)
        {
            if (c is { R: 255, G: 165, B: 0 }) return new Rgb(255, 60, 0);
            if (c is { R: 255, G: 255, B: 0 }) return new Rgb(255, 120, 0);
            return new Rgb(c.R, (byte)(120 * c.G / 255), (byte)(220 * c.B / 255));
        }
        if (c is { R: 255, G: 100, B: 0 }) return new Rgb(255, 40, 0);
        if (c is { R: 255, G: 169, B: 0 }) return new Rgb(255, 70, 0);
        return new Rgb(c.R, (byte)(100 * c.G / 255), (byte)(200 * c.B / 255));
    }

    private static int Level(int level) => Math.Clamp(level, 0, 4);

    private static byte[] Packet(byte command, params byte[] args)
    {
        if (args.Length > FeatureReportLength - 2) throw new ArgumentException("Too many arguments.", nameof(args));
        var packet = new byte[FeatureReportLength];
        packet[1] = command;
        args.CopyTo(packet, 2);
        return packet;
    }
}

public readonly record struct Rgb(byte R, byte G, byte B);
