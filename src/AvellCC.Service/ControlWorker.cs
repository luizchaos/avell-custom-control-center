using AvellCC.Ipc;

namespace AvellCC.Service;

/// <summary>Service loop: initializes, runs the UI pipe and calls the fan cycle every 2 s.</summary>
public sealed class ControlWorker(ControlHub hub, HubOptions options, ILogger<ControlWorker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        hub.Initialize();
        var server = new PipeServer(options.PipeName, hub.HandleAsync, ex => log.LogWarning("UI command failed: {Message}", ex.Message));
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
            // normal stop
        }
        finally
        {
            hub.Shutdown();
            try { await serverTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }
}
