using System.Net;
using System.Net.Sockets;

namespace EmergencyComm.Common;

/// <summary>
/// Gửi video MJPEG qua RTP (payload type động 96).
/// Mỗi khung hình JPEG được cắt thành nhiều gói RTP có cùng timestamp;
/// gói cuối cùng của khung đặt Marker = 1.
/// </summary>
public sealed class RtpJpegSender
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _dest;
    private readonly DateTime _start = DateTime.UtcNow;
    private readonly uint _tsOffset;
    private ushort _seq;

    public uint Ssrc { get; }
    public uint PacketCount { get; private set; }
    public uint OctetCount { get; private set; }

    public RtpJpegSender(UdpClient udp, IPEndPoint dest, uint ssrc)
    {
        _udp = udp; _dest = dest; Ssrc = ssrc;
        _seq = (ushort)Random.Shared.Next(0, 65536);                    // RFC 3550: seq/ts khởi tạo ngẫu nhiên
        _tsOffset = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
    }

    public uint NowTimestamp() =>
        unchecked((uint)(long)((DateTime.UtcNow - _start).TotalSeconds * Protocol.ClockRate) + _tsOffset);

    public void SendFrame(byte[] jpeg)
    {
        uint ts = NowTimestamp();
        int offset = 0;
        while (offset < jpeg.Length)
        {
            int n = Math.Min(Protocol.MaxPayload, jpeg.Length - offset);
            var payload = new byte[n];
            Buffer.BlockCopy(jpeg, offset, payload, 0, n);
            offset += n;

            var pkt = new RtpPacket
            {
                Marker = offset >= jpeg.Length,
                PayloadType = Protocol.JpegPayloadType,
                Sequence = _seq++,
                Timestamp = ts,
                Ssrc = Ssrc,
                Payload = payload
            };
            byte[] data = pkt.Serialize();
            _udp.Send(data, data.Length, _dest);
            PacketCount++;
            OctetCount += (uint)n;
        }
    }
}

/// <summary>
/// Ghép các gói RTP thành khung JPEG hoàn chỉnh. Khung bị mất gói (seq đứt quãng)
/// hoặc không bắt đầu bằng SOI (FF D8) sẽ bị loại bỏ thay vì hiển thị ảnh hỏng.
/// </summary>
public sealed class JpegReassembler
{
    private readonly MemoryStream _ms = new();
    private uint _ts;
    private bool _hasTs, _active, _broken;
    private ushort _expectSeq;

    public int FramesOk { get; private set; }
    public int FramesDropped { get; private set; }

    /// <summary>Trả về mảng byte JPEG khi đủ một khung, ngược lại trả về null.</summary>
    public byte[] Push(RtpPacket p)
    {
        if (_hasTs && unchecked((int)(p.Timestamp - _ts)) < 0) return null;   // gói cũ đến muộn

        if (!_active || p.Timestamp != _ts)
        {
            if (_active) FramesDropped++;                                      // khung trước chưa kết thúc
            _ms.SetLength(0);
            _ts = p.Timestamp; _hasTs = true; _active = true; _broken = false;
            _expectSeq = p.Sequence;
            if (p.Payload.Length < 2 || p.Payload[0] != 0xFF || p.Payload[1] != 0xD8) _broken = true;
        }

        if (p.Sequence != _expectSeq) _broken = true;
        _expectSeq = unchecked((ushort)(p.Sequence + 1));

        if (!_broken) _ms.Write(p.Payload, 0, p.Payload.Length);

        if (p.Marker)
        {
            _active = false;
            if (_broken) { FramesDropped++; return null; }
            FramesOk++;
            return _ms.ToArray();
        }
        return null;
    }
}
