using System.Net;
using System.Runtime.InteropServices;

namespace XamppRecoveryTool.Services;

public sealed class PortChecker
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    public List<PortOwner> Listeners(int port)
    {
        var result = new List<PortOwner>();
        foreach (int family in new[] { 2, 23 })
        {
            int size = 0;
            uint code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (code != 122 && code != 0) throw new IOException("Cannot inspect TCP listeners: " + code);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                code = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                if (code != 0) throw new IOException("Cannot inspect TCP listeners: " + code);
                int count = Marshal.ReadInt32(buffer), stride = family == 2 ? 24 : 56;
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(buffer, 4 + i * stride);
                    int offset = family == 2 ? 8 : 20;
                    int rowPort = (Marshal.ReadByte(row, offset) << 8) | Marshal.ReadByte(row, offset + 1);
                    if (rowPort != port) continue;
                    byte[] address = new byte[family == 2 ? 4 : 16];
                    Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), address, 0, address.Length);
                    result.Add(new(Marshal.ReadInt32(row, family == 2 ? 20 : 52), new IPAddress(address).ToString()));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }
}
