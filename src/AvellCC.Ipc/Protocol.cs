using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvellCC.Ipc;

/// <summary>
/// UI ↔ service protocol: one JSON line per message over a local named pipe.
/// The service is the only owner of the hardware; the UI only asks.
/// </summary>
public static class Protocol
{
    public const string PipeName = "AvellCC.Control";
    public const int Version = 1;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static class Commands
    {
        public const string Info = "info";
        public const string Status = "status";
        public const string GetSettings = "getSettings";
        public const string ListCurves = "listCurves";
        public const string SaveCurve = "saveCurve";
        public const string SetFanMode = "setFanMode";
        public const string SetKeyboard = "setKeyboard";
    }
}

public sealed record Request(int Id, string Command, JsonElement? Args = null);

public sealed record Response(int Id, bool Ok, JsonElement? Result = null, string? Error = null);

public sealed record SetFanModeArgs(string Mode, string? CurveName);

public sealed record ServiceInfo(int ProtocolVersion, string Version, bool Simulated, string Machine);

/// <summary>Error returned by the service (validation, hardware...).</summary>
public sealed class ServiceException(string message) : Exception(message);
