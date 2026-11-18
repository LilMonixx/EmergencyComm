using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using EmergencyComm.Common;

namespace EmergencyComm.Server;

public sealed class ServerMainForm : Form
{
    private sealed class ClientSession
    {
        public string ClientId { get; set; } = "";
        public string Name { get; set; } = "Unknown";
        public uint Ssrc { get; set; }
        public TcpClient Tcp { get; set; }
        public NetworkStream Stream { get; set; }
        public IPEndPoint RtcpEndPoint { get; set; }
        public JpegReassembler Reassembler { get; } = new();
        public RtpReceiverStats Stats { get; } = new();
        public double Latitude { get; set; } = 16.0544;  // Mặc định khu vực Đà Nẵng
        public double Longitude { get; set; } = 108.2022;
        public float SpeedKmh { get; set; }
        public bool IsSos { get; set; }
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<uint, ClientSession> _sessionsBySsrc = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly uint _serverSsrc = (uint)Random.Shared.Next(1, int.MaxValue);

    private TcpListener _tcpListener;
    private UdpClient _locUdp;
    private UdpClient _rtpUdp;
    private UdpClient _rtcpUdp;
    private uint _activeVideoSsrc;

    // UI Controls
    private PictureBox _picVideo;
    private Panel _pnlRadar;
    private ListView _lvUnits;
    private RichTextBox _rtbLog;
    private TextBox _txtDispatch;
    private Button _btnSendCmd, _btnAckSos;
    private Label _lblQos, _lblStatus;

    public ServerMainForm()
    {
        Text = "TRUNG TÂM ĐIỀU PHỐI KHẨN CẤP (SERVER) - TCP/UDP/RTP/RTCP";
        Width = 1280;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 28, 36);
        ForeColor = Color.WhiteSmoke;

        BuildUi();
        Load += (_, _) => StartNetworkServices();
        FormClosing += (_, _) => _cts.Cancel();
    }

    private void BuildUi()
    {
        var topBar = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Color.FromArgb(16, 20, 28), Padding = new Padding(12, 8, 12, 8) };
        _lblStatus = new Label
        {
            Text = $"Đang lắng nghe: TCP {Protocol.TcpPort} | GPS UDP {Protocol.LocationUdpPort} | RTP {Protocol.RtpPort} | RTCP {Protocol.RtcpPort}",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.LightSkyBlue,
            TextAlign = ContentAlignment.MiddleLeft
        };
        topBar.Controls.Add(_lblStatus);

