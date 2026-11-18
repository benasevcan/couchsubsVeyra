// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using CouchSubs.Core;
using CouchSubs.RemotePlay;
using CouchSubs.Shell;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace CouchSubs.App.Services;

/// <summary>
/// Görüntüyü PS5'e bağlanmadan, bu bilgisayarda açık olan Remote Play penceresinden alır (Windows.Graphics.Capture):
/// Sony'nin PS Remote Play'i ya da açık kaynak chiaki-ng (bkz. <see cref="RemotePlayApp"/>). Oyun o uygulamada oynanır
/// ya da izlenir; CouchSubs PS5'e ikinci bir Remote Play oturumu açmadığı için ikinci hesap gerekmez. Yalnız o pencere
/// yakalanır: üstündeki altyazı penceremiz okunmaz. Kareler <see cref="StreamSession"/>'ınkiler gibi
/// <see cref="FrameReceived"/> ile gelir; okuma hattının geri kalanı aynıdır (Mac'teki RemotePlayWindowStream'in karşılığı).
/// </summary>
public sealed class RemotePlayWindowStream : IStreamSession
{
    /// <summary>Normal ve düşük gecikme modunda saniyedeki kare hızı.</summary>
    private const int DefaultFramesPerSecond = 15;
    private const int LowLatencyFramesPerSecond = 25;
    /// <summary>Remote Play'in küçük pencereleri (giriş, denetim) oyun penceresi sanılmasın.</summary>
    private const int MinimumWidth = 400, MinimumHeight = 250;
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(2);
    /// <summary>İlk kare gelmeden siyah gelirse yakalama engelleniyor olabilir; sahne kararması için daha uzun beklenir.</summary>
    private const long BlackMsBeforeWarning = 4_000;
    private const long BlackMsAfterContentWarning = 15_000;

    private static readonly string[] SonyProcesses = ["RemotePlay"];
    private static readonly string[] ChiakiProcesses = ["chiaki", "chiaki-ng"];
    private static readonly string[] VeyraProcesses = ["veyra_qml_ui", "veyra"];

    public static string? CustomVeyraPath { get; set; }
    /// <summary>Düşük gecikme modunda yakalama hızı 25 FPS'ye çıkarılır.</summary>
    public static bool LowLatencyCapture { get; set; } = true;

    public event Action<Frame>? FrameReceived;
    public event Action<SessionState>? StateChanged;
    /// <summary>Bu kaynakta PS5'in adresi bilinmez; hiç çağrılmaz.</summary>
    public event Action<RegisteredConsole>? ConsoleMoved { add { } remove { } }

    private readonly RemotePlayApp app;
    private readonly WindowCaptureTarget? selectedWindow;
    private readonly string[] processNames;
    private readonly object gate = new();
    private int generation;
    private bool running;
    private CancellationTokenSource? search;
    private Capture? capture;
    private SessionState? reported;

    public RemotePlayWindowStream(RemotePlayApp app)
    {
        this.app = app;
        processNames = app switch
        {
            RemotePlayApp.Sony => SonyProcesses,
            RemotePlayApp.Chiaki => ChiakiProcesses,
            RemotePlayApp.Veyra => VeyraProcesses,
            _ => [.. VeyraProcesses, .. SonyProcesses, .. ChiakiProcesses],
        };
    }

