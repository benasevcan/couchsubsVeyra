// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows.Media;
using System.Windows.Media.Imaging;
using Frame = CouchSubs.Core.Frame;

namespace CouchSubs.App.Services;

/// <summary>
/// Ekran yakalama karesini dondurulmuş, alfa kanalından bağımsız (tam opak) WPF bitmap'ine çevirir.
/// Qt/DirectX pencerelerinden gelen karelerde alfa baytı 0 kalsa bile beyaz/saydam parlama oluşmaz.
/// </summary>
internal static class FrameBitmap
{
    public static BitmapSource Create(Frame frame)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Pixels, frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}