        var mainSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 740 };

        // Left: Video Stream + QoS + Radar Map
        var leftSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 430 };

        var videoPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        _picVideo = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle
        };
        _lblQos = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            BackColor = Color.FromArgb(32, 38, 48),
            ForeColor = Color.LightGreen,
            Font = new Font("Consolas", 9.5f, FontStyle.Bold),
            Text = "RTP QoS: Chưa nhận luồng video...",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0)
        };
        videoPanel.Controls.Add(_picVideo);
        videoPanel.Controls.Add(_lblQos);
        leftSplit.Panel1.Controls.Add(videoPanel);

        // Radar / Tactical Map Panel
        var mapContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var lblMapTitle = new Label { Text = "BẢN ĐỒ RADAR ĐỊNH VỊ THỜI GIAN THỰC (UDP 8889 & TCP 8888)", Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _pnlRadar = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(14, 26, 22), BorderStyle = BorderStyle.FixedSingle };
        _pnlRadar.Paint += PnlRadar_Paint;
        mapContainer.Controls.Add(_pnlRadar);
        mapContainer.Controls.Add(lblMapTitle);
        leftSplit.Panel2.Controls.Add(mapContainer);

        mainSplit.Panel1.Controls.Add(leftSplit);

        // Right: Active Units + Dispatch Log + Controls
        var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };

        _lvUnits = new ListView
        {
            Dock = DockStyle.Top,
            Height = 210,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            BackColor = Color.FromArgb(30, 35, 45),
            ForeColor = Color.White
        };
        _lvUnits.Columns.Add("Đơn vị", 120);
        _lvUnits.Columns.Add("SSRC", 85);
        _lvUnits.Columns.Add("Tọa độ (Lat, Lon)", 155);
        _lvUnits.Columns.Add("Tốc độ", 75);
        _lvUnits.Columns.Add("Trạng thái", 85);
        _lvUnits.SelectedIndexChanged += (_, _) =>
        {
            if (_lvUnits.SelectedItems.Count > 0 && _lvUnits.SelectedItems[0].Tag is uint ssrc)
                _activeVideoSsrc = ssrc;
        };

        var cmdBar = new Panel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(0, 6, 0, 0) };
        _txtDispatch = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Nhập lệnh điều phối gửi tới đội hiện trường..." };
        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.LeftToRight };
        _btnSendCmd = new Button { Text = "Gửi lệnh điều phối (TCP)", Width = 185, Height = 30, BackColor = Color.SteelBlue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        _btnAckSos = new Button { Text = "Xác nhận cứu hộ SOS", Width = 175, Height = 30, BackColor = Color.Crimson, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        _btnSendCmd.Click += async (_, _) => await SendCommandToSelectedAsync("DISPATCH", _txtDispatch.Text);
        _btnAckSos.Click += async (_, _) => await SendCommandToSelectedAsync("ACK_SOS", "Trung tâm đã tiếp nhận SOS và đang điều động lực lượng!");
        btnRow.Controls.Add(_btnSendCmd);
        btnRow.Controls.Add(_btnAckSos);
        cmdBar.Controls.Add(_txtDispatch);
        cmdBar.Controls.Add(btnRow);

        _rtbLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(18, 22, 28),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9.5f),
            BorderStyle = BorderStyle.FixedSingle
        };

        rightPanel.Controls.Add(_rtbLog);
        rightPanel.Controls.Add(_lvUnits);
        rightPanel.Controls.Add(cmdBar);
        mainSplit.Panel2.Controls.Add(rightPanel);

        Controls.Add(mainSplit);
        Controls.Add(topBar);
    }

    private void StartNetworkServices()
    {
        _tcpListener = new TcpListener(IPAddress.Any, Protocol.TcpPort);
        _tcpListener.Start();

        _locUdp = NetUtil.CreateUdp(Protocol.LocationUdpPort);
        _rtpUdp = NetUtil.CreateUdp(Protocol.RtpPort);
        _rtcpUdp = NetUtil.CreateUdp(Protocol.RtcpPort);

        _ = Task.Run(() => AcceptTcpLoopAsync(_cts.Token));
        _ = Task.Run(() => ReceiveLocationUdpLoopAsync(_cts.Token));
        _ = Task.Run(() => ReceiveRtpLoopAsync(_cts.Token));
        _ = Task.Run(() => ReceiveRtcpLoopAsync(_cts.Token));
        _ = Task.Run(() => SendRtcpReportsLoopAsync(_cts.Token));

        var uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        uiTimer.Tick += (_, _) => RefreshDashboard();
        uiTimer.Start();

        AppendLog("[SYSTEM] Máy chủ trung tâm đã sẵn sàng trên tất cả các cổng.");
    }

    private async Task AcceptTcpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpListener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleTcpClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { AppendLog($"[TCP ERROR] {ex.Message}"); }
        }
    }

    private async Task HandleTcpClientAsync(TcpClient tcp, CancellationToken ct)
    {
        ClientSession session = null;
        var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
        try
        {
            await using var stream = tcp.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var frame = await ControlProtocol.ReadFrameAsync(stream, ct);
                if (frame == null) break;

                var (type, payload) = frame.Value;
                switch (type)
                {
                    case ControlMsgType.Hello:
                    {
                        var hello = ControlProtocol.Deserialize<HelloPayload>(payload);
                        session = _sessionsBySsrc.GetOrAdd(hello.Ssrc, s => new ClientSession { Ssrc = s });
                        session.ClientId = hello.ClientId;
                        session.Name = hello.ClientName;
                        session.Tcp = tcp;
                        session.Stream = stream;
                        session.RtcpEndPoint = new IPEndPoint(remoteIp, hello.RtcpPort);
                        session.LastSeenUtc = DateTime.UtcNow;
                        if (_activeVideoSsrc == 0) _activeVideoSsrc = hello.Ssrc;
                        AppendLog($"[TCP] Kết nối mới: {hello.ClientName} (SSRC=0x{hello.Ssrc:X8}) từ {remoteIp}");
                        break;
                    }
                    case ControlMsgType.Chat:
                    {
                        var chat = ControlProtocol.Deserialize<ChatPayload>(payload);
                        AppendLog($"[TIN NHẮN - {chat.Sender}] {chat.Text}", chat.IsUrgent ? Color.Orange : Color.White);
                        break;
                    }
                    case ControlMsgType.SosAlert:
                    {
                        var sos = ControlProtocol.Deserialize<LocationPayload>(payload);
                        if (session != null)
                        {
                            session.IsSos = true;
                            session.Latitude = sos.Latitude;
                            session.Longitude = sos.Longitude;
                            session.LastSeenUtc = DateTime.UtcNow;
                            _activeVideoSsrc = session.Ssrc;
                        }
                        AppendLog($"[CẢNH BÁO SOS] {sos.ClientName} tại ({sos.Latitude:F5}, {sos.Longitude:F5}): {sos.Note}", Color.Red);
                        break;
                    }
                    case ControlMsgType.ReliableLocation:
                    {
                        var loc = ControlProtocol.Deserialize<LocationPayload>(payload);
                        if (session != null)
                        {
                            session.Latitude = loc.Latitude;
                            session.Longitude = loc.Longitude;
                            session.SpeedKmh = (float)loc.SpeedKmh;
                            session.LastSeenUtc = DateTime.UtcNow;
                        }
                        AppendLog($"[GPS TIN CẬY] {loc.ClientName}: ({loc.Latitude:F5}, {loc.Longitude:F5}) - {loc.Note}", Color.Cyan);
                        break;
                    }
                    case ControlMsgType.FileTransfer:
                    {
                        int metaLen = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
                        var meta = ControlProtocol.Deserialize<FileHeaderPayload>(payload.AsSpan(4, metaLen).ToArray());
                        string saveDir = Path.Combine(AppContext.BaseDirectory, "ReceivedFiles");
                        Directory.CreateDirectory(saveDir);
                        string outPath = Path.Combine(saveDir, $"{DateTime.Now:HHmmss}_{Path.GetFileName(meta.FileName)}");
                        await File.WriteAllBytesAsync(outPath, payload.AsSpan(4 + metaLen).ToArray(), ct);
                        AppendLog($"[TỆP ĐÍNH KÈM] Nhận '{meta.FileName}' ({meta.FileSize / 1024.0:F1} KB) từ {meta.Sender} -> {outPath}", Color.LightGreen);
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"[TCP] Ngắt kết nối {session?.Name ?? remoteIp.ToString()}: {ex.Message}");
        }
        finally
        {
            if (session != null)
            {
                session.Tcp = null;
                session.Stream = null;
            }
            tcp.Dispose();
        }
    }

    private async Task ReceiveLocationUdpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _locUdp.ReceiveAsync(ct);
                if (ControlProtocol.TryParseLocationDatagram(res.Buffer, res.Buffer.Length, out var loc))
                {
                    var session = _sessionsBySsrc.GetOrAdd(loc.Ssrc, s => new ClientSession { Ssrc = s, Name = loc.Name });
                    session.Name = loc.Name;
                    session.Latitude = loc.Lat;
                    session.Longitude = loc.Lon;
                    session.SpeedKmh = loc.SpeedKmh;
                    session.IsSos = loc.IsSos;
                    session.LastSeenUtc = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task ReceiveRtpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _rtpUdp.ReceiveAsync(ct);
                if (!RtpPacket.TryParse(res.Buffer, res.Buffer.Length, out var pkt)) continue;

                var session = _sessionsBySsrc.GetOrAdd(pkt.Ssrc, s => new ClientSession { Ssrc = s, Name = $"Unit-{s:X4}" });
                session.LastSeenUtc = DateTime.UtcNow;
                session.Stats.OnPacket(pkt.Sequence, pkt.Timestamp, DateTime.UtcNow, res.Buffer.Length);

                if (pkt.PayloadType == Protocol.JpegPayloadType)
                {
                    byte[] jpeg = session.Reassembler.Push(pkt);
                    if (jpeg != null && (_activeVideoSsrc == 0 || _activeVideoSsrc == pkt.Ssrc))
                    {
                        _activeVideoSsrc = pkt.Ssrc;
                        RenderJpegFrame(jpeg);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task ReceiveRtcpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _rtcpUdp.ReceiveAsync(ct);
                foreach (var msg in Rtcp.Parse(res.Buffer, res.Buffer.Length))
                {
                    var session = _sessionsBySsrc.GetOrAdd(msg.Ssrc, s => new ClientSession { Ssrc = s });
                    session.RtcpEndPoint = res.RemoteEndPoint;

                    if (msg.Type == Rtcp.SR)
                    {
                        uint middle32 = Rtcp.Middle32(msg.NtpSec, msg.NtpFrac);
                        session.Stats.OnSenderReport(middle32);
                    }
                    else if (msg.Type == Rtcp.BYE)
                    {
                        AppendLog($"[RTCP BYE] Đơn vị {session.Name} (0x{msg.Ssrc:X8}) đã dừng luồng video.");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task SendRtcpReportsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1500, ct);
            foreach (var session in _sessionsBySsrc.Values)
            {
                if (session.RtcpEndPoint == null || session.Stats.Received == 0) continue;
                try
                {
                    var (fraction, cumLost) = session.Stats.Snapshot();
                    byte[] rr = Rtcp.BuildReceiverReport(
                        senderSsrc: _serverSsrc,
                        sourceSsrc: session.Ssrc,
                        fractionLost: fraction,
                        cumulativeLost: cumLost,
                        extHighestSeq: session.Stats.ExtendedHighestSeq,
                        jitter: session.Stats.JitterUnits,
                        lsr: session.Stats.LastSrMiddle,
                        dlsr: session.Stats.DelaySinceLastSr()
                    );
                    await _rtcpUdp.SendAsync(rr, rr.Length, session.RtcpEndPoint);
                }
                catch { }
            }
        }
    }

    private void RenderJpegFrame(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            using var img = Image.FromStream(ms);
            var bmp = new Bitmap(img);

            if (IsHandleCreated)
            {
                BeginInvoke(() =>
                {
                    var old = _picVideo.Image;
                    _picVideo.Image = bmp;
                    old?.Dispose();
                });
            }
            else bmp.Dispose();
        }
        catch { }
    }

    private void RefreshDashboard()
    {
        _lvUnits.BeginUpdate();
        _lvUnits.Items.Clear();
        foreach (var s in _sessionsBySsrc.Values)
        {
            var item = new ListViewItem(s.Name) { Tag = s.Ssrc };
            item.SubItems.Add($"0x{s.Ssrc:X8}");
            item.SubItems.Add($"{s.Latitude:F4}, {s.Longitude:F4}");
            item.SubItems.Add($"{s.SpeedKmh:F1} km/h");
            item.SubItems.Add(s.IsSos ? "SOS KHẨN CẤP" : "Hoạt động");
            if (s.IsSos) item.BackColor = Color.DarkRed;
            _lvUnits.Items.Add(item);
        }
        _lvUnits.EndUpdate();

        if (_sessionsBySsrc.TryGetValue(_activeVideoSsrc, out var active))
        {
            _lblQos.Text = $"[{active.Name}] Frames OK: {active.Reassembler.FramesOk} | Dropped: {active.Reassembler.FramesDropped} | " +
                           $"Loss: {active.Stats.LossPercent:F2}% | Jitter: {active.Stats.JitterMs:F2} ms | RX: {active.Stats.Bytes / 1024.0:F0} KB";
        }
        _pnlRadar.Invalidate();
    }

    private void PnlRadar_Paint(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = _pnlRadar.Width, h = _pnlRadar.Height;
        int cx = w / 2, cy = h / 2;

        using var gridPen = new Pen(Color.FromArgb(40, 80, 65), 1);
        for (int r = 40; r < Math.Max(w, h) / 2; r += 45)
            g.DrawEllipse(gridPen, cx - r, cy - r, r * 2, r * 2);
        g.DrawLine(gridPen, 0, cy, w, cy);
        g.DrawLine(gridPen, cx, 0, cx, h);

        // Tâm radar: 16.0544, 108.2022 (Tỷ lệ ~ 1 độ = 2500 px)
        const double centerLat = 16.0544, centerLon = 108.2022, scale = 2500.0;
        foreach (var s in _sessionsBySsrc.Values)
        {
            int px = cx + (int)((s.Longitude - centerLon) * scale);
            int py = cy - (int)((s.Latitude - centerLat) * scale);
            px = Math.Clamp(px, 16, w - 16);
            py = Math.Clamp(py, 16, h - 16);

            Brush b = s.IsSos ? Brushes.Red : Brushes.LimeGreen;
            g.FillEllipse(b, px - 6, py - 6, 12, 12);
            g.DrawString($"{s.Name} ({s.Latitude:F4},{s.Longitude:F4})", Font, Brushes.White, px + 8, py - 7);
        }
    }

    private async Task SendCommandToSelectedAsync(string cmdType, string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return;
        ClientSession target = null;
        if (_lvUnits.SelectedItems.Count > 0 && _lvUnits.SelectedItems[0].Tag is uint ssrc)
            _sessionsBySsrc.TryGetValue(ssrc, out target);
        else
            target = _sessionsBySsrc.Values.FirstOrDefault(x => x.Stream != null);

        if (target?.Stream == null)
        {
            AppendLog("[CẢNH BÁO] Chưa chọn đơn vị hoặc đơn vị chưa kết nối TCP.");
            return;
        }

        await ControlProtocol.SendJsonAsync(target.Stream, ControlMsgType.ServerCommand,
            new CommandPayload { Command = cmdType, Message = msg }, _cts.Token);
        if (cmdType == "ACK_SOS") target.IsSos = false;
        AppendLog($"[ĐIỀU PHỐI -> {target.Name}] {msg}", Color.Gold);
        _txtDispatch.Clear();
    }

    private void AppendLog(string text, Color? color = null)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.SelectionColor = color ?? Color.Gainsboro;
            _rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
            _rtbLog.ScrollToCaret();
        });
    }

    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() => DoubleBuffered = true;
    }
}