namespace AvellCC.Core.Fan;

/// <summary>
/// Backups da tabela de ventoinha em %ProgramData%\AvellCC\backup.
/// Regra: fan-table.original.json é criado UMA vez e nunca é sobrescrito (docs/ARCHITECTURE.md, Regra 2).
/// </summary>
public sealed class FanTableBackupStore(string? directory = null)
{
    public const string OriginalFileName = "fan-table.original.json";

    public string Directory { get; } = directory ?? BackupFiles.DefaultDirectory;
    public string OriginalPath => Path.Combine(Directory, OriginalFileName);
    public bool HasOriginal => File.Exists(OriginalPath);

    /// <summary>Lê e valida o original. Lança se não existir ou estiver corrompido.</summary>
    public FanTableSnapshot LoadOriginal()
    {
        var snapshot = BackupFiles.Load<FanTableSnapshot>(OriginalPath);
        snapshot.Validate();
        return snapshot;
    }

    /// <summary>Cria o original se ainda não existir. Retorna true se criou agora.</summary>
    public bool TryCreateOriginal(FanTableSnapshot snapshot)
    {
        snapshot.Validate();
        return BackupFiles.TryCreateReadOnly(OriginalPath, snapshot);
    }

    /// <summary>Snapshot datado: um antes de cada escrita. Retorna o caminho.</summary>
    public string SaveSnapshot(FanTableSnapshot snapshot, string reason)
    {
        snapshot.Validate();
        return BackupFiles.SaveTimestamped(Directory, "fan-table", snapshot.CreatedAt, reason, snapshot);
    }
}
