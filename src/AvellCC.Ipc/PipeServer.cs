using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AvellCC.Ipc;

/// <summary>
/// Servidor do pipe. Cada conexão pode mandar várias requisições (uma por linha).
/// ACL: SYSTEM e Administradores (total), usuários interativos e o próprio usuário do processo (leitura/escrita).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeServer(string pipeName, Func<string, JsonElement?, CancellationToken, Task<object?>> handler, Action<Exception>? onError = null)
{
    private const int MaxInstances = 8;

    public async Task RunAsync(CancellationToken cancel)
    {
        var clients = new List<Task>();
        while (!cancel.IsCancellationRequested)
        {
            var pipe = Create();
            try
            {
                await pipe.WaitForConnectionAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            clients.RemoveAll(t => t.IsCompleted);
            clients.Add(HandleClientAsync(pipe, cancel));
        }
        await Task.WhenAll(clients).ConfigureAwait(false);
    }

    private NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } me)
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
                                               PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                while (!cancel.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(cancel).ConfigureAwait(false);
                    if (line is null) break;
                    var response = await DispatchAsync(line, cancel).ConfigureAwait(false);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, Protocol.Json)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // cliente desconectou
            }
        }
    }

    private async Task<Response> DispatchAsync(string line, CancellationToken cancel)
    {
        Request? request;
        try
        {
            request = JsonSerializer.Deserialize<Request>(line, Protocol.Json);
            if (request is null) return new Response(0, false, Error: "Requisição vazia.");
        }
        catch (JsonException ex)
        {
            return new Response(0, false, Error: $"JSON inválido: {ex.Message}");
        }

        try
        {
            var result = await handler(request.Command, request.Args, cancel).ConfigureAwait(false);
            return new Response(request.Id, true, result is null ? null : JsonSerializer.SerializeToElement(result, Protocol.Json));
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return new Response(request.Id, false, Error: ex.Message);
        }
    }
}
