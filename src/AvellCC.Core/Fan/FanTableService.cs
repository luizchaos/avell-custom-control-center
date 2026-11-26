using AvellCC.Core.Ec;
using AvellCC.Core.Safety;

namespace AvellCC.Core.Fan;

/// <summary>
/// Read, back up, apply and restore the EC's 16-level fan table (0xF00–0xF5F).
/// NOTE: this EC ignores the table (docs/HARDWARE.md, tests 1–3); the service uses <see cref="DirectFanService"/>.
/// This class is kept for the original-state backup/restore and for testing other Uniwill models.
///
/// Every write: (1) requires the vendor app stopped, (2) ensures the original backup, (3) saves a snapshot,
/// (4) writes in the safe order, (5) reads back and compares; on mismatch, restores the snapshot.
/// </summary>
public sealed class FanTableService(IEcAccess ec, FanTableBackupStore store, Func<MachineInfo> machine, Func<string[]> vendorRunning)
{
    /// <summary>0x7A5 bit 3: makes the EC follow the 0xF00 table ("UserHiModeToFantable" in the vendor app).</summary>
    public const byte UserTableBit = 0x08;
    /// <summary>0x7A5 bit 2: vendor quiet mode. Cleared when applying our own curve (as the vendor's Standard mode does).</summary>
    public const byte QuietBit = 0x04;
    /// <summary>0x751 in the vendor's Standard mode (MyFanManager_Intel.SetFanMode(2)).</summary>
    public const byte FanControlStandard = 0xA0;
    /// <summary>0x751 in the vendor's Performance/Benchmark mode (bit 6 = Fan Boost).</summary>
    public const byte FanControlPerformance = 0x40;

    public enum WriteOrder
    {
        /// <summary>Table, then modes: enable the user table only after it is written and verified.</summary>
        TableFirst,
        /// <summary>Modes, then table: disable the user table before touching it.</summary>
        ModesFirst,
    }

    /// <summary>Write progress (addresses done, total). Useful because each WMI access takes ~100 ms.</summary>
    public event Action<int, int>? Progress;

    /// <summary>Reads the table twice; only accepts it if both reads match.</summary>
    public FanTableSnapshot ReadStable(string createdBy = "AvellCC")
    {
        var first = ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength);
        var second = ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength);
        if (!first.AsSpan().SequenceEqual(second))
            throw new InvalidOperationException("The table changed between two reads: another process is writing to the EC.");
        return Snapshot(first, createdBy);
    }

    /// <summary>Creates the original backup if it does not exist yet. Always safe to call (only reads the EC).</summary>
    public (FanTableSnapshot Original, bool CreatedNow) EnsureOriginalBackup()
    {
        if (store.HasOriginal) return (store.LoadOriginal(), false);
        var snapshot = ReadStable("AvellCC/original");
        var created = store.TryCreateOriginal(snapshot);
        return (created ? snapshot : store.LoadOriginal(), created);
    }

    /// <summary>
    /// Returns to the original backup. Modes are written FIRST: with this laptop's original
    /// (zeroed table, 0x7A5 = 0x00) that disables the user table before it becomes all zeros.
    /// </summary>
    public void RestoreOriginal()
    {
        var original = store.LoadOriginal();
        Apply(original.FanModeRegisterValues(), original.Raw, WriteOrder.ModesFirst, "restore-original");
    }

    /// <summary>
    /// Applies a curve: writes and verifies the table and ONLY THEN enables the user table
    /// (0x7A5: bit 3 set, bit 2 cleared; 0x751 per <see cref="FanCurve.EcMode"/>).
    /// </summary>
    public byte[] ApplyCurve(FanCurve curve)
    {
        var table = FanTableEncoder.Encode(curve);
        var current0x7A5 = ec.Read(EcAddresses.ApOemByte3);
        var modes = new Dictionary<ushort, byte>
        {
            [EcAddresses.ApOemByte3] = (byte)((current0x7A5 & ~QuietBit) | UserTableBit),
        };
        switch (curve.EcMode)
        {
            case "standard": modes[EcAddresses.FanControl] = FanControlStandard; break;
            case "performance": modes[EcAddresses.FanControl] = FanControlPerformance; break;
        }
        Apply(modes, table, WriteOrder.TableFirst, $"curve-{curve.Name}");
        return table;
    }

    /// <summary>true if the EC has the user table enabled.</summary>
    public bool IsUserTableActive() => (ec.Read(EcAddresses.ApOemByte3) & UserTableBit) != 0;

    /// <summary>Single write path (internal: the public entry points are ApplyCurve and RestoreOriginal).</summary>
    internal void Apply(IReadOnlyDictionary<ushort, byte> modes, byte[] table, WriteOrder order, string reason)
    {
        if (table.Length != EcAddresses.FanTableLength) throw new ArgumentException("Invalid size.", nameof(table));
        if (modes.Keys.Any(a => !EcAddresses.FanModeRegisters.Contains(a)))
            throw new ArgumentException("Mode register outside the allow list.", nameof(modes));

        var running = vendorRunning();                 // Rule 1: one controller at a time
        if (running.Length > 0) throw new VendorRunningException(running);

        EnsureOriginalBackup();                        // Rule 2: never write without the original saved
        var before = Snapshot(ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength), $"AvellCC/before-{reason}");
        store.SaveSnapshot(before, $"before-{reason}");

        try
        {
            Write(modes, table, before, order);
        }
        catch (Exception ex)
        {
            // Back to the previous state, disabling the user table first.
            var current = Snapshot(ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength), "AvellCC/rollback");
            Write(before.FanModeRegisterValues(), before.Raw, current, WriteOrder.ModesFirst);
            throw new InvalidOperationException($"Write failed ({ex.Message}). The previous state was restored.", ex);
        }
    }

    private void Write(IReadOnlyDictionary<ushort, byte> modes, byte[] table, FanTableSnapshot current, WriteOrder order)
    {
        var currentModes = current.FanModeRegisterValues();
        var currentTable = current.Raw;
        var total = modes.Count + table.Length;
        var done = 0;

        void WriteModes()
        {
            foreach (var (address, value) in modes)
            {
                if (currentModes[address] != value) ec.Write(address, value);
                if (ec.Read(address) != value) throw new InvalidOperationException($"Read-back mismatch at 0x{address:X3}.");
                Progress?.Invoke(++done, total);
            }
        }

        void WriteTable()
        {
            for (var i = 0; i < table.Length; i++)
            {
                if (currentTable[i] != table[i]) ec.Write((ushort)(EcAddresses.FanTableStart + i), table[i]);
                Progress?.Invoke(++done, total);
            }
            var after = ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength);
            if (!after.AsSpan().SequenceEqual(table)) throw new InvalidOperationException("Read-back mismatch in the table.");
        }

        if (order == WriteOrder.TableFirst) { WriteTable(); WriteModes(); }
        else { WriteModes(); WriteTable(); }
    }

    private FanTableSnapshot Snapshot(byte[] table, string createdBy)
    {
        var modes = EcAddresses.FanModeRegisters.ToDictionary(a => a, ec.Read);
        return FanTableSnapshot.FromRaw(table, machine(), modes, vendorRunning(), createdBy);
    }
}
