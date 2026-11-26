namespace AvellCC.Core.Fan;

/// <summary>
/// Fan table backups in %ProgramData%\AvellCC\backup.
/// Rule: fan-table.original.json is created ONCE and never overwritten (docs/ARCHITECTURE.md, Rule 2).
/// </summary>
public sealed class FanTableBackupStore(string? directory = null)
{
    public const string OriginalFileName = "fan-table.original.json";

    public string Directory { get; } = directory ?? BackupFiles.DefaultDirectory;
    public string OriginalPath => Path.Combine(Directory, OriginalFileName);
    public bool HasOriginal => File.Exists(OriginalPath);

    /// <summary>Reads and validates the original. Throws if missing or corrupt.</summary>
    public FanTableSnapshot LoadOriginal()
    {
        var snapshot = BackupFiles.Load<FanTableSnapshot>(OriginalPath);
        snapshot.Validate();
        return snapshot;
    }

    /// <summary>Creates the original if it does not exist yet. Returns true if it was created now.</summary>
    public bool TryCreateOriginal(FanTableSnapshot snapshot)
    {
        snapshot.Validate();
        return BackupFiles.TryCreateReadOnly(OriginalPath, snapshot);
    }

    /// <summary>Timestamped snapshot: one before every write. Returns the path.</summary>
    public string SaveSnapshot(FanTableSnapshot snapshot, string reason)
    {
        snapshot.Validate();
        return BackupFiles.SaveTimestamped(Directory, "fan-table", snapshot.CreatedAt, reason, snapshot);
    }
}
