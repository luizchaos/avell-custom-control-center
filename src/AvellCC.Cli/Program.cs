using System.Globalization;
using System.Management;
using AvellCC.Core.Ec;
using AvellCC.Core.Fan;
using AvellCC.Core.Keyboard;
using AvellCC.Core.Safety;

// EC (exige administrador):
//   avellcc status | fan show | fan backup | fan restore-original --yes
// Teclado (não exige administrador):
//   avellcc kb info
//   avellcc kb color <RRGGBB> [-b 0-4] [--save] [--raw]
//   avellcc kb zones <RRGGBB> <RRGGBB> <RRGGBB> <RRGGBB> [-b 0-4] [--save] [--raw]
//   avellcc kb effect <nome> [-s 0-4] [-b 0-4] [-d ltr|rtl|btt|ttb] [-c RRGGBB,RRGGBB,...] [--save] [--raw]
//   avellcc kb brightness <0-4> | kb off | kb raw <9 bytes hex>

if (args.Length == 0) return Usage();

try
{
    return args[0] == "kb" ? Keyboard(args[1..]) : Ec(args);
}
catch (VendorRunningException ex) { Console.Error.WriteLine(ex.Message); return 3; }
catch (ManagementException ex) { Console.Error.WriteLine($"Falha no WMI ({ex.ErrorCode}). Comandos de EC exigem administrador. {ex.Message}"); return 4; }
catch (UnauthorizedAccessException) { Console.Error.WriteLine("Acesso negado. Comandos de EC exigem administrador."); return 4; }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 1; }

static int Ec(string[] args)
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
            Console.WriteLine("Estado original restaurado (modos + tabela) e conferido.");
            return 0;

        case ["fan", "restore-original"]:
            Console.Error.WriteLine("Isto ESCREVE no EC. Confirme com: avellcc fan restore-original --yes");
            return 2;

        default:
            return Usage();
    }
}

static int Keyboard(string[] args)
{
    if (args.Length == 0) return Usage();
    var options = Options.Parse(args[1..]);
    using var kb = new FourZoneKeyboard(HidFeatureDevice.Open(Ite8291.VendorId, Ite8291.UsagePage4Zone),
                                        FourZoneKeyboard.DefaultProjectId, colorCorrection: !options.Raw);
    switch (args[0])
    {
        case "info":
            Console.WriteLine($"Firmware : {Convert.ToHexString(kb.FirmwareVersion())}");
            Console.WriteLine($"Efeito   : {kb.CurrentEffect()}");
            return 0;
        case "color" when options.Positional.Count == 1:
            kb.Static(Options.ParseRgb(options.Positional[0]), options.Brightness, options.Save);
            return 0;
        case "zones" when options.Positional.Count == Ite8291.Zones:
            kb.Zones(options.Positional.Select(Options.ParseRgb).ToArray(), options.Brightness, options.Save);
            return 0;
        case "effect" when options.Positional.Count == 1:
            if (!Enum.TryParse<Ite8291.Effect>(options.Positional[0], ignoreCase: true, out var effect))
                throw new ArgumentException($"Efeito desconhecido. Opções: {string.Join(", ", Enum.GetNames<Ite8291.Effect>())}");
            kb.Effect(effect, options.Speed, options.Brightness, options.Direction, options.Colors, options.Save);
            return 0;
        case "brightness" when options.Positional.Count == 1:
            kb.Brightness(int.Parse(options.Positional[0], CultureInfo.InvariantCulture));
            return 0;
        case "off":
            kb.Off();
            return 0;
        case "raw" when options.Positional.Count == Ite8291.FeatureReportLength:
            kb.Raw(options.Positional.Select(h => Convert.ToByte(h, 16)).ToArray());
            return 0;
        default:
            return Usage();
    }
}

static int Usage()
{
    Console.Error.WriteLine("""
        uso:
          avellcc status | fan show | fan backup | fan restore-original --yes      (admin)
          avellcc kb info
          avellcc kb color <RRGGBB> [-b 0-4] [--save] [--raw]
          avellcc kb zones <RRGGBB> <RRGGBB> <RRGGBB> <RRGGBB> [-b 0-4] [--save] [--raw]
          avellcc kb effect <nome> [-s 0-4] [-b 0-4] [-d ltr|rtl|btt|ttb] [-c RRGGBB,...] [--save]
          avellcc kb brightness <0-4> | kb off | kb raw <9 bytes hex>
        """);
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

sealed record Options(List<string> Positional, int Brightness, int Speed, Ite8291.Direction Direction, List<Rgb>? Colors, bool Save, bool Raw)
{
    public static Options Parse(string[] args)
    {
        var positional = new List<string>();
        int brightness = 4, speed = 2;
        var direction = Ite8291.Direction.LeftToRight;
        List<Rgb>? colors = null;
        bool save = false, raw = false;
        var i = 0;
        for (; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-b": brightness = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "-s": speed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "-d":
                    direction = Next() switch
                    {
                        "ltr" => Ite8291.Direction.LeftToRight, "rtl" => Ite8291.Direction.RightToLeft,
                        "btt" => Ite8291.Direction.BottomToTop, "ttb" => Ite8291.Direction.TopToBottom,
                        var d => throw new ArgumentException($"Direção inválida: {d}"),
                    };
                    break;
                case "-c": colors = Next().Split(',').Select(ParseRgb).ToList(); break;
                case "--save": save = true; break;
                case "--raw": raw = true; break;
                default: positional.Add(args[i]); break;
            }
        }
        return new Options(positional, brightness, speed, direction, colors, save, raw);

        string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"Falta valor depois de {args[i - 1]}");
    }

    public static Rgb ParseRgb(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) throw new ArgumentException($"Cor inválida: {hex} (use RRGGBB)");
        var v = Convert.ToInt32(hex, 16);
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
