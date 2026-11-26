using System.Management;
using System.Runtime.Versioning;

namespace AvellCC.Core.Ec;

/// <summary>
/// EC via método ACPI/WMI do BIOS Uniwill: root\WMI AcpiTest_MULong.GetSetULong.
/// Exige administrador. Formato confirmado no IL de MyControlCenter.WMIEC (GCUService.exe).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiEc : IEcAccess, IDisposable
{
    public const string Namespace = @"root\WMI";
    public const string ObjectPath = @"AcpiTest_MULong.InstanceName='ACPI\PNP0C14\1_1'";
    private const string Method = "GetSetULong";

    /// <summary>Bit 40. Sem ele, a chamada é interpretada como ESCRITA de 0.</summary>
    internal const ulong ReadFlag = 0x100_0000_0000UL;

    private readonly ManagementObject _obj;
    private readonly object _lock = new();

    public WmiEc()
    {
        _obj = new ManagementObject(Namespace, ObjectPath, null);
        _obj.Get(); // falha cedo se a instância não existir ou faltar privilégio
    }

    internal static ulong EncodeRead(ushort address) => ReadFlag + address;
    internal static ulong EncodeWrite(ushort address, byte value) => ((ulong)value << 16) + address;

    public byte Read(ushort address)
    {
        lock (_lock)
        {
            using var inParams = _obj.GetMethodParameters(Method);
            inParams["Data"] = EncodeRead(address);
            using var outParams = _obj.InvokeMethod(Method, inParams, null);
            return (byte)(Convert.ToUInt64(outParams["Return"]) & 0xFF);
        }
    }

    public void Write(ushort address, byte value)
    {
        lock (_lock)
        {
            using var inParams = _obj.GetMethodParameters(Method);
            inParams["Data"] = EncodeWrite(address, value);
            using var _ = _obj.InvokeMethod(Method, inParams, null);
        }
    }

    public void Dispose() => _obj.Dispose();
}
