using AvellCC.Core.Ec;
using AvellCC.Core.Safety;

namespace AvellCC.Core.Fan;

/// <summary>
/// Leitura, backup e restauração da tabela de ventoinha.
/// Toda escrita: (1) exige o vendor parado, (2) garante o backup original, (3) salva um snapshot,
/// (4) grava, (5) relê e compara; se divergir, volta ao snapshot.
/// </summary>
public sealed class FanTableService(IEcAccess ec, FanTableBackupStore store, Func<MachineInfo> machine, Func<string[]> vendorRunning)
{
    /// <summary>Lê a tabela duas vezes; só aceita se as duas leituras forem iguais.</summary>
    public FanTableSnapshot ReadStable(string createdBy = "AvellCC")
    {
        var first = ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength);
        var second = ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength);
        if (!first.AsSpan().SequenceEqual(second))
            throw new InvalidOperationException("A tabela mudou entre duas leituras: outro processo está escrevendo no EC.");

        var modes = EcAddresses.FanModeRegisters.ToDictionary(a => a, ec.Read);
        return FanTableSnapshot.FromRaw(first, machine(), modes, vendorRunning(), createdBy);
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
    /// Volta ao estado do backup original: registradores de modo e bytes da tabela.
    /// Os modos são gravados PRIMEIRO. Com o original deste notebook (tabela zerada, 0x7A5 = 0x00),
    /// isso desliga o modo "tabela do usuário" antes de a tabela virar zeros. Assim o EC nunca roda
    /// com uma tabela de duty 0.
    /// </summary>
    public void RestoreOriginal()
    {
        var original = store.LoadOriginal();
        Apply(original.FanModeRegisterValues(), original.Raw, "restore-original");
    }

    /// <summary>
    /// Caminho único de escrita. Não é público: curvas novas passam pelo validador (M3).
    /// <paramref name="modes"/> é gravado antes da tabela.
    /// </summary>
    internal void Apply(IReadOnlyDictionary<ushort, byte> modes, byte[] table, string reason)
    {
        if (table.Length != EcAddresses.FanTableLength) throw new ArgumentException("Tamanho inválido.", nameof(table));
        if (modes.Keys.Any(a => !EcAddresses.FanModeRegisters.Contains(a)))
            throw new ArgumentException("Registrador de modo fora da lista permitida.", nameof(modes));
        var running = vendorRunning();                 // Regra 1: só um controlador por vez
        if (running.Length > 0) throw new VendorRunningException(running);

        EnsureOriginalBackup();                       // Regra 2: nunca escrever sem o original salvo
        var before = ReadStable($"AvellCC/before-{reason}");
        store.SaveSnapshot(before, $"before-{reason}");

        try
        {
            WriteVerified(modes, table);
        }
        catch (Exception ex) when (ex is not VendorRunningException)
        {
            WriteVerified(before.FanModeRegisterValues(), before.Raw);
            throw new InvalidOperationException($"Falha ao gravar ({ex.Message}). O estado anterior foi restaurado.", ex);
        }
    }

    private void WriteVerified(IReadOnlyDictionary<ushort, byte> modes, byte[] table)
    {
        foreach (var (address, value) in modes) WriteIfDifferent(address, value);
        for (var i = 0; i < table.Length; i++) WriteIfDifferent((ushort)(EcAddresses.FanTableStart + i), table[i]);

        foreach (var (address, value) in modes)
            if (ec.Read(address) != value) throw new InvalidOperationException($"Read-back divergente em 0x{address:X3}.");
        var after = ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength);
        if (!after.AsSpan().SequenceEqual(table)) throw new InvalidOperationException("Read-back divergente na tabela.");
    }

    private void WriteIfDifferent(ushort address, byte value)
    {
        if (ec.Read(address) != value) ec.Write(address, value);
    }
}
