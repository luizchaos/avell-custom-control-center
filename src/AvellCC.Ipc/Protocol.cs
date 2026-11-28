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
    /// <summary>2: freeMemory. 3: keyboard profiles, mode button (status.modeSwitchCount), pressModeButton (simulation).</summary>
    public const int Version = 4;   // 4: setLightbar, info.lightbarPresent (experimental light bar)

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
        public const string FreeMemory = "freeMemory";
        public const string SaveKeyboardProfile = "saveKeyboardProfile";
        public const string DeleteKeyboardProfile = "deleteKeyboardProfile";
        public const string ApplyKeyboardProfile = "applyKeyboardProfile";
        public const string SetModeButton = "setModeButton";
        public const string SetLightbar = "setLightbar";
        /// <summary>Simulation only: behaves as if the operational mode button was pressed.</summary>
        public const string PressModeButton = "pressModeButton";
    }
}

public sealed record Request(int Id, string Command, JsonElement? Args = null);

public sealed record Response(int Id, bool Ok, JsonElement? Result = null, string? Error = null);

public sealed record SetFanModeArgs(string Mode, string? CurveName);

public sealed record NameArgs(string Name);

public sealed record EnabledArgs(bool Enabled);

/// <param name="LightbarPresent">The light bar's ITE controller (048D:6005) was found (protocol 4).</param>
public sealed record ServiceInfo(int ProtocolVersion, string Version, bool Simulated, string Machine, bool LightbarPresent = false);

/// <summary>Error returned by the service (validation, hardware...).</summary>
public sealed class ServiceException(string message) : Exception(message);
