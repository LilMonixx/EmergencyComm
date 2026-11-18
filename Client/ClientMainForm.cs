using System.Net;
using System.Net.Sockets;
using EmergencyComm.Common;

namespace EmergencyComm.Client;

public sealed class ClientMainForm : Form
{
    private readonly string _clientId = Guid.NewGuid().ToString("N")[..8];
    private readonly uint _ssrc = (uint)Random.Shared.Next(1, int.MaxValue);
    private CancellationTokenSource _cts;

    private TcpClient _tcp;
    private NetworkStream _tcpStream;
    private UdpClient _locUdp;
    private UdpClient _rtpUdp;
    private UdpClient _rtcpUdp;
    private RtpJpegSender _jpegSender;

    private volatile bool _isStreamingVideo;
    private volatile bool _isSosActive;
    private volatile int _jpegQuality = 70;
    private double _lat = 16.0605;
    private double _lon = 108.2110;

    // UI Controls
    private TextBox _txtServerIp, _txtUnitName, _txtLat, _txtLon, _txtChat;
    private ComboBox _cmbVideoSource;
    private CheckBox _chkSimulateMove;
    private Button _btnConnect, _btnToggleVideo, _btnSos, _btnSendPin, _btnSendFile, _btnSendChat;
    private Label _lblRtcpStats;
    private RichTextBox _rtbLog;

    public ClientMainForm()
    {
        Text = "THIẾT BỊ HIỆN TRƯỜNG (CLIENT) - LIÊN LẠC KHẨN CẤP";
        Width = 860;
        Height = 660;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(26, 30, 38);
        ForeColor = Color.WhiteSmoke;

        BuildUi();
        FormClosing += async (_, _) => await DisconnectAsync();
    }

