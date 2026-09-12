using System.Runtime.InteropServices;

namespace AgentLimits;

/// <summary>
/// Ports a given process is listening on. agy picks random ports on every
/// launch, so the only way to find them is by PID.
/// </summary>
internal static class TcpTable
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    public static List<int> ListeningPorts(int pid)
    {
        var ports = new List<int>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) return ports;

        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                return ports;

            int rows = Marshal.ReadInt32(buf);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var ptr = IntPtr.Add(buf, 4);

            for (int i = 0; i < rows; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr);
                if (row.owningPid == pid)
                {
                    // dwLocalPort is stored in network byte order in the low word.
                    int port = ((row.localPort & 0xFF) << 8) | ((row.localPort >> 8) & 0xFF);
                    if (port > 0 && !ports.Contains(port)) ports.Add(port);
                }
                ptr = IntPtr.Add(ptr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return ports;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public int localPort;
        public uint remoteAddr;
        public int remotePort;
        public int owningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, int reserved);
}
