// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CouchSubs.App.Services;
using CouchSubs.Core;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>
/// Bilgisayar ekranındaki altyazı penceresi (Mac: SubtitleOverlay.swift): kenarsız, her pencerenin üstünde, saydam
/// zeminli; görev çubuğunda görünmez, odağı almaz. "Tıklamalar geçsin" açıkken fare alttaki pencereye (oyun, PS Remote
/// Play) gider. Satırlar TV'ye gidenlerin aynısıdır ve TV uygulamasıyla aynı yığınla (<see cref="SubtitleStack"/>)
/// gösterilir. Yeri ve genişliği ayarlara kaydedilir; altyazı yokken gizlenir.
/// </summary>
public sealed partial class SubtitleOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExToolWindow = 0x80;
    private const long WsExNoActivate = 0x08000000;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    private readonly AppController controller;
    private readonly SubtitleStack stack = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new();
    private readonly StackPanel linesPanel = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StackPanel regionsPanel = new() { VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    /// <summary>Görünen satırların kutuları; solan satırın animasyonu yeniden çizimde baştan başlamasın.</summary>
    private readonly Dictionary<int, Border> boxes = [];
    private readonly Grid adjustLayer = new() { Visibility = Visibility.Collapsed };
    /// <summary>Başka bir tam ekran ya da üstte duran pencere öne gelince altyazıyı yeniden en üste koyar.</summary>
    private readonly TopmostKeeper topmost;
    private IntPtr handle;
    /// <summary>Pencere bir kez açılamadı ya da çizilemedi: bu oturumda bir daha denenmez, çeviri pencere olmadan sürer.</summary>
    private bool broken;

    private double Now => clock.Elapsed.TotalSeconds;

    /// <summary>Yazı boyu TV'deki gibi 1080 satırlık ekrana göredir.</summary>
    private static double Scale => OverlayPlacement.Scale(SystemParameters.PrimaryScreenHeight);

    public static SubtitleOverlayWindow Attach(AppController controller) => new(controller);

    private SubtitleOverlayWindow(AppController controller)
    {
        this.controller = controller;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Title = "CouchSubs";
        topmost = new TopmostKeeper(this);

        var root = new Grid();
        root.Children.Add(adjustLayer);
        root.Children.Add(new Border { Padding = new Thickness(24 * Scale, 8 * Scale, 24 * Scale, 8 * Scale), Child = linesPanel });
        root.Children.Add(new Border { Padding = new Thickness(24 * Scale, 8 * Scale, 24 * Scale, 8 * Scale), Child = regionsPanel });
        BuildAdjustLayer();
        Content = root;
        ApplyAnchorAlignment();
        PlaceFromSettings();

        timer.Tick += (_, _) => Guard("zamanlayıcı", () =>
        {
            timer.Stop();
            Refresh();
        });
        SourceInitialized += (_, _) => Guard("pencere tutamağı", () =>
        {
            handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var style = (long)GetWindowLongPtr(handle, GwlExStyle);
            // Odağı almaz ve Alt+Tab listesinde görünmez.
            SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(style | WsExNoActivate | WsExToolWindow));
            ApplyClickThrough();
        });
        // Tıklamalar geçmiyorsa pencere her yerinden sürüklenir.
        MouseLeftButtonDown += (_, e) => Guard("sürükleme", () =>
        {
            if (e.OriginalSource is Thumb or ButtonBase || e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { return; /* düğme bu arada bırakıldı */ }
            SaveBounds();
        });
        controller.OverlayEvent += OnEvent;
        controller.OverlayHidden += OnHidden;
        controller.PropertyChanged += OnChanged;
    }

    public void Detach()
    {
        controller.OverlayEvent -= OnEvent;
        controller.OverlayHidden -= OnHidden;
        controller.PropertyChanged -= OnChanged;
        timer.Stop();
        topmost.Dispose();
        try { Close(); }
        catch (InvalidOperationException error) { StartupGuard.Log("Altyazı penceresi kapatılamadı", error); }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AppController.Settings) or nameof(AppController.OverlayAdjusting))) return;
        Guard("ayar değişikliği", () =>
        {
            // Yazı boyu, rengi, yönü ya da konumu değişmiş olabilir; kutular yeniden kurulur.
            if (e.PropertyName == nameof(AppController.Settings))
            {
                SyncStack();
                boxes.Clear();
            }
            ApplyClickThrough();
            ApplyAnchorAlignment();
            PlaceFromSettings();
            Refresh();
        });
    }

    private void SyncStack()
    {
        var style = controller.Settings.Style;
        stack.StackPrevious = style.StackPrevious;
        stack.StackDirection = style.StackDirection;
        stack.PreviousHoldSeconds = style.PreviousHoldSeconds;
    }

    private void OnEvent(SubtitleEvent e) => Guard("altyazı", () =>
    {
        SyncStack();
        switch (e.Kind)
        {
            case SubtitleEventKind.Subtitle:
                screenUntil = 0;
                stack.Show(e.Text, e.DurationMs, e.Paragraphs, Now, e.Replaces, e.Box);
                break;
            // "Ekranı çevir": çeviriler satır satır gösterilir. Hemen ardından gelen "temizle" ("Ekran çevriliyor…"
            // yazısı için) onları kaldırmaz; yeniden basmak (ScreenClear) ya da yeni altyazı kaldırır.
            case SubtitleEventKind.Screen:
                stack.Show(e.Text, e.DurationMs, paragraphs: true, Now);
                screenUntil = Now + e.DurationMs / 1000.0;
                break;
            case SubtitleEventKind.ScreenClear:
                if (screenUntil > Now) stack.Hide();
                screenUntil = 0;
                break;
            // Ek alanların çevirisi altyazının üstünde, alanın bulunduğu yana yaslı küçük kutularda durur.
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
    });

    /// <summary>"Ekranı çevir" çevirilerinin pencerede kalacağı an (<see cref="Now"/> saatiyle); yoksa 0.</summary>
    private double screenUntil;

    /// <summary>Ek alanların penceredeki çevirileri (kimlik → metin ve oyundaki yeri).</summary>
    private readonly Dictionary<string, (string Text, CouchSubs.Core.TextBox? Box)> regions = [];

    private void OnHidden() => Guard("gizleme", () =>
    {
        stack.Hide();
        regions.Clear();
        Refresh();
    });

    /// <summary>
    /// Penceredeki her iş bundan geçer. Pencere açılamaz ya da çizilemezse hata hata.log'a yazılır, kullanıcıya bir kez
    /// söylenir ve pencere kapatılır; uygulama kapanmaz, çeviri (TV'ye, OBS'ye, dublaja) sürer.
    /// </summary>
    private void Guard(string where, Action action)
    {
        if (broken) return;
        try
        {
            action();
        }
        catch (Exception error)
        {
            broken = true;
            StartupGuard.Log($"Altyazı penceresi ({where})", error);
            try
            {
                timer.Stop();
                if (IsVisible) Hide();
            }
            catch (Exception hideError)
            {
                StartupGuard.Log("Altyazı penceresi gizlenemedi", hideError);
            }
            StartupGuard.Show(Application.Current, StartupGuard.OverlayFailedMessage(error));
        }
    }

    /// <summary>Görünen satırları çizer, pencereyi gösterir ya da gizler, bir sonraki değişime zamanlayıcı kurar.</summary>
    private void Refresh()
    {
        var now = Now;
        var lines = stack.Lines(now).ToList();
        var activeBox = lines.FirstOrDefault(l => l.Role == SubtitleStack.Role.Current)?.Box;
        var cover = PlaceFromSettings(activeBox);
        var adjusting = controller.OverlayAdjusting;
        if (lines.Count == 0 && adjusting)
            lines.Add(new SubtitleStack.Line(0, Localizer.Current["Subtitles appear here."], SubtitleStack.Role.Current, false, false));
        adjustLayer.Visibility = adjusting ? Visibility.Visible : Visibility.Collapsed;

        linesPanel.Children.Clear();
        foreach (var id in boxes.Keys.Except(lines.Select(l => l.Id)).ToList()) boxes.Remove(id);
        foreach (var line in lines)
        {
            if (!boxes.TryGetValue(line.Id, out var box))
            {
                box = MakeBox(line, cover);
                boxes[line.Id] = box;
            }
            if (line.Role == SubtitleStack.Role.Current)
            {
                box.MinWidth = cover.MinBoxWidth;
                box.MinHeight = cover.MinBoxHeight;
            }
            if (line.Fading && box.Tag is not "fading")
            {
                box.Tag = "fading";
                box.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(SubtitleStack.PreviousFade)));
            }
            linesPanel.Children.Add(box);
        }

        regionsPanel.Children.Clear();
        foreach (var (text, area) in regions.Values.OrderBy(region => region.Box?.Top ?? 0)) regionsPanel.Children.Add(MakeRegionBox(text, area));

        var visible = controller.ShowsOnComputer && (lines.Count > 0 || regions.Count > 0);
        if (visible && !IsVisible) Show();
        else if (!visible && IsVisible) Hide();
        // Arada öne gelen tam ekran/üstte duran pencere altyazıyı örtmüş olabilir; odak alınmadan yeniden üste konur.
        if (visible) topmost.RaiseThrottled();

        timer.Stop();
        if (stack.NextChange(now) is { } next)
        {
            timer.Interval = TimeSpan.FromSeconds(Math.Max(0.01, next - now));
            timer.Start();
        }
    }

    /// <summary>TV'deki altyazı kutusunun aynısı: siyah yarı saydam zemin, beyaz ya da sarı yarı kalın yazı, üst satırlar soluk.</summary>
    private Border MakeBox(SubtitleStack.Line line, OverlayPlacement.CoverLayout cover)
    {
        var style = controller.Settings.Style;
        var scale = Scale;
        var current = line.Role == SubtitleStack.Role.Current;
        var (fontFactor, textOpacity, boxAlphaFactor) = SubtitleStyleVisuals.ResolveLineVisual(line.Role, style);
        var baseSize = Math.Max(1, OverlayPlacement.FontSize(style.FontSize) * scale * fontFactor);
        var padding = SubtitleStyleVisuals.ResolveBoxPadding(style.BoxPadding, current, scale);
        var (borderBrush, borderThickness) = SubtitleStyleVisuals.ResolveBorder(style.BorderStyle, scale);
        var workWidth = SystemParameters.WorkArea.Width;
        var wrapWidth = Math.Max(160, (controller.Settings.Overlay is { } saved ? saved.Width : workWidth * (Math.Clamp(style.BoxWidthPercent, 30, 100) / 100.0)) - 48 * scale);
        var wrapped = SubtitleStyleVisuals.WrapLine(line, style, wrapWidth, padding, borderThickness, baseSize);
        var size = Math.Max(1, baseSize * wrapped.Scale);
        var alpha = Math.Clamp(style.Background, 0, 100) / 100.0 * boxAlphaFactor;
        // Bant: kutu pencerenin tüm genişliğini kaplar, oyundaki İngilizce yazıyı yan yana da gizler.
        var band = style.BackgroundShape == SubtitleBackgroundShape.Band;
        var text = new TextBlock
        {
            Text = wrapped.Text,
            FontFamily = SubtitleStyleVisuals.ResolveFontFamily(style.FontFamily),
            FontSize = size,
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.SemiBold,
            FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = SubtitleStyleVisuals.ResolveTextBrush(style, textOpacity),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = band && !line.Paragraphs ? Math.Max(80, wrapWidth - padding.Left - padding.Right) : double.PositiveInfinity,
            TextAlignment = line.Paragraphs ? TextAlignment.Left : SubtitleStyleVisuals.ResolveTextAlignment(style.HorizontalAlign),
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = size * SubtitleStyleVisuals.ResolveLineHeightFactor(style.LineSpacing),
            Effect = SubtitleStyleVisuals.ResolveShadowEffect(style.ShadowStrength, scale),
        };
        return new Border
        {
            Background = new SolidColorBrush(SubtitleStyleVisuals.ResolveBackgroundColor(style, (byte)Math.Round(alpha * 255))),
            BorderBrush = borderBrush,
            BorderThickness = borderThickness,
            CornerRadius = new CornerRadius(SubtitleStyleVisuals.ResolveCornerRadius(style, band, scale)),
            Padding = padding,
            Margin = new Thickness(band ? -24 * scale : 0, 7 * scale, band ? -24 * scale : 0, 7 * scale),
            MinWidth = current ? cover.MinBoxWidth : 0,
            MinHeight = current ? cover.MinBoxHeight : 0,
            HorizontalAlignment = band ? HorizontalAlignment.Stretch : SubtitleStyleVisuals.ResolveHorizontalAlignment(style.HorizontalAlign),
            Opacity = 1,
            Child = text,
        };
    }

    /// <summary>Ek alanın çevirisi: altyazıdan küçük, koyu zeminli kutu; oyundaki yazının bulunduğu yana yaslı.</summary>
    private Border MakeRegionBox(string text, CouchSubs.Core.TextBox? area)
    {
        var style = controller.Settings.Style;
        var scale = Scale;
        var size = Math.Max(12, OverlayPlacement.FontSize(style.FontSize) * scale * 0.55);
        var middle = area is { } box ? (box.Left + box.Right) / 2 : 0.5;
        var (borderBrush, borderThickness) = SubtitleStyleVisuals.ResolveBorder(style.BorderStyle, scale * 0.75);
        return new Border
        {
            Background = new SolidColorBrush(SubtitleStyleVisuals.ResolveBackgroundColor(style, 0xD9)),
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

    /// <summary>Yeri ayarlanırken: kesik çizgili çerçeve, açıklama, "Bitti" ve iki yandan genişletme tutamakları.</summary>
    private void BuildAdjustLayer()
    {
        var accent = Application.Current?.TryFindResource("accentBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D));
        adjustLayer.Children.Add(new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)),
            Stroke = accent,
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
            RadiusX = 14,
            RadiusY = 14,
        });
        var done = new Button { Content = Localizer.Current["Done"], Margin = new Thickness(12, 0, 0, 0) };
        if (Application.Current?.TryFindResource("PrimaryButton") is Style primary) done.Style = primary;
        done.Click += (_, _) => Guard("Bitti", () => controller.OverlayAdjusting = false);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) };
        bar.Children.Add(new TextBlock
        {
            Text = Localizer.Current["Drag to move, drag the edges to resize."],
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        bar.Children.Add(done);
        adjustLayer.Children.Add(bar);
        adjustLayer.Children.Add(MakeHandle(HorizontalAlignment.Left, accent));
        adjustLayer.Children.Add(MakeHandle(HorizontalAlignment.Right, accent));
    }

    /// <summary>Kenardan genişletme: ortası yerinde kalır, iki yana eşit büyür.</summary>
    private Thumb MakeHandle(HorizontalAlignment side, Brush accent)
    {
        var face = new FrameworkElementFactory(typeof(Border));
        face.SetValue(Border.BackgroundProperty, accent);
        face.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var thumb = new Thumb
        {
            Width = 6,
            Height = 48,
            Margin = new Thickness(6, 0, 6, 0),
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.SizeWE,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = face },
        };
        thumb.DragDelta += (_, e) => Guard("genişletme", () =>
        {
            var grow = side == HorizontalAlignment.Right ? e.HorizontalChange : -e.HorizontalChange;
            var center = Left + Width / 2;
            if (!double.IsFinite(center)) return;
            // Math.Clamp en büyük değer en küçükten küçükse hata verir; sınırlar OverlayPlacement'ta güvenle hesaplanır.
            var width = OverlayPlacement.ClampWidth(Width + 2 * grow, SystemParameters.VirtualScreenWidth);
            Width = width;
            Left = center - width / 2;
        });
        thumb.DragCompleted += (_, _) => Guard("genişletme", SaveBounds);
        return thumb;
    }

    /// <summary>"Tıklamalar geçsin" açıksa (ve yeri ayarlanmıyorsa) fare pencereden geçer.</summary>
    private void ApplyClickThrough()
    {
        if (handle == IntPtr.Zero) return;
        var through = controller.Settings.OverlayClickThrough && !controller.OverlayAdjusting;
        var style = (long)GetWindowLongPtr(handle, GwlExStyle);
        var wanted = through ? style | WsExTransparent : style & ~WsExTransparent;
        if (wanted != style) SetWindowLongPtr(handle, GwlExStyle, (IntPtr)wanted);
    }

    /// <summary>Üstte/altta yaslamayı ayarlar.</summary>
    private void ApplyAnchorAlignment()
    {
        var top = controller.Settings.Style.PositionAnchor == SubtitleAnchor.Top;
        linesPanel.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        regionsPanel.VerticalAlignment = top ? VerticalAlignment.Bottom : VerticalAlignment.Top;
    }

    /// <summary>
    /// Kullanıcı fareyle taşıdıysa kayıtlı konum korunur; ayar penceresinden Altta/Üstte ya da konum kaydırıcısı
    /// kullanıldıysa çalışma alanı üzerinden hesaplanan yerleşim uygulanır. Kaynak İngilizce satır kutuyu taşıyorsa
    /// pencere ve arka plan kutusu İngilizce yazıyı tamamen kapatacak kadar genişletilir.
    /// </summary>
    private OverlayPlacement.CoverLayout PlaceFromSettings(CouchSubs.Core.TextBox? sourceBox = null)
    {
        var style = controller.Settings.Style;
        var scale = Scale;
        var height = OverlayPlacement.Height(style.FontSize, scale);
        var work = SystemParameters.WorkArea;
        var workArea = new ScreenArea(work.Left, work.Top, work.Width, work.Height);
        var basePlace = controller.Settings.Overlay is { } saved
            ? OverlayPlacement.Place(saved,
                new ScreenArea(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight),
                workArea, height)
            : OverlayPlacement.AnchoredPlace(workArea, height, style.PositionAnchor, style.BottomPercent, style.BoxWidthPercent, style.HorizontalAlign);
        var cover = OverlayPlacement.CoverForSource(
            workArea,
            Math.Max(work.Height, SystemParameters.PrimaryScreenHeight),
            basePlace,
            height,
            sourceBox,
            style.HorizontalAlign,
            style.BackgroundShape == SubtitleBackgroundShape.Band,
            style.Background > 0,
            controller.OverlayAdjusting,
            24 * scale);
        if (Math.Abs(Height - height) >= 1) Height = height;
        if (Math.Abs(Width - cover.Window.Width) >= 1) Width = cover.Window.Width;
        if (Math.Abs(Left - cover.Window.Left) >= 1) Left = cover.Window.Left;
        if (Math.Abs(Top - cover.Window.Top) >= 1) Top = cover.Window.Top;
        return cover;
    }

    /// <summary>NaN ya da sonsuz değer JSON'a yazılamaz (ayar kaydı hata verirdi); öyleyse kaydedilmez.</summary>
    private void SaveBounds()
    {
        if (OverlayPlacement.ToSave(Left, Top, Width) is { } bounds) controller.SaveOverlayBounds(bounds);
    }
}
