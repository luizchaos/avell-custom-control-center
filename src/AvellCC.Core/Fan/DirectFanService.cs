using AvellCC.Core.Ec;
using AvellCC.Core.Safety;

namespace AvellCC.Core.Fan;

/// <summary>
/// Controle direto (legado) das ventoinhas, como no tuxedo-drivers para EC Uniwill sem controle universal
/// (0x78E bit 6 = 0, caso deste notebook):
///   ligar    → grava o duty em 0x1804/0x1809, liga 0x751 bit 6 ("full fan mode") e regrava o duty algumas vezes
///              logo em seguida, antes de o EC completar a aceleração para o máximo;
///   manter   → regrava o duty periodicamente (quem chama: o loop do serviço / do teste);
///   desligar → devolve 0x751 ao valor original. O EC volta à curva interna.
/// Falha segura: se o processo morrer com o bit 6 ligado, o modo "full fan" tende a acelerar as ventoinhas, não a pará-las.
/// </summary>
public sealed class DirectFanService(IEcAccess ec, EcRegisterBackupStore store, Func<MachineInfo> machine, Func<string[]> vendorRunning)
{
    public const string BackupGroup = "ec-direct";
    /// <summary>Mínimo com a ventoinha girando (tuxedo: FAN_ON_MIN_SPEED_PERCENT). Nunca gravamos 0.</summary>
    public const int MinPercent = 25;

    public static readonly ushort[] TrackedRegisters = [EcAddresses.FanControl, EcAddresses.DirectFan1Duty, EcAddresses.DirectFan2Duty];

    private byte? _originalFanControl;

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
            throw new ArgumentOutOfRangeException(nameof(percent), $"Duty direto entre {MinPercent} e 100 % (é {percent}).");
        return (byte)(percent * 2);
    }

    public (EcRegisterSnapshot Original, bool CreatedNow) EnsureOriginalBackup()
    {
        if (store.HasOriginal) return (store.LoadOriginal(), false);
        var first = ReadTracked();
        var second = ReadTracked();
        if (first[EcAddresses.FanControl] != second[EcAddresses.FanControl])
            throw new InvalidOperationException("0x751 mudou entre duas leituras: outro processo está escrevendo no EC.");
        var snapshot = EcRegisterSnapshot.From(first, machine(), vendorRunning(), "AvellCC/original");
        var created = store.TryCreateOriginal(snapshot);
        return (created ? snapshot : store.LoadOriginal(), created);
    }

    public void Enable(int fan1Percent, int fan2Percent)
    {
        var running = vendorRunning();                 // Regra 1
        if (running.Length > 0) throw new VendorRunningException(running);
        var (original, _) = EnsureOriginalBackup();    // Regra 2
        store.SaveSnapshot(EcRegisterSnapshot.From(ReadTracked(), machine(), vendorRunning(), "AvellCC/before-direct"), "before-direct");

        var current = ec.Read(EcAddresses.FanControl);
        if ((current & EcAddresses.FullFanModeBit) != 0)
            throw new InvalidOperationException($"0x751 já está com o bit 6 ligado (0x{current:X2}): outro controle ativo?");
        _originalFanControl = original.Values()[EcAddresses.FanControl];

        try
        {
            SetDuty(fan1Percent, fan2Percent);
            ec.Write(EcAddresses.FanControl, (byte)(current | EcAddresses.FullFanModeBit));
            for (var i = 0; i < 10; i++) { SetDuty(fan1Percent, fan2Percent); Thread.Sleep(10); }   // antes do ramp-up completo
            if ((ec.Read(EcAddresses.FanControl) & EcAddresses.FullFanModeBit) == 0)
                throw new InvalidOperationException("Read-back divergente em 0x751.");
        }
        catch
        {
            Disable();
            throw;
        }
    }

    /// <summary>Grava o duty das duas ventoinhas (chamar periodicamente enquanto ativo).</summary>
    public void SetDuty(int fan1Percent, int fan2Percent)
    {
        var a = ToRaw(fan1Percent);
        var b = ToRaw(fan2Percent);
        ec.Write(EcAddresses.DirectFan1Duty, a);
        ec.Write(EcAddresses.DirectFan2Duty, b);
    }

    /// <summary>Devolve 0x751 ao valor original (sem o bit 6) e confere. Idempotente.</summary>
    public void Disable()
    {
        var target = _originalFanControl ?? (store.HasOriginal ? store.LoadOriginal().Values()[EcAddresses.FanControl] : (byte)0x00);
        target = (byte)(target & ~EcAddresses.FullFanModeBit);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ec.Write(EcAddresses.FanControl, target);
            if (ec.Read(EcAddresses.FanControl) == target) { _originalFanControl = null; return; }
            Thread.Sleep(50);
        }
        throw new InvalidOperationException($"Não foi possível devolver 0x751 a 0x{target:X2}.");
    }

    private Dictionary<ushort, byte> ReadTracked() => TrackedRegisters.ToDictionary(a => a, ec.Read);
}

public sealed record DirectState(byte FanControl, byte Fan1DutyRaw, byte Fan2DutyRaw, byte FanCtrlStatus, byte CustomProfile)
{
    public bool HasUniversalControl => (FanCtrlStatus & EcAddresses.UniversalFanCtrlBit) != 0;
    public bool FullFanMode => (FanControl & EcAddresses.FullFanModeBit) != 0;
}
