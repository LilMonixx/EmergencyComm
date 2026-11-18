using System.Buffers.Binary;

namespace EmergencyComm.Common;

/// <summary>Gói RTP theo RFC 3550 (header 12 byte, không CSRC/extension khi gửi).</summary>
public sealed class RtpPacket
{
    public const int HeaderSize = 12;

    public bool Marker;
    public byte PayloadType;
    public ushort Sequence;
    public uint Timestamp;
    public uint Ssrc;
    public byte[] Payload = Array.Empty<byte>();

    public byte[] Serialize()
    {
        var buf = new byte[HeaderSize + Payload.Length];
        buf[0] = 0x80;                                              // V=2, P=0, X=0, CC=0
        buf[1] = (byte)((Marker ? 0x80 : 0x00) | (PayloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), Ssrc);
        Buffer.BlockCopy(Payload, 0, buf, HeaderSize, Payload.Length);
        return buf;
    }

    public static bool TryParse(byte[] data, int length, out RtpPacket packet)
    {
        packet = null;
        if (length < HeaderSize || (data[0] >> 6) != 2) return false;

        int cc = data[0] & 0x0F;
        bool ext = (data[0] & 0x10) != 0;
        bool pad = (data[0] & 0x20) != 0;

        int offset = HeaderSize + cc * 4;
        if (length < offset) return false;
        if (ext)
        {
            if (length < offset + 4) return false;
            int extWords = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2, 2));
            offset += 4 + extWords * 4;
        }
        int end = length;
        if (pad) end -= data[length - 1];
        if (offset > end) return false;

        var payload = new byte[end - offset];
        Buffer.BlockCopy(data, offset, payload, 0, payload.Length);

        packet = new RtpPacket
        {
            Marker = (data[1] & 0x80) != 0,
            PayloadType = (byte)(data[1] & 0x7F),
            Sequence = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2)),
            Timestamp = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)),
            Ssrc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4)),
            Payload = payload
        };
        return true;
    }
}
