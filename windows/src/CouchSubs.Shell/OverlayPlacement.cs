// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Shell;

/// <summary>Bir ekran alanı (WPF birimiyle).</summary>
public readonly record struct ScreenArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    /// <summary>Sonlu ve boş olmayan alan (ekran bilgisi okunamayınca sıfır ya da NaN gelebilir).</summary>
    public bool IsUsable => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height)
                            && Width > 0 && Height > 0;
}

/// <summary>
/// Bilgisayar ekranındaki altyazı penceresinin yeri ve boyu. Kayıtlı yer bozuksa (NaN, sıfır genişlik) ya da artık
/// bağlı olmayan bir ekrandaysa pencere ana ekranın altında ortada açılır; WPF'e hiçbir zaman geçersiz değer verilmez.
/// </summary>
public static class OverlayPlacement
{
    public const double SmallestWidth = 320;
    /// <summary>Kayıtlı yerin ekranda en az bu kadarı görünmüyorsa pencere varsayılan yere döner.</summary>
    public const double VisiblePart = 80;
    /// <summary>Ekran bilgisi hiç okunamazsa kullanılan alan.</summary>
    private static readonly ScreenArea Fallback = new(0, 0, 1280, 720);

    /// <summary>Yazı boyu TV'deki gibi 1080 satırlık ekrana göredir.</summary>
    public static double Scale(double primaryScreenHeight) =>
        double.IsFinite(primaryScreenHeight) && primaryScreenHeight > 0 ? Math.Clamp(primaryScreenHeight / 1080, 0.5, 4) : 1;

    /// <summary>Ayarlardaki yazı boyu; bozuk kayıtta varsayılan, uç değerlerde sınırda.</summary>
    public static double FontSize(int fontSize) => fontSize > 0 ? Math.Clamp(fontSize, 12, 200) : 46;

    /// <summary>Pencere en çok üç satıra (ve satırların kaymasına) yetecek kadar yüksektir.</summary>
    public static double Height(int fontSize, double scale) => Math.Round(FontSize(fontSize) * scale * 8.1 + 140 * scale);

    /// <summary>Kayıtlı yer (ekranlardan birinde yeterince görünüyorsa) ya da çalışma alanının altında ortada, %70 genişlikte.</summary>
    public static OverlayBounds Place(OverlayBounds? saved, ScreenArea virtualScreen, ScreenArea workArea, double height)
    {
        var work = workArea.IsUsable ? workArea : virtualScreen.IsUsable ? virtualScreen : Fallback;
        var screen = virtualScreen.IsUsable ? virtualScreen : work;
        if (!double.IsFinite(height) || height <= 0) height = Height(0, 1);
        if (saved is { } s && double.IsFinite(s.Left) && double.IsFinite(s.Top) && double.IsFinite(s.Width) && s.Width > 0)
        {
            var width = Math.Clamp(s.Width, SmallestWidth, Math.Max(SmallestWidth, screen.Width));
            if (Visible(s.Left, s.Top, width, height, screen)) return new OverlayBounds(s.Left, s.Top, width);
        }
        var placed = Math.Max(SmallestWidth, Math.Round(work.Width * 0.7));
        return new OverlayBounds(work.Left + (work.Width - placed) / 2, work.Bottom - work.Height * 0.04 - height, placed);
    }

    /// <summary>Altta veya üstte yaslı, yatay hizalı ve genişlik yüzdeli konum hesaplar.</summary>
    public static OverlayBounds AnchoredPlace(
        ScreenArea workArea,
        double height,
        SubtitleAnchor anchor,
        int edgePercent,
        int widthPercent,
        SubtitleHorizontalAlign horizontalAlign)
    {
        var work = workArea.IsUsable ? workArea : Fallback;
        if (!double.IsFinite(height) || height <= 0) height = Height(0, 1);
        var fraction = Math.Clamp(widthPercent, 30, 100) / 100.0;
        var minWidth = Math.Min(SmallestWidth, work.Width);
        var width = Math.Clamp(Math.Round(work.Width * fraction), minWidth, Math.Max(minWidth, work.Width));
        var sideMargin = Math.Round(work.Width * 0.02);
        var left = horizontalAlign switch
        {
            SubtitleHorizontalAlign.Left => work.Left + sideMargin,
            SubtitleHorizontalAlign.Right => work.Right - width - sideMargin,
            _ => work.Left + (work.Width - width) / 2,
        };
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        var edge = work.Height * (Math.Clamp(edgePercent, 0, 45) / 100.0);
        var top = anchor == SubtitleAnchor.Top
            ? work.Top + edge
            : work.Bottom - edge - height;
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        return new OverlayBounds(Math.Round(left), Math.Round(top), Math.Round(width));
    }

