using System.Text.Json.Serialization;

namespace AvellCC.Core.Keyboard;

/// <summary>Configuração persistida do teclado. Cores em "RRGGBB".</summary>
public sealed record KeyboardSettings
{
    /// <summary>"static" (uma cor), "zones" (uma cor por zona), "effect" (efeito do firmware) ou "off".</summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = "static";
    [JsonPropertyName("colors")] public string[] Colors { get; init; } = ["FFFFFF"];
    [JsonPropertyName("brightness")] public int Brightness { get; init; } = 3;
    [JsonPropertyName("effect")] public string Effect { get; init; } = nameof(Ite8291.Effect.Wave);
    [JsonPropertyName("speed")] public int Speed { get; init; } = 2;
    [JsonPropertyName("direction")] public string Direction { get; init; } = nameof(Ite8291.Direction.LeftToRight);

    public void Validate()
    {
        if (Mode is not ("static" or "zones" or "effect" or "off")) throw new ArgumentException($"Modo de teclado inválido: {Mode}");
        if (Brightness is < 0 or > 4) throw new ArgumentException("Brilho entre 0 e 4.");
        if (Speed is < 0 or > 4) throw new ArgumentException("Velocidade entre 0 e 4.");
        if (!Enum.TryParse<Ite8291.Effect>(Effect, true, out _)) throw new ArgumentException($"Efeito desconhecido: {Effect}");
        if (!Enum.TryParse<Ite8291.Direction>(Direction, true, out _)) throw new ArgumentException($"Direção desconhecida: {Direction}");
        foreach (var c in Colors) ParseColor(c);
        if (Mode == "static" && Colors.Length < 1) throw new ArgumentException("Modo static precisa de 1 cor.");
        if (Mode == "zones" && Colors.Length != Ite8291.Zones) throw new ArgumentException($"Modo zones precisa de {Ite8291.Zones} cores.");
        if (Colors.Length > Ite8291.MaxColorSlots) throw new ArgumentException($"No máximo {Ite8291.MaxColorSlots} cores.");
    }

    public static Rgb ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
            throw new ArgumentException($"Cor inválida: {hex} (use RRGGBB)");
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>Aplica no teclado. save = true grava na memória do teclado (sobrevive a reboot sem o serviço).</summary>
    public void ApplyTo(FourZoneKeyboard kb, bool save = true)
    {
        Validate();
        var colors = Colors.Select(ParseColor).ToArray();
        switch (Mode)
        {
            case "off": kb.Off(); break;
            case "static": kb.Static(colors[0], Brightness, save); break;
            case "zones": kb.Zones(colors, Brightness, save); break;
            case "effect":
                kb.Effect(Enum.Parse<Ite8291.Effect>(Effect, true), Speed, Brightness,
                          Enum.Parse<Ite8291.Direction>(Direction, true), colors.Length > 0 ? colors : null, save);
                break;
        }
    }
}
