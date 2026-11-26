using System.Runtime.InteropServices;

namespace AvellCC.Core.Monitoring;

/// <summary>
/// Thin wrapper over the Performance Data Helper (the API behind perfmon and Task Manager).
/// Counters are added by their English path and may use a wildcard instance (<c>(*)</c>); new instances
/// (e.g. a process that starts using the GPU) are picked up on the next collection.
/// Rate counters need two collections before they return data. Not thread-safe.
/// </summary>
public sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = [];

    public PdhQuery()
    {
        Check(Native.PdhOpenQueryW(null, IntPtr.Zero, out _query), "PdhOpenQuery");
    }

    /// <summary>Adds a counter; returns false if it does not exist on this machine.</summary>
    public bool TryAdd(string englishPath)
    {
        if (_counters.ContainsKey(englishPath)) return true;
        if (Native.PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out var counter) != 0) return false;
        _counters[englishPath] = counter;
        return true;
    }

    public void Collect() => Native.PdhCollectQueryData(_query);

    /// <summary>All valid instances of a counter as (instance name, value). Empty if there is no data yet.</summary>
    public IReadOnlyList<(string Instance, double Value)> Read(string englishPath)
    {
        if (!_counters.TryGetValue(englishPath, out var counter)) return [];
        const uint format = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;
        uint size = 0;
        var status = Native.PdhGetFormattedCounterArrayW(counter, format, ref size, out _, IntPtr.Zero);
        if (status != Native.PDH_MORE_DATA || size == 0) return [];

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = Native.PdhGetFormattedCounterArrayW(counter, format, ref size, out var count, buffer);
            if (status != 0) return [];
            var itemSize = Marshal.SizeOf<Native.PDH_FMT_COUNTERVALUE_ITEM>();
            var result = new List<(string, double)>((int)count);
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Native.PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.Value.CStatus is not (Native.PDH_CSTATUS_VALID_DATA or Native.PDH_CSTATUS_NEW_DATA)) continue;
                result.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.DoubleValue));
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        Native.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    private static void Check(uint status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"{what} failed (0x{status:X8}).");
    }
}
