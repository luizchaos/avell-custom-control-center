using System.Text.Json;
using AvellCC.Core.Control;
using AvellCC.Core.Fan;
using AvellCC.Ipc;

namespace AvellCC.Core.Tests;

public sealed class IpcTests : IAsyncLifetime
{
    private readonly string _pipe = "AvellCC.Test." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _cts = new();
    private Task? _server;
    private string? _lastMode;

    public Task InitializeAsync()
    {
        var server = new PipeServer(_pipe, Handle);
        _server = Task.Run(() => server.RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private Task<object?> Handle(string command, JsonElement? args, CancellationToken _) => Task.FromResult<object?>(command switch
    {
        Protocol.Commands.Status => new ControlStatus { State = LoopState.Curve, CpuTemp = 61, Fan1Rpm = 3200, CurveName = "balanced" },
        Protocol.Commands.ListCurves => CurvePresets.All,
        Protocol.Commands.SetFanMode => SetMode(args!.Value.Deserialize<SetFanModeArgs>(Protocol.Json)!),
        Protocol.Commands.FreeMemory => new AvellCC.Core.Maintenance.MemoryCleanResult(64UL << 30, 16UL << 30, 12UL << 30, "test"),
        _ => throw new ArgumentException($"Unknown command: {command}"),
    });

    private AppSettings SetMode(SetFanModeArgs a)
    {
        _lastMode = a.Mode;
        return new AppSettings { FanMode = a.Mode, CurveName = a.CurveName ?? "balanced" };
    }

    [Fact]
    public async Task Status_round_trips_with_enums_as_strings()
    {
        using var client = new AvellClient(_pipe);
        var s = await client.StatusAsync();
        Assert.Equal(LoopState.Curve, s.State);
        Assert.Equal(61, s.CpuTemp);
        Assert.Equal("balanced", s.CurveName);
    }

    [Fact]
    public async Task Curves_round_trip_with_points()
    {
        using var client = new AvellClient(_pipe);
        var curves = await client.ListCurvesAsync();
        Assert.Equal(CurvePresets.All.Count, curves.Length);
        Assert.Equal(CurvePresets.Balanced.Cpu, curves[0].Cpu);
    }

    [Fact]
    public async Task Commands_with_args_reach_the_handler_and_many_calls_share_a_connection()
    {
        using var client = new AvellClient(_pipe);
        var settings = await client.SetFanModeAsync("curve", "silent");
        Assert.Equal("curve", _lastMode);
        Assert.Equal("silent", settings.CurveName);
        for (var i = 0; i < 20; i++) await client.StatusAsync();
    }

    [Fact]
    public async Task Server_errors_become_ServiceException()
    {
        using var client = new AvellClient(_pipe);
        var ex = await Assert.ThrowsAsync<ServiceException>(() => client.CallAsync<object>("nope", null, default));
        Assert.Contains("Unknown command", ex.Message);
    }

    [Fact]
    public async Task Free_memory_result_round_trips()
    {
        using var client = new AvellClient(_pipe);
        var r = await client.FreeMemoryAsync();
        Assert.Equal(16UL << 30, r.InUseBefore);
        Assert.Equal(12UL << 30, r.InUseAfter);
        Assert.Equal(4UL << 30, r.Freed);
    }

    [Fact]
    public void Freed_memory_is_never_negative()
    {
        Assert.Equal(0UL, new AvellCC.Core.Maintenance.MemoryCleanResult(100, 40, 45, "x").Freed);
    }

    [Fact]
    public async Task Missing_service_is_reported_as_unavailable()
    {
        using var client = new AvellClient("AvellCC.NoSuchPipe." + Guid.NewGuid().ToString("N"), connectTimeoutMs: 200);
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.StatusAsync());
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_server is not null) await _server;
        _cts.Dispose();
    }
}
