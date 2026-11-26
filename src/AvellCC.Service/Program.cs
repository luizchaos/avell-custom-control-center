using AvellCC.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

// Windows service "AvellCC" (LocalSystem): single owner of the EC and keyboard.
//   AvellCC.Service.exe              → as a service (installed by tools/install.ps1)
//   AvellCC.Service.exe --simulate   → console, simulated EC, pipe "AvellCC.Control.sim" (UI development, no admin)

var simulate = args.Contains("--simulate");
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "AvellCC");
builder.Services.AddSingleton(new HubOptions(simulate));
builder.Services.AddSingleton<ControlHub>();
builder.Services.AddHostedService<ControlWorker>();
if (WindowsServiceHelpers.IsWindowsService())
    builder.Services.AddSingleton<IHostLifetime, PowerAwareServiceLifetime>();

builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));

var host = builder.Build();
host.Run();
