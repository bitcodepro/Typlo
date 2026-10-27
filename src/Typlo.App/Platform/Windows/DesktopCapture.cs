using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Typlo.Core.Imaging;

namespace Typlo.App.Platform.Windows;

/// <summary>One GDI copy before any selection windows appear; no temporary image files.</summary>
internal static class DesktopCapture
{
    internal sealed record Frame(PixelRect Bounds, byte[] Bgra);
    internal static Frame Grab()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var bounds = new PixelRect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        if (bounds.Width < 1 || bounds.Height < 1 || (long)bounds.Width * bounds.Height > CaptureRegion.MaxPixels)
            throw new InvalidOperationException("Desktop dimensions exceed the screen capture limit.");
        var screen = GetDC(0);
        if (screen == 0) throw new Win32Exception();
        nint dc = 0, bitmap = 0, previous = 0;
        try
        {
            dc = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            if (dc == 0 || bitmap == 0) throw new Win32Exception();
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1) throw new Win32Exception();
            if (!BitBlt(dc, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x40CC0020 /* SRCCOPY | CAPTUREBLT */))
                throw new Win32Exception();
            SelectObject(dc, previous); previous = 0; // GetDIBits requires a deselected bitmap
            var header = new BitmapInfoHeader
            {
                Size = 40, Width = bounds.Width, Height = -bounds.Height, Planes = 1, BitCount = 32,
            };
            var pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
            if (GetDIBits(dc, bitmap, 0, (uint)bounds.Height, pixels, ref header, 0) != bounds.Height)
                throw new Win32Exception();
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new(bounds, pixels);
        }
        finally
        {
            if (previous != 0) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (dc != 0) DeleteDC(dc);
            ReleaseDC(0, screen);
        }
    }

    internal static void FitWindow(nint window, PixelRect bounds) =>
        SetWindowPos(window, -1, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 /* NOACTIVATE */);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int w, int h, nint source, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] data, ref BitmapInfoHeader header, uint usage);
}
