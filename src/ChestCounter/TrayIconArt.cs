using System.Drawing;
using System.Drawing.Drawing2D;

namespace ChestCounter;

/// <summary>Draws the little treasure-chest tray icon at runtime (no image files to ship).</summary>
internal static class TrayIconArt
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var lid = new SolidBrush(Color.FromArgb(0x9A, 0x5B, 0x2E));
            using var body = new SolidBrush(Color.FromArgb(0x7A, 0x43, 0x22));
            using var gold = new SolidBrush(Color.FromArgb(0xFF, 0xC8, 0x3D));
            using var latch = new SolidBrush(Color.FromArgb(0xFF, 0xE0, 0x7A));
            using var outline = new Pen(Color.FromArgb(0x3A, 0x1E, 0x0E), 1.5f);

            using (var lidPath = new GraphicsPath())
            {
                lidPath.AddArc(2, 3, 28, 16, 180, 180);
                lidPath.AddLine(30, 11, 30, 14);
                lidPath.AddLine(30, 14, 2, 14);
                lidPath.CloseFigure();
                g.FillPath(lid, lidPath);
                g.DrawPath(outline, lidPath);
            }

            g.FillRectangle(body, 2, 14, 28, 15);
            g.DrawRectangle(outline, 2, 14, 28, 15);
            g.FillRectangle(gold, 2, 12, 28, 3);
            g.FillRectangle(gold, 7, 4, 2, 25);
            g.FillRectangle(gold, 23, 4, 2, 25);
            g.FillRectangle(latch, 13, 10, 6, 8);
            g.DrawRectangle(outline, 13, 10, 6, 8);
        }

        var handle = bmp.GetHicon();
        try
        {
            // Clone so the icon owns its own handle, then free the GDI one.
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
