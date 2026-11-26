using AvellCC.Core.Ec;
using AvellCC.Core.Safety;

namespace AvellCC.Core.Fan;

/// <summary>
/// Fan curve through the vendor's "Office Mode Advanced" (5 speeds at 0x743–0x747 + 0x751 = 0xA0).
/// NOTE: this EC ignores it (docs/HARDWARE.md, tests 4 and 5); kept as a documented experiment for other models.
///
/// Apply:   (1) vendor stopped, (2) original register backup, (3) "before" snapshot,
///          (4) write T1..T5 to 0x743–0x747 and verify, (5) ONLY THEN enable the mode (0x751 = 0xA0) and verify.
/// Restore: 0x751 FIRST (leave Office mode before touching the speeds), then the rest.
/// Any read-back mismatch returns to the "before" snapshot, in restore order.
/// </summary>
public sealed class OfficeFanService(IEcAccess ec, EcRegisterBackupStore store, Func<MachineInfo> machine, Func<string[]> vendorRunning)
{
    public const byte OfficeAdvancedMode = 0xA0;

    private static readonly ushort[] PwmRegisters =
        Enumerable.Range(EcAddresses.OfficeAdvPwmStart, EcAddresses.OfficeAdvPwmCount).Select(a => (ushort)a).ToArray();

    /// <summary>Backup group name (v2 includes 0x727; the old "ec-registers" group is kept as history).</summary>
    public const string BackupGroup = "ec-office";

    /// <summary>Everything in the backup. Restore order starts with the mode register.</summary>
    public static readonly ushort[] TrackedRegisters =
    [
        EcAddresses.FanControl, EcAddresses.CustomProfile, EcAddresses.ApOemByte3,
        EcAddresses.OfficeAdvMinSpeed, EcAddresses.OfficeAdvMinTemp, EcAddresses.OfficeAdvExtraSpeed,
        .. PwmRegisters,
    ];

    public event Action<int, int>? Progress;

    public OfficeState ReadState() => new(
        ec.Read(EcAddresses.FanControl),
        OfficeFanCurve.FromRaw(ec.ReadRange(EcAddresses.OfficeAdvPwmStart, EcAddresses.OfficeAdvPwmCount)),
        OfficeFanCurve.FromRaw(ec.ReadRange(EcAddresses.OfficeAdvPwmDefaultStart, EcAddresses.OfficeAdvPwmCount)),
        ec.Read(EcAddresses.OfficeAdvMinSpeed), ec.Read(EcAddresses.OfficeAdvMinTemp), ec.Read(EcAddresses.OfficeAdvExtraSpeed),
        (ec.Read(EcAddresses.BiosOemByte2) & 0x08) != 0,
        ec.Read(EcAddresses.CustomProfile));

    /// <summary>Reads the registers twice; only accepts them if both reads match.</summary>
    public EcRegisterSnapshot ReadStable(string createdBy)
    {
        var first = ReadTracked();
        var second = ReadTracked();
        if (first.Any(kv => second[kv.Key] != kv.Value))
            throw new InvalidOperationException("The registers changed between two reads: another process is writing to the EC.");
        return EcRegisterSnapshot.From(first, machine(), vendorRunning(), createdBy);
    }

    public (EcRegisterSnapshot Original, bool CreatedNow) EnsureOriginalBackup()
    {
        if (store.HasOriginal) return (store.LoadOriginal(), false);
        var snapshot = ReadStable("AvellCC/original");
        var created = store.TryCreateOriginal(snapshot);
        return (created ? snapshot : store.LoadOriginal(), created);
    }

    public void Apply(OfficeFanCurve curve)
    {
        var raw = curve.ToRaw();
        var target = new Dictionary<ushort, byte>();
        for (var i = 0; i < raw.Length; i++) target[PwmRegisters[i]] = raw[i];
        WriteGuarded($"office-{curve.Name}", () =>
        {
            WriteVerified(PwmRegisters.Select(a => (a, target[a])));                 // speeds first
            if (curve.CustomProfile)
                WriteVerified([(EcAddresses.CustomProfile, (byte)(ec.Read(EcAddresses.CustomProfile) | EcAddresses.CustomProfileBit))]);
            WriteVerified([(EcAddresses.FanControl, OfficeAdvancedMode)]);           // mode last
        });
    }

    public void RestoreOriginal()
    {
        var original = store.LoadOriginal().Values();
        WriteGuarded("office-restore-original", () => WriteInRestoreOrder(original));
    }

    private void WriteGuarded(string reason, Action write)
    {
        var running = vendorRunning();                 // Rule 1
        if (running.Length > 0) throw new VendorRunningException(running);

        EnsureOriginalBackup();                        // Rule 2
        var before = EcRegisterSnapshot.From(ReadTracked(), machine(), vendorRunning(), $"AvellCC/before-{reason}");
        store.SaveSnapshot(before, $"before-{reason}");

        try
        {
            write();
        }
        catch (Exception ex)
        {
            WriteInRestoreOrder(before.Values());
            throw new InvalidOperationException($"Write failed ({ex.Message}). The previous state was restored.", ex);
        }
    }

    /// <summary>Mode (0x751) first, then the others in TrackedRegisters order.</summary>
    private void WriteInRestoreOrder(IReadOnlyDictionary<ushort, byte> values) =>
        WriteVerified(TrackedRegisters.Where(values.ContainsKey).Select(a => (a, values[a])));

    private void WriteVerified(IEnumerable<(ushort Address, byte Value)> writes)
    {
        var list = writes.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var (address, value) = list[i];
            if (ec.Read(address) != value) ec.Write(address, value);
            if (ec.Read(address) != value) throw new InvalidOperationException($"Read-back mismatch at 0x{address:X3}.");
            Progress?.Invoke(i + 1, list.Count);
        }
    }

    private Dictionary<ushort, byte> ReadTracked() => TrackedRegisters.ToDictionary(a => a, ec.Read);
}

public sealed record OfficeState(byte FanControl, int[] Duties, int[] DefaultDuties, byte MinSpeed, byte MinTemp, byte ExtraSpeed, bool SupportsMyFan3, byte CustomProfile = 0)
{
    public bool OfficeAdvancedActive => FanControl == OfficeFanService.OfficeAdvancedMode;

    public string ModeName => FanControl switch
    {
        0x00 => "Gaming (firmware internal curve)",
        0x40 => "Turbo / Fan Boost",
        OfficeFanService.OfficeAdvancedMode => "Office Advanced (T1..T5)",
        >= 0x80 and <= 0x85 => $"Office Basic, level {FanControl - 0x80}",
        _ => $"unknown (0x{FanControl:X2})",
    };
}
