using System.Management;

namespace AvellCC.Core.Ec;

/// <summary>
/// Events the EC raises through ACPI WMI (<c>root\WMI AcpiTest_EventULong</c>, value in <c>ULong</c>) — the same source
/// the vendor's <c>WMIEC.WMIHandleEvent</c> listens to (Fn keys, OSD, light bar...). Needs administrator rights.
/// </summary>
public static class EcEvents
{
    /// <summary>Operational mode button (vendor "OSD_FanModeSwitch"). Captured on the A65 LIV: 0xAB per press.</summary>
    public const uint ModeButton = 0xAB;
}

public sealed class EcEventWatcher : IDisposable
{
    private readonly ManagementEventWatcher _watcher;

    /// <summary>Raised on a WMI thread with the event code.</summary>
    public event Action<uint>? EventReceived;

    public EcEventWatcher()
    {
        _watcher = new ManagementEventWatcher(new ManagementScope(@"\\.\root\WMI"), new WqlEventQuery("SELECT * FROM AcpiTest_EventULong"));
        _watcher.EventArrived += (_, e) =>
        {
            if (e.NewEvent["ULong"] is { } value) EventReceived?.Invoke(Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture));
        };
    }

    public void Start() => _watcher.Start();

    public void Dispose()
    {
        try { _watcher.Stop(); } catch (ManagementException) { }
        _watcher.Dispose();
    }
}
