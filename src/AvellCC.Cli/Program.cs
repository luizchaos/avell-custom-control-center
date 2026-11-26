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

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length == 0) return Usage();

var rawBytes = args.Contains("--raw");
args = args.Where(a => a != "--raw").ToArray();

try
{
    return args switch
    {
        ["kb", ..] => Keyboard(rawBytes ? [.. args[1..], "--raw"] : args[1..]),
        ["fan", "preview", var path] => Preview(FanCurve.Load(path)),
        _ => Ec(args, rawBytes),
    };
}
catch (VendorRunningException ex) { Console.Error.WriteLine(ex.Message); return 3; }
catch (ManagementException ex) { Console.Error.WriteLine($"Falha no WMI ({ex.ErrorCode}). Comandos de EC exigem administrador. {ex.Message}"); return 4; }
catch (UnauthorizedAccessException) { Console.Error.WriteLine("Acesso negado. Comandos de EC exigem administrador."); return 4; }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 1; }

static int Ec(string[] args, bool rawBytes)
{
    using var ec = new WmiEc();
    var store = new FanTableBackupStore();
    var fan = new FanTableService(ec, store, ReadMachineInfo(ec), VendorGuard.RunningVendorProcesses);
    var officeStore = new EcRegisterBackupStore(name: OfficeFanService.BackupGroup);
    var office = new OfficeFanService(ec, officeStore, ReadMachineInfo(ec), VendorGuard.RunningVendorProcesses);
    var directStore = new EcRegisterBackupStore(name: DirectFanService.BackupGroup);
    var direct = new DirectFanService(ec, directStore, ReadMachineInfo(ec), VendorGuard.RunningVendorProcesses);

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

        case ["ec", "dump", var startText, var lengthText]:
        {
            // SOMENTE LEITURA: só chama ec.Read.
            var start = Convert.ToUInt16(startText.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
            var length = Convert.ToInt32(lengthText.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
            if (length is < 1 or > 0x400) throw new ArgumentException("Tamanho entre 0x1 e 0x400.");
            var data = ec.ReadRange(start, length);
            Console.WriteLine("       00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F");
            for (var row = start & ~0xF; row < start + length; row += 16)
            {
                var cells = Enumerable.Range(0, 16).Select(c =>
                {
                    var a = row + c;
                    return a >= start && a < start + length ? data[a - start].ToString("X2", CultureInfo.InvariantCulture) : "  ";
                });
                Console.WriteLine($"{row:X4}:  {string.Join(' ', cells)}");
            }
            return 0;
        }

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

        case ["fan", "monitor", ..]:
            var seconds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 60;
            Console.WriteLine($"Modo: {office.ReadState().ModeName}");
            Monitor(ec, TimeSpan.FromSeconds(seconds), expected: null, watchdog: null, rawBytes, CancellationToken.None);
            return 0;

        case ["fan", "trial", var curvePath, ..]:
            var duration = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 180;
            return Trial(ec, fan, FanCurve.Load(curvePath), TimeSpan.FromSeconds(duration), rawBytes);

        case ["fan", "office", "show"]:
            PrintOffice(office.ReadState());
            return 0;

        case ["fan", "office", "backup"]:
            var (officeOriginal, officeCreated) = office.EnsureOriginalBackup();
            Console.WriteLine(officeCreated
                ? $"Backup original dos registradores criado: {officeStore.OriginalPath}"
                : $"Backup original já existia (de {officeOriginal.CreatedAt:g}), não foi alterado: {officeStore.OriginalPath}");
            return 0;

        case ["fan", "office", "trial", var officePath, ..]:
            var officeDuration = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 120;
            return OfficeTrial(ec, office, officeStore, OfficeFanCurve.Load(officePath), TimeSpan.FromSeconds(officeDuration), rawBytes);

        case ["fan", "office", "restore-original", "--yes"]:
            office.Progress += (done, total) => Console.Write($"\r  gravando {done}/{total}...");
            office.RestoreOriginal();
            Console.WriteLine("\nRegistradores originais restaurados e conferidos.");
            PrintOffice(office.ReadState());
            return 0;

        case ["fan", "office", "restore-original"]:
            Console.Error.WriteLine("Isto ESCREVE no EC. Confirme com: avellcc fan office restore-original --yes");
            return 2;

        case ["fan", "direct", "show"]:
            PrintDirect(direct.ReadState());
            return 0;

        case ["fan", "direct", "trial", var percentText, ..]:
            var percent = int.Parse(percentText, CultureInfo.InvariantCulture);
            var directDuration = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 60;
            return DirectTrial(ec, direct, directStore, percent, TimeSpan.FromSeconds(directDuration), rawBytes);

        case ["fan", "curve", "run", var runCurvePath, ..]:
            var runSeconds = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 0;
            return CurveRun(ec, direct, directStore, FanCurve.Load(runCurvePath), runSeconds, rawBytes);

        case ["fan", "direct", "off", "--yes"]:
            direct.Disable();
            Console.WriteLine("0x751 devolvido ao original (sem full fan mode).");
            PrintDirect(direct.ReadState());
            return 0;

        default:
            return Usage();
    }
}

static void PrintDirect(DirectState s)
{
    Console.WriteLine($"0x751 (modo)          : 0x{s.FanControl:X2}  full fan mode (bit 6): {(s.FullFanMode ? "LIGADO" : "desligado")}");
    Console.WriteLine($"0x1804 / 0x1809 (duty): {s.Fan1DutyRaw} / {s.Fan2DutyRaw}  (= {s.Fan1DutyRaw / 2.0:0.#}% / {s.Fan2DutyRaw / 2.0:0.#}%)");
    Console.WriteLine($"0x78E (status)        : 0x{s.FanCtrlStatus:X2}  controle universal (bit 6): {(s.HasUniversalControl ? "sim" : "não")}");
    Console.WriteLine($"0x727 (custom profile): 0x{s.CustomProfile:X2}");
}

static int DirectTrial(IEcAccess ec, DirectFanService direct, EcRegisterBackupStore store, int percent, TimeSpan duration, bool rawBytes)
{
    DirectFanService.ToRaw(percent); // valida antes de qualquer coisa
    Console.WriteLine($"Controle direto: as duas ventoinhas em {percent}% (≈ {FanCalibration.EstimateRpm(percent)} RPM). Sempre restaura no fim.");
    Console.WriteLine("\nEstado antes:");
    PrintDirect(direct.ReadState());
    Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);

    var (_, created) = direct.EnsureOriginalBackup();
    Console.WriteLine(created ? $"Backup original criado: {store.OriginalPath}" : $"Backup original: {store.OriginalPath} (já existia)");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    string? abort = null;
    try
    {
        Console.WriteLine("Ligando (duty → 0x751 bit 6 → duty de novo)...");
        direct.Enable(percent, percent);
        PrintDirect(direct.ReadState());
        var end = DateTimeOffset.Now + duration;
        var watchdog = new FanWatchdog();
        while (DateTimeOffset.Now < end && !cts.IsCancellationRequested && abort is null)
        {
            direct.SetDuty(percent, percent);                 // regrava a cada ciclo
            abort = Monitor(ec, TimeSpan.Zero, _ => (percent, percent), watchdog, rawBytes, cts.Token);
            cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        }
    }
    finally
    {
        Console.WriteLine(abort is not null ? $"\n!! WATCHDOG: {abort}" : cts.IsCancellationRequested ? "\nCancelado." : "\nFim do teste.");
        Console.WriteLine("Desligando o controle direto (0x751 → original)...");
        direct.Disable();
        PrintDirect(direct.ReadState());
        Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);
    }
    return abort is null ? 0 : 5;
}

