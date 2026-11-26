using System.Diagnostics;

namespace AvellCC.Core.Safety;

/// <summary>
/// Rule 1 (docs/ARCHITECTURE.md): one controller at a time.
/// If Avell Custom Control is running, hardware writes are refused.
/// </summary>
public static class VendorGuard
{
    public static readonly string[] VendorProcessNames = ["GCUService", "GCUBridge", "ControlCenterU", "OSDTpDetect"];

    public const string HowToStop =
        "Stop Avell Custom Control (PowerShell as administrator):\n" +
        "  Get-Process ControlCenterU, OSDTpDetect, EnableTray -ErrorAction SilentlyContinue | Stop-Process -Force\n" +
        "  Stop-Service GCUBridge -Force; Set-Service GCUBridge -StartupType Disabled\n" +
        "Details: docs/ARCHITECTURE.md (Rule 1).";

    public static string[] RunningVendorProcesses() =>
        VendorProcessNames
            .Where(name => Process.GetProcessesByName(name).Length > 0)
            .ToArray();

    /// <summary>Throws if any vendor process is active. Call before ANY write.</summary>
    public static void EnsureVendorStopped()
    {
        var running = RunningVendorProcesses();
        if (running.Length > 0)
            throw new VendorRunningException(running);
    }
}

public sealed class VendorRunningException(string[] processes)
    : InvalidOperationException($"Another controller is running ({string.Join(", ", processes)}). Write refused.\n{VendorGuard.HowToStop}")
{
    public string[] Processes { get; } = processes;
}
