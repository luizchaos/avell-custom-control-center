using System.Text.Json;

namespace AvellCC.Core.Fan;

/// <summary>
/// Backups da tabela de ventoinha em %ProgramData%\AvellCC\backup.
/// Regra: fan-table.original.json é criado UMA vez e nunca é sobrescrito (docs/ARCHITECTURE.md, Regra 2).
/// </summary>
public sealed class FanTableBackupStore
{
    public const string OriginalFileName = "fan-table.original.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Directory { get; }
    public string OriginalPath => Path.Combine(Directory, OriginalFileName);

    public FanTableBackupStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AvellCC", "backup");
    }

    public bool HasOriginal => File.Exists(OriginalPath);

    /// <summary>Lê e valida o original. Lança se não existir ou estiver corrompido.</summary>
    public FanTableSnapshot LoadOriginal()
    {
        var snapshot = JsonSerializer.Deserialize<FanTableSnapshot>(File.ReadAllText(OriginalPath))
                       ?? throw new InvalidDataException("Backup original vazio.");
        snapshot.Validate();
        return snapshot;
    }

    /// <summary>
    /// Cria o original se ainda não existir. Retorna true se criou agora.
    /// Usa FileMode.CreateNew para nunca sobrescrever, nem em corrida com outro processo.
    /// </summary>
    public bool TryCreateOriginal(FanTableSnapshot snapshot)
    {
        snapshot.Validate();
        System.IO.Directory.CreateDirectory(Directory);
        try
        {
            using (var fs = new FileStream(OriginalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(fs, snapshot, Json);
        }
        catch (IOException) when (File.Exists(OriginalPath))
        {
            return false;
        }
        File.SetAttributes(OriginalPath, File.GetAttributes(OriginalPath) | FileAttributes.ReadOnly);
        return true;
    }

    /// <summary>Snapshot datado: um antes de cada escrita. Retorna o caminho.</summary>
    public string SaveSnapshot(FanTableSnapshot snapshot, string reason)
    {
        snapshot.Validate();
        System.IO.Directory.CreateDirectory(Directory);
        var safeReason = string.Concat(reason.Where(char.IsLetterOrDigit));
        var path = Path.Combine(Directory, $"fan-table.{snapshot.CreatedAt:yyyyMMdd-HHmmss-fff}.{safeReason}.json");
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(fs, snapshot, Json);
        return path;
    }
}
