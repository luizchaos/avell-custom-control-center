using AvellCC.Core.Ec;
using AvellCC.Core.Safety;

namespace AvellCC.Core.Fan;

/// <summary>
/// Direct (legacy) fan control, as tuxedo-drivers does for Uniwill ECs without universal fan control
/// (0x78E bit 6 = 0, which is this laptop):
///   enable  → write the duty to 0x1804/0x1809, set 0x751 bit 6 ("full fan mode"), then rewrite the duty a few times
///             right away, before the EC finishes ramping up to maximum;
///   keep    → rewrite the duty periodically (the caller does this: service loop / CLI);
///   disable → restore 0x751 to its original value. The EC goes back to its internal curve.
/// Fail-safe: if the process dies with bit 6 set, "full fan mode" tends to speed the fans up, not stop them.
/// </summary>
public sealed class DirectFanService(IEcAccess ec, EcRegisterBackupStore store, Func<MachineInfo> machine, Func<string[]> vendorRunning)
{
    public const string BackupGroup = "ec-direct";
    /// <summary>Minimum with the fan spinning (tuxedo: FAN_ON_MIN_SPEED_PERCENT). We never write 0.</summary>
    public const int MinPercent = 25;

    public static readonly ushort[] TrackedRegisters = [EcAddresses.FanControl, EcAddresses.DirectFan1Duty, EcAddresses.DirectFan2Duty];

    private byte? _originalFanControl;
    private bool _snapshotSaved;

    public bool Active => _originalFanControl is not null;

    public DirectState ReadState() => new(
        ec.Read(EcAddresses.FanControl),
        ec.Read(EcAddresses.DirectFan1Duty),
        ec.Read(EcAddresses.DirectFan2Duty),
        ec.Read(EcAddresses.FanCtrlStatus),
        ec.Read(EcAddresses.CustomProfile));

    public static byte ToRaw(int percent)
    {
        if (percent is < MinPercent or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), $"Direct duty must be between {MinPercent} and 100 % (got {percent}).");
        return (byte)(percent * 2);
    }

    public (EcRegisterSnapshot Original, bool CreatedNow) EnsureOriginalBackup()
    {
        if (store.HasOriginal) return (store.LoadOriginal(), false);
        var first = ReadTracked();
        var second = ReadTracked();
        if (first[EcAddresses.FanControl] != second[EcAddresses.FanControl])
            throw new InvalidOperationException("0x751 changed between two reads: another process is writing to the EC.");
        var snapshot = EcRegisterSnapshot.From(first, machine(), vendorRunning(), "AvellCC/original");
        var created = store.TryCreateOriginal(snapshot);
        return (created ? snapshot : store.LoadOriginal(), created);
    }

    /// <param name="takeOver">
    /// true = accept bit 6 already set. This is the service restarting after a crash, taking back the direct control
    /// it left active. Only safe with the vendor app stopped, which is checked first.
    /// </param>
    public void Enable(int fan1Percent, int fan2Percent, bool takeOver = false)
    {
        var running = vendorRunning();                 // Rule 1
        if (running.Length > 0) throw new VendorRunningException(running);
        var (original, _) = EnsureOriginalBackup();    // Rule 2
        if (!_snapshotSaved)
        {
            store.SaveSnapshot(EcRegisterSnapshot.From(ReadTracked(), machine(), vendorRunning(), "AvellCC/before-direct"), "before-direct");
            _snapshotSaved = true;
        }

        var current = ec.Read(EcAddresses.FanControl);
        if ((current & EcAddresses.FullFanModeBit) != 0 && !takeOver)
            throw new InvalidOperationException($"0x751 already has bit 6 set (0x{current:X2}): is another controller active?");
        _originalFanControl = original.Values()[EcAddresses.FanControl];

        try
        {
            SetDuty(fan1Percent, fan2Percent);
            ec.Write(EcAddresses.FanControl, (byte)(current | EcAddresses.FullFanModeBit));
            for (var i = 0; i < 10; i++) { SetDuty(fan1Percent, fan2Percent); Thread.Sleep(10); }   // before full ramp-up
            if ((ec.Read(EcAddresses.FanControl) & EcAddresses.FullFanModeBit) == 0)
                throw new InvalidOperationException("Read-back mismatch at 0x751.");
        }
        catch
        {
            Disable();
            throw;
        }
    }

    /// <summary>
    /// Sets ONLY 0x751 bit 6, without writing a duty: exactly the vendor app's "Fan Boost"
    /// (MyFanManager_Intel.SetFanBoost: 0x751 |= 0x40). Used to measure the firmware maximum. Turn off with <see cref="Disable"/>.
    /// </summary>
    public void EnableFanBoostOnly()
    {
        var running = vendorRunning();
        if (running.Length > 0) throw new VendorRunningException(running);
        var (original, _) = EnsureOriginalBackup();
        var current = ec.Read(EcAddresses.FanControl);
        if ((current & EcAddresses.FullFanModeBit) != 0)
            throw new InvalidOperationException($"0x751 already has bit 6 set (0x{current:X2}).");
        _originalFanControl = original.Values()[EcAddresses.FanControl];
        ec.Write(EcAddresses.FanControl, (byte)(current | EcAddresses.FullFanModeBit));
    }

    /// <summary>Writes both fans' duty (call periodically while active).</summary>
    public void SetDuty(int fan1Percent, int fan2Percent)
    {
        var a = ToRaw(fan1Percent);
        var b = ToRaw(fan2Percent);
        ec.Write(EcAddresses.DirectFan1Duty, a);
        ec.Write(EcAddresses.DirectFan2Duty, b);
    }

    /// <summary>
    /// Hands the fans back to the firmware: clears only bit 6 of 0x751 and verifies. Idempotent.
    /// The other bits belong to the EC: the operational mode button changes them (bits 4, 5 and 7) while AvellCC
    /// is in control, so restoring the old byte would undo the user's mode choice.
    /// </summary>
    public void Disable()
    {
        var target = (byte)(ec.Read(EcAddresses.FanControl) & ~EcAddresses.FullFanModeBit);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ec.Write(EcAddresses.FanControl, target);
            if (ec.Read(EcAddresses.FanControl) == target) { _originalFanControl = null; return; }
            Thread.Sleep(50);
        }
        throw new InvalidOperationException($"Could not restore 0x751 to 0x{target:X2}.");
    }

    private Dictionary<ushort, byte> ReadTracked() => TrackedRegisters.ToDictionary(a => a, ec.Read);
}

public sealed record DirectState(byte FanControl, byte Fan1DutyRaw, byte Fan2DutyRaw, byte FanCtrlStatus, byte CustomProfile)
{
    public bool HasUniversalControl => (FanCtrlStatus & EcAddresses.UniversalFanCtrlBit) != 0;
    public bool FullFanMode => (FanControl & EcAddresses.FullFanModeBit) != 0;
}
