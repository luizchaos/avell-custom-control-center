using System.Text.Json;

namespace AvellCC.Core.Fan;

/// <summary>Escrita de backups: originais são criados uma única vez (CreateNew) e marcados como somente leitura.</summary>
internal static class BackupFiles
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AvellCC", "backup");

    /// <summary>Cria o arquivo se não existir. Retorna false se já existia (nunca sobrescreve).</summary>
    public static bool TryCreateReadOnly<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(fs, value, Json);
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        return true;
    }

    public static string SaveTimestamped<T>(string directory, string prefix, DateTimeOffset at, string reason, T value)
    {
        Directory.CreateDirectory(directory);
        var safeReason = string.Concat(reason.Where(char.IsLetterOrDigit));
        var path = Path.Combine(directory, $"{prefix}.{at:yyyyMMdd-HHmmss-fff}.{safeReason}.json");
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(fs, value, Json);
        return path;
    }

    public static T Load<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Backup vazio: {path}");
}