    /// <summary>OBS veya oyun penceresi kullanıcı tarafından seçildi: uygulama açılmaz, tam ekran yakalanmaz.</summary>
    public RemotePlayWindowStream(WindowCaptureTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.ProcessName) || string.IsNullOrWhiteSpace(target.Title))
            throw new ArgumentException("A process and window title must be selected.", nameof(target));
        app = RemotePlayApp.Automatic;
        selectedWindow = target;
        processNames = [target.ProcessName];
    }

    public void Start()
    {
        int current;
        lock (gate)
        {
            running = true;
            current = ++generation;
            reported = null;
        }
        Report(SessionState.Connecting);
        if (selectedWindow is null) OpenIfNeeded();
        StartSearch(current);
    }

    public void Stop()
    {
        lock (gate)
        {
            running = false;
            generation++;
        }
        TearDown();
        Report(SessionState.Disconnected(DisconnectKind.Stopped));
    }

    /// <summary>Uykudan uyanınca ya da ağ değişince yakalama baştan kurulur.</summary>
    public void ReconnectNow()
    {
        int current;
        lock (gate)
        {
            if (!running) return;
            current = ++generation;
        }
        TearDown();
        Report(SessionState.Connecting);
        StartSearch(current);
    }

    public void Dispose() => Stop();

    private bool IsCurrent(int number)
    {
        lock (gate) return running && generation == number;
    }

    /// <summary>Aynı durum yeniden bildirilmez (her "bağlantı yok" bildirimi okuma hattını sıfırlıyor).</summary>
    private void Report(SessionState state)
    {
        lock (gate)
        {
            if (reported == state) return;
            reported = state;
        }
        StateChanged?.Invoke(state);
    }

    private void TearDown()
    {
        CancellationTokenSource? oldSearch;
        Capture? oldCapture;
        lock (gate)
        {
            oldSearch = search;
            oldCapture = capture;
            search = null;
            capture = null;
        }
        oldSearch?.Cancel();
        oldCapture?.Dispose();
    }

    // --- Pencereyi bulma ---

    private void StartSearch(int number)
    {
        var token = new CancellationTokenSource();
        CancellationTokenSource? old;
        lock (gate)
        {
            old = search;
            search = token;
        }
        old?.Cancel();
        _ = Task.Run(async () =>
        {
            var noted = false;
            while (!token.IsCancellationRequested && IsCurrent(number))
            {
                var window = FindGameWindow(processNames, selectedWindow?.Title);
                if (window != IntPtr.Zero)
                {
                    try
                    {
                        var started = new Capture(window, this, number);
                        bool kept;
                        lock (gate)
                        {
                            kept = running && generation == number;
                            if (kept) capture = started;
                        }
                        if (!kept)
                        {
                            started.Dispose();
                            return;
                        }
                        RemotePlayLog.Note($"{started.Name} penceresi okunuyor: {started.Width}x{started.Height}");
                        Report(SessionState.Streaming);
                        return;
                    }
                    catch (Exception error)
                    {
                        RemotePlayLog.Note($"Remote Play penceresi yakalanamadı: {error.GetType().Name}: {error.Message}");
                        Report(SessionState.Disconnected(DisconnectKind.Other, error.Message));
                    }
                }
                else
                {
                    if (!noted)
                    {
                        RemotePlayLog.Note(selectedWindow is { } target
                            ? $"Seçilen pencere bekleniyor: {target.ProcessName} / {target.Title}"
                            : app == RemotePlayApp.Veyra
                                ? "Veyra penceresi bekleniyor (veyra_qml_ui)"
                                : "Remote Play penceresi bekleniyor (Veyra, PS Remote Play ya da chiaki-ng)");
                        noted = true;
                    }
                    Report(SessionState.Connecting);
                }
                try { await Task.Delay(SearchInterval, token.Token); } catch (TaskCanceledException) { return; }
            }
        });
    }

    /// <summary>Pencere kapandı ya da yakalama düştü: pencere yeniden aranır (çeviri durmaz).</summary>
    private void CaptureEnded(Capture ended, int number, string why)
    {
        int current;
        lock (gate)
        {
            if (!running || generation != number || capture != ended) return;
            capture = null;
            current = ++generation;
        }
        ended.Dispose();
        RemotePlayLog.Note($"Remote Play penceresi kapandı: {why}");
        Report(SessionState.Connecting);
        StartSearch(current);
    }

    private void Deliver(Frame frame) => FrameReceived?.Invoke(frame);

    private void BlackWarning(bool black)
    {
        if (black)
            Report(SessionState.Disconnected(DisconnectKind.Other,
                selectedWindow is not null
                    ? Localizer.Current["The selected window is black. Check the OBS projector or game window."]
                    : app == RemotePlayApp.Veyra
                        ? Localizer.Current["The Veyra window comes through black. Check that Veyra is actively rendering on this PC."]
                        : Localizer.Current["The Remote Play window comes through black: this app may block screen capture. Try chiaki-ng."]));
        else
            Report(SessionState.Streaming);
    }

    /// <summary>Seçilen uygulamaların en büyük, görünür, simge durumunda olmayan üst düzey penceresi.</summary>
    private static IntPtr FindGameWindow(string[] names, string? selectedTitle = null)
    {
        var processes = new Dictionary<uint, bool>();
        var best = IntPtr.Zero;
        long bestArea = 0;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Native.GetWindow(hwnd, Native.GwOwner) != IntPtr.Zero) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (!processes.TryGetValue(pid, out var wanted))
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    wanted = names.Any(name => string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception) { wanted = false; }
                processes[pid] = wanted;
            }
            if (!wanted || (selectedTitle is not null && (pid == Environment.ProcessId || WindowTitle(hwnd) != selectedTitle))
                || !Native.GetWindowRect(hwnd, out var rect)) return true;
            var (width, height) = (rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (width < MinimumWidth || height < MinimumHeight) return true;
            var area = (long)width * height;
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>Yalnız kullanıcıya görünür ve uygulamanın kendisine ait olmayan pencereler seçilebilir.</summary>
    public static IReadOnlyList<WindowCaptureTarget> ListCaptureWindows()
    {
        var windows = new List<WindowCaptureTarget>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || Native.GetWindow(hwnd, Native.GwOwner) != IntPtr.Zero
                || !Native.GetWindowRect(hwnd, out var rect)
                || rect.Right - rect.Left < MinimumWidth || rect.Bottom - rect.Top < MinimumHeight) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId) return true; // CouchSubs altyazı penceresini seçip geri besleme oluşturma.
            var title = WindowTitle(hwnd);
            if (title.Length == 0) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                windows.Add(new WindowCaptureTarget(process.ProcessName, title));
            }
            catch (Exception) { /* Süreç arama sırasında kapanmış olabilir. */ }
            return true;
        }, IntPtr.Zero);
        return [.. windows.Distinct().OrderBy(w => w.ProcessName).ThenBy(w => w.Title)];
    }

    private static string WindowTitle(IntPtr window)
    {
        var length = Native.GetWindowTextLength(window);
        if (length <= 0) return "";
        var buffer = new StringBuilder(Math.Min(length + 1, 1025));
        Native.GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    /// <summary>Seçilen uygulamalardan hiçbiri açık değilse kurulu olan ilki açılır.</summary>
    private void OpenIfNeeded()
    {
        if (processNames.Any(name => Process.GetProcessesByName(name).Length > 0)) return;
        var path = InstalledPath(app);
        if (path is null) return;
        try
        {
            RemotePlayLog.Note($"{Path.GetFileNameWithoutExtension(path)} açılıyor");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) })?.Dispose();
        }
        catch (Exception error)
        {
            RemotePlayLog.Note($"Remote Play açılamadı: {error.Message}");
        }
    }

    /// <summary>Seçilen Remote Play uygulaması bu bilgisayarda kurulu mu ("hangisi açıksa"da ikisinden biri yeter).</summary>
    public static bool IsInstalled(RemotePlayApp app) => InstalledPath(app) is not null;

    /// <summary>Seçilen uygulamanın kurulu yolu ("hangisi açıksa"da varsa Veyra önce); kurulu değilse null.</summary>
    internal static string? InstalledPath(RemotePlayApp app) => app switch
    {
        RemotePlayApp.Sony => FindSonyPath(),
        RemotePlayApp.Chiaki => FindChiakiPath(),
        RemotePlayApp.Veyra => FindVeyraPath(),
        _ => FindVeyraPath() ?? FindSonyPath() ?? FindChiakiPath(),
    };

    private static string? FindSonyPath()
    {
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var x64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(x86, "Sony", "PS Remote Play", "RemotePlay.exe"),
            Path.Combine(x64, "Sony", "PS Remote Play", "RemotePlay.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindChiakiPath()
    {
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var x64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in new[] { x64, x86, Path.Combine(local, "Programs") })
        {
            foreach (var exe in new[] { "chiaki.exe", "chiaki-ng.exe" })
            {
                var path = Path.Combine(root, "chiaki-ng", exe);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    internal static string? FindVeyraPath()
    {
        if (!string.IsNullOrWhiteSpace(CustomVeyraPath) && File.Exists(CustomVeyraPath))
            return CustomVeyraPath;

        foreach (var name in VeyraProcesses)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var exePath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath)) return exePath;
                }
                catch (Exception) { }
                finally { process.Dispose(); }
            }
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppContext.BaseDirectory,
        };
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            foreach (var relative in new[] { Path.Combine("Veyra", "veyra_qml_ui.exe"), Path.Combine("Veyra", "veyra.exe"), "veyra_qml_ui.exe" })
            {
                var candidate = Path.Combine(root, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var scanRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(userProfile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        foreach (var dir in scanRoots)
        {
            if (FindVeyraRecursive(dir, depth: 0) is { } found) return found;
        }
        return null;
    }

    private static string? FindVeyraRecursive(string directory, int depth)
    {
        if (depth > 4 || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        foreach (var exeName in new[] { "veyra_qml_ui.exe", "veyra.exe" })
        {
            var direct = Path.Combine(directory, exeName);
            if (File.Exists(direct)) return direct;
        }
        if (depth == 4) return null;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Masaüstü/İndirilenler içindeki ilgisiz dev klasörleri gezmemek için derinlik >= 1 iken yalnız Veyra alt ağaçlarına inilir.
                if (depth >= 1 && !name.Contains("veyra", StringComparison.OrdinalIgnoreCase)) continue;
                if (FindVeyraRecursive(sub, depth + 1) is { } found) return found;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Bir pencerenin yakalanması: Direct3D aygıtı, kare havuzu ve oturum. Kareler saniyede en çok
    /// <see cref="FramesPerSecond"/> kez işlemciye kopyalanıp BGRA <see cref="Frame"/> olarak verilir.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly RemotePlayWindowStream owner;
        private readonly int number;
        private readonly IDirect3DDevice device;
        private readonly Direct3D.StagingReader staging;
        private readonly GraphicsCaptureItem item;
        private readonly Direct3D11CaptureFramePool pool;
        private readonly GraphicsCaptureSession session;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long lastFrameMs = -1000;
        private int busy;
        private long? blackSinceMs;
        private bool seenContent;
        private bool warnedBlack;
        private Windows.Graphics.SizeInt32 size;
        private bool disposed;

        public string Name { get; }
        public int Width => size.Width;
        public int Height => size.Height;

        public Capture(IntPtr window, RemotePlayWindowStream owner, int number)
        {
            this.owner = owner;
            this.number = number;
            Native.GetWindowThreadProcessId(window, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                Name = process.ProcessName;
            }
            catch (Exception) { Name = "Remote Play"; }
            (device, staging) = Direct3D.CreateDevice();
            item = CaptureInterop.CreateItemForWindow(window);
            size = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            HideCaptureBorder(session);
            item.Closed += (_, _) => owner.CaptureEnded(this, number, "pencere kapandı");
            pool.FrameArrived += OnFrameArrived;
            session.StartCapture();
        }

        /// <summary>Windows yakalanan pencerenin çevresine sarı bir çerçeve çizer. Destekleyen sürümlerde (Windows 11;
        /// Windows 10'da yalnız Server 2022, 20348+) çerçevesiz yakalama izni istenip çerçeve kapatılır. Windows 10'un ev
        /// sürümlerinde (22H2 dahil, 19045) Windows çerçeveyi kapatmaya izin vermez; yakalama yine sürer. Çerçeve neden
        /// kaldıysa günlüğe bir kez yazılır (GitHub #224).</summary>
        private static void HideCaptureBorder(GraphicsCaptureSession session)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) ||
                !Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                NoteBorderOnce($"sarı yakalama çerçevesi kapatılamıyor: Windows {Environment.OSVersion.Version} bunu desteklemiyor (Windows 11 gerekir)");
                return;
            }
            try
            {
                var access = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
                if (access != Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed)
                {
                    // Windows 11'de "Ayarlar › Gizlilik ve güvenlik › Ekran görüntüsü kenarlıkları" kapalıysa izin gelmez.
                    NoteBorderOnce($"sarı yakalama çerçevesi kapatılamadı: Windows çerçevesiz yakalamaya izin vermedi ({access})");
                    return;
                }
                session.IsBorderRequired = false;
            }
            catch (Exception error)
            {
                // İzin verilmezse çerçeve kalır; yakalama sürer.
                NoteBorderOnce($"sarı yakalama çerçevesi kapatılamadı: {error.GetType().Name}: {error.Message}");
            }
        }

        private static int borderNoted;

        private static void NoteBorderOnce(string text)
        {
            if (Interlocked.Exchange(ref borderNoted, 1) == 0) RemotePlayLog.Note(text);
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null || disposed) return;
            if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
            {
                // Pencere büyütüldü ya da tam ekrana geçti: havuz yeni boya kurulur, bu kare atlanır.
                size = frame.ContentSize;
                if (size.Width > 0 && size.Height > 0) pool.Recreate(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                return;
            }
            var now = clock.ElapsedMilliseconds;
            var fps = LowLatencyCapture ? LowLatencyFramesPerSecond : DefaultFramesPerSecond;
            if (now - lastFrameMs < 1000 / fps) return;
            // Önceki kare hâlâ kopyalanıyorsa bu kare atlanır.
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            lastFrameMs = now;
            try
            {
                var captured = staging.TryCopySurface(frame.Surface, size.Width, size.Height);
                if (captured is null)
                {
                    using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
                    var (width, height) = (bitmap.PixelWidth, bitmap.PixelHeight);
                    if (width < 2 || height < 2) return;
                    var pixels = new byte[width * height * 4];
                    bitmap.CopyToBuffer(pixels.AsBuffer());
                    captured = new Frame(pixels, width, height, width * 4);
                }
                if (captured.Width < 2 || captured.Height < 2) return;
                CheckBlack(captured.Pixels, now);
                owner.Deliver(captured);
            }
            catch (Exception error)
            {
                RemotePlayLog.Note($"Remote Play karesi kopyalanamadı: {error.GetType().Name}: {error.Message}");
            }
            finally
            {
                Volatile.Write(ref busy, 0);
            }
        }

        /// <summary>Bazı uygulamalar penceresinin yakalanmasını engeller; o zaman kareler simsiyah gelir.</summary>
        private void CheckBlack(byte[] pixels, long nowMs)
        {
            var black = true;
            for (var i = 0; i < pixels.Length && black; i += 4 * 997)
                black = pixels[i] < 10 && pixels[i + 1] < 10 && pixels[i + 2] < 10;
            if (black)
            {
                blackSinceMs ??= nowMs;
                var limit = seenContent ? BlackMsAfterContentWarning : BlackMsBeforeWarning;
                if (nowMs - blackSinceMs.Value >= limit && !warnedBlack)
                {
                    warnedBlack = true;
                    RemotePlayLog.Note($"{Name} penceresi siyah geliyor: ekran yakalama engelleniyor olabilir");
                    owner.BlackWarning(true);
                }
            }
            else
            {
                seenContent = true;
                blackSinceMs = null;
                if (warnedBlack)
                {
                    warnedBlack = false;
                    owner.BlackWarning(false);
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { session.Dispose(); } catch (Exception) { }
            try { pool.Dispose(); } catch (Exception) { }
            try { staging.Dispose(); } catch (Exception) { }
            try { device.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>Pencereden yakalama öğesi (IGraphicsCaptureItemInterop).</summary>
    private static class CaptureInterop
    {
        private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
            IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
        }

        public static GraphicsCaptureItem CreateItemForWindow(IntPtr window)
        {
            var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
            var iid = GraphicsCaptureItemIid;
            var pointer = interop.CreateForWindow(window, ref iid);
            try { return GraphicsCaptureItem.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
        }
    }

    /// <summary>Yakalama için Direct3D 11 aygıtı ve eşzamanlı staging okuyucu (WinRT async kopyası olmadan doğrudan Map).</summary>
    internal static unsafe class Direct3D
    {
        private static readonly Guid DxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        private static readonly Guid Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
                                                    uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel,
                                                    out IntPtr immediateContext);

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        public static (IDirect3DDevice Device, StagingReader Staging) CreateDevice()
        {
            // D3D_DRIVER_TYPE_HARDWARE = 1, D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20, D3D11_SDK_VERSION = 7
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out var device, out _, out var context));
            try
            {
                var iid = DxgiDeviceIid;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out var dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                    try
                    {
                        var winRt = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                        return (winRt, new StagingReader(device, context));
                    }
                    finally { Marshal.Release(inspectable); }
                }
                finally { Marshal.Release(dxgi); }
            }
            catch
            {
                if (context != IntPtr.Zero) Marshal.Release(context);
                Marshal.Release(device);
                throw;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Texture2DDesc
        {
            public uint Width, Height, MipLevels, ArraySize, Format;
            public uint SampleCount, SampleQuality;
            public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MappedSubresource
        {
            public IntPtr Data;
            public uint RowPitch, DepthPitch;
        }

        internal sealed class StagingReader(IntPtr device, IntPtr context) : IDisposable
        {
            private const uint DxgiFormatB8G8R8A8Unorm = 87;
            private const uint D3D11UsageStaging = 3;
            private const uint D3D11CpuAccessRead = 0x20000;
            private const uint D3D11MapRead = 1;

            private IntPtr staging;
            private int stagingWidth;
            private int stagingHeight;
            private bool fallbackOnly;
            private bool disposed;

            public Frame? TryCopySurface(IDirect3DSurface surface, int width, int height)
            {
                if (disposed || fallbackOnly || width < 2 || height < 2) return null;
                IntPtr access = IntPtr.Zero;
                IntPtr srcTex = IntPtr.Zero;
                try
                {
                    using var marshaler = WinRT.MarshalInterface<IDirect3DSurface>.CreateMarshaler(surface);
                    var surfacePtr = marshaler?.ThisPtr ?? IntPtr.Zero;
                    if (surfacePtr == IntPtr.Zero) return null;
                    var accessIid = DxgiInterfaceAccessIid;
                    if (Marshal.QueryInterface(surfacePtr, in accessIid, out access) < 0 || access == IntPtr.Zero) return null;
                    var texIid = Texture2DIid;
                    var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, out IntPtr, int>)(*(IntPtr**)access)[3];
                    if (getInterface(access, &texIid, out srcTex) < 0 || srcTex == IntPtr.Zero) return null;
                    return CopyTexture(srcTex, width, height);
                }
                catch
                {
                    fallbackOnly = true;
                    return null;
                }
                finally
                {
                    if (srcTex != IntPtr.Zero) Marshal.Release(srcTex);
                    if (access != IntPtr.Zero) Marshal.Release(access);
                }
            }

            internal Frame? CopyTexture(IntPtr srcTexture, int width, int height)
            {
                if (disposed || fallbackOnly || srcTexture == IntPtr.Zero || width < 2 || height < 2) return null;
                if (!EnsureStaging(width, height))
                {
                    fallbackOnly = true;
                    return null;
                }
                var ctxVtbl = *(IntPtr**)context;
                // ID3D11DeviceContext::CopyResource (slot 47)
                var copyResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)ctxVtbl[47];
                copyResource(context, staging, srcTexture);
                // ID3D11DeviceContext::Map (slot 14) / Unmap (slot 15)
                var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)ctxVtbl[14];
                MappedSubresource mapped;
                if (map(context, staging, 0, D3D11MapRead, 0, &mapped) < 0 || mapped.Data == IntPtr.Zero) return null;
                try
                {
                    var rowBytes = width * 4;
                    var pixels = new byte[height * rowBytes];
                    fixed (byte* dstBase = pixels)
                    {
                        var srcBase = (byte*)mapped.Data;
                        if (mapped.RowPitch == rowBytes)
                        {
                            Buffer.MemoryCopy(srcBase, dstBase, pixels.Length, pixels.Length);
                        }
                        else
                        {
                            var srcPitch = (long)mapped.RowPitch;
                            for (var y = 0; y < height; y++)
                                Buffer.MemoryCopy(srcBase + (y * srcPitch), dstBase + ((long)y * rowBytes), rowBytes, rowBytes);
                        }
                    }
                    return new Frame(pixels, width, height, rowBytes);
                }
                finally
                {
                    var unmap = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)ctxVtbl[15];
                    unmap(context, staging, 0);
                }
            }

            private bool EnsureStaging(int width, int height)
            {
                if (staging != IntPtr.Zero && stagingWidth == width && stagingHeight == height) return true;
                if (staging != IntPtr.Zero)
                {
                    Marshal.Release(staging);
                    staging = IntPtr.Zero;
                }
                var desc = new Texture2DDesc
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = DxgiFormatB8G8R8A8Unorm,
                    SampleCount = 1,
                    SampleQuality = 0,
                    Usage = D3D11UsageStaging,
                    BindFlags = 0,
                    CPUAccessFlags = D3D11CpuAccessRead,
                    MiscFlags = 0,
                };
                // ID3D11Device::CreateTexture2D (slot 5)
                var createTex = (delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, out IntPtr, int>)(*(IntPtr**)device)[5];
                if (createTex(device, &desc, IntPtr.Zero, out var created) < 0 || created == IntPtr.Zero) return false;
                staging = created;
                stagingWidth = width;
                stagingHeight = height;
                return true;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (staging != IntPtr.Zero) { Marshal.Release(staging); staging = IntPtr.Zero; }
                if (context != IntPtr.Zero) Marshal.Release(context);
                if (device != IntPtr.Zero) Marshal.Release(device);
            }
        }
    }

    private static class Native
    {
        public const uint GwOwner = 4;

        public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    }
}
