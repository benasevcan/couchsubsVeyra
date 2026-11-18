// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using System.Windows;
using CouchSubs.Core;

namespace CouchSubs.App.Services;

/// <summary>
/// Masaüstünün etkin monitörünü doğrudan piksel olarak yakalar (BGRA32 <see cref="Frame"/>); böylece canlı oyun
/// yakalama çalışmıyorken bile fareyle herhangi bir ekran bölgesi seçilip çevrilebilir.
/// </summary>
internal static class ScreenSnapshot
{
    public sealed record Capture(Frame Frame, int PixelLeft, int PixelTop, int PixelWidth, int PixelHeight);

    private const int BiRgb = 0;
    private const uint DibRgbColors = 0;
    private const uint MonitorDefaultToNearest = 2;
    private const int SrcCopy = 0x00CC0020;

    public static Capture? CaptureMonitor(Window? owner)
    {
        var anchor = GetCursorOrWindowPoint(owner);
        var monitor = MonitorFromPoint(anchor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return null;

        var left = info.Monitor.Left;
        var top = info.Monitor.Top;
        var width = info.Monitor.Right - left;
        var height = info.Monitor.Bottom - top;
        if (width <= 0 || height <= 0) return null;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        var memDc = CreateCompatibleDC(screenDc);
        var dib = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            var bmi = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
            };
            dib = CreateDIBSection(memDc, ref bmi, DibRgbColors, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;

            oldBitmap = SelectObject(memDc, dib);
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, left, top, SrcCopy)) return null;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            // Alt piksellerde alfa kanalı 0xFF'e sabitlenir ki WPF BitmapSource transparan delik göstermesin.
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            return new Capture(new Frame(pixels, width, height, stride), left, top, width, height);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static NativePoint GetCursorOrWindowPoint(Window? owner)
    {
        if (owner is { IsVisible: true } && double.IsFinite(owner.Left) && double.IsFinite(owner.Top))
        {
            var source = PresentationSource.FromVisual(owner);
            var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            var cx = (int)Math.Round((owner.Left + owner.ActualWidth / 2) * dpiX);
            var cy = (int)Math.Round((owner.Top + owner.ActualHeight / 2) * dpiY);
            return new NativePoint { X = cx, Y = cy };
        }
        return GetCursorPos(out var pt) ? pt : default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
}
