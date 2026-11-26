using System.Security.Cryptography;
using System.Text.Json.Serialization;
using AvellCC.Core.Ec;

namespace AvellCC.Core.Fan;

/// <summary>
/// Raw image of the EC fan table (0xF00–0xF5F) plus metadata.
/// Same JSON schema as written by tools/diag/Invoke-AvellDiag.ps1 ("avellcc.fan-table.v1").
/// </summary>
public sealed partial record FanTableSnapshot
{
    public const string SchemaId = "avellcc.fan-table.v1";

    [JsonPropertyName("schema")] public string Schema { get; init; } = SchemaId;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("createdBy")] public string CreatedBy { get; init; } = "AvellCC";
    [JsonPropertyName("machine")] public MachineInfo Machine { get; init; } = new();
    [JsonPropertyName("vendorRunning")] public string[] VendorRunning { get; init; } = [];
    [JsonPropertyName("baseAddress")] public string BaseAddress { get; init; } = "0x0F00";
    [JsonPropertyName("rawHex")] public string RawHex { get; init; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("modeRegisters")] public Dictionary<string, string> ModeRegisters { get; init; } = [];

    [JsonIgnore] public byte[] Raw => Convert.FromHexString(RawHex);

    public static FanTableSnapshot FromRaw(byte[] raw, MachineInfo machine, IReadOnlyDictionary<ushort, byte> modes, string[] vendorRunning, string createdBy)
    {
        if (raw.Length != EcAddresses.FanTableLength)
            throw new ArgumentException($"The table must be {EcAddresses.FanTableLength} bytes, got {raw.Length}.", nameof(raw));
        return new FanTableSnapshot
        {
            CreatedAt = DateTimeOffset.Now,
            CreatedBy = createdBy,
            Machine = machine,
            VendorRunning = vendorRunning,
            RawHex = Convert.ToHexString(raw),
            Sha256 = ComputeSha256(raw),
            ModeRegisters = modes.ToDictionary(kv => $"0x{kv.Key:X3}", kv => $"0x{kv.Value:X2}"),
        };
    }

    public static string ComputeSha256(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw));

    /// <summary>Checks schema, size and hash. Throws if the file is corrupt.</summary>
    public void Validate()
    {
        if (Schema != SchemaId) throw new InvalidDataException($"Unknown schema: {Schema}");
        var raw = Raw;
        if (raw.Length != EcAddresses.FanTableLength) throw new InvalidDataException($"Invalid size: {raw.Length}");
        if (!string.Equals(ComputeSha256(raw), Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 mismatch: corrupt backup.");
    }

    /// <summary>
    /// Saved values of the fan mode registers (EcAddresses.FanModeRegisters).
    /// Accepts both key formats: C# ("0x751") and the PowerShell script ("FAN_CONTROL_0x751").
    /// Extra registers written by the script (e.g. 0x782) are ignored.
    /// </summary>
    public IReadOnlyDictionary<ushort, byte> FanModeRegisterValues()
    {
        var result = new Dictionary<ushort, byte>();
        foreach (var (key, value) in ModeRegisters)
        {
            var match = AddressSuffix().Match(key);
            if (!match.Success) throw new InvalidDataException($"Invalid register key: {key}");
            var address = Convert.ToUInt16(match.Groups[1].Value, 16);
            if (!EcAddresses.FanModeRegisters.Contains(address)) continue;
            result[address] = Convert.ToByte(value.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
        }
        var missing = EcAddresses.FanModeRegisters.Where(a => !result.ContainsKey(a)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"Backup is missing mode registers: {string.Join(", ", missing.Select(a => $"0x{a:X3}"))}");
        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"0x([0-9A-Fa-f]{3,4})$")]
    private static partial System.Text.RegularExpressions.Regex AddressSuffix();

    [JsonIgnore] public FanLevel[] CpuLevels => FanLevel.Decode(Raw, 0x00, 0x10, 0x20);
    [JsonIgnore] public FanLevel[] GpuLevels => FanLevel.Decode(Raw, 0x30, 0x40, 0x50);
}

public sealed record MachineInfo
{
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; init; } = "";
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("bios")] public string Bios { get; init; } = "";
    [JsonPropertyName("projectId")] public string ProjectId { get; init; } = "";
}

/// <summary>Decoded view of one table level (raw bytes, as stored in the EC).</summary>
public readonly record struct FanLevel(int Level, byte UpRaw, byte DownRaw, byte DutyRaw)
{
    public double DutyPercent => DutyRaw / 2.0;

    internal static FanLevel[] Decode(byte[] raw, int upOffset, int downOffset, int dutyOffset)
    {
        var levels = new FanLevel[EcAddresses.FanLevels];
        for (var i = 0; i < levels.Length; i++)
            levels[i] = new FanLevel(i, raw[upOffset + i], raw[downOffset + i], raw[dutyOffset + i]);
        return levels;
    }
}
