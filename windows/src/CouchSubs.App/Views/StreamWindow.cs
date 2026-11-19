// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CouchSubs.Core;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>
/// "Yayın penceresi" (Mac: StreamWindow.swift): şeffaflık tanımayan yayın programları (TikTok LIVE Studio, Streamlabs,
/// OBS dışındakiler) için düz renkli zeminde altyazı çizen sıradan bir pencere. Programda "Pencere yakalama" ile eklenir,
/// zemin chroma key ile silinir. Başlığı sabittir ki yakalama listesinde bulunsun. Saydam altyazı penceresinden
/// (<see cref="SubtitleOverlayWindow"/>) ayrıdır; altyazı yalnız TV'de gösterilse de satırlar gelir.
/// </summary>
public sealed class StreamWindow : Window
{
    private static StreamWindow? open;

    private readonly AppController controller;
    private readonly SubtitleStack stack = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new();
    private readonly StackPanel linesPanel = new() { VerticalAlignment = VerticalAlignment.Bottom };
    /// <summary>Ek alanların (görev, telefon mesajı, bildirim) çevirileri; saydam penceredeki gibi üstte, alanın yanına yaslı.</summary>
    private readonly StackPanel regionsPanel = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly Dictionary<string, (string Text, CouchSubs.Core.TextBox? Box)> regions = [];
    private readonly Border linesHost;
    private readonly Dictionary<int, Border> boxes = [];
    private double screenUntil;

    private double Now => clock.Elapsed.TotalSeconds;

    /// <summary>Yazı boyu TV'deki gibi 1080 satırlık ekrana göredir; pencerenin içerik yüksekliğine ölçeklenir.</summary>
    private double Scale => Math.Max(0.2, (linesHost.ActualHeight > 0 ? linesHost.ActualHeight : 720) / 1080);

    /// <summary>Pencereyi açar; zaten açıksa öne getirir.</summary>
    public static void Open(AppController controller)
    {
        if (open is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            return;
        }
        open = new StreamWindow(controller);
        open.Show();
    }

    public static bool IsOpen => open is not null;

    /// <summary>Yayın programının pencere listesinde görünen ad; altyazıya göre değişmez (Türkçede "CouchSubs Yayın").</summary>
    public static string WindowTitle => Localizer.Current["CouchSubs Stream"];

    private StreamWindow(AppController controller)
    {
        this.controller = controller;
        Title = WindowTitle;
        // 1280x720 içerik: 16:9, yayın programında kaynak oranı bozulmaz.
        SizeToContent = SizeToContent.Manual;
        Width = 1296;
        Height = 759;
        MinWidth = 400;
        MinHeight = 260;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        // Video kaynağında renkli alt piksel yumuşatma kenarda renk saçağı bırakır; gri tonlu yumuşatma keskin kalır.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        var layers = new Grid();
        layers.Children.Add(regionsPanel);
        layers.Children.Add(linesPanel);
        linesHost = new Border { Child = layers };
        Content = linesHost;
        ApplyBackground();
        ApplyLayout();
        linesHost.SizeChanged += (_, _) =>
        {
            ApplyLayout();
            boxes.Clear();
            Refresh();
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Refresh();
        };
        controller.StreamEvent += OnEvent;
        controller.StreamHidden += OnHidden;
        controller.PropertyChanged += OnChanged;
        Closed += (_, _) =>
        {
            controller.StreamEvent -= OnEvent;
            controller.StreamHidden -= OnHidden;
            controller.PropertyChanged -= OnChanged;
            timer.Stop();
            open = null;
        };
    }

    private void ApplyLayout()
    {
        var style = controller.Settings.Style;
        var hostH = linesHost.ActualHeight > 0 ? linesHost.ActualHeight : 720;
        var margin = hostH * (Math.Clamp(style.BottomPercent, 0, 45) / 100.0);
        var top = style.PositionAnchor == SubtitleAnchor.Top;
        linesPanel.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        regionsPanel.VerticalAlignment = top ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        linesHost.Padding = top
            ? new Thickness(24 * Scale, margin, 24 * Scale, 16 * Scale)
            : new Thickness(24 * Scale, 16 * Scale, 24 * Scale, margin);
        regionsPanel.Margin = new Thickness(0, 16 * Scale, 0, 16 * Scale);
    }

