// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using CouchSubs.App.Services;
using CouchSubs.App.Views;
using CouchSubs.Shell;
using Microsoft.Win32;

namespace CouchSubs.App;

/// <summary>Uygulamanın tek penceresi: kurulum bitmediyse sihirbaz, bittiyse ana ekran.</summary>
public partial class ShellWindow : Window
{
    private readonly AppController controller;
    private bool showingMain;
    private GlobalHotkeys? hotkeys;

    public ShellWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();
        controller.PropertyChanged += OnChanged;
        // Bilgisayar uykudan uyanınca bağlantılar hemen yenilenir.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // Çeviri kısayolları çeviri açıkken, "Çeviriyi başlat / durdur" kısayolu ise kapalıyken de tutulur.
        SourceInitialized += (_, _) =>
        {
            hotkeys = new GlobalHotkeys(new WindowInteropHelper(this).Handle, OnHotkey);
            ApplyHotkeys();
        };
        // Bilgisayarda gösterilecek çeviriler küçük, üstte duran bir kutuda çıkar.
        controller.PcResultReady += PcResultWindow.Display;
        // Bilgisayar ekranındaki altyazı penceresi (altyazı yokken gizli). Kurulamazsa uygulama onsuz açılır.
        SubtitleOverlayWindow? overlay = null;
        try
        {
            overlay = SubtitleOverlayWindow.Attach(controller);
        }
        catch (Exception error)
        {
            StartupGuard.Log("Altyazı penceresi kurulamadı", error);
            StartupGuard.Show(Application.Current, StartupGuard.OverlayFailedMessage(error));
        }
        Closed += (_, _) =>
        {
            controller.PcResultReady -= PcResultWindow.Display;
            overlay?.Detach();
            hotkeys?.Dispose();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            controller.Dispose();
        };
        ShowScreen(force: true);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppController.Step)) ShowScreen();
        if (e.PropertyName is nameof(AppController.IsRunning) or nameof(AppController.HotkeysPaused)
            or nameof(AppController.Settings) or nameof(AppController.Step))
            ApplyHotkeys();
    }

    /// <summary>Kısayol tablosu ayarlardan gelir; Ayarlar'da yeni tuş beklenirken kısayollar bırakılır.</summary>
    private void ApplyHotkeys()
    {
        if (hotkeys is null) return;
        var bindings = HotkeyTable.ActiveBindings(controller.Settings.Hotkeys, controller.IsRunning);
        hotkeys.Bindings = bindings;
        hotkeys.Enabled = controller.Step == OnboardingStep.Done && !controller.HotkeysPaused && bindings.Count > 0;
    }

    internal static async Task ToggleTranslationAsync(AppController controller)
    {
        var starting = !controller.IsRunning;
        try
        {
            await controller.ToggleTranslationAsync();
        }
        catch (Exception error)
        {
            StartupGuard.Log(starting ? "Çeviri başlatılamadı" : "Çeviri durdurulamadı", error);
            StartupGuard.Show(Application.Current, StartupGuard.ErrorMessage(error));
        }
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleTranslation: _ = ToggleTranslationAsync(controller); break;
            case HotkeyAction.ExtraRegion1: _ = controller.TranslateExtraRegionAsync(1); break;
            case HotkeyAction.ExtraRegion2: _ = controller.TranslateExtraRegionAsync(2); break;
            case HotkeyAction.ExtraRegion3: _ = controller.TranslateExtraRegionAsync(3); break;
            case HotkeyAction.PauseSubtitles: controller.ToggleSubtitles(); break;
            case HotkeyAction.Snapshot: controller.RequestSnapshot(); break;
            case HotkeyAction.FreeArea: FreeAreaWindow.Open(controller); break;
            case HotkeyAction.Screen: _ = controller.TranslateScreenAsync(); break;
            case HotkeyAction.ClickThrough: controller.ToggleClickThrough(); break;
        }
    }

    private void ShowScreen(bool force = false)
    {
        var main = controller.Step == OnboardingStep.Done;
        if (!force && main == showingMain) return;
        showingMain = main;
        Host.Content = main ? new MainView(controller) : new OnboardingView(controller);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.Invoke(controller.SystemResumed);
    }
}
