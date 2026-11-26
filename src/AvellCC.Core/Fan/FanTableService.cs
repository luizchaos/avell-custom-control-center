using AvellCC.Core.Ec;
using AvellCC.Core.Safety;

namespace AvellCC.Core.Fan;

/// <summary>
/// Leitura, backup, aplicação de curva e restauração da tabela de ventoinha.
/// Toda escrita: (1) exige o vendor parado, (2) garante o backup original, (3) salva um snapshot,
/// (4) grava na ordem segura, (5) relê e compara; se divergir, volta ao snapshot.
/// </summary>
public sealed class FanTableService(IEcAccess ec, FanTableBackupStore store, Func<MachineInfo> machine, Func<string[]> vendorRunning)
{
    /// <summary>0x7A5 bit 3: o EC passa a seguir a tabela 0xF00 ("UserHiModeToFantable" no vendor).</summary>
    public const byte UserTableBit = 0x08;
    /// <summary>0x7A5 bit 2: modo silencioso do vendor. Desligado ao aplicar curva própria (como no modo Standard).</summary>
    public const byte QuietBit = 0x04;
    /// <summary>0x751 no modo Standard do vendor (MyFanManager_Intel.SetFanMode(2)).</summary>
    public const byte FanControlStandard = 0xA0;
    /// <summary>0x751 no modo Performance/Benchmark do vendor (bit 6 = FanBoost).</summary>
    public const byte FanControlPerformance = 0x40;

    public enum WriteOrder
    {
        /// <summary>Tabela, depois modos: liga a tabela do usuário só depois de ela estar gravada e conferida.</summary>
        TableFirst,
        /// <summary>Modos, depois tabela: desliga a tabela do usuário antes de mexer nela.</summary>
        ModesFirst,
    }

    /// <summary>Progresso das escritas (endereços processados, total). Útil porque cada acesso WMI leva ~100 ms.</summary>
    public event Action<int, int>? Progress;

    /// <summary>Lê a tabela duas vezes; só aceita se as duas leituras forem iguais.</summary>
    public FanTableSnapshot ReadStable(string createdBy = "AvellCC")
    {
        var first = ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength);
        var second = ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength);
        if (!first.AsSpan().SequenceEqual(second))
            throw new InvalidOperationException("A tabela mudou entre duas leituras: outro processo está escrevendo no EC.");
        return Snapshot(first, createdBy);
    }

    /// <summary>Cria o backup original se ainda não existir. Seguro para chamar sempre (só lê o EC).</summary>
    public (FanTableSnapshot Original, bool CreatedNow) EnsureOriginalBackup()
    {
        if (store.HasOriginal) return (store.LoadOriginal(), false);
        var snapshot = ReadStable("AvellCC/original");
        var created = store.TryCreateOriginal(snapshot);
        return (created ? snapshot : store.LoadOriginal(), created);
    }

    /// <summary>
    /// Volta ao estado do backup original. Os modos são gravados PRIMEIRO: com o original deste notebook
    /// (tabela zerada, 0x7A5 = 0x00), isso desliga a tabela do usuário antes de ela virar zeros.
    /// </summary>
    public void RestoreOriginal()
    {
        var original = store.LoadOriginal();
        Apply(original.FanModeRegisterValues(), original.Raw, WriteOrder.ModesFirst, "restore-original");
    }

    /// <summary>
    /// Aplica uma curva: grava e confere a tabela e SÓ DEPOIS liga a tabela do usuário
    /// (0x7A5: bit 3 ligado, bit 2 desligado; 0x751 conforme <see cref="FanCurve.EcMode"/>).
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

    /// <summary>true se o EC está com a tabela do usuário ligada.</summary>
    public bool IsUserTableActive() => (ec.Read(EcAddresses.ApOemByte3) & UserTableBit) != 0;

    /// <summary>Caminho único de escrita (internal: entradas públicas são ApplyCurve e RestoreOriginal).</summary>
    internal void Apply(IReadOnlyDictionary<ushort, byte> modes, byte[] table, WriteOrder order, string reason)
    {
        if (table.Length != EcAddresses.FanTableLength) throw new ArgumentException("Tamanho inválido.", nameof(table));
        if (modes.Keys.Any(a => !EcAddresses.FanModeRegisters.Contains(a)))
            throw new ArgumentException("Registrador de modo fora da lista permitida.", nameof(modes));

        var running = vendorRunning();                 // Regra 1: só um controlador por vez
        if (running.Length > 0) throw new VendorRunningException(running);

        EnsureOriginalBackup();                        // Regra 2: nunca escrever sem o original salvo
        var before = Snapshot(ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength), $"AvellCC/before-{reason}");
        store.SaveSnapshot(before, $"before-{reason}");

        try
        {
            Write(modes, table, before, order);
        }
        catch (Exception ex)
        {
            // Volta ao estado anterior desligando a tabela do usuário primeiro.
            var current = Snapshot(ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength), "AvellCC/rollback");
            Write(before.FanModeRegisterValues(), before.Raw, current, WriteOrder.ModesFirst);
            throw new InvalidOperationException($"Falha ao gravar ({ex.Message}). O estado anterior foi restaurado.", ex);
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
                if (ec.Read(address) != value) throw new InvalidOperationException($"Read-back divergente em 0x{address:X3}.");
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
            if (!after.AsSpan().SequenceEqual(table)) throw new InvalidOperationException("Read-back divergente na tabela.");
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
