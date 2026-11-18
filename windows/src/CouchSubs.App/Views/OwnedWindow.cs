// SPDX-License-Identifier: AGPL-3.0-only

using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CouchSubs.App.Views;

/// <summary>
/// Ayarlar, Geçmiş ve Hakkında: ana pencerenin ortasında açılan ama ona bağlanmayan tek pencere (#223, #268). WPF'te
/// <see cref="Window.Owner"/> verilen pencere hep sahibinin üstünde çizildiğinden Owner atanmaz; ana pencereye tıklayınca
/// o öne gelir. Aynı pencere zaten açıksa yenisi açılmaz, var olan öne getirilir. Sahibi kapanınca bu pencere de kapanır.
/// Kipli pencereler (oyun paketi içe aktarma, PSN girişi) bu yolu kullanmaz, Owner'lı kalır.
/// </summary>
internal static partial class OwnedWindow
{
    /// <summary>Açık olan ya da yeni açılan pencereyi döndürür.</summary>
    public static T Show<T>(Window? owner, Func<T> create) where T : Window
    {
        var open = Application.Current?.Windows.OfType<T>().FirstOrDefault(window => window.IsVisible);
        if (open is not null)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return open;
        }
        var window = create();
        if (owner is null || !owner.IsLoaded)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            // İlk tahmin gösterilmeden önce; SizeToContent'li pencerede gerçek boy Loaded'da belli olur, orada düzeltilir.
            CenterOn(window, owner, Size(window.Width, window.MinWidth), Size(window.Height, window.MinHeight));
            window.Loaded += (_, _) => CenterOn(window, owner, window.ActualWidth, window.ActualHeight);
            EventHandler closeWithOwner = (_, _) => window.Close();
            owner.Closed += closeWithOwner;
            window.Closed += (_, _) => owner.Closed -= closeWithOwner;
        }
        window.Show();
        return window;
    }

    private static double Size(double value, double fallback) =>
        double.IsNaN(value) || value <= 0 ? Math.Max(fallback, 0) : value;

    /// <summary>Pencereyi sahibinin ortasına koyar; sahibin bulunduğu ekranın çalışma alanından taşırmaz.</summary>
    private static void CenterOn(Window window, Window owner, double width, double height)
    {
        // Simge durumundaki sahip -32000'de durur; o zaman eski (normal) konumu esas alınır.
        var bounds = owner.WindowState == WindowState.Normal
            ? new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight)
            : owner.RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.Left)) return;

        var left = bounds.Left + (bounds.Width - width) / 2;
        var top = bounds.Top + (bounds.Height - height) / 2;

        var work = OwnerWorkArea(owner) ?? SystemParameters.WorkArea;
        left = Math.Max(work.Left, Math.Min(left, work.Right - width));
        top = Math.Max(work.Top, Math.Min(top, work.Bottom - height));

        window.Left = left;
        window.Top = top;
    }

    /// <summary>Bir ekranın fiziksel piksel sınırları ve DPI ölçeği.</summary>
    internal readonly record struct MonitorBounds(int Left, int Top, int Width, int Height, double ScaleX, double ScaleY);

    /// <summary>Pencere tutamağının hâlâ geçerli bir Win32 penceresi olup olmadığını denetler.</summary>
    internal static bool IsLiveWindow(IntPtr window) => window != IntPtr.Zero && IsWindow(window);

    /// <summary>Verilen pencerenin bulunduğu ekranın fiziksel piksel sınırlarını ve etkin DPI ölçeğini verir.</summary>
    internal static MonitorBounds? MonitorFor(IntPtr window)
    {
        if (MonitorInfoFor(window) is not var (monitor, info)) return null;
        var width = info.Monitor.Right - info.Monitor.Left;
        var height = info.Monitor.Bottom - info.Monitor.Top;
        if (width <= 0 || height <= 0) return null;
        var (scaleX, scaleY) = MonitorScale(monitor);
        return new MonitorBounds(info.Monitor.Left, info.Monitor.Top, width, height, scaleX, scaleY);
    }

    /// <summary>Pencereyi yeniden boyutlandırıp verilen ekran koordinatlarına yerleştirir (odağı ve z-sırasını bozmadan).</summary>
    internal static void PlaceWindow(IntPtr window, MonitorBounds bounds) =>
        SetWindowPos(window, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpNoZOrder | SwpNoActivate);

    /// <summary>Sahibin bulunduğu ekranın çalışma alanı, WPF birimlerinde (DPI'a göre çevrilmiş).</summary>
    private static Rect? OwnerWorkArea(Window owner)
    {
        try
        {
            var handle = new WindowInteropHelper(owner).Handle;
            if (MonitorInfoFor(handle) is not var (_, info)) return null;
            var fromDevice = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformFromDevice;
            if (fromDevice is null) return null;
            var topLeft = fromDevice.Value.Transform(new Point(info.Work.Left, info.Work.Top));
            var bottomRight = fromDevice.Value.Transform(new Point(info.Work.Right, info.Work.Bottom));
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            return null;
        }
    }

    private static (IntPtr Monitor, MonitorInfo Info)? MonitorInfoFor(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        try
        {
            var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return null;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            return GetMonitorInfo(monitor, ref info) ? (monitor, info) : null;
        }
        catch
        {
            return null;
        }
    }

    private static (double X, double Y) MonitorScale(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0 && dpiX > 0 && dpiY > 0)
                return (dpiX / 96.0, dpiY / 96.0);
        }
        catch { }
        return (1.0, 1.0);
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
