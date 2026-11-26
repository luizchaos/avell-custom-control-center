using System.Management;
using AvellCC.Core.Ec;
using AvellCC.Core.Fan;
using AvellCC.Core.Safety;

// avellcc fan show                 — lê e mostra a tabela (somente leitura)
// avellcc fan backup               — cria o backup original se ainda não existir (somente leitura no EC)
// avellcc fan restore-original --yes — grava de volta os bytes do backup original (ESCREVE no EC)
// avellcc status                   — RPM, modo e identidade (somente leitura)

if (args.Length == 0) return Usage();

try
{
    using var ec = new WmiEc();
    var store = new FanTableBackupStore();
    var fan = new FanTableService(ec, store, ReadMachineInfo(ec), VendorGuard.RunningVendorProcesses);

    switch (args)
    {
        case ["status"]:
            var vendor = VendorGuard.RunningVendorProcesses();
            Console.WriteLine($"Project ID : 0x{ec.Read(EcAddresses.ProjectId):X2}");
            Console.WriteLine($"Fan mode   : 0x751=0x{ec.Read(EcAddresses.FanControl):X2}  0x7A5=0x{ec.Read(EcAddresses.ApOemByte3):X2}");
            Console.WriteLine($"CPU / GPU  : {ec.Read(EcAddresses.CpuTemp)} °C / {ec.Read(EcAddresses.GpuTemp)} °C (EC)");
            Console.WriteLine($"Fan 1 / 2  : {ec.ReadWord(EcAddresses.Fan1High, EcAddresses.Fan1Low)} / {ec.ReadWord(EcAddresses.Fan2High, EcAddresses.Fan2Low)} (raw, provável RPM)");
            Console.WriteLine($"Vendor     : {(vendor.Length == 0 ? "parado" : string.Join(", ", vendor))}");
            return 0;

        case ["fan", "show"]:
            PrintTable(fan.ReadStable());
            return 0;

        case ["fan", "backup"]:
            var (original, created) = fan.EnsureOriginalBackup();
            Console.WriteLine(created
                ? $"Backup original criado: {store.OriginalPath}"
                : $"Backup original já existia (de {original.CreatedAt:g}), não foi alterado: {store.OriginalPath}");
            return 0;

        case ["fan", "restore-original", "--yes"]:
            fan.RestoreOriginal();
            Console.WriteLine("Tabela original restaurada e conferida byte a byte.");
            return 0;

        case ["fan", "restore-original"]:
            Console.Error.WriteLine("Isto ESCREVE no EC. Confirme com: avellcc fan restore-original --yes");
            return 2;

        default:
            return Usage();
    }
}
catch (VendorRunningException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}
catch (ManagementException ex)
{
    Console.Error.WriteLine($"Falha no WMI ({ex.ErrorCode}). Rode como administrador. {ex.Message}");
    return 4;
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine("Acesso negado. Rode como administrador.");
    return 4;
}

static int Usage()
{
    Console.Error.WriteLine("uso: avellcc status | fan show | fan backup | fan restore-original --yes");
    return 1;
}

static Func<MachineInfo> ReadMachineInfo(IEcAccess ec) => () =>
{
    using var cs = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem").Get().Cast<ManagementObject>().First();
    using var bios = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS").Get().Cast<ManagementObject>().First();
    return new MachineInfo
    {
        Manufacturer = cs["Manufacturer"]?.ToString() ?? "",
        Model = cs["Model"]?.ToString() ?? "",
        Bios = bios["SMBIOSBIOSVersion"]?.ToString() ?? "",
        ProjectId = $"0x{ec.Read(EcAddresses.ProjectId):X2}",
    };
};

static void PrintTable(FanTableSnapshot snapshot)
{
    Console.WriteLine($"SHA-256 {snapshot.Sha256}");
    Console.WriteLine("nível | CPU subida descida duty% | GPU subida descida duty%");
    for (var i = 0; i < EcAddresses.FanLevels; i++)
    {
        var c = snapshot.CpuLevels[i];
        var g = snapshot.GpuLevels[i];
        Console.WriteLine($"{i,5} | {c.UpRaw,10} {c.DownRaw,7} {c.DutyPercent,5:0.#} | {g.UpRaw,10} {g.DownRaw,7} {g.DutyPercent,5:0.#}");
    }
}
