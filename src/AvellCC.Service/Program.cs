using AvellCC.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

// Serviço Windows "AvellCC" (LocalSystem): único dono do EC e do teclado.
//   AvellCC.Service.exe              → como serviço (instalado por tools/install.ps1)
//   AvellCC.Service.exe --simulate   → console, EC simulado, pipe "AvellCC.Control.sim" (desenvolvimento da UI, sem admin)

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