    private void ApplyBackground()
    {
        var (red, green, blue) = controller.StreamBackground.Rgb();
        linesHost.Background = new SolidColorBrush(Color.FromRgb(red, green, blue));
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppController.StreamBackground)) ApplyBackground();
        if (e.PropertyName != nameof(AppController.Settings)) return;
        // Yazı boyu, rengi, yönü ya da konumu değişmiş olabilir; kutular yeniden kurulur.
        SyncStack();
        ApplyLayout();
        boxes.Clear();
        Refresh();
    }

    private void SyncStack()
    {
        var style = controller.Settings.Style;
        stack.StackPrevious = style.StackPrevious;
        stack.StackDirection = style.StackDirection;
        stack.PreviousHoldSeconds = style.PreviousHoldSeconds;
    }

    /// <summary>Saydam penceredekiyle aynı kurallar (bkz. <see cref="SubtitleOverlayWindow"/>).</summary>
    private void OnEvent(SubtitleEvent e)
    {
        SyncStack();
        switch (e.Kind)
        {
            case SubtitleEventKind.Subtitle:
                screenUntil = 0;
                stack.Show(e.Text, e.DurationMs, e.Paragraphs, Now, e.Replaces, e.Box);
                break;
            case SubtitleEventKind.Screen:
                stack.Show(e.Text, e.DurationMs, paragraphs: true, Now);
                screenUntil = Now + e.DurationMs / 1000.0;
                break;
            case SubtitleEventKind.ScreenClear:
                if (screenUntil > Now) stack.Hide();
                screenUntil = 0;
                break;
            // Ek alan olayı altyazıya dokunmaz: önceden "temizle" sayılıp yayındaki altyazıyı siliyordu ve çeviri hiç
            // görünmüyordu.
            case SubtitleEventKind.Region:
                if (e.Id is null) break;
                regions.Remove(e.Id);
                if (e.Text.Length > 0) regions[e.Id] = (e.Text, e.Box);
                break;
            case SubtitleEventKind.RegionClear:
                regions.Clear();
                break;
            default:
                if (screenUntil <= Now) stack.Clear(Now);
                break;
        }
        Refresh();
    }

    private void OnHidden()
    {
        stack.Hide();
        regions.Clear();
        Refresh();
    }

    private void Refresh()
    {
        var now = Now;
        var lines = stack.Lines(now).ToList();
        regionsPanel.Children.Clear();
        foreach (var (text, area) in regions.Values.OrderBy(region => region.Box?.Top ?? 0)) regionsPanel.Children.Add(MakeRegionBox(text, area));
        linesPanel.Children.Clear();
        foreach (var id in boxes.Keys.Except(lines.Select(l => l.Id)).ToList()) boxes.Remove(id);
        foreach (var line in lines)
        {
            if (!boxes.TryGetValue(line.Id, out var box))
            {
                box = MakeBox(line);
                boxes[line.Id] = box;
            }
            if (line.Fading && box.Tag is not "fading")
            {
                box.Tag = "fading";
                box.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(SubtitleStack.PreviousFade)));
            }
            linesPanel.Children.Add(box);
        }
        timer.Stop();
        if (stack.NextChange(now) is { } next)
        {
            timer.Interval = TimeSpan.FromSeconds(Math.Max(0.01, next - now));
            timer.Start();
        }
    }

    /// <summary>
    /// TV'deki altyazı kutusu; düz zeminde yarı saydamlık zeminle karışıp chroma key'i bozacağından kutu tam koyudur
    /// (ya da hiç yoktur) ve üst satırlar yazı renginden soldurulur.
    /// </summary>
    private Border MakeBox(SubtitleStack.Line line)
    {
        var style = controller.Settings.Style;
        var scale = Scale;
        var current = line.Role == SubtitleStack.Role.Current;
        var (fontFactor, textOpacity, _) = SubtitleStyleVisuals.ResolveLineVisual(line.Role, style);
        var baseSize = Math.Max(1, OverlayPlacement.FontSize(style.FontSize) * scale * fontFactor);
        var padding = SubtitleStyleVisuals.ResolveBoxPadding(style.BoxPadding, current, scale);
        var (borderBrush, borderThickness) = SubtitleStyleVisuals.ResolveBorder(style.BorderStyle, scale);
        var hostW = linesHost.ActualWidth > 0 ? linesHost.ActualWidth : 1280;
        var hostH = linesHost.ActualHeight > 0 ? linesHost.ActualHeight : 720;
        var wrapWidth = Math.Max(160, hostW * (Math.Clamp(style.BoxWidthPercent, 30, 100) / 100.0) - 48 * scale);
        var wrapped = SubtitleStyleVisuals.WrapLine(line, style, wrapWidth, padding, borderThickness, baseSize);
        var size = Math.Max(1, baseSize * wrapped.Scale);
        var boxed = StreamBackgrounds.BoxOpacity(style.Background) > 0;
        var band = style.BackgroundShape == SubtitleBackgroundShape.Band;
        var text = new TextBlock
        {
            Text = wrapped.Text,
            FontFamily = SubtitleStyleVisuals.ResolveFontFamily(style.FontFamily),
            FontSize = size,
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.SemiBold,
            FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = SubtitleStyleVisuals.ResolveTextBrush(style, boxed ? textOpacity : 1.0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = band && !line.Paragraphs ? Math.Max(80, wrapWidth - padding.Left - padding.Right) : double.PositiveInfinity,
            TextAlignment = line.Paragraphs ? TextAlignment.Left : SubtitleStyleVisuals.ResolveTextAlignment(style.HorizontalAlign),
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = size * SubtitleStyleVisuals.ResolveLineHeightFactor(style.LineSpacing),
            Effect = boxed ? null : SubtitleStyleVisuals.ResolveShadowEffect(style.ShadowStrength, scale),
        };
        var minW = boxed && !band && current && line.Box is { } s
            ? Math.Round(Math.Min(Math.Max(0, hostW - 48 * scale), Math.Clamp(s.Right - s.Left + 0.04, 0, 1) * hostW))
            : 0;
        var minH = boxed && current && line.Box is { } sh
            ? Math.Round(Math.Clamp(sh.Bottom - sh.Top + 0.016, 0, 0.35) * hostH)
            : 0;
        return new Border
        {
            Background = boxed
                ? SubtitleStyleVisuals.FrozenBrush(SubtitleStyleVisuals.ResolveBackgroundColor(style, 255))
                : Brushes.Transparent,
            BorderBrush = borderBrush,
            BorderThickness = borderThickness,
            CornerRadius = new CornerRadius(SubtitleStyleVisuals.ResolveCornerRadius(style, band, scale)),
            Padding = padding,
            Margin = new Thickness(band ? -24 * scale : 0, 7 * scale, band ? -24 * scale : 0, 7 * scale),
            MinWidth = minW,
            MinHeight = minH,
            HorizontalAlignment = band ? HorizontalAlignment.Stretch : SubtitleStyleVisuals.ResolveHorizontalAlignment(style.HorizontalAlign),
            Child = text,
        };
    }

    /// <summary>Ek alanın çevirisi: altyazıdan küçük, tam koyu kutu (düz zeminde chroma key bozulmasın); alanın yanına yaslı.</summary>
    private Border MakeRegionBox(string text, CouchSubs.Core.TextBox? area)
    {
        var style = controller.Settings.Style;
        var scale = Scale;
        var size = Math.Max(12, OverlayPlacement.FontSize(style.FontSize) * scale * 0.55);
        var middle = area is { } box ? (box.Left + box.Right) / 2 : 0.5;
        var (borderBrush, borderThickness) = SubtitleStyleVisuals.ResolveBorder(style.BorderStyle, scale * 0.75);
        return new Border
        {
            Background = SubtitleStyleVisuals.FrozenBrush(SubtitleStyleVisuals.ResolveBackgroundColor(style, 255)),
            BorderBrush = borderBrush,
            BorderThickness = borderThickness,
            CornerRadius = new CornerRadius(SubtitleStyleVisuals.ResolveCornerRadius(style, fullWidthBand: false, scale * 0.8)),
            Padding = new Thickness(14 * scale, 6 * scale, 14 * scale, 6 * scale),
            Margin = new Thickness(0, 0, 0, 10 * scale),
            HorizontalAlignment = middle < 0.4 ? HorizontalAlignment.Left : middle > 0.6 ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = SubtitleStyleVisuals.ApplyTextCase(text, style.TextCase),
                FontFamily = SubtitleStyleVisuals.ResolveFontFamily(style.FontFamily),
                FontSize = size,
                FontWeight = FontWeights.SemiBold,
                FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal,
                Foreground = SubtitleStyleVisuals.ResolveTextBrush(style),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size * 1.25,
                Effect = SubtitleStyleVisuals.ResolveShadowEffect(style.ShadowStrength, scale),
            },
        };
    }
}
