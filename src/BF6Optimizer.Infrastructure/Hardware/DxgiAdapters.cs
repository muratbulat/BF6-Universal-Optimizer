using System.Runtime.InteropServices;

namespace BF6Optimizer.Infrastructure.Hardware;

internal sealed record DxgiAdapter(string Description, uint VendorId, uint DeviceId, uint SubSysId, long DedicatedVideoMemory, bool IsSoftware);

/// <summary>
/// Direct DXGI enumeration. It reports 64-bit dedicated memory, unlike Win32_VideoController.AdapterRAM,
/// and creates no D3D device, so it does not wake a sleeping laptop dGPU.
/// </summary>
internal static unsafe class DxgiAdapters
{
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const int VtblRelease = 2;
    private const int VtblEnumAdapters1 = 12;
    private const int VtblGetDesc1 = 10;

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");


    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDesc
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    private static void** Vtbl(nint com) => *(void***)com;

    private static void Release(nint com) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(com)[VtblRelease])(com);

    public static IReadOnlyList<DxgiAdapter> Enumerate()
    {
        var result = new List<DxgiAdapter>();
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(IID_IDXGIFactory1, out var factory));
        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(factory)[VtblEnumAdapters1];
            for (uint i = 0; ; i++)
            {
                nint adapter;
                int hr = enumAdapters(factory, i, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    NativeDesc desc;
                    var getDesc = (delegate* unmanaged[Stdcall]<nint, NativeDesc*, int>)Vtbl(adapter)[VtblGetDesc1];
                    Marshal.ThrowExceptionForHR(getDesc(adapter, &desc));
                    result.Add(new DxgiAdapter(
                        new string(desc.Description).TrimEnd('\0'),
                        desc.VendorId, desc.DeviceId, desc.SubSysId,
                        (long)desc.DedicatedVideoMemory,
                        (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0));
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        return result;
    }
}
