using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace AvellCC.Service;

/// <summary>
/// Windows service lifetime with power events: on suspend, hands the fans back to the firmware;
/// on resume, takes control again (as tuxedo-drivers does on suspend/resume).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerAwareServiceLifetime : WindowsServiceLifetime
{
    private readonly ControlHub _hub;

    public PowerAwareServiceLifetime(ControlHub hub, IHostEnvironment environment, IHostApplicationLifetime applicationLifetime,
                                     ILoggerFactory loggerFactory, IOptions<HostOptions> optionsAccessor,
                                     IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _hub = hub;
        CanHandlePowerEvent = true;
        CanShutdown = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                _hub.Suspend();
                break;
            case PowerBroadcastStatus.ResumeSuspend:
            case PowerBroadcastStatus.ResumeAutomatic:
                _hub.Resume();
                break;
        }
        return base.OnPowerEvent(powerStatus);
    }

    protected override void OnShutdown()
    {
        _hub.Shutdown();
        base.OnShutdown();
    }
}
