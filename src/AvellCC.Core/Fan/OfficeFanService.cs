using AvellCC.Core.Ec;
using AvellCC.Core.Safety;

namespace AvellCC.Core.Fan;

/// <summary>
/// Curva de ventoinha via "Office Mode Advanced" (o mecanismo que este EC obedece).
///
/// Aplicar:   (1) vendor parado, (2) backup original dos registradores, (3) snapshot "antes",
///            (4) grava T1..T5 em 0x743–0x747 e confere, (5) SÓ ENTÃO liga o modo (0x751 = 0xA0) e confere.
/// Restaurar: volta 0x751 PRIMEIRO (sai do modo Office antes de mexer nas velocidades), depois o resto.
/// Qualquer divergência na conferência leva ao snapshot "antes", na ordem de restauração.
/// </summary>
public sealed class OfficeFanService(IEcAccess ec, EcRegisterBackupStore store, Func<MachineInfo> machine, Func<string[]> vendorRunning)
{
    public const byte OfficeAdvancedMode = 0xA0;

    private static readonly ushort[] PwmRegisters =
        Enumerable.Range(EcAddresses.OfficeAdvPwmStart, EcAddresses.OfficeAdvPwmCount).Select(a => (ushort)a).ToArray();

    /// <summary>Nome do grupo de backup (v2 inclui 0x727; o grupo antigo "ec-registers" fica como histórico).</summary>
    public const string BackupGroup = "ec-office";

    /// <summary>Tudo que entra no backup. A ordem de restauração começa pelo modo.</summary>
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

    /// <summary>Lê os registradores duas vezes; só aceita se forem iguais.</summary>
    public EcRegisterSnapshot ReadStable(string createdBy)
    {
        var first = ReadTracked();
        var second = ReadTracked();
        if (first.Any(kv => second[kv.Key] != kv.Value))
            throw new InvalidOperationException("Os registradores mudaram entre duas leituras: outro processo está escrevendo no EC.");
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
            WriteVerified(PwmRegisters.Select(a => (a, target[a])));                 // velocidades primeiro
            if (curve.CustomProfile)
                WriteVerified([(EcAddresses.CustomProfile, (byte)(ec.Read(EcAddresses.CustomProfile) | EcAddresses.CustomProfileBit))]);
            WriteVerified([(EcAddresses.FanControl, OfficeAdvancedMode)]);           // modo por último
        });
    }

    public void RestoreOriginal()
    {
        var original = store.LoadOriginal().Values();
        WriteGuarded("office-restore-original", () => WriteInRestoreOrder(original));
    }

    private void WriteGuarded(string reason, Action write)
    {
        var running = vendorRunning();                 // Regra 1
        if (running.Length > 0) throw new VendorRunningException(running);

        EnsureOriginalBackup();                        // Regra 2
        var before = EcRegisterSnapshot.From(ReadTracked(), machine(), vendorRunning(), $"AvellCC/before-{reason}");
        store.SaveSnapshot(before, $"before-{reason}");

        try
        {
            write();
        }
        catch (Exception ex)
        {
            WriteInRestoreOrder(before.Values());
            throw new InvalidOperationException($"Falha ao gravar ({ex.Message}). O estado anterior foi restaurado.", ex);
        }
    }

    /// <summary>Modo (0x751) primeiro, depois os demais na ordem de TrackedRegisters.</summary>
    private void WriteInRestoreOrder(IReadOnlyDictionary<ushort, byte> values) =>
        WriteVerified(TrackedRegisters.Where(values.ContainsKey).Select(a => (a, values[a])));

    private void WriteVerified(IEnumerable<(ushort Address, byte Value)> writes)
    {
        var list = writes.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var (address, value) = list[i];
            if (ec.Read(address) != value) ec.Write(address, value);
            if (ec.Read(address) != value) throw new InvalidOperationException($"Read-back divergente em 0x{address:X3}.");
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
        0x00 => "Gaming (curva interna do firmware)",
        0x40 => "Turbo / FanBoost",
        OfficeFanService.OfficeAdvancedMode => "Office Advanced (T1..T5)",
        >= 0x80 and <= 0x85 => $"Office Basic, nível {FanControl - 0x80}",
        _ => $"desconhecido (0x{FanControl:X2})",
    };
}