    /// <summary>Kaynak İngilizce altyazı kutusunu tamamen örtecek pencere konumu ve kutu en az boyutları.</summary>
    public readonly record struct CoverLayout(OverlayBounds Window, double MinBoxWidth, double MinBoxHeight);

    /// <summary>
    /// Oyundaki İngilizce altyazının yanlarından veya altından taşmasını önlemek için kaplama penceresini ve kutu
    /// minimum boyutlarını hesaplar; kullanıcı pencereyi ayarlıyorsa ya da zemin saydamsa temel konuma dokunmaz.
    /// </summary>
    public static CoverLayout CoverForSource(
        ScreenArea workArea,
        double screenHeight,
        OverlayBounds baseWindow,
        double windowHeight,
        TextBox? sourceBox,
        SubtitleHorizontalAlign horizontalAlign,
        bool isBand,
        bool hasBackground,
        bool adjusting,
        double sidePadding)
    {
        var work = workArea.IsUsable ? workArea : Fallback;
        if (!hasBackground || adjusting) return new CoverLayout(baseWindow, 0, 0);
        var fullH = double.IsFinite(screenHeight) && screenHeight > 0 ? screenHeight : work.Height;
        // Birden fazla ayrı yazı (ör. konuşma + görev satırı) aynı kutuda birleşse bile siyah zemin ekrana taşmasın.
        var coverHeight = sourceBox is { } s ? Math.Clamp(s.Bottom - s.Top + 0.016, 0, 0.10) * fullH : 0;
        if (isBand) return new CoverLayout(new OverlayBounds(work.Left, baseWindow.Top, work.Width), 0, Math.Round(coverHeight));
        if (sourceBox is not { } box) return new CoverLayout(baseWindow, 0, 0);

        var sourceY = ((box.Top + box.Bottom) / 2.0) * fullH;
        if (sourceY < baseWindow.Top - 40 || sourceY > baseWindow.Top + windowHeight + 90)
            return new CoverLayout(baseWindow, 0, 0);

        var leftFrac = Math.Clamp(box.Left - 0.02, 0, 1);
        var rightFrac = Math.Clamp(box.Right + 0.02, leftFrac, 1);
        var coverLeft = work.Left + leftFrac * work.Width;
        var coverRight = work.Left + rightFrac * work.Width;
        var coverWidth = Math.Max(0, coverRight - coverLeft);
        var pad = Math.Max(0, sidePadding);
        var newLeft = Math.Round(Math.Max(work.Left, Math.Min(baseWindow.Left, coverLeft - pad)));
        var newRight = Math.Round(Math.Min(work.Right, Math.Max(baseWindow.Left + baseWindow.Width, coverRight + pad)));
        var window = new OverlayBounds(newLeft, baseWindow.Top, Math.Max(SmallestWidth, newRight - newLeft));

        var innerLeft = window.Left + pad;
        var innerRight = window.Left + window.Width - pad;
        var innerWidth = Math.Max(0, innerRight - innerLeft);
        var middle = (box.Left + box.Right) / 2.0;
        double minBoxWidth;
        if (horizontalAlign == SubtitleHorizontalAlign.Center && middle is >= 0.25 and <= 0.75)
        {
            var panelMid = (innerLeft + innerRight) / 2.0;
            var reach = Math.Max(panelMid - coverLeft, coverRight - panelMid);
            minBoxWidth = Math.Min(innerWidth, Math.Max(coverWidth, 2 * reach));
        }
        else
        {
            minBoxWidth = Math.Min(innerWidth, coverWidth);
        }
        return new CoverLayout(window, Math.Round(minBoxWidth), Math.Round(coverHeight));
    }

    /// <summary>Kenardan genişletilen pencerenin yeni genişliği: en az <see cref="SmallestWidth"/>, en çok tüm ekranlar.</summary>
    public static double ClampWidth(double width, double virtualScreenWidth)
    {
        // Ekranların genişliği okunamadıysa üst sınır yok.
        var widest = double.IsFinite(virtualScreenWidth) ? Math.Max(SmallestWidth, virtualScreenWidth) : double.MaxValue;
        return double.IsFinite(width) ? Math.Clamp(width, SmallestWidth, widest) : SmallestWidth;
    }

    /// <summary>Kaydedilecek yer; değerlerden biri sonlu değilse (JSON'a yazılamaz) kaydedilmez.</summary>
    public static OverlayBounds? ToSave(double left, double top, double width) =>
        double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(width) && width > 0
            ? new OverlayBounds(Math.Round(left), Math.Round(top), Math.Round(width))
            : null;

    private static bool Visible(double left, double top, double width, double height, ScreenArea screen)
    {
        var across = Math.Min(left + width, screen.Right) - Math.Max(left, screen.Left);
        var down = Math.Min(top + height, screen.Bottom) - Math.Max(top, screen.Top);
        return across >= Math.Min(VisiblePart, width) && down >= Math.Min(VisiblePart, height);
    }
}
