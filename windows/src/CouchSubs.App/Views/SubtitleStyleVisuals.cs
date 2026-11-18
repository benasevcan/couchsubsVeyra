// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CouchSubs.Core;

namespace CouchSubs.App.Views;

/// <summary>
/// Masaüstü altyazı katmanı (<see cref="SubtitleOverlayWindow"/>) ile yayın penceresinin
/// (<see cref="StreamWindow"/>) ortak kullandığı yazı tipi, renk, gölge, zemin, hizalama ve kenarlık çözücüleri.
/// </summary>
internal static class SubtitleStyleVisuals
{
    public static FontFamily ResolveFontFamily(string? name) =>
        new(string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name);

    public static Color ResolveTextColor(SubtitleStyle style) => style.TextColor switch
    {
        SubtitleTextColor.Yellow => Color.FromRgb(0xFF, 0xE1, 0x35),
        SubtitleTextColor.Cyan => Color.FromRgb(0x22, 0xD3, 0xEE),
        SubtitleTextColor.Green => Color.FromRgb(0x4A, 0xDE, 0x80),
        SubtitleTextColor.Orange => Color.FromRgb(0xFB, 0x92, 0x3C),
        SubtitleTextColor.Pink => Color.FromRgb(0xF4, 0x72, 0xB6),
        SubtitleTextColor.LightBlue => Color.FromRgb(0x93, 0xC5, 0xFD),
        SubtitleTextColor.Custom => ParseHexColor(style.CustomTextColor, Colors.White),
        _ => Colors.White,
    };

    public static SolidColorBrush ResolveTextBrush(SubtitleStyle style, double opacity = 1.0)
    {
        var color = ResolveTextColor(style);
        if (opacity < 1.0) color.A = (byte)Math.Round(Math.Clamp(opacity, 0.0, 1.0) * 255);
        return new SolidColorBrush(color);
    }

    public static Color ResolveBackgroundColor(SubtitleStyle style, byte alpha)
    {
        if (style.BackgroundColor == SubtitleBackgroundColor.Custom)
        {
            var custom = ParseHexColor(style.CustomBackgroundColor, Colors.Black);
            custom.A = alpha;
            return custom;
        }
        return ResolveBackgroundColor(style.BackgroundColor, alpha);
    }

    public static Color ResolveBackgroundColor(SubtitleBackgroundColor color, byte alpha) => color switch
    {
        SubtitleBackgroundColor.DarkGray => Color.FromArgb(alpha, 0x1E, 0x20, 0x24),
        SubtitleBackgroundColor.Navy => Color.FromArgb(alpha, 0x0F, 0x17, 0x2A),
        SubtitleBackgroundColor.DarkPurple => Color.FromArgb(alpha, 0x1E, 0x10, 0x2E),
        SubtitleBackgroundColor.Forest => Color.FromArgb(alpha, 0x0D, 0x24, 0x18),
        SubtitleBackgroundColor.Wine => Color.FromArgb(alpha, 0x2B, 0x0E, 0x16),
        _ => Color.FromArgb(alpha, 0x00, 0x00, 0x00),
    };

    public static double ResolveLineHeightFactor(SubtitleLineSpacing spacing) => spacing switch
    {
        SubtitleLineSpacing.Compact => 1.15,
        SubtitleLineSpacing.Relaxed => 1.50,
        SubtitleLineSpacing.Loose => 1.70,
        _ => 1.30,
    };

    public static Thickness ResolveBoxPadding(SubtitleBoxPadding padding, bool current, double scale)
    {
        var mult = padding switch
        {
            SubtitleBoxPadding.Compact => 0.7,
            SubtitleBoxPadding.Spacious => 1.4,
            _ => 1.0,
        };
        var h = (current ? 26 : 20) * mult * scale;
        var v = (current ? 10 : 6) * mult * scale;
        return new Thickness(h, v, h, v);
    }

    public static (Brush? Brush, Thickness Thickness) ResolveBorder(SubtitleBorderStyle borderStyle, double scale) => borderStyle switch
    {
        SubtitleBorderStyle.Subtle => (new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), new Thickness(Math.Max(1, 1.2 * scale))),
        SubtitleBorderStyle.Accent => (new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)), new Thickness(Math.Max(1, 1.8 * scale))),
        SubtitleBorderStyle.White => (new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), new Thickness(Math.Max(1, 1.8 * scale))),
        _ => (null, new Thickness(0)),
    };

    public static HorizontalAlignment ResolveHorizontalAlignment(SubtitleHorizontalAlign align) => align switch
    {
        SubtitleHorizontalAlign.Left => HorizontalAlignment.Left,
        SubtitleHorizontalAlign.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center,
    };

    public static TextAlignment ResolveTextAlignment(SubtitleHorizontalAlign align) => align switch
    {
        SubtitleHorizontalAlign.Left => TextAlignment.Left,
        SubtitleHorizontalAlign.Right => TextAlignment.Right,
        _ => TextAlignment.Center,
    };

    public static string ApplyTextCase(string text, SubtitleTextCase textCase) =>
        textCase == SubtitleTextCase.Upper ? text.ToUpper(CultureInfo.CurrentCulture) : text;

    public static DropShadowEffect? ResolveShadowEffect(SubtitleShadowStrength strength, double scale) => strength switch
    {
        SubtitleShadowStrength.None => null,
        SubtitleShadowStrength.Strong => new DropShadowEffect { Color = Colors.Black, ShadowDepth = 3 * scale, BlurRadius = 8 * scale, Opacity = 1, Direction = 270 },
        SubtitleShadowStrength.Outline => new DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3.5 * scale, Opacity = 1, Direction = 0 },
        _ => new DropShadowEffect { Color = Colors.Black, ShadowDepth = 2 * scale, BlurRadius = 4 * scale, Opacity = 1, Direction = 270 },
    };

    public static double ResolveCornerRadius(SubtitleStyle style, bool fullWidthBand, double scale) =>
        fullWidthBand ? 0 : Math.Clamp(style.BackgroundRadius, 0, 48) * scale;

    public static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var normalized = hex.Trim();
        if (!normalized.StartsWith('#')) normalized = "#" + normalized;
        try
        {
            if (ColorConverter.ConvertFromString(normalized) is Color parsed) return parsed;
        }
        catch (FormatException) { }
        return fallback;
    }
}