/// <summary>Loop de controle em primeiro plano: é o núcleo do futuro serviço. Ctrl+C devolve o controle ao firmware.</summary>
static int CurveRun(IEcAccess ec, DirectFanService direct, EcRegisterBackupStore store, FanCurve curve, int seconds, bool rawBytes)
{
    var controller = new FanCurveController(curve);
    Console.WriteLine($"Curva '{curve.Name}' (histerese {curve.Hysteresis} °C, {(curve.Shared ? "ventoinhas juntas" : "CPU→fan1, GPU→fan2")}).");
    Console.WriteLine($"CPU: {string.Join("  ", curve.Cpu.Select(p => $"{p.Temp}°C→{p.Duty}%"))}");
    Console.WriteLine($"GPU: {string.Join("  ", curve.Gpu.Select(p => $"{p.Temp}°C→{p.Duty}%"))}");
    Console.WriteLine(seconds > 0 ? $"Rodando por {seconds} s." : "Rodando até Ctrl+C.");

    var (_, created) = direct.EnsureOriginalBackup();
    if (created) Console.WriteLine($"Backup original criado: {store.OriginalPath}");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var end = seconds > 0 ? DateTimeOffset.Now.AddSeconds(seconds) : DateTimeOffset.MaxValue;
    var writes = 0;
    try
    {
        var first = controller.Step(FanSample.Read(ec));
        direct.Enable(first.Fan1, first.Fan2);
        writes++;
        Console.WriteLine($"Controle direto ligado: {first.Fan1}% / {first.Fan2}%");

        while (!cts.IsCancellationRequested && DateTimeOffset.Now < end)
        {
            cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            if (cts.IsCancellationRequested) break;

            var s = FanSample.Read(ec);
            var d = controller.Step(s);

            if ((ec.Read(EcAddresses.FanControl) & EcAddresses.FullFanModeBit) == 0)
            {
                Console.WriteLine("!! 0x751 perdeu o bit 6 (tecla Fn / BIOS?). Reassumindo.");
                direct.Enable(d.Fan1, d.Fan2);
                writes++;
            }
            else if (d.ShouldWrite)
            {
                direct.SetDuty(d.Fan1, d.Fan2);
                writes++;
            }

            var line = $"{s.Time:HH:mm:ss}  CPU {s.CpuTemp,3} °C (ef {d.CpuEffective,5:0.#})  GPU {s.GpuTemp,3} °C (ef {d.GpuEffective,5:0.#})  " +
                       $"→ {d.Fan1,3}% / {d.Fan2,3}%{(d.ShouldWrite ? "*" : " ")} (≈{FanCalibration.EstimateRpm(d.Fan1),5})  fan1 {s.Fan1,5}  fan2 {s.Fan2,5}";
            if (d.Reason is not null) line += $"  !! {d.Reason}";
            if (rawBytes) line += $"  duty {ec.Read(EcAddresses.DirectFan1Duty)}/{ec.Read(EcAddresses.DirectFan2Duty)}";
            Console.WriteLine(line);
        }
    }
    finally
    {
        Console.WriteLine($"\nEncerrando ({writes} gravações). Devolvendo o controle ao firmware...");
        direct.Disable();
        PrintDirect(direct.ReadState());
    }
    return 0;
}

