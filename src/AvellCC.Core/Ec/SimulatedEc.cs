using AvellCC.Core.Fan;

namespace AvellCC.Core.Ec;

/// <summary>
/// Simulated EC for developing the UI without hardware or admin rights (service with --simulate).
/// Load follows a wave (idle ↔ load every ~2 min); the simulated firmware follows a simple curve;
/// under direct control (0x751 bit 6) the RPM follows the duty written to 0x1804/0x1809 (real calibration).
/// </summary>
public sealed class SimulatedEc : IEcAccess
{
    private readonly Ram _ram = new();

    private sealed class Ram
    {
        private readonly byte[] _bytes = new byte[0x10000];
        public byte Read(ushort a) => _bytes[a];
        public void Write(ushort a, byte v) => _bytes[a] = v;
        public void Seed(ushort start, ReadOnlySpan<byte> data) => data.CopyTo(_bytes.AsSpan(start));
    }
    private readonly DateTimeOffset _start = DateTimeOffset.Now;
    private readonly object _lock = new();
    private double _cpu = 50, _gpu = 45;
    private DateTimeOffset _last = DateTimeOffset.Now;

    public SimulatedEc()
    {
        _ram.Seed(EcAddresses.ProjectId, [0x0F]);
        _ram.Seed(EcAddresses.FanCtrlStatus, [0x0C]);
        _ram.Seed(EcAddresses.OfficeAdvPwmStart, [0x3C, 0x46, 0x50, 0x64, 0x78]);
    }

    public byte Read(ushort address)
    {
        lock (_lock)
        {
            Advance();
            return _ram.Read(address);
        }
    }

    public void Write(ushort address, byte value)
    {
        lock (_lock) _ram.Write(address, value);
    }

    private void Advance()
    {
        var now = DateTimeOffset.Now;
        var dt = (now - _last).TotalSeconds;
        if (dt < 0.2) return;
        _last = now;

        var load = (Math.Sin((now - _start).TotalSeconds / 120.0 * 2 * Math.PI) + 1) / 2;   // 0..1
        var direct = (_ram.Read(EcAddresses.FanControl) & EcAddresses.FullFanModeBit) != 0;
        var duty1 = direct ? _ram.Read(EcAddresses.DirectFan1Duty) / 2.0 : FirmwareDuty(_cpu);
        var duty2 = direct ? _ram.Read(EcAddresses.DirectFan2Duty) / 2.0 : FirmwareDuty(_cpu);
        var cooling = (duty1 + duty2) / 2 / 100.0;

        // First-order thermal model: equilibrium rises with load and falls with airflow.
        var cpuTarget = 45 + 50 * load - 18 * cooling;
        var gpuTarget = 42 + 38 * load - 14 * cooling;
        _cpu += (cpuTarget - _cpu) * Math.Min(1, dt / 8);
        _gpu += (gpuTarget - _gpu) * Math.Min(1, dt / 15);

        if (!direct)
        {
            _ram.Write(EcAddresses.DirectFan1Duty, (byte)(duty1 * 2));
            _ram.Write(EcAddresses.DirectFan2Duty, (byte)(duty2 * 2));
        }
        _ram.Seed(EcAddresses.CpuTemp, [(byte)Math.Round(_cpu)]);
        _ram.Seed(EcAddresses.GpuTemp, [(byte)Math.Round(_gpu)]);
        SeedRpm(EcAddresses.Fan1High, EcAddresses.Fan1Low, FanCalibration.EstimateRpm(duty1));
        SeedRpm(EcAddresses.Fan2High, EcAddresses.Fan2Low, FanCalibration.EstimateRpm(duty2) + 20);
    }

    private static double FirmwareDuty(double cpu) => Math.Clamp(30 + (cpu - 55) * 1.6, 30, 80);

    private void SeedRpm(ushort hi, ushort lo, int rpm)
    {
        _ram.Seed(hi, [(byte)(rpm >> 8)]);
        _ram.Seed(lo, [(byte)rpm]);
    }
}
