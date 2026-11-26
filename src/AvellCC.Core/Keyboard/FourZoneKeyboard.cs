namespace AvellCC.Core.Keyboard;

/// <summary>
/// Teclado RGB 4 zonas (ITE 8291, 048D:CE00, usage page 0xFF12).
/// Segue a sequência do vendor (LM_ITE_RGB.Set_ITE_Effect_Type_FwMode):
///   uma cor     → 0x14 slot 1, depois 0x08 com color = 1
///   várias cores → 0x14 slots 1..N, depois 0x08 com color = 8
///   cor ao vivo de uma zona → só 0x14 no slot da zona (1..4)
/// </summary>
public sealed class FourZoneKeyboard(IFeatureTransport device, byte projectId, bool colorCorrection = true) : IDisposable
{
    /// <summary>Project ID do EC deste notebook (0x740 = 0x0F, lido em 2026-11-26).</summary>
    public const byte DefaultProjectId = 0x0F;

    private const byte ColorSingleSlot = 1;
    private const byte ColorCycleSlots = 8;
    private readonly object _lock = new();

    public void Static(Rgb color, int brightness, bool save) =>
        Send(Color(1, color), Ite8291.SetEffect(Ite8291.Control.Apply, Ite8291.Effect.Static, 2, brightness, ColorSingleSlot, Ite8291.Direction.LeftToRight, save));

    /// <summary>Uma cor por zona: entra em Static e depois grava a cor de cada zona.</summary>
    public void Zones(IReadOnlyList<Rgb> colors, int brightness, bool save)
    {
        if (colors.Count != Ite8291.Zones) throw new ArgumentException($"São {Ite8291.Zones} zonas.", nameof(colors));
        var packets = new List<byte[]>
        {
            Color(1, colors[0]),
            Ite8291.SetEffect(Ite8291.Control.Apply, Ite8291.Effect.Static, 2, brightness, ColorSingleSlot, Ite8291.Direction.LeftToRight, save),
        };
        for (var zone = 1; zone <= Ite8291.Zones; zone++) packets.Add(Color(zone, colors[zone - 1]));
        Send([.. packets]);
    }

    /// <summary>Efeito de firmware. Com cores, usa o modo cíclico (color = 8); sem cores, o arco-íris do firmware (color = 0).</summary>
    public void Effect(Ite8291.Effect effect, int speed, int brightness, Ite8291.Direction direction, IReadOnlyList<Rgb>? colors, bool save)
    {
        var packets = new List<byte[]>();
        byte colorMode = 0;
        if (colors is { Count: 1 }) { packets.Add(Color(1, colors[0])); colorMode = ColorSingleSlot; }
        else if (colors is { Count: > 1 })
        {
            if (colors.Count > 7) throw new ArgumentException("No máximo 7 cores.", nameof(colors));
            for (var i = 0; i < colors.Count; i++) packets.Add(Color(i + 1, colors[i]));
            colorMode = ColorCycleSlots;
        }
        packets.Add(Ite8291.SetEffect(Ite8291.Control.Apply, effect, speed, brightness, colorMode, direction, save));
        Send([.. packets]);
    }

    public void Brightness(int level) => Send(Ite8291.SetBrightness(level));

    public void Off() => Send(Ite8291.TurnOff());

    /// <summary>Versão do firmware (comando 0x80). Bytes [2..5] da resposta.</summary>
    public byte[] FirmwareVersion() => Query(Ite8291.QueryFirmware(), Ite8291.CmdGetFirmware)[2..6];

    /// <summary>
    /// Efeito atual (comando 0x88). A resposta espelha o comando 0x08, a partir do byte 2:
    /// ctrl, effect, speed, brightness (valor de firmware), color, direction, save.
    /// Confirmado em 2026-11-26: "02 03 01 24 08 01" = Apply, Wave, speed 1, brilho 36, paleta cíclica, direita→esquerda.
    /// </summary>
    public EffectState CurrentEffect()
    {
        var r = Query(Ite8291.QueryEffect(), Ite8291.CmdGetEffect);
        return new EffectState((Ite8291.Control)r[2], (Ite8291.Effect)r[3], r[4], r[5], r[6], (Ite8291.Direction)r[7], r[8]);
    }

    /// <summary>Envia um report cru de 9 bytes (ferramenta de desenvolvimento).</summary>
    public void Raw(byte[] report) => Send(report);

    private byte[] Color(int slot, Rgb color) =>
        Ite8291.SetSlotColor(slot, colorCorrection ? Ite8291.CorrectColor4Zone(projectId, color) : color);

    private byte[] Query(byte[] request, byte expectedCommand)
    {
        lock (_lock)
        {
            for (var attempt = 0; attempt < 10; attempt++)   // o vendor também tenta até 10 vezes
            {
                device.SetFeature(request);
                Thread.Sleep(2);
                var response = device.GetFeature(0, Ite8291.FeatureReportLength);
                if (response[1] == expectedCommand) return response;
                Thread.Sleep(20);
            }
            throw new InvalidOperationException($"Sem resposta válida ao comando 0x{expectedCommand:X2}.");
        }
    }

    private void Send(params byte[][] packets)
    {
        lock (_lock)
        {
            foreach (var packet in packets)
            {
                device.SetFeature(packet);
                Thread.Sleep(2); // o vendor espera 1 ms entre comandos
            }
        }
    }

    public void Dispose() => device.Dispose();
}

/// <summary>Estado reportado pelo comando 0x88 (valores crus de firmware em Speed/Brightness).</summary>
public readonly record struct EffectState(Ite8291.Control Control, Ite8291.Effect Effect, byte SpeedRaw, byte BrightnessRaw,
                                          byte ColorMode, Ite8291.Direction Direction, byte Save)
{
    public override string ToString() =>
        $"ctrl={Control} effect={Effect} speed={SpeedRaw} brightness={BrightnessRaw} color={ColorMode} dir={Direction} save={Save}";
}
