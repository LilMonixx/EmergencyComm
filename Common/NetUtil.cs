using System.Net.Sockets;

namespace EmergencyComm.Common;

public static class NetUtil
{
    /// <summary>Tạo UdpClient IPv4 (port = 0 nghĩa là cổng ngẫu nhiên).</summary>
    public static UdpClient CreateUdp(int port)
    {
        var udp = new UdpClient(port);
        try { udp.Client.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }
        DisableConnReset(udp);
        return udp;
    }

    /// <summary>
    /// Windows ném SocketException 10054 khi nhận ICMP "port unreachable" trên socket UDP.
    /// Tắt hành vi này để vòng lặp nhận không bị ngắt khi đối tác chưa mở cổng.
    /// </summary>
    public static void DisableConnReset(UdpClient udp)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch { }
    }
}
