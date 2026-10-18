using System.Runtime.InteropServices;

namespace ProactiveAI.Desktop.Interop;

/// <summary>
/// Draws small status icons (rounded square with waveform bars) into 32-bit DIBs for the tray.
/// Color communicates state together with the tooltip text, never alone.
/// </summary>
internal static class TrayIconFactory
{
    public static IntPtr Create(uint argb, int size = 32)
    {
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr color = Win32.CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr mask = Win32.CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        try
        {
            var pixels = new int[size * size];
            byte r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
            double radius = size * 0.22;
            double[] bars = [0.30, 0.55, 0.80, 0.55, 0.30];
            double barW = Math.Max(1.5, size * 0.09), gap = size * 0.05;
            double total = bars.Length * barW + (bars.Length - 1) * gap;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double coverage = RoundedRectCoverage(x + 0.5, y + 0.5, size, radius);
                    if (coverage <= 0)
                    {
                        continue;
                    }
                    bool white = false;
                    double bx = (size - total) / 2;
                    foreach (double h in bars)
                    {
                        double bh = size * h * 0.62;
                        if (x + 0.5 >= bx && x + 0.5 < bx + barW && y + 0.5 >= (size - bh) / 2 && y + 0.5 < (size + bh) / 2)
                        {
                            white = true;
                            break;
                        }
                        bx += barW + gap;
                    }
                    byte a = (byte)Math.Round(255 * coverage);
                    byte pr = white ? (byte)255 : r, pg = white ? (byte)255 : g, pb = white ? (byte)255 : b;
                    // premultiplied BGRA
                    pixels[y * size + x] = (a << 24) | ((pr * a / 255) << 16) | ((pg * a / 255) << 8) | (pb * a / 255);
                }
            }
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var info = new Win32.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            return Win32.CreateIconIndirect(ref info);
        }
        finally
        {
            Win32.DeleteObject(color);
            Win32.DeleteObject(mask);
        }
    }

    private static double RoundedRectCoverage(double px, double py, int size, double r)
    {
        double cx = Math.Clamp(px, r, size - r);
        double cy = Math.Clamp(py, r, size - r);
        double d = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        return Math.Clamp(r - d + 0.5, 0, 1);
    }
}
