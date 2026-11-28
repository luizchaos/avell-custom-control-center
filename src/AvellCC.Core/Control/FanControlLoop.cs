using AvellCC.Core.Ec;
using AvellCC.Core.Fan;

namespace AvellCC.Core.Control;

public enum LoopState { Firmware, Curve, Emergency, BlockedByVendor, Suspended, Error }

/// <summary>State snapshot, published every cycle for the UI.</summary>
public sealed record ControlStatus
{
    public DateTimeOffset Time { get; init; }
    public LoopState State { get; init; }
    public string? CurveName { get; init; }
    public int CpuTemp { get; init; }
    public int GpuTemp { get; init; }
    public int Fan1Rpm { get; init; }
    public int Fan2Rpm { get; init; }
    /// <summary>Actual duty read from 0x1804/0x1809 (also valid while the firmware is in control).</summary>
    public double Fan1Duty { get; init; }
    public double Fan2Duty { get; init; }
    public bool DirectControl { get; init; }
    public string[] VendorRunning { get; init; } = [];
    public string? Message { get; init; }
    /// <summary>Incremented by the service each time the mode button switched the curve (the UI shows a notification).</summary>
    public int ModeSwitchCount { get; init; }
    /// <summary>Curve chosen by the last mode button press.</summary>
    public string? ModeSwitchCurve { get; init; }
}

/// <summary>
/// Fan control loop used by the service. Thread-safe. <see cref="Tick"/> is called every ~2 s.
/// Rules:
///  - firmware mode: direct control stays off (including any left over from a previous crash);
///  - curve mode:    <see cref="FanCurveController"/> decides, and the duty is written only on change/keep-alive;
///                   if 0x751 loses bit 6, the loop takes control again;
///  - vendor app running, sleep or error: control returns to the firmware (safe state).
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

    /// <summary>Service stop: hand control back to the firmware.</summary>
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
                    // Rule 1: with the vendor app active, we do not fight over the EC.
                    if (direct.Active) direct.Disable();
                    state = LoopState.BlockedByVendor;
                    message = $"Avell Custom Control is running ({string.Join(", ", vendor)}).";
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
                        if (direct.Active && !fullFan) message = "0x751 lost bit 6; control re-acquired.";
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
                // Failure: hand back to the firmware (safe) and report. The next Tick tries again.
                try { if (direct.Active) direct.Disable(); } catch { /* the original error is what matters */ }
                return Publish(sample, LoopState.Error, ex.Message, []);
            }
        }
    }

    private void ReleaseToFirmware()
    {
        // Also covers a bit 6 left over from a crash (direct.Active = false, but the EC still in direct mode).
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
