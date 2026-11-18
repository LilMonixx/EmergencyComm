namespace EmergencyComm.Common;

/// <summary>Hằng số dùng chung cho Server và Client.</summary>
public static class Protocol
{
    public const int TcpPort = 8888;          // Kênh điều khiển: tin nhắn, tọa độ tin cậy, file (TCP)
    public const int LocationUdpPort = 8889;  // Theo dõi GPS liên tục (UDP datagram)
    public const int RtpPort = 9000;          // Video RTP (UDP)
    public const int RtcpPort = 9001;         // Báo cáo chất lượng RTCP (UDP)

    public const byte JpegPayloadType = 96;   // Dynamic payload type: khung hình JPEG (MJPEG qua RTP)
    public const int ClockRate = 90000;       // 90 kHz - chuẩn cho video RTP
    public const int MaxPayload = 1200;       // Kích thước payload tối đa / gói RTP (tránh phân mảnh IP, MTU ~1500)
    public const long MaxFileSize = 100L * 1024 * 1024;
}
