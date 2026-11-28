using System.Globalization;
using System.Management;
using AvellCC.Core.Ec;
using AvellCC.Core.Fan;
using AvellCC.Core.Keyboard;
using AvellCC.Core.Monitoring;
using AvellCC.Core.Safety;

// EC (requires administrator):
//   avellcc status | fan show | fan backup | fan restore-original --yes
// Keyboard (no administrator needed):
//   avellcc kb info
//   avellcc kb color <RRGGBB> [-b 0-4] [--save] [--raw]
//   avellcc kb zones <RRGGBB> <RRGGBB> <RRGGBB> <RRGGBB> [-b 0-4] [--save] [--raw]
//   avellcc kb effect <name> [-s 0-4] [-b 0-4] [-d ltr|rtl|btt|ttb] [-c RRGGBB,RRGGBB,...] [--save] [--raw]
//   avellcc kb brightness <0-4> | kb off | kb raw <9 bytes hex>

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length == 0) return Usage();

var rawBytes = args.Contains("--raw");
args = args.Where(a => a != "--raw").ToArray();

try
{
    return args switch
    {
        ["sys"] => Sys(3),
        ["sys", var n] => Sys(int.Parse(n, CultureInfo.InvariantCulture)),
        ["kb", ..] => Keyboard(rawBytes ? [.. args[1..], "--raw"] : args[1..]),
        ["lb", ..] => Keyboard(rawBytes ? [.. args[1..], "--raw"] : args[1..], lightbar: true),
        ["fan", "preview", var path] => Preview(FanCurve.Load(path)),
        _ => Ec(args, rawBytes),
    };
}
catch (VendorRunningException ex) { Console.Error.WriteLine(ex.Message); return 3; }
catch (ManagementException ex) { Console.Error.WriteLine($"WMI failure ({ex.ErrorCode}). EC commands require administrator rights. {ex.Message}"); return 4; }
catch (UnauthorizedAccessException) { Console.Error.WriteLine("Access denied. EC commands require administrator rights."); return 4; }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 1; }

// Prints what the dashboard shows (CPU/GPU usage, memory, drives, network, power), one sample per second.
static int Sys(int samples)
{
    using var monitor = new SystemMonitor();
    var info = monitor.Info;
    Console.WriteLine($"CPU  {info.CpuName}");
    Console.WriteLine($"RAM  {MonitorMath.Bytes(info.InstalledMemory)} installed");
    foreach (var g in info.Gpus)
        Console.WriteLine($"GPU  {g.Name}  vendor=0x{g.VendorId:X4} luid=0x{g.Luid:X} vram={MonitorMath.Bytes(g.DedicatedMemory)} {(g.IsDiscrete ? "discrete" : "integrated")}");
    for (var i = 0; i < samples; i++)
    {
        Thread.Sleep(1000);
        var s = monitor.Sample();
        Console.WriteLine();
        Console.WriteLine($"cpu {s.CpuPercent:0.0} %   mem {s.Memory.Percent:0.0} % ({MonitorMath.Bytes(s.Memory.Used)} / {MonitorMath.Bytes(s.Memory.Total)})   disk active {(s.DiskActivePercent is { } d ? $"{d:0.0} %" : "n/a")}");
        foreach (var g in s.Gpus)
            Console.WriteLine($"  gpu {g.Adapter.Name}: {g.Percent:0.0} %  vram {(g.VramUsed is { } v ? MonitorMath.Bytes(v) : "n/a")}");
        foreach (var dr in s.Drives)
            Console.WriteLine($"  drive {dr.Name} '{dr.Label}': {MonitorMath.Bytes(dr.Free)} free of {MonitorMath.Bytes(dr.Total)} ({dr.Percent:0} % used)");
        foreach (var n in s.Networks)
            Console.WriteLine($"  net {n.Name} ({(n.IsWireless ? "wireless" : "wired")}, {(n.IsUp ? "up" : "down")}): down {MonitorMath.Rate(n.DownBytesPerSecond)} up {MonitorMath.Rate(n.UpBytesPerSecond)}");
        Console.WriteLine($"  power plan '{s.Power.PlanName}', mode {s.Power.Mode}, ac {s.Power.OnAc?.ToString() ?? "?"}, battery {s.Power.BatteryPercent?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} %");
    }
    return 0;
}

