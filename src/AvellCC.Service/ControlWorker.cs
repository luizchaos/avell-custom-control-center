using AvellCC.Ipc;

namespace AvellCC.Service;

/// <summary>Loop do serviço: inicializa, roda o pipe para a UI e chama o ciclo das ventoinhas a cada 2 s.</summary>
public sealed class ControlWorker(ControlHub hub, HubOptions options, ILogger<ControlWorker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        hub.Initialize();
        var server = new PipeServer(options.PipeName, hub.HandleAsync, ex => log.LogWarning("Comando da UI falhou: {Message}", ex.Message));
        var serverTask = Task.Run(() => server.RunAsync(stoppingToken), stoppingToken);

        try
        {
            using var timer = new PeriodicTimer(Interval);
            do
            {
                hub.Tick();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // parada normal
        }
        finally
        {
            hub.Shutdown();
            try { await serverTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }
}
