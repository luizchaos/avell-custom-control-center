using System.Diagnostics;

namespace AvellCC.Core.Safety;

/// <summary>
/// Regra 1 (docs/ARCHITECTURE.md): só um controlador por vez.
/// Se o Avell Custom Control estiver rodando, escritas no hardware são recusadas.
/// </summary>
public static class VendorGuard
{
    public static readonly string[] VendorProcessNames = ["GCUService", "GCUBridge", "ControlCenterU", "OSDTpDetect"];

    public const string HowToStop =
        "Pare o Avell Custom Control (PowerShell como admin):\n" +
        "  Get-Process ControlCenterU, OSDTpDetect, EnableTray -ErrorAction SilentlyContinue | Stop-Process -Force\n" +
        "  Stop-Service GCUBridge -Force; Set-Service GCUBridge -StartupType Disabled\n" +
        "Detalhes: docs/ARCHITECTURE.md (Regra 1).";

    public static string[] RunningVendorProcesses() =>
        VendorProcessNames
            .Where(name => Process.GetProcessesByName(name).Length > 0)
            .ToArray();

    /// <summary>Lança se algum processo do vendor estiver ativo. Chamar antes de QUALQUER escrita.</summary>
    public static void EnsureVendorStopped()
    {
        var running = RunningVendorProcesses();
        if (running.Length > 0)
            throw new VendorRunningException(running);
    }
}

public sealed class VendorRunningException(string[] processes)
    : InvalidOperationException($"Avell Custom Control em execução ({string.Join(", ", processes)}). Escrita recusada.\n{VendorGuard.HowToStop}")
{
    public string[] Processes { get; } = processes;
}
