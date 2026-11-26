using System.Runtime.InteropServices;

namespace AvellCC.Core.Monitoring;

/// <summary>A hardware GPU, identified by its LUID (the same id used in the "GPU Engine" performance counters).</summary>
public sealed record GpuAdapter(string Name, long Luid, uint VendorId, ulong DedicatedMemory)
{
    public const uint VendorNvidia = 0x10DE, VendorAmd = 0x1002, VendorIntel = 0x8086;

    /// <summary>Discrete GPU: has its own video memory (integrated GPUs report a small carve-out at most).</summary>
    public bool IsDiscrete => DedicatedMemory >= 1UL << 30;
}

/// <summary>Lists GPUs through DXGI (names, LUID, video memory). Software adapters (Basic Render Driver) are skipped.</summary>
public static class GpuAdapters
{
    public static IReadOnlyList<GpuAdapter> Enumerate()
    {
        var result = new List<GpuAdapter>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) < 0) return result;
            try
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) >= 0; i++)
                {
                    try
                    {
                        if (adapter.GetDesc1(out var d) < 0 || (d.Flags & DxgiAdapterFlagSoftware) != 0) continue;
                        var luid = ((long)d.LuidHigh << 32) | d.LuidLow;
                        if (result.Any(a => a.Luid == luid)) continue;
                        result.Add(new GpuAdapter(d.Description.Trim(), luid, d.VendorId, d.DedicatedVideoMemory));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            // no DXGI: the monitor falls back to "GPU" + LUID
        }
        return result;
    }

    private const uint DxgiAdapterFlagSoftware = 2;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public ulong DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    // Only the vtable order matters for the placeholder methods: they are never called.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();   // IDXGIObject
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter(); // IDXGIFactory
        [PreserveSig] int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();   // IDXGIObject
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();                                 // IDXGIAdapter
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