static void PrintOffice(OfficeState s)
{
    Console.WriteLine($"Modo (0x751)      : 0x{s.FanControl:X2} = {s.ModeName}");
    Console.WriteLine($"Custom profile    : 0x727 = 0x{s.CustomProfile:X2} (bit 6 {(((s.CustomProfile & 0x40) != 0) ? "ligado" : "desligado")})");
    Console.WriteLine($"T1..T5 (0x743)    : {string.Join(" / ", s.Duties.Select(d => $"{d}%"))}");
    Console.WriteLine($"Defaults (0x786)  : {string.Join(" / ", s.DefaultDuties.Select(d => $"{d}%"))}");
    Console.WriteLine($"Mín/temp/extra    : 0x{s.MinSpeed:X2} / 0x{s.MinTemp:X2} / 0x{s.ExtraSpeed:X2}");
    Console.WriteLine($"Suporta MyFan3    : {(s.SupportsMyFan3 ? "sim" : "não")}");
}

static int OfficeTrial(IEcAccess ec, OfficeFanService office, EcRegisterBackupStore store, OfficeFanCurve curve, TimeSpan duration, bool rawBytes)
{
    Console.WriteLine($"Curva Office '{curve.Name}': T1..T5 = {string.Join(" / ", curve.Duties.Select(d => $"{d}%"))}  (≈ {string.Join(" / ", curve.Duties.Select(d => $"{FanCalibration.EstimateRpm(d)}"))} RPM)");
    Console.WriteLine("\nEstado antes:");
    PrintOffice(office.ReadState());
    Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);

    var (_, created) = office.EnsureOriginalBackup();
    Console.WriteLine(created ? $"Backup original criado: {store.OriginalPath}" : $"Backup original: {store.OriginalPath} (já existia)");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    office.Progress += (done, total) => Console.Write($"\r  gravando {done}/{total}...");

    Console.WriteLine("Aplicando (velocidades primeiro, depois 0x751 = 0xA0)...");
    office.Apply(curve);
    Console.WriteLine("\nAplicado. Monitorando com watchdog. Ctrl+C restaura o original.");
    PrintOffice(office.ReadState());

    // Nível do firmware (0x460) 1..5 → T1..T5. Fora disso, assume a menor velocidade para o watchdog.
    (double, double) Expected(FanSample s)
    {
        var d = s.Level is >= 1 and <= OfficeFanCurve.Levels ? curve.Duties[s.Level - 1] : curve.Duties[0];
        return (d, d);
    }
    var abort = Monitor(ec, duration, Expected, new FanWatchdog(), rawBytes, cts.Token);

    bool keep;
    if (abort is not null) { Console.WriteLine($"\n!! WATCHDOG: {abort}"); keep = false; }
    else if (cts.IsCancellationRequested) { Console.WriteLine("\nCancelado."); keep = false; }
    else
    {
        Console.Write("\nManter esta curva? (pode voltar ao padrão no reboot) [s/N]: ");
        keep = Console.ReadLine()?.Trim().ToLowerInvariant() is "s" or "sim";
    }

    if (keep) { Console.WriteLine("Curva mantida."); return 0; }
    Console.WriteLine("Restaurando o original (0x751 primeiro)...");
    office.RestoreOriginal();
    Console.WriteLine("\nOriginal restaurado e conferido.");
    PrintOffice(office.ReadState());
    Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);
    return abort is null ? 0 : 5;
}

