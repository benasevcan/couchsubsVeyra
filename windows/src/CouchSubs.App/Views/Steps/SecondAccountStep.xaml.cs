// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CouchSubs.Shell;

namespace CouchSubs.App.Views.Steps;

public partial class SecondAccountStep : UserControl
{
    public SecondAccountStep()
    {
        InitializeComponent();
        // Veyra, PS Remote Play ya da chiaki-ng bulunamadıysa seçme/indirme düğmeleri görünür.
        Loaded += (_, _) => DownloadPanel.Visibility =
            Controller.RemotePlayInstalled(RemotePlayApp.Automatic) ? Visibility.Collapsed : Visibility.Visible;
    }

    private AppController Controller => (AppController)DataContext;

    /// <summary>"İkinci hesap yok mu?": görüntü bu bilgisayardaki Remote Play penceresinden okunur, kurulum biter.</summary>
    private void OnUseRemotePlayWindow(object sender, RoutedEventArgs e) => Controller.UseRemotePlayWindow();

    private void OnSelectVeyra(object sender, RoutedEventArgs e)
    {
        if (SettingsWindow.PickVeyraExecutable(Window.GetWindow(this), Controller.Settings.VeyraExecutablePath) is not { } path) return;
        Controller.Settings = Controller.Settings with { VeyraExecutablePath = path, RemotePlayApp = RemotePlayApp.Veyra };
        Controller.UseRemotePlayWindow();
    }

    private void OnUseCaptureWindow(object sender, RoutedEventArgs e) => Controller.UseCaptureWindow();

    private void OnDownloadSony(object sender, RoutedEventArgs e) => Open(AppController.SonyRemotePlayDownload);
    private void OnDownloadChiaki(object sender, RoutedEventArgs e) => Open(AppController.ChiakiDownload);

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}
