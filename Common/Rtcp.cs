using System.Buffers.Binary;

namespace EmergencyComm.Common;

/// <summary>Một gói RTCP đã giải mã (SR / RR / BYE).</summary>
public sealed class RtcpMessage
{
    public byte Type;
    public uint Ssrc;                       // SSRC người gửi gói RTCP

    // Sender Report
    public uint NtpSec, NtpFrac, RtpTimestamp, PacketCount, OctetCount;

    // Receiver Report (report block đầu tiên)
    public bool HasBlock;
    public uint SourceSsrc;
    public byte FractionLost;
    public int CumulativeLost;
    public uint ExtHighestSeq, Jitter, Lsr, Dlsr;
}

/// <summary>Tạo / phân tích gói RTCP theo RFC 3550 (SR=200, RR=201, BYE=203).</summary>
public static class Rtcp
{
    public const byte SR = 200, RR = 201, BYE = 203;
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static void ToNtp(DateTime utc, out uint sec, out uint frac)
    {
        double s = (utc - NtpEpoch).TotalSeconds;
        double whole = Math.Floor(s);
        sec = unchecked((uint)(long)whole);
        frac = unchecked((uint)(long)((s - whole) * 4294967296.0));
    }

    /// <summary>32 bit giữa của NTP timestamp (dùng cho LSR/DLSR, đơn vị 1/65536 giây).</summary>
    public static uint Middle32(uint sec, uint frac) => ((sec & 0xFFFF) << 16) | (frac >> 16);

    public static uint NowMiddle32()
    {
        ToNtp(DateTime.UtcNow, out var s, out var f);
        return Middle32(s, f);
    }

    private static void Be16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o, 2), v);
    private static void Be32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o, 4), v);
    private static uint Rd32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));

    public static byte[] BuildSenderReport(uint ssrc, uint rtpTimestamp, uint packets, uint octets)
    {
        var b = new byte[28];
        b[0] = 0x80; b[1] = SR; Be16(b, 2, 6);
        Be32(b, 4, ssrc);
        ToNtp(DateTime.UtcNow, out var s, out var f);
        Be32(b, 8, s); Be32(b, 12, f);
        Be32(b, 16, rtpTimestamp); Be32(b, 20, packets); Be32(b, 24, octets);
        return b;
    }

    public static byte[] BuildReceiverReport(uint senderSsrc, uint sourceSsrc, byte fractionLost, int cumulativeLost,
                                             uint extHighestSeq, uint jitter, uint lsr, uint dlsr)
    {
        var b = new byte[32];
        b[0] = 0x81; b[1] = RR; Be16(b, 2, 7);          // RC = 1 report block
        Be32(b, 4, senderSsrc);
        Be32(b, 8, sourceSsrc);
        b[12] = fractionLost;
        int lost = Math.Clamp(cumulativeLost, 0, 0xFFFFFF);
        b[13] = (byte)(lost >> 16); b[14] = (byte)(lost >> 8); b[15] = (byte)lost;
        Be32(b, 16, extHighestSeq);
        Be32(b, 20, jitter);
        Be32(b, 24, lsr);
        Be32(b, 28, dlsr);
        return b;
    }

    public static byte[] BuildBye(uint ssrc)
    {
        var b = new byte[8];
        b[0] = 0x81; b[1] = BYE; Be16(b, 2, 1);
        Be32(b, 4, ssrc);
        return b;
    }

    /// <summary>Phân tích gói RTCP đơn hoặc compound packet.</summary>
    public static List<RtcpMessage> Parse(byte[] d, int len)
    {
        var list = new List<RtcpMessage>();
        int pos = 0;
        while (pos + 8 <= len)
        {
            if ((d[pos] >> 6) != 2) break;
            int rc = d[pos] & 0x1F;
            byte pt = d[pos + 1];
            int size = (BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 2, 2)) + 1) * 4;
            if (pos + size > len) break;

            var m = new RtcpMessage { Type = pt, Ssrc = Rd32(d, pos + 4) };
            if (pt == SR && size >= 28)
            {
                m.NtpSec = Rd32(d, pos + 8);
                m.NtpFrac = Rd32(d, pos + 12);
                m.RtpTimestamp = Rd32(d, pos + 16);
                m.PacketCount = Rd32(d, pos + 20);
                m.OctetCount = Rd32(d, pos + 24);
            }
            else if (pt == RR && rc >= 1 && size >= 32)
            {
                m.HasBlock = true;
                m.SourceSsrc = Rd32(d, pos + 8);
                m.FractionLost = d[pos + 12];
                m.CumulativeLost = (d[pos + 13] << 16) | (d[pos + 14] << 8) | d[pos + 15];
                m.ExtHighestSeq = Rd32(d, pos + 16);
                m.Jitter = Rd32(d, pos + 20);
                m.Lsr = Rd32(d, pos + 24);
                m.Dlsr = Rd32(d, pos + 28);
            }
            list.Add(m);
            pos += size;
        }
        return list;
    }
}
