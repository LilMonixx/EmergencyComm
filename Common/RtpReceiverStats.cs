namespace EmergencyComm.Common;

/// <summary>
/// Thống kê phía nhận RTP theo RFC 3550 (Phụ lục A.1 và A.8):
/// số gói mong đợi / đã nhận, tỉ lệ mất gói, jitter, LSR/DLSR phục vụ tính RTT.
/// </summary>
public sealed class RtpReceiverStats
{
    private readonly object _l = new();
    private bool _init;
    private ushort _baseSeq, _maxSeq;
    private uint _cycles;
    private long _received, _bytes;
    private long _expectedPrior, _receivedPrior;
    private double _jitter;
    private int _lastTransit;
    private bool _hasTransit;
    private uint _lastSrMiddle;
    private DateTime _lastSrArrival;

    public void OnPacket(ushort seq, uint rtpTs, DateTime arrivalUtc, int bytes)
    {
        lock (_l)
        {
            if (!_init) { _init = true; _baseSeq = _maxSeq = seq; }
            else
            {
                ushort delta = unchecked((ushort)(seq - _maxSeq));
                if (delta != 0 && delta < 32768)            // gói mới (có thể vòng qua 65535 -> 0)
                {
                    if (seq < _maxSeq) _cycles += 65536;
                    _maxSeq = seq;
                }
            }
            _received++;
            _bytes += bytes;

            // Jitter: J += (|D| - J) / 16, tính theo đơn vị đồng hồ RTP (90 kHz)
            long arrivalUnits = (long)((arrivalUtc - DateTime.UnixEpoch).TotalSeconds * Protocol.ClockRate);
            uint arrivalTs = unchecked((uint)arrivalUnits);
            int transit = unchecked((int)(arrivalTs - rtpTs));
            if (_hasTransit)
            {
                long d = Math.Abs((long)unchecked(transit - _lastTransit));
                _jitter += (d - _jitter) / 16.0;
            }
            _lastTransit = transit;
            _hasTransit = true;
        }
    }

    public void OnSenderReport(uint ntpMiddle32)
    {
        lock (_l) { _lastSrMiddle = ntpMiddle32; _lastSrArrival = DateTime.UtcNow; }
    }

    public uint LastSrMiddle { get { lock (_l) return _lastSrMiddle; } }

    /// <summary>DLSR: thời gian (đơn vị 1/65536 s) từ lúc nhận SR cuối đến bây giờ.</summary>
    public uint DelaySinceLastSr()
    {
        lock (_l)
        {
            if (_lastSrMiddle == 0) return 0;
            return (uint)((DateTime.UtcNow - _lastSrArrival).TotalSeconds * 65536.0);
        }
    }

    public uint ExtendedHighestSeq { get { lock (_l) return _cycles | _maxSeq; } }
    public long Received { get { lock (_l) return _received; } }
    public long Bytes { get { lock (_l) return _bytes; } }
    public uint JitterUnits { get { lock (_l) return (uint)_jitter; } }
    public double JitterMs { get { lock (_l) return _jitter / (Protocol.ClockRate / 1000.0); } }

    private long ExpectedLocked() => _init ? (long)(_cycles | _maxSeq) - _baseSeq + 1 : 0;

    public double LossPercent
    {
        get
        {
            lock (_l)
            {
                long exp = ExpectedLocked();
                return exp <= 0 ? 0 : Math.Max(0, exp - _received) * 100.0 / exp;
            }
        }
    }

    /// <summary>Lấy fraction lost (0..255) của khoảng từ lần gọi trước + tổng số gói mất (cumulative).</summary>
    public (byte fraction, int cumulativeLost) Snapshot()
    {
        lock (_l)
        {
            long expected = ExpectedLocked();
            long expInt = expected - _expectedPrior; _expectedPrior = expected;
            long recInt = _received - _receivedPrior; _receivedPrior = _received;
            long lostInt = expInt - recInt;
            byte fraction = (expInt <= 0 || lostInt <= 0) ? (byte)0 : (byte)Math.Min(255L, (lostInt << 8) / expInt);
            long cum = Math.Max(0, expected - _received);
            return (fraction, (int)Math.Min(cum, int.MaxValue));
        }
    }
}