static int Preview(FanCurve curve)
{
    var table = FanTableEncoder.Encode(curve);
    Console.WriteLine($"Curva '{curve.Name}' (histerese {curve.Hysteresis} °C) → tabela do EC (nada é gravado):");
    Console.WriteLine("nível | CPU desce<  sobe>  duty% | GPU desce<  sobe>  duty%");
    var cpu = FanTableEncoder.Decode(table, 0x00);
    var gpu = FanTableEncoder.Decode(table, 0x30);
    for (var i = 0; i < FanTableEncoder.Levels; i++)
        Console.WriteLine($"{i,5} | {cpu[i].StartTemp,9} {Fmt(cpu[i].EndTemp),6} {cpu[i].DutyPercent,6:0.#} | {gpu[i].StartTemp,9} {Fmt(gpu[i].EndTemp),6} {gpu[i].DutyPercent,6:0.#}");
    Console.WriteLine($"raw 0xF00: {Convert.ToHexString(table)}");
    return 0;

    static string Fmt(byte end) => end == 0xFF ? "max" : end.ToString(CultureInfo.InvariantCulture);
}

static int Trial(IEcAccess ec, FanTableService fan, FanCurve curve, TimeSpan duration, bool rawBytes)
{
    Preview(curve);
    Console.WriteLine($"ecMode: {curve.EcMode}");
    Console.WriteLine();
    Console.WriteLine("Estado antes:"); Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    fan.Progress += (done, total) => Console.Write($"\r  gravando {done}/{total}...");

    Console.WriteLine("\nAplicando curva (tabela primeiro, depois liga a tabela do usuário)...");
    var table = fan.ApplyCurve(curve);
    Console.WriteLine("\nCurva aplicada. Monitorando com watchdog. Ctrl+C restaura o original.");

    var cpuLevels = FanTableEncoder.Decode(table, 0x00);
    var gpuLevels = FanTableEncoder.Decode(table, 0x30);
    Console.WriteLine($"0x751=0x{ec.Read(EcAddresses.FanControl):X2}  0x7A5=0x{ec.Read(EcAddresses.ApOemByte3):X2}");
    var abort = Monitor(ec, duration,
        s => (FanTableEncoder.ExpectedDuty(cpuLevels, s.CpuTemp), FanTableEncoder.ExpectedDuty(gpuLevels, s.GpuTemp)),
        new FanWatchdog(), rawBytes, cts.Token);

    bool keep;
    if (abort is not null) { Console.WriteLine($"\n!! WATCHDOG: {abort}"); keep = false; }
    else if (cts.IsCancellationRequested) { Console.WriteLine("\nCancelado."); keep = false; }
    else
    {
        Console.Write("\nManter esta curva? (o EC pode voltar ao firmware no reboot) [s/N]: ");
        keep = Console.ReadLine()?.Trim().ToLowerInvariant() is "s" or "sim";
    }

    if (keep) { Console.WriteLine("Curva mantida."); return 0; }
    Console.WriteLine("Restaurando o original...");
    fan.RestoreOriginal();
    Console.WriteLine("\nOriginal restaurado e conferido.");
    Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);
    return abort is null ? 0 : 5;
}