    private void BuildUi()
    {
        var topPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8), BackColor = Color.FromArgb(18, 22, 28) };
        topPanel.Controls.Add(new Label { Text = "Server IP:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        _txtServerIp = new TextBox { Text = "127.0.0.1", Width = 120 };
        topPanel.Controls.Add(_txtServerIp);

        topPanel.Controls.Add(new Label { Text = "Tên đội:", AutoSize = true, Margin = new Padding(8, 6, 4, 0) });
        _txtUnitName = new TextBox { Text = $"CuuHo-{_clientId[..4].ToUpper()}", Width = 130 };
        topPanel.Controls.Add(_txtUnitName);

        _btnConnect = new Button { Text = "Kết nối Trung tâm", Width = 150, Height = 28, BackColor = Color.SeaGreen, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        _btnConnect.Click += async (_, _) =>
        {
            if (_tcp == null) await ConnectAsync();
            else await DisconnectAsync();
        };
        topPanel.Controls.Add(_btnConnect);

        // Video & SOS Panel
        var actionPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8), BackColor = Color.FromArgb(32, 37, 46) };
        _cmbVideoSource = new ComboBox { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbVideoSource.Items.AddRange(["Camera Mô Phỏng (HUD)", "Chụp Màn Hình Trực Tiếp"]);
        _cmbVideoSource.SelectedIndex = 0;
        actionPanel.Controls.Add(_cmbVideoSource);

        _btnToggleVideo = new Button { Text = "Bắt đầu phát Video RTP", Width = 175, Height = 32, Enabled = false, BackColor = Color.SteelBlue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        _btnToggleVideo.Click += (_, _) =>
        {
            _isStreamingVideo = !_isStreamingVideo;
            _btnToggleVideo.Text = _isStreamingVideo ? "Dừng phát Video" : "Bắt đầu phát Video RTP";
            _btnToggleVideo.BackColor = _isStreamingVideo ? Color.DarkOrange : Color.SteelBlue;
        };
        actionPanel.Controls.Add(_btnToggleVideo);

        _btnSos = new Button { Text = "PHÁT TÍN HIỆU SOS", Width = 185, Height = 32, Enabled = false, BackColor = Color.Crimson, ForeColor = Color.White, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), FlatStyle = FlatStyle.Flat };
        _btnSos.Click += async (_, _) => await TriggerSosAsync();
        actionPanel.Controls.Add(_btnSos);

        // GPS Panel
        var gpsPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8) };
        gpsPanel.Controls.Add(new Label { Text = "Vĩ độ (Lat):", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        _txtLat = new TextBox { Text = _lat.ToString("F5"), Width = 95 };
        gpsPanel.Controls.Add(_txtLat);

        gpsPanel.Controls.Add(new Label { Text = "Kinh độ (Lon):", AutoSize = true, Margin = new Padding(6, 6, 4, 0) });
        _txtLon = new TextBox { Text = _lon.ToString("F5"), Width = 95 };
        gpsPanel.Controls.Add(_txtLon);

        _chkSimulateMove = new CheckBox { Text = "Giả lập di chuyển GPS (UDP 8889)", Checked = true, AutoSize = true, Margin = new Padding(8, 5, 8, 0) };
        gpsPanel.Controls.Add(_chkSimulateMove);

        _btnSendPin = new Button { Text = "Chốt tọa độ (TCP)", Width = 145, Height = 28, Enabled = false };
        _btnSendPin.Click += async (_, _) => await SendReliableLocationAsync();
        gpsPanel.Controls.Add(_btnSendPin);

        // RTCP QoS Bar
        _lblRtcpStats = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            BackColor = Color.FromArgb(16, 20, 26),
            ForeColor = Color.LightGreen,
            Font = new Font("Consolas", 9.5f, FontStyle.Bold),
            Text = "RTCP Feedback: Chưa kết nối...",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0)
        };

        // Bottom Chat & File Panel
        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8, 6, 8, 6) };
        _txtChat = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Nhập báo cáo tình huống gửi về Trung tâm..." };
        _btnSendChat = new Button { Text = "Gửi tin (TCP)", Dock = DockStyle.Right, Width = 110, Enabled = false };
        _btnSendFile = new Button { Text = "Gửi ảnh/tệp...", Dock = DockStyle.Right, Width = 115, Enabled = false };
        _btnSendChat.Click += async (_, _) => await SendChatAsync();
        _btnSendFile.Click += async (_, _) => await SendFileDialogAsync();
        bottomPanel.Controls.Add(_txtChat);
        bottomPanel.Controls.Add(_btnSendChat);
        bottomPanel.Controls.Add(_btnSendFile);

        _rtbLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(18, 22, 28),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9.5f)
        };

        Controls.Add(_rtbLog);
        Controls.Add(_lblRtcpStats);
        Controls.Add(gpsPanel);
        Controls.Add(actionPanel);
        Controls.Add(topPanel);
        Controls.Add(bottomPanel);
    }

    private async Task ConnectAsync()
    {
        try
        {
            string ipStr = _txtServerIp.Text.Trim();
            var serverIp = IPAddress.Parse(ipStr);
            _cts = new CancellationTokenSource();

            _locUdp = NetUtil.CreateUdp(0);
            _rtpUdp = NetUtil.CreateUdp(0);
            _rtcpUdp = NetUtil.CreateUdp(0);
            int localRtcpPort = ((IPEndPoint)_rtcpUdp.Client.LocalEndPoint).Port;

            _jpegSender = new RtpJpegSender(_rtpUdp, new IPEndPoint(serverIp, Protocol.RtpPort), _ssrc);

            _tcp = new TcpClient();
            await _tcp.ConnectAsync(serverIp, Protocol.TcpPort, _cts.Token);
            _tcpStream = _tcp.GetStream();

            await ControlProtocol.SendJsonAsync(_tcpStream, ControlMsgType.Hello, new HelloPayload
            {
                ClientId = _clientId,
                ClientName = _txtUnitName.Text.Trim(),
                Ssrc = _ssrc,
                RtcpPort = localRtcpPort
            }, _cts.Token);

            SetConnectedUi(true);
            AppendLog($"[KẾT NỐI] Đã kết nối tới {ipStr}:{Protocol.TcpPort} | SSRC=0x{_ssrc:X8} | Local RTCP={localRtcpPort}");

            _ = Task.Run(() => ReceiveTcpCommandsLoopAsync(_cts.Token));
            _ = Task.Run(() => GpsUdpLoopAsync(new IPEndPoint(serverIp, Protocol.LocationUdpPort), _cts.Token));
            _ = Task.Run(() => VideoRtpLoopAsync(_cts.Token));
            _ = Task.Run(() => RtcpLoopAsync(new IPEndPoint(serverIp, Protocol.RtcpPort), _cts.Token));
        }
        catch (Exception ex)
        {
            AppendLog($"[LỖI KẾT NỐI] {ex.Message}", Color.Red);
            await DisconnectAsync();
        }
    }

    private async Task DisconnectAsync()
    {
        try
        {
            _isStreamingVideo = false;
            if (_rtcpUdp != null && IPAddress.TryParse(_txtServerIp.Text.Trim(), out var ip))
            {
                byte[] bye = Rtcp.BuildBye(_ssrc);
                await _rtcpUdp.SendAsync(bye, bye.Length, new IPEndPoint(ip, Protocol.RtcpPort));
            }
        }
        catch { }

        _cts?.Cancel();
        _tcpStream?.Dispose();
        _tcp?.Dispose();
        _locUdp?.Dispose();
        _rtpUdp?.Dispose();
        _rtcpUdp?.Dispose();
        _tcp = null;
        _tcpStream = null;
        SetConnectedUi(false);
    }

    private async Task ReceiveTcpCommandsLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _tcpStream != null)
            {
                var frame = await ControlProtocol.ReadFrameAsync(_tcpStream, ct);
                if (frame == null) break;
                if (frame.Value.Type == ControlMsgType.ServerCommand)
                {
                    var cmd = ControlProtocol.Deserialize<CommandPayload>(frame.Value.Payload);
                    if (cmd.Command == "ACK_SOS") _isSosActive = false;
                    AppendLog($"[LỆNH TỪ TRUNG TÂM - {cmd.Command}] {cmd.Message}", Color.Gold);
                }
            }
        }
        catch { }
    }

    private async Task GpsUdpLoopAsync(IPEndPoint dest, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_chkSimulateMove.Checked)
                {
                    _lat += 0.00012;
                    _lon += 0.00015;
                    BeginInvoke(() =>
                    {
                        _txtLat.Text = _lat.ToString("F5");
                        _txtLon.Text = _lon.ToString("F5");
                    });
                }
                else
                {
                    double.TryParse(_txtLat.Text, out _lat);
                    double.TryParse(_txtLon.Text, out _lon);
                }

                float speed = _chkSimulateMove.Checked ? 42.5f : 0f;
                byte[] dgram = ControlProtocol.BuildLocationDatagram(_ssrc, _txtUnitName.Text.Trim(), _lat, _lon, speed, _isSosActive);
                await _locUdp.SendAsync(dgram, dgram.Length, dest);
            }
            catch { }
            await Task.Delay(1000, ct);
        }
    }

    private async Task VideoRtpLoopAsync(CancellationToken ct)
    {
        int frameIdx = 0;
        while (!ct.IsCancellationRequested)
        {
            if (_isStreamingVideo && _jpegSender != null)
            {
                try
                {
                    bool useScreen = false;
                    Invoke(() => useScreen = _cmbVideoSource.SelectedIndex == 1);
                    byte[] jpeg = CameraSource.CaptureFrame(useScreen, _txtUnitName.Text.Trim(), _lat, _lon, _isSosActive, ++frameIdx, _jpegQuality);
                    _jpegSender.SendFrame(jpeg);
                }
                catch { }
                await Task.Delay(50, ct); // ~20 FPS
            }
            else
            {
                await Task.Delay(150, ct);
            }
        }
    }

    private async Task RtcpLoopAsync(IPEndPoint serverRtcpEp, CancellationToken ct)
    {
        // Task phụ nhận Receiver Report (RR) từ Server để tính RTT và điều chỉnh bitrate
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var res = await _rtcpUdp.ReceiveAsync(ct);
                    foreach (var msg in Rtcp.Parse(res.Buffer, res.Buffer.Length))
                    {
                        if (msg.Type == Rtcp.RR && msg.HasBlock && msg.SourceSsrc == _ssrc)
                        {
                            double lossPct = msg.FractionLost * 100.0 / 256.0;
                            double jitterMs = msg.Jitter / (Protocol.ClockRate / 1000.0);
                            double rttMs = 0;
                            if (msg.Lsr != 0)
                            {
                                uint nowMid = Rtcp.NowMiddle32();
                                int diff = unchecked((int)(nowMid - msg.Lsr - msg.Dlsr));
                                if (diff > 0) rttMs = (diff / 65536.0) * 1000.0;
                            }

                            // Tự động điều chỉnh chất lượng JPEG khi mạng nghẽn
                            if (lossPct > 5.0 && _jpegQuality > 35) _jpegQuality -= 10;
                            else if (lossPct < 1.0 && _jpegQuality < 75) _jpegQuality += 5;

                            BeginInvoke(() =>
                            {
                                _lblRtcpStats.Text = $"RTCP RR -> Loss: {lossPct:F1}% | Jitter: {jitterMs:F2} ms | RTT: {rttMs:F1} ms | JPEG Quality: {_jpegQuality}%";
                            });
                        }
                    }
                }
                catch { }
            }
        }, ct);

        // Gửi định kỳ Sender Report (SR) mỗi 1.5 giây
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1500, ct);
            try
            {
                if (_jpegSender != null)
                {
                    byte[] sr = Rtcp.BuildSenderReport(_ssrc, _jpegSender.NowTimestamp(), _jpegSender.PacketCount, _jpegSender.OctetCount);
                    await _rtcpUdp.SendAsync(sr, sr.Length, serverRtcpEp);
                }
            }
            catch { }
        }
    }

    private async Task TriggerSosAsync()
    {
        if (_tcpStream == null) return;
        _isSosActive = true;
        _isStreamingVideo = true;
        _btnToggleVideo.Text = "Dừng phát Video";

        await ControlProtocol.SendJsonAsync(_tcpStream, ControlMsgType.SosAlert, new LocationPayload
        {
            ClientId = _clientId,
            ClientName = _txtUnitName.Text.Trim(),
            Latitude = _lat,
            Longitude = _lon,
            IsSos = true,
            Note = "Yêu cầu hỗ trợ khẩn cấp ngay lập tức!"
        }, _cts.Token);

        AppendLog("[SOS] Đã phát tín hiệu khẩn cấp và tự động bật truyền hình trực tiếp!", Color.Red);
    }

    private async Task SendReliableLocationAsync()
    {
        if (_tcpStream == null) return;
        await ControlProtocol.SendJsonAsync(_tcpStream, ControlMsgType.ReliableLocation, new LocationPayload
        {
            ClientId = _clientId,
            ClientName = _txtUnitName.Text.Trim(),
            Latitude = _lat,
            Longitude = _lon,
            SpeedKmh = _chkSimulateMove.Checked ? 42.5 : 0,
            Note = "Điểm tập kết cứu hộ xác nhận"
        }, _cts.Token);
        AppendLog($"[GPS TCP] Đã gửi tọa độ tin cậy ({_lat:F5}, {_lon:F5}).", Color.Cyan);
    }

    private async Task SendChatAsync()
    {
        if (_tcpStream == null || string.IsNullOrWhiteSpace(_txtChat.Text)) return;
        string msg = _txtChat.Text.Trim();
        _txtChat.Clear();
        await ControlProtocol.SendJsonAsync(_tcpStream, ControlMsgType.Chat, new ChatPayload
        {
            Sender = _txtUnitName.Text.Trim(),
            Text = msg,
            IsUrgent = _isSosActive
        }, _cts.Token);
        AppendLog($"[TÔI] {msg}");
    }

    private async Task SendFileDialogAsync()
    {
        if (_tcpStream == null) return;
        using var dlg = new OpenFileDialog { Title = "Chọn hình ảnh / tài liệu hiện trường (tối đa 100 MB)" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            await ControlProtocol.SendFileAsync(_tcpStream, _txtUnitName.Text.Trim(), dlg.FileName, _cts.Token);
            AppendLog($"[TỆP] Đã gửi tệp '{Path.GetFileName(dlg.FileName)}' thành công.", Color.LightGreen);
        }
        catch (Exception ex) { AppendLog($"[LỖI GỬI TỆP] {ex.Message}", Color.Red); }
    }

    private void SetConnectedUi(bool connected)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _btnConnect.Text = connected ? "Ngắt kết nối" : "Kết nối Trung tâm";
            _btnConnect.BackColor = connected ? Color.DimGray : Color.SeaGreen;
            _btnToggleVideo.Enabled = connected;
            _btnSos.Enabled = connected;
            _btnSendPin.Enabled = connected;
            _btnSendChat.Enabled = connected;
            _btnSendFile.Enabled = connected;
        });
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
}