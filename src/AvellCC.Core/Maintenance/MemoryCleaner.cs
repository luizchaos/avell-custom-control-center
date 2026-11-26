using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AvellCC.Core.Maintenance;

/// <summary>Result of "free up memory": RAM in use before and after (bytes).</summary>
public sealed record MemoryCleanResult(ulong Total, ulong InUseBefore, ulong InUseAfter, string Method)
{
    public ulong Freed => InUseBefore > InUseAfter ? InUseBefore - InUseAfter : 0;
}

public interface IMemoryCleaner
{
    MemoryCleanResult FreeUpMemory();
}

/// <summary>
/// "Free up memory" (like MSI Center): empties the working sets of all processes.
///
/// What it does and does not do (docs/CLEANUP.md):
/// - Trimmed pages are NOT discarded: they move to the standby/modified lists and are still in RAM, so a process
///   that needs them gets them back with a cheap soft fault. No data is lost and no process is closed.
/// - The file cache (standby list) is deliberately NOT purged: that would only slow the next app launches and
///   would not change the "in use" number anyway.
/// - The effect is mostly cosmetic: Windows trims working sets by itself when memory is actually needed.
///
/// Needs administrator rights (runs in the service as LocalSystem). Uses
/// <c>NtSetSystemInformation(SystemMemoryListInformation, MemoryEmptyWorkingSets)</c> — the call RAMMap's
/// "Empty Working Sets" uses — and falls back to <c>EmptyWorkingSet</c> per process.
/// </summary>
public sealed class MemoryCleaner : IMemoryCleaner
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryEmptyWorkingSets = 2;

    public MemoryCleanResult FreeUpMemory()
    {
        var (total, before) = InUse();
        string method;
        EnablePrivilege("SeProfileSingleProcessPrivilege");
        EnablePrivilege("SeIncreaseQuotaPrivilege");
        var command = MemoryEmptyWorkingSets;
        var status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        if (status == 0)
        {
            method = "system";
        }
        else
        {
            var trimmed = TrimEachProcess();
            method = $"per-process ({trimmed} processes; NtSetSystemInformation 0x{status:X8})";
        }
        var (_, after) = InUse();
        return new MemoryCleanResult(total, before, after, method);
    }

    private static int TrimEachProcess()
    {
        var count = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetQuota, false, process.Id);
                if (handle == IntPtr.Zero) continue;   // protected / system processes
                try
                {
                    if (K32EmptyWorkingSet(handle)) count++;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }
        return count;
    }

    internal static (ulong Total, ulong InUse) InUse()
    {
        var status = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        return (status.TotalPhys, status.TotalPhys - Math.Min(status.TotalPhys, status.AvailPhys));
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out var token)) return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return;
            var privileges = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ------------------------------------------------------------------ interop

    private const uint ProcessQueryLimitedInformation = 0x1000, ProcessSetQuota = 0x0100;
    private const uint TokenAdjustPrivileges = 0x0020, TokenQuery = 0x0008, SePrivilegeEnabled = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint length, IntPtr previous, IntPtr returnLength);
}

/// <summary>Simulation (service --simulate): pretends to free 18 % of the RAM in use, touches nothing.</summary>
public sealed class SimulatedMemoryCleaner : IMemoryCleaner
{
    public MemoryCleanResult FreeUpMemory()
    {
        var (total, inUse) = MemoryCleaner.InUse();
        return new MemoryCleanResult(total, inUse, (ulong)(inUse * 0.82), "simulated");
    }
}