static int Ec(string[] args, bool rawBytes)
{
    using var ec = new WmiEc();
    // Rule 1 also applies to our own service: while it runs, the CLI does not write to the EC.
    static string[] Busy() =>
        [.. VendorGuard.RunningVendorProcesses(), .. System.Diagnostics.Process.GetProcessesByName("AvellCC.Service").Length > 0 ? ["AvellCC.Service (stop the AvellCC service)"] : Array.Empty<string>()];
    var store = new FanTableBackupStore();
    var fan = new FanTableService(ec, store, ReadMachineInfo(ec), Busy);
    var officeStore = new EcRegisterBackupStore(name: OfficeFanService.BackupGroup);
    var office = new OfficeFanService(ec, officeStore, ReadMachineInfo(ec), Busy);
    var directStore = new EcRegisterBackupStore(name: DirectFanService.BackupGroup);
    var direct = new DirectFanService(ec, directStore, ReadMachineInfo(ec), Busy);

    switch (args)
    {
        case ["status"]:
            var vendor = VendorGuard.RunningVendorProcesses();
            Console.WriteLine($"Project ID : 0x{ec.Read(EcAddresses.ProjectId):X2}");
            Console.WriteLine($"Fan mode   : 0x751=0x{ec.Read(EcAddresses.FanControl):X2}  0x7A5=0x{ec.Read(EcAddresses.ApOemByte3):X2}");
            Console.WriteLine($"CPU / GPU  : {ec.Read(EcAddresses.CpuTemp)} °C / {ec.Read(EcAddresses.GpuTemp)} °C (EC)");
            Console.WriteLine($"Fan 1 / 2  : {ec.ReadWord(EcAddresses.Fan1High, EcAddresses.Fan1Low)} / {ec.ReadWord(EcAddresses.Fan2High, EcAddresses.Fan2Low)} (RPM)");
            Console.WriteLine($"Vendor     : {(vendor.Length == 0 ? "stopped" : string.Join(", ", vendor))}");
            return 0;

        case ["ec", "dump", var startText, var lengthText]:
        {
            // READ-ONLY: only calls ec.Read.
            var start = Convert.ToUInt16(startText.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
            var length = Convert.ToInt32(lengthText.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
            if (length is < 1 or > 0x400) throw new ArgumentException("Length must be between 0x1 and 0x400.");
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

        case ["ec", "lightbar"]:
            // READ-ONLY. Vendor MySettingManager: 0x768 bit 1 = light bar on; 0x767 bit 1 = toggle trigger.
            PrintLightbarRegisters(ec);
            return 0;

        case ["ec", "lightbar", "toggle", "--yes"]:
        {
            // Vendor MySettingManager.LightBar_Trigger: 0x767 = (0x767 & 0xFD) + 2. The EC toggles the bar and
            // reports the new state in 0x768 bit 1.
            var running = Busy();
            if (running.Length > 0) throw new VendorRunningException(running);
            var lbStore = new EcRegisterBackupStore(name: "ec-lightbar");
            var regs = LightbarRegisters().ToDictionary(a => a, ec.Read);
            var snapshot = EcRegisterSnapshot.From(regs, ReadMachineInfo(ec)(), running, "avellcc ec lightbar toggle");
            if (lbStore.TryCreateOriginal(snapshot)) Console.WriteLine($"Original saved: {lbStore.OriginalPath}");
            Console.WriteLine($"Snapshot: {lbStore.SaveSnapshot(snapshot, "lightbar-toggle")}");
            PrintLightbarRegisters(ec);
            var trigger = ec.Read(0x767);
            ec.Write(0x767, (byte)((trigger & 0xFD) + 2));
            Console.WriteLine($"Wrote 0x767 = 0x{(trigger & 0xFD) + 2:X2} (trigger). After 1 s:");
            Thread.Sleep(1000);
            PrintLightbarRegisters(ec);
            return 0;
        }

        case ["ec", "lightbar", "set", var ctrlText, var rText, var gText, var bText, "--yes"]:
        {
            // EC light bar (vendor MyRgbLightbarManager): 0x749/0x74A/0x74B = R/G/B level (vendor level 9 = 200/170/180),
            // 0x748 = control (bit0 app present, bit2 off, bit3 no breathing, bit4 apply, bit7 colorful).
            var running = Busy();
            if (running.Length > 0) throw new VendorRunningException(running);
            var lbStore = new EcRegisterBackupStore(name: "ec-lightbar");
            var snapshot = EcRegisterSnapshot.From(LightbarRegisters().ToDictionary(a => a, ec.Read), ReadMachineInfo(ec)(), running, "avellcc ec lightbar set");
            lbStore.TryCreateOriginal(snapshot);
            lbStore.SaveSnapshot(snapshot, "lightbar-set");
            static byte Hex(string s) => Convert.ToByte(s.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
            ec.Write(0x749, Hex(rText));
            ec.Write(0x74A, Hex(gText));
            ec.Write(0x74B, Hex(bText));
            ec.Write(0x748, Hex(ctrlText));
            Thread.Sleep(500);
            PrintLightbarRegisters(ec);
            return 0;
        }

        case ["fan", "show"]:
            PrintTable(fan.ReadStable());
            return 0;

        case ["fan", "backup"]:
            var (original, created) = fan.EnsureOriginalBackup();
            Console.WriteLine(created
                ? $"Original backup created: {store.OriginalPath}"
                : $"Original backup already existed (from {original.CreatedAt:g}), left unchanged: {store.OriginalPath}");
            return 0;

        case ["fan", "restore-original", "--yes"]:
            fan.RestoreOriginal();
            Console.WriteLine("Original state restored (modes + table) and verified.");
            return 0;

        case ["fan", "restore-original"]:
            Console.Error.WriteLine("This WRITES to the EC. Confirm with: avellcc fan restore-original --yes");
            return 2;

        case ["fan", "monitor", ..]:
            var seconds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 60;
            Console.WriteLine($"Mode: {office.ReadState().ModeName}");
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
                ? $"Original register backup created: {officeStore.OriginalPath}"
                : $"Original backup already existed (from {officeOriginal.CreatedAt:g}), left unchanged: {officeStore.OriginalPath}");
            return 0;

        case ["fan", "office", "trial", var officePath, ..]:
            var officeDuration = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 120;
            return OfficeTrial(ec, office, officeStore, OfficeFanCurve.Load(officePath), TimeSpan.FromSeconds(officeDuration), rawBytes);

        case ["fan", "office", "restore-original", "--yes"]:
            office.Progress += (done, total) => Console.Write($"\r  writing {done}/{total}...");
            office.RestoreOriginal();
            Console.WriteLine("\nOriginal registers restored and verified.");
            PrintOffice(office.ReadState());
            return 0;

        case ["fan", "office", "restore-original"]:
            Console.Error.WriteLine("This WRITES to the EC. Confirm with: avellcc fan office restore-original --yes");
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

        case ["fan", "boost-test", ..]:
        {
            // Measures the vendor "Fan Boost" (0x751 bit 6 only) against our 100 % (duty 200 ≈ 5543 RPM).
            var boostSeconds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 40;
            Console.WriteLine($"Vendor Fan Boost (0x751 bit 6 only, no duty written) for {boostSeconds} s. Reference: our 100 % ≈ 5543 / 5590 RPM.");
            PrintDirect(direct.ReadState());
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            int max1 = 0, max2 = 0;
            try
            {
                direct.EnableFanBoostOnly();
                var end = DateTimeOffset.Now.AddSeconds(boostSeconds);
                while (DateTimeOffset.Now < end && !cts.IsCancellationRequested)
                {
                    var s = FanSample.Read(ec);
                    max1 = Math.Max(max1, s.Fan1);
                    max2 = Math.Max(max2, s.Fan2);
                    Console.WriteLine($"{s.Time:HH:mm:ss}  CPU {s.CpuTemp,3} °C  fan1 {s.Fan1,5}  fan2 {s.Fan2,5}  duty {ec.Read(EcAddresses.DirectFan1Duty),3}/{ec.Read(EcAddresses.DirectFan2Duty),3}  0x751=0x{ec.Read(EcAddresses.FanControl):X2}");
                    cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                }
            }
            finally
            {
                direct.Disable();
                Console.WriteLine($"\nFan Boost off. Peak: fan1 {max1} / fan2 {max2} RPM (our 100 %: 5543 / 5590).");
                PrintDirect(direct.ReadState());
            }
            return 0;
        }

        case ["fan", "direct", "off", "--yes"]:
            if (Busy() is { Length: > 0 } busy) throw new VendorRunningException(busy);
            direct.Disable();
            Console.WriteLine("0x751 restored to the original (no full fan mode).");
            PrintDirect(direct.ReadState());
            return 0;

        default:
            return Usage();
    }
}

static void PrintDirect(DirectState s)
{
    Console.WriteLine($"0x751 (mode)          : 0x{s.FanControl:X2}  full fan mode (bit 6): {(s.FullFanMode ? "ON" : "off")}");
    Console.WriteLine($"0x1804 / 0x1809 (duty): {s.Fan1DutyRaw} / {s.Fan2DutyRaw}  (= {s.Fan1DutyRaw / 2.0:0.#}% / {s.Fan2DutyRaw / 2.0:0.#}%)");
    Console.WriteLine($"0x78E (status)        : 0x{s.FanCtrlStatus:X2}  universal control (bit 6): {(s.HasUniversalControl ? "yes" : "no")}");
    Console.WriteLine($"0x727 (custom profile): 0x{s.CustomProfile:X2}");
}

static int DirectTrial(IEcAccess ec, DirectFanService direct, EcRegisterBackupStore store, int percent, TimeSpan duration, bool rawBytes)
{
    DirectFanService.ToRaw(percent); // validate before anything else
    Console.WriteLine($"Direct control: both fans at {percent}% (≈ {FanCalibration.EstimateRpm(percent)} RPM). Always restores at the end.");
    Console.WriteLine("\nState before:");
    PrintDirect(direct.ReadState());
    Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);

    var (_, created) = direct.EnsureOriginalBackup();
    Console.WriteLine(created ? $"Original backup created: {store.OriginalPath}" : $"Original backup: {store.OriginalPath} (already existed)");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    string? abort = null;
    try
    {
        Console.WriteLine("Enabling (duty → 0x751 bit 6 → duty again)...");
        direct.Enable(percent, percent);
        PrintDirect(direct.ReadState());
        var end = DateTimeOffset.Now + duration;
        var watchdog = new FanWatchdog();
        while (DateTimeOffset.Now < end && !cts.IsCancellationRequested && abort is null)
        {
            direct.SetDuty(percent, percent);                 // rewrite every cycle
            abort = Monitor(ec, TimeSpan.Zero, _ => (percent, percent), watchdog, rawBytes, cts.Token);
            cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        }
    }
    finally
    {
        Console.WriteLine(abort is not null ? $"\n!! WATCHDOG: {abort}" : cts.IsCancellationRequested ? "\nCancelled." : "\nEnd of test.");
        Console.WriteLine("Disabling direct control (0x751 → original)...");
        direct.Disable();
        PrintDirect(direct.ReadState());
        Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);
    }
    return abort is null ? 0 : 5;
}

/// <summary>Foreground control loop (same logic as the service). Ctrl+C hands control back to the firmware.</summary>
static int CurveRun(IEcAccess ec, DirectFanService direct, EcRegisterBackupStore store, FanCurve curve, int seconds, bool rawBytes)
{
    var controller = new FanCurveController(curve);
    Console.WriteLine($"Curve '{curve.Name}' (hysteresis {curve.Hysteresis} °C, {(curve.Shared ? "fans together" : "CPU→fan1, GPU→fan2")}).");
    Console.WriteLine($"CPU: {string.Join("  ", curve.Cpu.Select(p => $"{p.Temp}°C→{p.Duty}%"))}");
    Console.WriteLine($"GPU: {string.Join("  ", curve.Gpu.Select(p => $"{p.Temp}°C→{p.Duty}%"))}");
    Console.WriteLine(seconds > 0 ? $"Running for {seconds} s." : "Running until Ctrl+C.");

    var (_, created) = direct.EnsureOriginalBackup();
    if (created) Console.WriteLine($"Original backup created: {store.OriginalPath}");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var end = seconds > 0 ? DateTimeOffset.Now.AddSeconds(seconds) : DateTimeOffset.MaxValue;
    var writes = 0;
    try
    {
        var first = controller.Step(FanSample.Read(ec));
        direct.Enable(first.Fan1, first.Fan2);
        writes++;
        Console.WriteLine($"Direct control on: {first.Fan1}% / {first.Fan2}%");

        while (!cts.IsCancellationRequested && DateTimeOffset.Now < end)
        {
            cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            if (cts.IsCancellationRequested) break;

            var s = FanSample.Read(ec);
            var d = controller.Step(s);

            if ((ec.Read(EcAddresses.FanControl) & EcAddresses.FullFanModeBit) == 0)
            {
                Console.WriteLine("!! 0x751 lost bit 6 (Fn key / BIOS?). Taking control again.");
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
        Console.WriteLine($"\nStopping ({writes} writes). Handing control back to the firmware...");
        direct.Disable();
        PrintDirect(direct.ReadState());
    }
    return 0;
}

static void PrintOffice(OfficeState s)
{
    Console.WriteLine($"Mode (0x751)      : 0x{s.FanControl:X2} = {s.ModeName}");
    Console.WriteLine($"Custom profile    : 0x727 = 0x{s.CustomProfile:X2} (bit 6 {(((s.CustomProfile & 0x40) != 0) ? "on" : "off")})");
    Console.WriteLine($"T1..T5 (0x743)    : {string.Join(" / ", s.Duties.Select(d => $"{d}%"))}");
    Console.WriteLine($"Defaults (0x786)  : {string.Join(" / ", s.DefaultDuties.Select(d => $"{d}%"))}");
    Console.WriteLine($"Min/temp/extra    : 0x{s.MinSpeed:X2} / 0x{s.MinTemp:X2} / 0x{s.ExtraSpeed:X2}");
    Console.WriteLine($"MyFan3 supported  : {(s.SupportsMyFan3 ? "yes" : "no")}");
}

static int OfficeTrial(IEcAccess ec, OfficeFanService office, EcRegisterBackupStore store, OfficeFanCurve curve, TimeSpan duration, bool rawBytes)
{
    Console.WriteLine($"Office curve '{curve.Name}': T1..T5 = {string.Join(" / ", curve.Duties.Select(d => $"{d}%"))}  (≈ {string.Join(" / ", curve.Duties.Select(d => $"{FanCalibration.EstimateRpm(d)}"))} RPM)");
    Console.WriteLine("\nState before:");
    PrintOffice(office.ReadState());
    Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);

    var (_, created) = office.EnsureOriginalBackup();
    Console.WriteLine(created ? $"Original backup created: {store.OriginalPath}" : $"Original backup: {store.OriginalPath} (already existed)");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    office.Progress += (done, total) => Console.Write($"\r  writing {done}/{total}...");

    Console.WriteLine("Applying (speeds first, then 0x751 = 0xA0)...");
    office.Apply(curve);
    Console.WriteLine("\nApplied. Monitoring with watchdog. Ctrl+C restores the original.");
    PrintOffice(office.ReadState());

    // Firmware level (0x460) 1..5 → T1..T5. Otherwise assume the lowest speed for the watchdog.
    (double, double) Expected(FanSample s)
    {
        var d = s.Level is >= 1 and <= OfficeFanCurve.Levels ? curve.Duties[s.Level - 1] : curve.Duties[0];
        return (d, d);
    }
    var abort = Monitor(ec, duration, Expected, new FanWatchdog(), rawBytes, cts.Token);

    bool keep;
    if (abort is not null) { Console.WriteLine($"\n!! WATCHDOG: {abort}"); keep = false; }
    else if (cts.IsCancellationRequested) { Console.WriteLine("\nCancelled."); keep = false; }
    else
    {
        Console.Write("\nKeep this curve? (may revert on reboot) [y/N]: ");
        keep = Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    if (keep) { Console.WriteLine("Curve kept."); return 0; }
    Console.WriteLine("Restoring the original (0x751 first)...");
    office.RestoreOriginal();
    Console.WriteLine("\nOriginal restored and verified.");
    PrintOffice(office.ReadState());
    Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);
    return abort is null ? 0 : 5;
}

static int Preview(FanCurve curve)
{
    var table = FanTableEncoder.Encode(curve);
    Console.WriteLine($"Curve '{curve.Name}' (hysteresis {curve.Hysteresis} °C) → EC table (nothing is written):");
    Console.WriteLine("level | CPU down<    up>  duty% | GPU down<    up>  duty%");
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
    Console.WriteLine("State before:"); Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    fan.Progress += (done, total) => Console.Write($"\r  writing {done}/{total}...");

    Console.WriteLine("\nApplying curve (table first, then enable the user table)...");
    var table = fan.ApplyCurve(curve);
    Console.WriteLine("\nCurve applied. Monitoring with watchdog. Ctrl+C restores the original.");

    var cpuLevels = FanTableEncoder.Decode(table, 0x00);
    var gpuLevels = FanTableEncoder.Decode(table, 0x30);
    Console.WriteLine($"0x751=0x{ec.Read(EcAddresses.FanControl):X2}  0x7A5=0x{ec.Read(EcAddresses.ApOemByte3):X2}");
    var abort = Monitor(ec, duration,
        s => (FanTableEncoder.ExpectedDuty(cpuLevels, s.CpuTemp), FanTableEncoder.ExpectedDuty(gpuLevels, s.GpuTemp)),
        new FanWatchdog(), rawBytes, cts.Token);

    bool keep;
    if (abort is not null) { Console.WriteLine($"\n!! WATCHDOG: {abort}"); keep = false; }
    else if (cts.IsCancellationRequested) { Console.WriteLine("\nCancelled."); keep = false; }
    else
    {
        Console.Write("\nKeep this curve? (the EC may revert to firmware on reboot) [y/N]: ");
        keep = Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    if (keep) { Console.WriteLine("Curve kept."); return 0; }
    Console.WriteLine("Restoring the original...");
    fan.RestoreOriginal();
    Console.WriteLine("\nOriginal restored and verified.");
    Monitor(ec, TimeSpan.Zero, null, null, rawBytes, CancellationToken.None);
    return abort is null ? 0 : 5;
}

/// <summary>Prints samples until the time is up. Returns the watchdog abort reason, if any.</summary>
static string? Monitor(IEcAccess ec, TimeSpan duration, Func<FanSample, (double Cpu, double Gpu)>? expected, FanWatchdog? watchdog, bool rawBytes, CancellationToken cancel)
{
    var end = DateTimeOffset.Now + duration;
    do
    {
        var s = FanSample.Read(ec);
        var line = $"{s.Time:HH:mm:ss}  CPU {s.CpuTemp,3} °C  GPU {s.GpuTemp,3} °C  fan1 {s.Fan1,5}  fan2 {s.Fan2,5}  level {s.Level} ({s.LevelTemp} °C)";
        if (rawBytes) line += $"  duty {ec.Read(EcAddresses.DirectFan1Duty),3}/{ec.Read(EcAddresses.DirectFan2Duty),3}  [460..46F] {Convert.ToHexString(ec.ReadRange(0x460, 16))}";
        if (expected is not null)
        {
            var (cpuDuty, gpuDuty) = expected(s);
            line += $"  | expected: {cpuDuty,5:0.#}% ≈ {FanCalibration.EstimateRpm(cpuDuty),5} RPM";
            if (watchdog?.Check(s, cpuDuty, gpuDuty) is { } reason) { Console.WriteLine(line); return reason; }
        }
        Console.WriteLine(line);
        if (duration == TimeSpan.Zero) break;
        cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
    } while (DateTimeOffset.Now < end && !cancel.IsCancellationRequested);
    return null;
}

static ushort[] LightbarRegisters() => [0x748, 0x749, 0x74A, 0x74B, 0x765, 0x766, 0x767, 0x768, 0x78E];

static void PrintLightbarRegisters(IEcAccess ec)
{
    var values = LightbarRegisters().Select(a => $"0x{a:X3}=0x{ec.Read(a):X2}");
    var status = ec.Read(0x768);
    Console.WriteLine($"  {string.Join("  ", values)}   → light bar {((status & 0x02) != 0 ? "ON" : "OFF")} (0x768 bit 1)");
}

// Light bar experiment: vendor sequence LM_ITE_RGB.Set_ITE_Effect_Type_UserMode_Lightbar.
// 0x08 [2, 0x33 user mode, 0, brightness, 0, 0, 0], then 8 rows: 0x16 row index + 65-byte output report
// [0, 0, B x21, G x21, R x21]. Every LED gets the same color here.
static int LightbarFill(Rgb color, int brightnessLevel)
{
    using var dev = HidFeatureDevice.Open(Ite8291.VendorId, Ite8291.UsagePageLightbar);
    var level = brightnessLevel < 0 ? Ite8291.BrightnessSteps[^1] : Ite8291.BrightnessSteps[Math.Clamp(brightnessLevel, 0, 4)];
    dev.SetFeature([0x00, 0x08, 0x02, 0x33, 0x00, level, 0x00, 0x00, 0x00]);
    Thread.Sleep(20);
    for (byte row = 0; row < 8; row++)
    {
        dev.SetFeature([0x00, 0x16, 0x00, row, 0x00, 0x00, 0x00, 0x00, 0x00]);
        var data = new byte[65];
        for (var i = 0; i < 21; i++)
        {
            data[2 + i] = color.B;
            data[23 + i] = color.G;
            data[44 + i] = color.R;
        }
        dev.WriteOutput(data);
        Thread.Sleep(1);
    }
    Console.WriteLine($"Light bar user mode: {color.R:X2}{color.G:X2}{color.B:X2}, brightness {level}.");
    return 0;
}

// "kb" = 4-zone keyboard; "lb" = front light bar (same ITE commands on another device, no color correction yet)
static int Keyboard(string[] args, bool lightbar = false)
{
    if (args.Length == 0) return Usage();
    var options = Options.Parse(args[1..]);
    if (lightbar && args[0] == "fill" && options.Positional.Count == 1) return LightbarFill(Options.ParseRgb(options.Positional[0]), options.Brightness);
    using var kb = new FourZoneKeyboard(HidFeatureDevice.Open(Ite8291.VendorId, lightbar ? Ite8291.UsagePageLightbar : Ite8291.UsagePage4Zone),
                                        FourZoneKeyboard.DefaultProjectId, colorCorrection: !options.Raw && !lightbar);
    switch (args[0])
    {
        case "info":
            Console.WriteLine($"Firmware : {Convert.ToHexString(kb.FirmwareVersion())}");
            Console.WriteLine($"Effect   : {kb.CurrentEffect()}");
            return 0;
        case "color" when options.Positional.Count == 1:
            kb.Static(Options.ParseRgb(options.Positional[0]), options.Brightness, options.Save);
            return 0;
        case "zones" when options.Positional.Count == Ite8291.Zones:
            kb.Zones(options.Positional.Select(Options.ParseRgb).ToArray(), options.Brightness, options.Save);
            return 0;
        case "effect" when options.Positional.Count == 1:
            if (!Enum.TryParse<Ite8291.Effect>(options.Positional[0], ignoreCase: true, out var effect))
                throw new ArgumentException($"Unknown effect. Options: {string.Join(", ", Enum.GetNames<Ite8291.Effect>())}");
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
        usage:
          avellcc fan curve run <curve.json> [seconds] [--raw]                  (admin, applies the curve; Ctrl+C hands back to firmware)
          avellcc status | fan monitor [seconds] [--raw]                        (admin, read-only)
          avellcc ec dump <start hex> <length hex>                              (admin, read-only)
          avellcc fan direct show                                               (admin, read-only)
          avellcc fan direct trial <percent 25-100> [seconds] [--raw]           (admin, WRITES, always restores)
          avellcc fan direct off --yes                                          (admin, turns direct control off)
          avellcc fan boost-test [seconds]                                      (admin, measures the vendor Fan Boost and restores)
          avellcc kb info
          avellcc kb color <RRGGBB> [-b 0-4] [--save] [--raw]
          avellcc kb zones <RRGGBB> <RRGGBB> <RRGGBB> <RRGGBB> [-b 0-4] [--save] [--raw]
          avellcc kb effect <name> [-s 0-4] [-b 0-4] [-d ltr|rtl|btt|ttb] [-c RRGGBB,...] [--save]
          avellcc kb brightness <0-4> | kb off | kb raw <9 bytes hex>
        experiments (mechanisms this EC ignores; kept for other Uniwill models):
          avellcc fan preview <curve.json> | fan show | fan backup              (0xF00 table; preview needs no admin)
          avellcc fan trial <curve.json> [seconds] [--raw]                      (admin, WRITES with watchdog)
          avellcc fan restore-original --yes                                    (admin, WRITES)
          avellcc fan office show | backup | trial <office.json> [seconds]      (admin, Office Advanced 0x743)
          avellcc fan office restore-original --yes                             (admin, WRITES)
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
    Console.WriteLine("level | CPU     up   down duty% | GPU     up   down duty%");
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
                        var d => throw new ArgumentException($"Invalid direction: {d}"),
                    };
                    break;
                case "-c": colors = Next().Split(',').Select(ParseRgb).ToList(); break;
                case "--save": save = true; break;
                case "--raw": raw = true; break;
                default: positional.Add(args[i]); break;
            }
        }
        return new Options(positional, brightness, speed, direction, colors, save, raw);

        string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value after {args[i - 1]}");
    }

    public static Rgb ParseRgb(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) throw new ArgumentException($"Invalid color: {hex} (use RRGGBB)");
        var v = Convert.ToInt32(hex, 16);
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
