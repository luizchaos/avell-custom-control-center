using AvellCC.Core.Ec;
using AvellCC.Core.Fan;

namespace AvellCC.Core.Tests;

public sealed class FanTableBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avellcc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEc _ec = new();
    private readonly FanTableBackupStore _store;
    private readonly FanTableService _service;

    public FanTableBackupTests()
    {
        _store = new FanTableBackupStore(_dir);
        _service = new FanTableService(_ec, _store, () => new MachineInfo { Model = "test" }, () => []);
        _ec.Seed(EcAddresses.FanTableStart, Enumerable.Range(1, EcAddresses.FanTableLength).Select(i => (byte)i).ToArray());
    }

    [Fact]
    public void First_backup_is_created_and_never_overwritten()
    {
        var (first, created) = _service.EnsureOriginalBackup();
        Assert.True(created);
        Assert.True(File.GetAttributes(_store.OriginalPath).HasFlag(FileAttributes.ReadOnly));

        _ec.Seed(EcAddresses.FanTableStart, new byte[EcAddresses.FanTableLength]); // EC mudou
        var (second, createdAgain) = _service.EnsureOriginalBackup();

        Assert.False(createdAgain);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Sha256, _store.LoadOriginal().Sha256);
    }

    [Fact]
    public void Reading_and_backing_up_never_writes_to_the_ec()
    {
        _service.ReadStable();
        _service.EnsureOriginalBackup();
        Assert.Empty(_ec.Writes);
    }

    [Fact]
    public void Corrupted_original_is_rejected()
    {
        _service.EnsureOriginalBackup();
        File.SetAttributes(_store.OriginalPath, FileAttributes.Normal);
        var text = File.ReadAllText(_store.OriginalPath).Replace("\"rawHex\": \"01", "\"rawHex\": \"FF");
        File.WriteAllText(_store.OriginalPath, text);

        Assert.Throws<InvalidDataException>(() => _store.LoadOriginal());
    }

    [Fact]
    public void Write_path_backs_up_original_then_restore_brings_back_table_and_modes()
    {
        var originalBytes = _ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength);
        var newTable = new byte[EcAddresses.FanTableLength];
        Array.Fill(newTable, (byte)0x42);

        _service.Apply(new Dictionary<ushort, byte> { [EcAddresses.ApOemByte3] = 0x08 }, newTable, "test");

        Assert.Equal(Convert.ToHexString(originalBytes), _store.LoadOriginal().RawHex);
        Assert.Equal(newTable, _ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength));
        Assert.Equal(0x08, _ec.Read(EcAddresses.ApOemByte3));

        _ec.Writes.Clear();
        _service.RestoreOriginal();
        Assert.Equal(originalBytes, _ec.ReadRange(EcAddresses.FanTableStart, EcAddresses.FanTableLength));
        Assert.Equal(0x00, _ec.Read(EcAddresses.ApOemByte3));
        // o modo é restaurado ANTES da tabela (desliga a tabela do usuário antes de mexer nela)
        Assert.Equal(EcAddresses.ApOemByte3, _ec.Writes[0].Address);
    }

    [Fact]
    public void Apply_refuses_to_write_while_vendor_is_running()
    {
        var guarded = new FanTableService(_ec, _store, () => new MachineInfo(), () => ["GCUService"]);
        Assert.Throws<Core.Safety.VendorRunningException>(() =>
            guarded.Apply(new Dictionary<ushort, byte>(), new byte[EcAddresses.FanTableLength], "x"));
        Assert.Empty(_ec.Writes);
    }

    [Fact]
    public void Apply_rejects_registers_outside_the_allow_list() =>
        Assert.Throws<ArgumentException>(() =>
            _service.Apply(new Dictionary<ushort, byte> { [EcAddresses.ProjectId] = 1 }, new byte[EcAddresses.FanTableLength], "x"));

    /// <summary>Mesmo formato gerado por tools/diag/Invoke-AvellDiag.ps1 (ConvertTo-Json do PowerShell 5.1).</summary>
    private const string ScriptBackup = """
        {
            "schema":  "avellcc.fan-table.v1",
            "createdAt":  "2026-11-26T10:16:57.9601489-03:00",
            "createdBy":  "tools/diag/Invoke-AvellDiag.ps1",
            "machine":  { "manufacturer":  "Avell High Performance", "model":  "A65 LIV", "bios":  "N.1.01", "projectId":  "0x0F" },
            "vendorRunning":  [ "GCUBridge", "GCUService", "OSDTpDetect" ],
            "baseAddress":  "0x0F00",
            "rawHex":  "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            "sha256":  "2EA9AB9198D1638007400CD2C3BEF1CC745B864B76011A0E1BC52180AC6452D4",
            "modeRegisters":  {
                "FAN_CONTROL_0x751":  "0x00",
                "AP_OEM_BYTE3_0x7A5":  "0x00",
                "AP_OEM_BYTE2_0x78C":  "0x04",
                "MODE_INDEX_0x7AB":  "0x00",
                "BIOS_OEM_BYTE2_0x782":  "0x5D"
            }
        }
        """;

    [Fact]
    public void Backup_created_by_the_powershell_script_is_readable()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.OriginalPath, ScriptBackup);

        var original = _store.LoadOriginal();
        var modes = original.FanModeRegisterValues();

        Assert.All(original.Raw, b => Assert.Equal(0, b));
        Assert.Equal(4, modes.Count);                       // 0x782 é ignorado
        Assert.Equal(0x04, modes[EcAddresses.TurboLevel]);
        Assert.Equal(0x00, modes[EcAddresses.ApOemByte3]);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_dir)) return;
        foreach (var f in Directory.GetFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }
}
