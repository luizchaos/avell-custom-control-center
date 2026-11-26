using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AvellCC.Core.Control;
using AvellCC.Core.Fan;
using AvellCC.Core.Keyboard;

namespace AvellCC.Ipc;

/// <summary>Cliente do serviço (usado pela UI). Mantém uma conexão e reconecta se cair. Thread-safe.</summary>
public sealed class AvellClient(string pipeName = Protocol.PipeName, int connectTimeoutMs = 1500) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;

    public Task<ServiceInfo> InfoAsync(CancellationToken c = default) => CallAsync<ServiceInfo>(Protocol.Commands.Info, null, c);
    public Task<ControlStatus> StatusAsync(CancellationToken c = default) => CallAsync<ControlStatus>(Protocol.Commands.Status, null, c);
    public Task<AppSettings> GetSettingsAsync(CancellationToken c = default) => CallAsync<AppSettings>(Protocol.Commands.GetSettings, null, c);
    public Task<FanCurve[]> ListCurvesAsync(CancellationToken c = default) => CallAsync<FanCurve[]>(Protocol.Commands.ListCurves, null, c);
    public Task SaveCurveAsync(FanCurve curve, CancellationToken c = default) => CallAsync<JsonElement?>(Protocol.Commands.SaveCurve, curve, c);
    public Task<AppSettings> SetFanModeAsync(string mode, string? curveName, CancellationToken c = default) =>
        CallAsync<AppSettings>(Protocol.Commands.SetFanMode, new SetFanModeArgs(mode, curveName), c);
    public Task<AppSettings> SetKeyboardAsync(KeyboardSettings keyboard, CancellationToken c = default) =>
        CallAsync<AppSettings>(Protocol.Commands.SetKeyboard, keyboard, c);

    public async Task<T> CallAsync<T>(string command, object? args, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await EnsureConnectedAsync(cancel).ConfigureAwait(false);
                    var request = new Request(++_nextId, command, args is null ? null : JsonSerializer.SerializeToElement(args, Protocol.Json));
                    await _writer!.WriteLineAsync(JsonSerializer.Serialize(request, Protocol.Json).AsMemory(), cancel).ConfigureAwait(false);
                    var line = await _reader!.ReadLineAsync(cancel).ConfigureAwait(false) ?? throw new IOException("Conexão encerrada pelo serviço.");
                    var response = JsonSerializer.Deserialize<Response>(line, Protocol.Json) ?? throw new IOException("Resposta vazia.");
                    if (!response.Ok) throw new ServiceException(response.Error ?? "Erro desconhecido no serviço.");
                    return response.Result is { } r ? r.Deserialize<T>(Protocol.Json)! : default!;
                }
                catch (IOException) when (attempt == 0)
                {
                    Disconnect();   // conexão velha: tenta uma vez de novo
                }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    Disconnect();
                    throw new ServiceUnavailableException(ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancel)
    {
        if (_pipe is { IsConnected: true }) return;
        Disconnect();
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(connectTimeoutMs, cancel).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    private void Disconnect()
    {
        _reader?.Dispose(); _writer?.Dispose(); _pipe?.Dispose();
        _reader = null; _writer = null; _pipe = null;
    }

    public void Dispose()
    {
        Disconnect();
        _gate.Dispose();
    }
}

public sealed class ServiceUnavailableException(Exception inner)
    : Exception("Serviço AvellCC indisponível. Ele está instalado e rodando?", inner);
