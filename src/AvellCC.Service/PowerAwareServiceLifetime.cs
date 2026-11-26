using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace AvellCC.Service;

/// <summary>
/// Ciclo de vida do serviço Windows com eventos de energia: ao suspender, devolve as ventoinhas ao firmware;
/// ao retomar, reassume (como o tuxedo-drivers faz no suspend/resume).
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
