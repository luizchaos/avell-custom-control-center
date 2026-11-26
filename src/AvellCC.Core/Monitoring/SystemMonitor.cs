using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AvellCC.Core.Monitoring;

/// <summary>
/// CPU, GPU, memory, disk, network and power readings for the dashboard. Runs in the UI process as a normal user:
/// it only reads public Windows counters and never touches the EC (temperatures and fans come from the service).
/// Call <see cref="Sample"/> periodically (e.g. every second) from one thread at a time; usage values are averages
/// since the previous call, so the first sample reports 0 %.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private const string GpuEngine = @"\GPU Engine(*)\Utilization Percentage";
    private const string GpuMemory = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string DiskIdle = @"\PhysicalDisk(*)\% Idle Time";

    private static readonly string[] VirtualAdapterHints =
        ["virtual", "hyper-v", "vmware", "virtualbox", "tap-", "wan miniport", "bluetooth", "loopback", "pseudo", "vpn", "wireguard", "tailscale"];

    private static readonly HashSet<string> DiskFormats = new(["NTFS", "ReFS", "exFAT"], StringComparer.OrdinalIgnoreCase);

    private readonly PdhQuery? _pdh;
    private Native.FILETIME _idle, _kernel, _user;
    private readonly Dictionary<string, (long Rx, long Tx)> _netBytes = [];
    private long _netTimestamp;

    public SystemInfo Info { get; }

    public SystemMonitor()
    {
        Info = new SystemInfo(ReadCpuName(), ReadInstalledMemory(), GpuAdapters.Enumerate());
        try
        {
            _pdh = new PdhQuery();
            _pdh.TryAdd(GpuEngine);
            _pdh.TryAdd(GpuMemory);
            _pdh.TryAdd(DiskIdle);
            _pdh.Collect();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException)
        {
            _pdh = null;   // counters unavailable (e.g. disabled performance counters): usage shows as "—"
        }
        Native.GetSystemTimes(out _idle, out _kernel, out _user);
        SampleNetwork();
    }

    public SystemSnapshot Sample()
    {
        _pdh?.Collect();
        return new SystemSnapshot(
            SampleCpu(),
            SampleGpus(),
            SampleMemory(),
            _pdh is null ? null : MonitorMath.DiskActivePercent(_pdh.Read(DiskIdle)),
            SampleDrives(),
            SampleNetwork(),
            SamplePower());
    }

    public void Dispose() => _pdh?.Dispose();

    // ------------------------------------------------------------------ static info

    private static string ReadCpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = key?.GetValue("ProcessorNameString") as string;
        return string.IsNullOrWhiteSpace(name) ? $"CPU ({Environment.ProcessorCount} threads)" : string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static ulong ReadInstalledMemory()
    {
        if (Native.GetPhysicallyInstalledSystemMemory(out var kb)) return kb * 1024;
        var status = new Native.MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        return Native.GlobalMemoryStatusEx(ref status) ? status.TotalPhys : 0;
    }

    // ------------------------------------------------------------------ samples

    private double SampleCpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var percent = MonitorMath.CpuPercent(_idle.Value, _kernel.Value, _user.Value, idle.Value, kernel.Value, user.Value);
        (_idle, _kernel, _user) = (idle, kernel, user);
        return percent;
    }

    private List<GpuLoad> SampleGpus()
    {
        var usage = _pdh is null ? [] : MonitorMath.GpuPercent(_pdh.Read(GpuEngine));
        var vram = _pdh is null ? [] : MonitorMath.VramUsed(_pdh.Read(GpuMemory));
        return Info.Gpus.Select(a => new GpuLoad(a, usage.GetValueOrDefault(a.Luid), vram.TryGetValue(a.Luid, out var v) ? v : null)).ToList();
    }

    private static MemoryUsage SampleMemory()
    {
        var status = new Native.MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        return Native.GlobalMemoryStatusEx(ref status) ? new MemoryUsage(status.TotalPhys, status.AvailPhys) : new MemoryUsage(0, 0);
    }

    private static List<DriveUsage> SampleDrives()
    {
        var result = new List<DriveUsage>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                // Fixed disks only. Cloud drives (Google Drive, ...) also report "Fixed" but as FAT32 → filtered by format.
                if (d.DriveType != DriveType.Fixed || !d.IsReady || !DiskFormats.Contains(d.DriveFormat)) continue;
                result.Add(new DriveUsage(d.Name.TrimEnd('\\'), d.VolumeLabel, (ulong)d.TotalSize, (ulong)d.TotalFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // drive removed or locked (BitLocker) between listing and reading
            }
        }
        return result;
    }

    private List<NetworkRate> SampleNetwork()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _netTimestamp == 0 ? 0 : (now - _netTimestamp) / (double)Stopwatch.Frequency;
        _netTimestamp = now;

        var result = new List<NetworkRate>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return result; }

        foreach (var nic in interfaces)
        {
            var wireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            if (!wireless && nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
            var description = nic.Description.ToLowerInvariant();
            if (VirtualAdapterHints.Any(description.Contains)) continue;

            var up = nic.OperationalStatus == OperationalStatus.Up;
            double down = 0, upload = 0;
            if (up)
            {
                var stats = nic.GetIPStatistics();
                var bytes = (stats.BytesReceived, stats.BytesSent);
                if (seconds > 0 && _netBytes.TryGetValue(nic.Id, out var previous))
                {
                    down = Math.Max(0, bytes.BytesReceived - previous.Rx) / seconds;
                    upload = Math.Max(0, bytes.BytesSent - previous.Tx) / seconds;
                }
                _netBytes[nic.Id] = bytes;
            }
            else
            {
                _netBytes.Remove(nic.Id);
            }
            result.Add(new NetworkRate(nic.Id, nic.Name, wireless, up, down, upload));
        }
        return result.OrderBy(n => n.IsWireless).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static PowerState SamplePower()
    {
        string? plan = null;
        if (Native.PowerGetActiveScheme(IntPtr.Zero, out var scheme) == 0)
        {
            try
            {
                uint size = 0;
                Native.PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
                if (size > 0)
                {
                    var buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (Native.PowerReadFriendlyName(IntPtr.Zero, scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0)
                            plan = Marshal.PtrToStringUni(buffer);
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            finally { Native.LocalFree(scheme); }
        }

        var mode = PowerMode.Unknown;
        try
        {
            if (Native.PowerGetEffectiveOverlayScheme(out var overlay) == 0) mode = MonitorMath.ModeFromOverlay(overlay);
        }
        catch (EntryPointNotFoundException)
        {
            // older Windows: no power mode slider
        }

        bool? onAc = null;
        int? battery = null;
        if (Native.GetSystemPowerStatus(out var power))
        {
            onAc = power.ACLineStatus switch { 0 => false, 1 => true, _ => null };
            if (power.BatteryLifePercent <= 100 && (power.BatteryFlag & 128) == 0) battery = power.BatteryLifePercent;
        }
        return new PowerState(plan, mode, onAc, battery);
    }
}
