namespace AvellCC.Core.Ec;

/// <summary>
/// Endereços da EC RAM da plataforma Uniwill (Avell A65 LIV).
/// Fonte: Define.ECSpec do GCUService.exe. Ver docs/HARDWARE.md.
/// </summary>
public static class EcAddresses
{
    // Identidade / capacidades
    public const ushort ProjectId = 0x740;
    public const ushort ApOemByte = 0x741;
    public const ushort SupportByte1 = 0x765;
    public const ushort SupportByte2 = 0x766;
    public const ushort TriggerByte = 0x767;
    public const ushort StatusByte = 0x768;

    // Temperaturas (°C, 1 byte). Confirmadas em 2026-11-26: 0x43E acompanha o Thermal Zone \_SB.ECTZ (±1 °C),
    // 0x44F acompanha a nvidia-smi (com alguns segundos de atraso). É o que o EC usa para escolher o nível da curva.
    public const ushort CpuTemp = 0x43E;
    public const ushort GpuTemp = 0x44F;

    // Ventoinhas — telemetria (hi, lo). Valores de 3300–5100 sob carga: provavelmente RPM direto.
    public const ushort Fan1High = 0x464;
    public const ushort Fan1Low = 0x465;
    public const ushort Fan2High = 0x46C;
    public const ushort Fan2Low = 0x46B;

    // Ventoinhas — modo
    public const ushort FanControl = 0x751;   // 0x40 = performance/boost, 0xA0 = standard/quiet
    public const ushort ApOemByte3 = 0x7A5;   // bit 3 = usar a tabela do usuário
    public const ushort TurboLevel = 0x78C;
    public const ushort ModeProfileIndex = 0x7AB;

    // Ventoinhas — tabela (16 níveis por bloco)
    public const ushort FanTableStart = 0xF00;
    public const int FanTableLength = 0x60;   // 0xF00..0xF5F
    public const int FanLevels = 16;
    public const ushort CpuUpTemp = 0xF00;
    public const ushort CpuDownTemp = 0xF10;
    public const ushort CpuDuty = 0xF20;
    public const ushort GpuUpTemp = 0xF30;
    public const ushort GpuDownTemp = 0xF40;
    public const ushort GpuDuty = 0xF50;

    /// <summary>Registradores de modo salvos junto com cada backup da tabela.</summary>
    public static readonly ushort[] FanModeRegisters = [FanControl, ApOemByte3, TurboLevel, ModeProfileIndex];
}
