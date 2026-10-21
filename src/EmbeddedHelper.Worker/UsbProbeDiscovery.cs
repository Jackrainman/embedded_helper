using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using EmbeddedHelper.Core;

internal static class UsbProbeDiscovery
{
    // SEGGER JLINKARM_EMU_CONNECT_INFO ABI, also documented by Square's pylink
    // JLinkConnectInfo: https://github.com/square/pylink/blob/master/pylink/structs.py
    // USB-only enumeration; this code never opens a probe or connects to a target.
    private const int InfoSize = 264, MaxProbes = 64;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetListDelegate(int interfaces, IntPtr entries, int count);
    internal static UsbProbe[] Read(string dll)
    {
        if (!Path.IsPathFullyQualified(dll)) throw new ArgumentException("J-Link 组件必须为绝对路径。");
        var library = NativeLibrary.Load(dll);
        try
        {
            var getList = Marshal.GetDelegateForFunctionPointer<GetListDelegate>(NativeLibrary.GetExport(library, "JLINKARM_EMU_GetList"));
            var memory = Marshal.AllocHGlobal(InfoSize * MaxProbes);
            try
            {
                var bytes = new byte[InfoSize * MaxProbes];
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                var count = getList(1, memory, MaxProbes);
                if (count < 0) throw new IOException("USB 探针枚举失败，请检查 J-Link 组件和 USB 驱动。");
                if (count > MaxProbes) throw new IOException("USB 探针数量超出枚举范围，无法确认完整列表。");
                Marshal.Copy(memory, bytes, 0, bytes.Length);
                var probes = new List<UsbProbe>();
                for (var i = 0; i < count; i++)
                {
                    var info = bytes.AsSpan(i * InfoSize, InfoSize);
                    var serial = BinaryPrimitives.ReadUInt32LittleEndian(info);
                    if (info[4] != 1 || serial == 0 || serial > int.MaxValue) continue;
                    var model = Encoding.ASCII.GetString(info.Slice(50, 32)).Split('\0')[0].Trim();
                    probes.Add(new((int)serial, model.Length == 0 ? "J-Link（型号未返回）" : model));
                }
                return probes.DistinctBy(p => p.SerialNumber).OrderBy(p => p.SerialNumber).ToArray();
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        finally { NativeLibrary.Free(library); }
    }
}
