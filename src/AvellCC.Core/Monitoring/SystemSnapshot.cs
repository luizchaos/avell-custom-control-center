using System.Globalization;
using System.Text.RegularExpressions;

namespace AvellCC.Core.Monitoring;

/// <summary>Things that do not change while the app runs.</summary>
public sealed record SystemInfo(string CpuName, ulong InstalledMemory, IReadOnlyList<GpuAdapter> Gpus);

public sealed record GpuLoad(GpuAdapter Adapter, double Percent, ulong? VramUsed);

public sealed record MemoryUsage(ulong Total, ulong Available)
{
    public ulong Used => Total - Math.Min(Total, Available);
    public double Percent => Total == 0 ? 0 : 100.0 * Used / Total;
}

public sealed record DriveUsage(string Name, string Label, ulong Total, ulong Free)
{
    public ulong Used => Total - Math.Min(Total, Free);
    public double Percent => Total == 0 ? 0 : 100.0 * Used / Total;
}

public sealed record NetworkRate(string Id, string Name, bool IsWireless, bool IsUp, double DownBytesPerSecond, double UpBytesPerSecond);

/// <summary>Windows 10/11 "power mode" slider (Settings → System → Power).</summary>
public enum PowerMode { Unknown, BestEfficiency, Balanced, BetterPerformance, BestPerformance }

public sealed record PowerState(string? PlanName, PowerMode Mode, bool? OnAc, int? BatteryPercent);

public sealed record SystemSnapshot(
    double CpuPercent,
    IReadOnlyList<GpuLoad> Gpus,
    MemoryUsage Memory,
    double? DiskActivePercent,
    IReadOnlyList<DriveUsage> Drives,
    IReadOnlyList<NetworkRate> Networks,
    PowerState Power);

/// <summary>Pure calculations behind the monitor (unit-tested).</summary>
public static partial class MonitorMath
{
    /// <summary>CPU busy % between two GetSystemTimes samples (kernel time includes idle time).</summary>
    public static double CpuPercent(ulong idle0, ulong kernel0, ulong user0, ulong idle1, ulong kernel1, ulong user1)
    {
        var idle = idle1 - idle0;
        var total = (kernel1 - kernel0) + (user1 - user0);
        if (total == 0 || idle > total) return 0;
        return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }

    /// <summary>LUID in a GPU counter instance ("pid_1_luid_0x00000000_0x0000EADD_phys_0_eng_3_engtype_3D").</summary>
    public static long? ParseLuid(string instance)
    {
        var m = LuidPattern().Match(instance);
        if (!m.Success) return null;
        var high = uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var low = uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((long)high << 32) | low;
    }

    /// <summary>
    /// GPU usage per adapter, the way Task Manager computes it: sum the utilization of each engine over all processes,
    /// then take the busiest engine (3D, copy, video decode...) of each adapter. Capped at 100 %.
    /// </summary>
    public static Dictionary<long, double> GpuPercent(IEnumerable<(string Instance, double Value)> engineSamples)
    {
        var perEngine = new Dictionary<(long Luid, string Engine), double>();
        foreach (var (instance, value) in engineSamples)
        {
            if (ParseLuid(instance) is not { } luid) continue;
            var engine = EnginePattern().Match(instance);
            var key = (luid, engine.Success ? engine.Value.ToLowerInvariant() : "");
            perEngine[key] = perEngine.GetValueOrDefault(key) + Math.Max(0, value);
        }
        return perEngine.GroupBy(kv => kv.Key.Luid)
                        .ToDictionary(g => g.Key, g => Math.Min(100, g.Max(kv => kv.Value)));
    }

    /// <summary>Dedicated video memory in use per adapter (sum over its physical nodes).</summary>
    public static Dictionary<long, ulong> VramUsed(IEnumerable<(string Instance, double Value)> memorySamples)
    {
        var result = new Dictionary<long, ulong>();
        foreach (var (instance, value) in memorySamples)
            if (ParseLuid(instance) is { } luid)
                result[luid] = result.GetValueOrDefault(luid) + (ulong)Math.Max(0, value);
        return result;
    }

    /// <summary>Highest active time among physical disks, from "% Idle Time" samples (the "_Total" instance is ignored).</summary>
    public static double? DiskActivePercent(IEnumerable<(string Instance, double Value)> idleSamples)
    {
        var active = idleSamples.Where(s => s.Instance != "_Total").Select(s => Math.Clamp(100 - s.Value, 0, 100)).ToList();
        return active.Count == 0 ? null : active.Max();
    }

    public static PowerMode ModeFromOverlay(Guid overlay) => overlay.ToString().ToLowerInvariant() switch
    {
        "961cc777-2547-4f9d-8174-7d86181b8a7a" => PowerMode.BestEfficiency,
        "00000000-0000-0000-0000-000000000000" => PowerMode.Balanced,
        "381b4222-f694-41f0-9685-ff5bb260df2e" => PowerMode.Balanced,
        "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => PowerMode.BetterPerformance,
        "ded574b5-45a0-4f42-8737-46345c09c238" => PowerMode.BestPerformance,
        _ => PowerMode.Unknown,
    };

    /// <summary>"512 MB", "1.5 GB", "2 TB" (binary units, like Windows Explorer).</summary>
    public static string Bytes(double bytes, IFormatProvider? culture = null)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (Math.Abs(bytes) >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        var format = i == 0 || bytes >= 100 ? "0" : "0.#";
        return bytes.ToString(format, culture ?? CultureInfo.CurrentCulture) + " " + units[i];
    }

    public static string Rate(double bytesPerSecond, IFormatProvider? culture = null) => Bytes(bytesPerSecond, culture) + "/s";

    [GeneratedRegex(@"luid_0x([0-9a-fA-F]{8})_0x([0-9a-fA-F]{8})")] private static partial Regex LuidPattern();
    [GeneratedRegex(@"phys_\d+_eng_\d+")] private static partial Regex EnginePattern();
}
