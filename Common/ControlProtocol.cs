using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EmergencyComm.Common;

public enum ControlMsgType : byte
{
    Hello = 1,
    Chat = 2,
    SosAlert = 3,
    ReliableLocation = 4,
    FileTransfer = 5,
    ServerCommand = 6
}

public sealed class HelloPayload
{
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public uint Ssrc { get; set; }
    public int RtcpPort { get; set; }
}

public sealed class ChatPayload
{
    public string Sender { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsUrgent { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocationPayload
{
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double SpeedKmh { get; set; }
    public bool IsSos { get; set; }
    public string Note { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public sealed class FileHeaderPayload
{
    public string Sender { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
}

public sealed class CommandPayload
{
    public string Command { get; set; } = ""; // ACK_SOS, DISPATCH, SET_QUALITY
    public string Message { get; set; } = "";
}

/// <summary>
/// Framing TCP dạng [4-byte Length Big-Endian][1-byte MsgType][Payload]
/// và gói tin GPS nhị phân gọn nhẹ gửi qua UDP 8889.
/// </summary>
public static class ControlProtocol
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task SendJsonAsync<T>(NetworkStream stream, ControlMsgType type, T obj, CancellationToken ct = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(obj, JsonOpts);
        int totalLen = 1 + json.Length;
        byte[] header = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), totalLen);
        header[4] = (byte)type;

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(json, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task SendFileAsync(NetworkStream stream, string sender, string filePath, CancellationToken ct = default)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists || fi.Length > Protocol.MaxFileSize)
            throw new InvalidOperationException("Tệp không tồn tại hoặc vượt quá giới hạn 100 MB.");

        var meta = new FileHeaderPayload { Sender = sender, FileName = fi.Name, FileSize = fi.Length };
        byte[] metaBytes = JsonSerializer.SerializeToUtf8Bytes(meta, JsonOpts);

        long totalLen = 1L + 4L + metaBytes.Length + fi.Length;
        if (totalLen > int.MaxValue) throw new InvalidOperationException("Kích thước khung vượt giới hạn.");

        byte[] prefix = new byte[9];
        BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(0, 4), (int)totalLen);
        prefix[4] = (byte)ControlMsgType.FileTransfer;
        BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(5, 4), metaBytes.Length);

        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(metaBytes, ct);

        await using var fs = fi.OpenRead();
        byte[] buf = new byte[64 * 1024];
        int read;
        while ((read = await fs.ReadAsync(buf, ct)) > 0)
            await stream.WriteAsync(buf.AsMemory(0, read), ct);

        await stream.FlushAsync(ct);
    }

    public static async Task<(ControlMsgType Type, byte[] Payload)?> ReadFrameAsync(NetworkStream stream, CancellationToken ct = default)
    {
        byte[] lenBuf = new byte[4];
        if (!await ReadExactAsync(stream, lenBuf, ct)) return null;

        int totalLen = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (totalLen <= 0 || totalLen > Protocol.MaxFileSize + 65536) return null;

        byte[] body = new byte[totalLen];
        if (!await ReadExactAsync(stream, body, ct)) return null;

        var type = (ControlMsgType)body[0];
        byte[] payload = new byte[totalLen - 1];
        Buffer.BlockCopy(body, 1, payload, 0, payload.Length);
        return (type, payload);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (n == 0) return false;
            offset += n;
        }
        return true;
    }

    public static T Deserialize<T>(byte[] payload) => JsonSerializer.Deserialize<T>(payload, JsonOpts);

    // --- Đóng gói / Giải mã gói tin GPS UDP (Port 8889) ---
    // Format: [Magic "LOC1" 4B][SSRC 4B][Lat 8B][Lon 8B][Speed 4B][IsSos 1B][UnixMs 8B][NameLen 1B][Name UTF8]
    public static byte[] BuildLocationDatagram(uint ssrc, string name, double lat, double lon, float speedKmh, bool isSos)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name ?? "Unit");
        if (nameBytes.Length > 200) Array.Resize(ref nameBytes, 200);

        byte[] buf = new byte[38 + nameBytes.Length];
        buf[0] = (byte)'L'; buf[1] = (byte)'O'; buf[2] = (byte)'C'; buf[3] = (byte)'1';
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), ssrc);
        BinaryPrimitives.WriteDoubleBigEndian(buf.AsSpan(8, 8), lat);
        BinaryPrimitives.WriteDoubleBigEndian(buf.AsSpan(16, 8), lon);
        BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(24, 4), speedKmh);
        buf[28] = (byte)(isSos ? 1 : 0);
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(29, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        buf[37] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, buf, 38, nameBytes.Length);
        return buf;
    }

    public static bool TryParseLocationDatagram(byte[] data, int len, out (uint Ssrc, string Name, double Lat, double Lon, float SpeedKmh, bool IsSos, DateTime TimeUtc) loc)
    {
        loc = default;
        if (len < 38 || data[0] != 'L' || data[1] != 'O' || data[2] != 'C' || data[3] != '1') return false;
        int nameLen = data[37];
        if (len < 38 + nameLen) return false;

        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
        double lat = BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(8, 8));
        double lon = BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(16, 8));
        float speed = BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(24, 4));
        bool isSos = data[28] == 1;
        long unixMs = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(29, 8));
        string name = Encoding.UTF8.GetString(data, 38, nameLen);

        loc = (ssrc, name, lat, lon, speed, isSos, DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime);
        return true;
    }
}