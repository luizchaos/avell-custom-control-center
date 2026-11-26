namespace AvellCC.Core.Ec;

/// <summary>
/// EC RAM addresses of the Uniwill platform (Avell A65 LIV).
/// Sources: Define.ECSpec in the vendor's GCUService.exe, tuxedo-drivers, and hardware tests. See docs/HARDWARE.md.
/// </summary>
public static class EcAddresses
{
    // Identity / capabilities
    public const ushort ProjectId = 0x740;
    public const ushort ApOemByte = 0x741;
    public const ushort SupportByte1 = 0x765;
    public const ushort SupportByte2 = 0x766;
    public const ushort TriggerByte = 0x767;
    public const ushort StatusByte = 0x768;

    // Temperatures (°C, 1 byte). Confirmed 2026-11-26: 0x43E tracks the \_SB.ECTZ thermal zone (±1 °C),
    // 0x44F tracks nvidia-smi (a few seconds behind). These are what the EC uses to pick its curve level.
    public const ushort CpuTemp = 0x43E;
    public const ushort GpuTemp = 0x44F;

    // Fans — telemetry (hi, lo), RPM.
    public const ushort Fan1High = 0x464;
    public const ushort Fan1Low = 0x465;
    public const ushort Fan2High = 0x46C;
    // The vendor uses 0x46B, but on this model 0x46B is constant (0x78, or 0x64 with 0x751 = 0xA0). The real low byte
    // is 0x46D (confirmed 2026-11-26: with 0x46C/0x46D fan 2 matches fan 1, without 256-RPM steps).
    public const ushort Fan2Low = 0x46D;

    // Firmware's internal curve state (observed 2026-11-26): current level and that level's temperature threshold.
    public const ushort Fan1Level = 0x460;
    public const ushort Fan1LevelTemp = 0x461;
    public const ushort Fan2Level = 0x468;
    public const ushort Fan2LevelTemp = 0x469;

    // "MyFan2/MyFan3" (Office Mode Advanced): 5 speeds (% × 2) at the firmware's T1..T5. Ignored by this EC (tested).
    public const ushort OfficeAdvPwmStart = 0x743;        // 0x743..0x747
    public const int OfficeAdvPwmCount = 5;
    public const ushort OfficeAdvPwmDefaultStart = 0x786; // 0x786..0x78A (MyFan3 defaults)
    public const ushort OfficeAdvMinSpeed = 0x79E;
    public const ushort OfficeAdvMinTemp = 0x79F;
    public const ushort OfficeAdvExtraSpeed = 0x7A0;
    public const ushort BiosOemByte2 = 0x782;             // bit 3 = MyFan3 supported; bit 2 = office table type

    // Reference: tuxedo-drivers (Linux), uniwill_keyboard.h / tuxedo_io.c
    public const ushort CustomProfile = 0x727;       // bit 6 = custom profile mode (manual 0x743 curve on some models)
    public const byte CustomProfileBit = 0x40;
    public const ushort FanCtrlStatus = 0x78E;       // bit 6 = EC "universal" fan control (0xF00 tables). Here 0x0C → not present
    public const byte UniversalFanCtrlBit = 0x40;
    public const ushort DirectFan1Duty = 0x1804;     // direct (legacy) control: fan 1 duty 0..200
    public const ushort DirectFan2Duty = 0x1809;     // same, fan 2
    public const byte FullFanModeBit = 0x40;         // 0x751 bit 6: "full fan mode", required for direct control (= vendor Fan Boost)
    public const byte MaxDutyRaw = 0xC8;             // 200 = 100 %

    // Fans — mode
    public const ushort FanControl = 0x751;   // 0x40 = performance/boost, 0xA0 = standard/office
    public const ushort ApOemByte3 = 0x7A5;   // bit 3 = use the user table (ignored by this EC)
    public const ushort TurboLevel = 0x78C;
    public const ushort ModeProfileIndex = 0x7AB;

    // Fans — 16-level table (ignored by this EC; kept for backup/restore of the original state)
    public const ushort FanTableStart = 0xF00;
    public const int FanTableLength = 0x60;   // 0xF00..0xF5F
    public const int FanLevels = 16;
    public const ushort CpuUpTemp = 0xF00;
    public const ushort CpuDownTemp = 0xF10;
    public const ushort CpuDuty = 0xF20;
    public const ushort GpuUpTemp = 0xF30;
    public const ushort GpuDownTemp = 0xF40;
    public const ushort GpuDuty = 0xF50;

    /// <summary>Mode registers saved with every fan-table backup.</summary>
    public static readonly ushort[] FanModeRegisters = [FanControl, ApOemByte3, TurboLevel, ModeProfileIndex];
}