/// <summary>Imprime amostras até o fim do tempo. Retorna o motivo de aborto do watchdog, se houver.</summary>
static string? Monitor(IEcAccess ec, TimeSpan duration, Func<FanSample, (double Cpu, double Gpu)>? expected, FanWatchdog? watchdog, bool rawBytes, CancellationToken cancel)
{
    var end = DateTimeOffset.Now + duration;
    do
    {
        var s = FanSample.Read(ec);
        var line = $"{s.Time:HH:mm:ss}  CPU {s.CpuTemp,3} °C  GPU {s.GpuTemp,3} °C  fan1 {s.Fan1,5}  fan2 {s.Fan2,5}  nível {s.Level} ({s.LevelTemp} °C)";
        if (rawBytes) line += $"  duty {ec.Read(EcAddresses.DirectFan1Duty),3}/{ec.Read(EcAddresses.DirectFan2Duty),3}  [460..46F] {Convert.ToHexString(ec.ReadRange(0x460, 16))}";
        if (expected is not null)
        {
            var (cpuDuty, gpuDuty) = expected(s);
            line += $"  | esperado: {cpuDuty,5:0.#}% ≈ {FanCalibration.EstimateRpm(cpuDuty),5} RPM";
            if (watchdog?.Check(s, cpuDuty, gpuDuty) is { } reason) { Console.WriteLine(line); return reason; }
        }
        Console.WriteLine(line);
        if (duration == TimeSpan.Zero) break;
        cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
    } while (DateTimeOffset.Now < end && !cancel.IsCancellationRequested);
    return null;
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
          avellcc fan preview <curva.json>                                      (simula, não grava)
          avellcc status | fan show | fan backup | fan monitor [segundos] [--raw] (admin, só leitura)
          avellcc ec dump <inicio hex> <tamanho hex>                             (admin, só leitura)
          avellcc fan office show | fan office backup                           (admin, só leitura)
          avellcc fan office trial <office.json> [segundos] [--raw]             (admin, GRAVA com watchdog)
          avellcc fan office restore-original --yes                             (admin, GRAVA)
          avellcc fan direct show                                               (admin, só leitura)
          avellcc fan direct trial <percent 25-100> [segundos] [--raw]          (admin, GRAVA, sempre restaura)
          avellcc fan direct off --yes                                          (admin, desliga o controle direto)
          avellcc fan curve run <curva.json> [segundos] [--raw]                 (admin, aplica a curva; Ctrl+C devolve ao firmware)
          avellcc fan trial <curva.json> ...   (tabela 0xF00: ignorada por este EC, só referência)
          avellcc fan restore-original --yes                                    (admin, GRAVA)
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
