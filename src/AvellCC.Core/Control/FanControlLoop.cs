using AvellCC.Core.Ec;
using AvellCC.Core.Fan;

namespace AvellCC.Core.Control;

public enum LoopState { Firmware, Curve, Emergency, BlockedByVendor, Suspended, Error }

/// <summary>Fotografia do estado, publicada a cada ciclo para a UI.</summary>
public sealed record ControlStatus
{
    public DateTimeOffset Time { get; init; }
    public LoopState State { get; init; }
    public string? CurveName { get; init; }
    public int CpuTemp { get; init; }
    public int GpuTemp { get; init; }
    public int Fan1Rpm { get; init; }
    public int Fan2Rpm { get; init; }
    /// <summary>Duty real lido de 0x1804/0x1809 (vale também com o firmware no controle).</summary>
    public double Fan1Duty { get; init; }
    public double Fan2Duty { get; init; }
    public bool DirectControl { get; init; }
    public string[] VendorRunning { get; init; } = [];
    public string? Message { get; init; }
}

/// <summary>
/// Loop de controle das ventoinhas, usado pelo serviço. Thread-safe. <see cref="Tick"/> é chamado a cada ~2 s.
/// Regras:
///  - modo firmware: o controle direto fica desligado (inclusive o que sobrou de uma queda anterior);
///  - modo curva:    o <see cref="FanCurveController"/> decide, e o duty é gravado só na mudança/keep-alive;
///                   se o 0x751 perder o bit 6, o loop reassume;
///  - vendor rodando, suspensão ou erro: o controle volta ao firmware (estado seguro).
/// </summary>
public sealed class FanControlLoop(IEcAccess ec, DirectFanService direct, Func<string[]> vendorRunning, ControllerOptions? options = null)
{
    private readonly object _lock = new();
    private FanCurve? _curve;
    private FanCurveController? _controller;
    private bool _suspended;

    public ControlStatus Status { get; private set; } = new() { Time = DateTimeOffset.Now, State = LoopState.Firmware };

    public void UseFirmware()
    {
        lock (_lock)
        {
            _curve = null;
            _controller = null;
            ReleaseToFirmware();
        }
    }

    public void UseCurve(FanCurve curve)
    {
        curve.Validate();
        lock (_lock)
        {
            _curve = curve;
            _controller = new FanCurveController(curve, options);   // reinicia histerese/rampa
        }
    }

    public void Suspend()
    {
        lock (_lock) { _suspended = true; ReleaseToFirmware(); }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _suspended = false;
            if (_curve is not null) _controller = new FanCurveController(_curve, options);
        }
    }

    /// <summary>Parada do serviço: devolve ao firmware.</summary>
    public void Shutdown()
    {
        lock (_lock) ReleaseToFirmware();
    }

    public ControlStatus Tick()
    {
        lock (_lock)
        {
            FanSample sample = default;
            try
            {
                sample = FanSample.Read(ec);
                var vendor = vendorRunning();

                LoopState state;
                string? message = null;
                if (_suspended)
                {
                    state = LoopState.Suspended;
                }
                else if (vendor.Length > 0)
                {
                    // Regra 1: com o vendor ativo, não disputamos o EC.
                    if (direct.Active) direct.Disable();
                    state = LoopState.BlockedByVendor;
                    message = $"Avell Custom Control em execução ({string.Join(", ", vendor)}).";
                }
                else if (_controller is null)
                {
                    ReleaseToFirmware();
                    state = LoopState.Firmware;
                }
                else
                {
                    var d = _controller.Step(sample);
                    var fullFan = (ec.Read(EcAddresses.FanControl) & EcAddresses.FullFanModeBit) != 0;
                    if (!direct.Active || !fullFan)
                    {
                        if (direct.Active && !fullFan) message = "0x751 perdeu o bit 6; reassumido.";
                        direct.Enable(d.Fan1, d.Fan2, takeOver: true);
                    }
                    else if (d.ShouldWrite)
                    {
                        direct.SetDuty(d.Fan1, d.Fan2);
                    }
                    state = d.Emergency ? LoopState.Emergency : LoopState.Curve;
                    message ??= d.Reason;
                }

                return Publish(sample, state, message, vendor);
            }
            catch (Exception ex)
            {
                // Falha: devolve ao firmware (seguro) e reporta. O próximo Tick tenta de novo.
                try { if (direct.Active) direct.Disable(); } catch { /* o erro original é o que importa */ }
                return Publish(sample, LoopState.Error, ex.Message, []);
            }
        }
    }

    private void ReleaseToFirmware()
    {
        // Também cobre o bit 6 que sobrou de uma queda (direct.Active = false, mas o EC ainda em modo direto).
        if (direct.Active || (vendorRunning().Length == 0 && (ec.Read(EcAddresses.FanControl) & EcAddresses.FullFanModeBit) != 0))
            direct.Disable();
    }

    private ControlStatus Publish(FanSample s, LoopState state, string? message, string[] vendor)
    {
        double duty1 = 0, duty2 = 0;
        try { duty1 = ec.Read(EcAddresses.DirectFan1Duty) / 2.0; duty2 = ec.Read(EcAddresses.DirectFan2Duty) / 2.0; } catch { }
        Status = new ControlStatus
        {
            Time = DateTimeOffset.Now,
            State = state,
            CurveName = _curve?.Name,
            CpuTemp = s.CpuTemp,
            GpuTemp = s.GpuTemp,
            Fan1Rpm = s.Fan1,
            Fan2Rpm = s.Fan2,
            Fan1Duty = duty1,
            Fan2Duty = duty2,
            DirectControl = direct.Active,
            VendorRunning = vendor,
            Message = message,
        };
        return Status;
    }
}
