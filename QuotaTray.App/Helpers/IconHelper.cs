using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace QuotaTray.App.Helpers;

public static class IconHelper
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateDynamicTrayIcon(double lowestPercent, bool hasError, int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            // Determine status color
            Color statusColor;
            Color bgColor;

            if (hasError)
            {
                statusColor = Color.FromArgb(255, 156, 163, 175); // Gray #9CA3AF
                bgColor = Color.FromArgb(255, 31, 41, 55);       // #1F2937
            }
            else if (lowestPercent >= 50.0)
            {
                statusColor = Color.FromArgb(255, 16, 185, 129); // Green #10B981
                bgColor = Color.FromArgb(255, 6, 78, 59);        // Deep Green #064E3B
            }
            else if (lowestPercent >= 20.0)
            {
                statusColor = Color.FromArgb(255, 245, 158, 11); // Orange #F59E0B
                bgColor = Color.FromArgb(255, 120, 53, 15);      // Deep Orange #78350F
            }
            else
            {
                statusColor = Color.FromArgb(255, 239, 68, 68);  // Red #EF4444
                bgColor = Color.FromArgb(255, 127, 29, 29);      // Deep Red #7F1D1D
            }

            // Draw rounded badge base
            using (var path = GetRoundedRect(new Rectangle(1, 1, size - 2, size - 2), 6))
            {
                using var bgBrush = new LinearGradientBrush(
                    new Point(0, 0),
                    new Point(size, size),
                    Color.FromArgb(255, 26, 27, 35),
                    Color.FromArgb(255, 15, 16, 22)
                );
                g.FillPath(bgBrush, path);

                // Subtle border
                using var borderPen = new Pen(Color.FromArgb(120, 63, 63, 70), 1f);
                g.DrawPath(borderPen, path);
            }

            // Draw circular gauge arc
            float arcMargin = size * 0.12f;
            float arcSize = size - (arcMargin * 2);
            var arcRect = new RectangleF(arcMargin, arcMargin, arcSize, arcSize);

            using (var trackPen = new Pen(Color.FromArgb(60, 255, 255, 255), 2.5f))
            {
                g.DrawArc(trackPen, arcRect, -90, 360);
            }

            using (var arcPen = new Pen(statusColor, 2.5f))
            {
                arcPen.StartCap = LineCap.Round;
                arcPen.EndCap = LineCap.Round;
                float sweep = (float)(Math.Max(0.0, Math.Min(100.0, lowestPercent)) / 100.0 * 360.0);
                if (sweep > 0)
                {
                    g.DrawArc(arcPen, arcRect, -90, sweep);
                }
            }

            // Inner text: show number (e.g. "2", "97", "100" or "!")
            string label;
            if (hasError)
            {
                label = "!";
            }
            else
            {
                int p = (int)Math.Round(lowestPercent);
                label = p >= 100 ? "99" : p.ToString();
            }

            float fontSize = label.Length >= 2 ? size * 0.32f : size * 0.40f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);

            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            // Offset slightly for optical centering
            var textRect = new RectangleF(0, 1, size, size);
            g.DrawString(label, font, textBrush, textRect, sf);
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}
