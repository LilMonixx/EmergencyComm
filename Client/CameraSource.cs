using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace EmergencyComm.Client;

public static class CameraSource
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static byte[] CaptureFrame(bool useScreenCapture, string unitName, double lat, double lon, bool isSos, int frameIndex, int jpegQuality)
    {
        using var bmp = new Bitmap(640, 480);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (useScreenCapture)
            {
                var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 640, 480);
                g.CopyFromScreen(bounds.Location, Point.Empty, new Size(Math.Min(640, bounds.Width), Math.Min(480, bounds.Height)));
            }
            else
            {
                // Tạo khung hình Camera hiện trường mô phỏng kèm radar chuyển động
                g.Clear(Color.FromArgb(18, 24, 32));
                using var gridPen = new Pen(Color.FromArgb(35, 70, 90), 1);
                for (int x = 0; x < 640; x += 40) g.DrawLine(gridPen, x, 0, x, 480);
                for (int y = 0; y < 480; y += 40) g.DrawLine(gridPen, 0, y, 640, y);

                double angle = (frameIndex * 6) % 360 * Math.PI / 180.0;
                int cx = 320, cy = 240, r = 150;
                g.DrawEllipse(Pens.Teal, cx - r, cy - r, r * 2, r * 2);
                g.DrawLine(new Pen(Color.Lime, 2), cx, cy, cx + (int)(Math.Cos(angle) * r), cy + (int)(Math.Sin(angle) * r));
            }

            // Lớp phủ thông tin kỹ thuật (Telemetry HUD)
            using var hudBg = new SolidBrush(Color.FromArgb(165, 0, 0, 0));
            g.FillRectangle(hudBg, 10, 10, 620, 68);
            using var font = new Font("Consolas", 11f, FontStyle.Bold);
            g.DrawString($"UNIT: {unitName} | FRAME #{frameIndex} | Q={jpegQuality}%", font, Brushes.Cyan, 18, 16);
            g.DrawString($"GPS: {lat:F5}, {lon:F5} | UTC: {DateTime.UtcNow:HH:mm:ss.fff}", font, Brushes.White, 18, 42);

            if (isSos)
            {
                g.FillRectangle(Brushes.Crimson, 460, 16, 160, 32);
                g.DrawString("SOS EMERGENCY", font, Brushes.White, 472, 22);
            }
        }

        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(jpegQuality, 20, 95));
        using var ms = new MemoryStream();
        bmp.Save(ms, JpegCodec, ep);
        return ms.ToArray();
    }
}