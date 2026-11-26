using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AvellCC.Core.Fan;

/// <summary>Valores de um conjunto de registradores do EC, com hash, para backup e restauração exata.</summary>
public sealed record EcRegisterSnapshot
{
    public const string SchemaId = "avellcc.ec-registers.v1";

    [JsonPropertyName("schema")] public string Schema { get; init; } = SchemaId;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("createdBy")] public string CreatedBy { get; init; } = "AvellCC";
    [JsonPropertyName("machine")] public MachineInfo Machine { get; init; } = new();
    [JsonPropertyName("vendorRunning")] public string[] VendorRunning { get; init; } = [];
    [JsonPropertyName("registers")] public SortedDictionary<string, string> Registers { get; init; } = [];
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";

    public static EcRegisterSnapshot From(IReadOnlyDictionary<ushort, byte> values, MachineInfo machine, string[] vendorRunning, string createdBy)
    {
        var registers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (address, value) in values) registers[$"0x{address:X3}"] = $"0x{value:X2}";
        return new EcRegisterSnapshot
        {
            CreatedAt = DateTimeOffset.Now,
            CreatedBy = createdBy,
            Machine = machine,
            VendorRunning = vendorRunning,
            Registers = registers,
            Sha256 = Hash(registers),
        };
    }

    public IReadOnlyDictionary<ushort, byte> Values() =>
        Registers.ToDictionary(kv => Convert.ToUInt16(kv.Key[2..], 16), kv => Convert.ToByte(kv.Value[2..], 16));

    public void Validate()
    {
        if (Schema != SchemaId) throw new InvalidDataException($"Esquema desconhecido: {Schema}");
        if (Registers.Count == 0) throw new InvalidDataException("Backup sem registradores.");
        if (!string.Equals(Hash(Registers), Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 não confere: backup corrompido.");
        _ = Values(); // formato dos endereços/valores
    }

    private static string Hash(SortedDictionary<string, string> registers) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", registers.Select(kv => $"{kv.Key}={kv.Value}")))));
}

/// <summary>Backups de registradores. ec-registers.original.json é criado UMA vez e nunca é sobrescrito.</summary>
public sealed class EcRegisterBackupStore(string? directory = null)
{
    public const string OriginalFileName = "ec-registers.original.json";

    public string Directory { get; } = directory ?? BackupFiles.DefaultDirectory;
    public string OriginalPath => Path.Combine(Directory, OriginalFileName);
    public bool HasOriginal => File.Exists(OriginalPath);

    public EcRegisterSnapshot LoadOriginal()
    {
        var snapshot = BackupFiles.Load<EcRegisterSnapshot>(OriginalPath);
        snapshot.Validate();
        return snapshot;
    }

    public bool TryCreateOriginal(EcRegisterSnapshot snapshot)
    {
        snapshot.Validate();
        return BackupFiles.TryCreateReadOnly(OriginalPath, snapshot);
    }

    public string SaveSnapshot(EcRegisterSnapshot snapshot, string reason)
    {
        snapshot.Validate();
        return BackupFiles.SaveTimestamped(Directory, "ec-registers", snapshot.CreatedAt, reason, snapshot);
    }
}
