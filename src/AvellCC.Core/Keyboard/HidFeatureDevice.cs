using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AvellCC.Core.Keyboard;

/// <summary>HID feature-report channel. Abstracted so the protocol can be tested without hardware.</summary>
public interface IFeatureTransport : IDisposable
{
    void SetFeature(byte[] report);
    byte[] GetFeature(byte reportId, int length);
}

/// <summary>
/// HID device opened by VID + usage page (e.g. 048D + 0xFF12 = 4-zone keyboard).
/// Does not require administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HidFeatureDevice : IFeatureTransport
{
    private readonly SafeFileHandle _handle;
    public string Path { get; }
    public ushort ProductId { get; }
    public ushort Version { get; }
    public int FeatureReportLength { get; }

    private HidFeatureDevice(SafeFileHandle handle, string path, ushort pid, ushort version, int featureLength)
    {
        _handle = handle;
        Path = path;
        ProductId = pid;
        Version = version;
        FeatureReportLength = featureLength;
    }

    public static HidFeatureDevice Open(ushort vendorId, ushort usagePage)
    {
        foreach (var path in EnumerateInterfacePaths())
        {
            if (path.IndexOf($"vid_{vendorId:x4}", StringComparison.OrdinalIgnoreCase) < 0) continue;

            // First: metadata only (access 0), so we never open the wrong interface for writing
            using (var probe = Native.CreateFile(path, 0, Native.FileShareReadWrite, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero))
            {
                if (probe.IsInvalid || ReadCaps(probe) is not { } caps || caps.UsagePage != usagePage) continue;
            }

            var handle = Native.CreateFile(path, Native.GenericRead | Native.GenericWrite, Native.FileShareReadWrite,
                                           IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open {path}");

            var attributes = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
            Native.HidD_GetAttributes(handle, ref attributes);
            var finalCaps = ReadCaps(handle)!.Value;
            return new HidFeatureDevice(handle, path, attributes.ProductID, attributes.VersionNumber, finalCaps.FeatureReportByteLength);
        }
        throw new InvalidOperationException($"HID device VID {vendorId:X4} with usage page 0x{usagePage:X4} not found.");
    }

    public void SetFeature(byte[] report)
    {
        if (report.Length != FeatureReportLength)
            throw new ArgumentException($"Report must be {FeatureReportLength} bytes, got {report.Length}.", nameof(report));
        if (!Native.HidD_SetFeature(_handle, report, report.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_SetFeature failed.");
    }

    /// <summary>Output report (interrupt OUT), e.g. the 65-byte per-LED rows of ITE "user mode".</summary>
    public void WriteOutput(byte[] report)
    {
        if (!Native.WriteFile(_handle, report, report.Length, out var written, IntPtr.Zero) || written != report.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteFile (HID output report) failed.");
    }

    public byte[] GetFeature(byte reportId, int length)
    {
        var buffer = new byte[length];
        buffer[0] = reportId;
        if (!Native.HidD_GetFeature(_handle, buffer, buffer.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetFeature failed.");
        return buffer;
    }

    public void Dispose() => _handle.Dispose();

    private static Native.HIDP_CAPS? ReadCaps(SafeFileHandle handle)
    {
        if (!Native.HidD_GetPreparsedData(handle, out var preparsed)) return null;
        try
        {
            var caps = new Native.HIDP_CAPS();
            return Native.HidP_GetCaps(preparsed, ref caps) == Native.HidpStatusSuccess ? caps : null;
        }
        finally { Native.HidD_FreePreparsedData(preparsed); }
    }

    private static List<string> EnumerateInterfacePaths()
    {
        var result = new List<string>();
        Native.HidD_GetHidGuid(out var hidGuid);
        var set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, Native.DigcfPresent | Native.DigcfDeviceInterface);
        try
        {
            var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (var i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data); i++)
            {
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                var buffer = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (Native.SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero))
                        result.Add(Marshal.PtrToStringUni(buffer + 4)!);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private static class Native
    {
        public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareReadWrite = 3, OpenExisting = 3;
        public const int DigcfPresent = 0x02, DigcfDeviceInterface = 0x10;
        public const int HidpStatusSuccess = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                          NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                          NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid hidGuid);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS caps);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int length, out int written, IntPtr overlapped);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    }
}
