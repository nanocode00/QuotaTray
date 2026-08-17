using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace QuotaTray.Desktop.Utils;

public static class TrayIconRenderer
{
    public static WindowIcon CreateTrayIcon(double remainingPercent, bool hasError, bool isDark = true)
    {
        const int size = 64; // High DPI 64x64
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // 1. Draw rounded dark container background
        using var bgPaint = new SKPaint
        {
            Color = new SKColor(22, 24, 31, 240), // Dark glass
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        var rrect = new SKRoundRect(new SKRect(2, 2, size - 2, size - 2), 14, 14);
        canvas.DrawRoundRect(rrect, bgPaint);

        // 2. Determine status color
        SKColor statusColor;
        if (hasError)
        {
            statusColor = new SKColor(239, 68, 68); // Red
        }
        else if (remainingPercent > 30)
        {
            statusColor = new SKColor(16, 185, 129); // Emerald Green
        }
        else if (remainingPercent >= 15)
        {
            statusColor = new SKColor(245, 158, 11); // Amber
        }
        else
        {
            statusColor = new SKColor(239, 68, 68); // Red
        }

        // 3. Draw outer border with status accent
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(45, 50, 68),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2
        };
        canvas.DrawRoundRect(rrect, borderPaint);

        // 4. Draw letter 'Q' or percent
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 32,
            FakeBoldText = true,
            TextAlign = SKTextAlign.Center
        };

        // Center text vertically and horizontally
        var fontMetrics = textPaint.FontMetrics;
        float y = (size / 2f) - ((fontMetrics.Ascent + fontMetrics.Descent) / 2f) - 1;
        canvas.DrawText("Q", size / 2f - 2, y, textPaint);

        // 5. Draw status indicator dot on top-right
        using var dotPaint = new SKPaint
        {
            Color = statusColor,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        using var dotGlowPaint = new SKPaint
        {
            Color = statusColor.WithAlpha(100),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        canvas.DrawCircle(size - 13, 13, 7, dotGlowPaint);
        canvas.DrawCircle(size - 13, 13, 5, dotPaint);

        // 6. Encode to PNG stream and create WindowIcon
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;

        return new WindowIcon(ms);
    }
}
