using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexStart;

internal static class Smoke
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--short-lived")
        {
            Thread.Sleep(1100);
            return 0;
        }
        if (args is ["--ui", var clip]) return MediaPlayback(clip);
        if (args is ["--handoff", var video]) return WindowHandoff(video);
        if (args is ["--handoff", var fullScreenVideo, "--maximized-only"]) return WindowHandoff(fullScreenVideo, maximizedOnly: true);
        if (args is ["--loading-handoff", var loadingVideo]) return WindowHandoff(loadingVideo, loadingOnly: true);
        if (args is ["--handoff-client", var windowMode, var behavior])
            return HandoffClient(Enum.Parse<StartupWindowMode>(windowMode), behavior);
        if (args is ["--visuals", var directory]) return SettingsVisuals(directory);
        if (args is ["--visuals", var visualDirectory, var videoSkin]) return SettingsVisuals(visualDirectory, videoSkin);
        if (args is ["--official-window"])
        {
            var processes = WindowsApp.Running();
            var window = WindowsApp.MainWindow(processes);
            if (window == 0) { Console.WriteLine("No visible registered official window."); return 1; }
            var consecutive = 0;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                consecutive = WindowsApp.ContentReady(window) ? consecutive + 1 : 0;
                if (consecutive == 2)
                {
                    Console.WriteLine("PASS recognizes accessible content on two successive probes of the official window");
                    return 0;
                }
                Thread.Sleep(350);
            }
            Console.WriteLine("The official window did not report accessible content on two successive probes.");
            InspectOfficialContent(window);
            return 1;
        }
        var output = args.FirstOrDefault() ?? Path.GetTempPath();
        var checks = new (string Name, Action Check)[]
        {
            ("timeline rejects invalid boundaries and accepts the three segments", Timeline),
            ("empty and legacy preset configurations play no built-in animation", NoDefaultAnimation),
            ("legacy preferences use proportional sizing and the video fits the monitor work area", StartupBounds),
            ("invalid/remote images fall back without enabling theming", MissingPictures),
            ("picture mode remains the default and missing videos fall back", MissingWallpaperVideo),
            ("only the expected local DevTools page socket is accepted", EndpointIdentity),
            ("native TCP ownership resolves a loopback listener", PortOwner),
            ("background task cancels when its monitored process exits", ProcessLifetime),
            ("the installed app yields its own shortcut icon", () => OfficialIcon(output))
        };
        var failed = 0;
        foreach (var (name, check) in checks)
        {
            try { check(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + " — " + ex.Message); }
        }
        return failed == 0 ? 0 : 1;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Unexpected result");
    }

    private static void InspectOfficialContent(nint window)
    {
        var root = AutomationElement.FromHandle(window);
        var documents = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        Console.WriteLine($"Documents: {documents.Count}");
        foreach (AutomationElement document in documents)
        {
            var editors = document.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            Console.WriteLine($"Document offscreen: {document.Current.IsOffscreen}; bounds: {document.Current.BoundingRectangle}; edits: {editors.Count}");
            foreach (AutomationElement editor in editors)
                Console.WriteLine($"Edit enabled: {editor.Current.IsEnabled}; offscreen: {editor.Current.IsOffscreen}; focusable: {editor.Current.IsKeyboardFocusable}; bounds: {editor.Current.BoundingRectangle}; writable ValuePattern: {editor.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && !((ValuePattern)pattern).Current.IsReadOnly}; TextPattern: {editor.TryGetCurrentPattern(TextPattern.Pattern, out _)}");
        }
    }

    private static void Timeline()
    {
        var settings = new Preferences { WaitFrom = 3, RevealFrom = 6 };
        Require(settings.HasValidTimeline(8));
        Require(!settings.HasValidTimeline(6));
        settings.WaitFrom = 6;
        Require(!settings.HasValidTimeline(8));
        settings.WaitFrom = double.NaN;
        Require(!settings.HasValidTimeline(8));
    }

    private static void NoDefaultAnimation()
    {
        Require(!new Preferences().HasCustomAnimation);
        var old = new Preferences { Animation = (AnimationMode)0, Opening = Environment.ProcessPath! };
        Require(!old.HasCustomAnimation);
        old.Animation = AnimationMode.Off;
        Require(!old.HasCustomAnimation);
        old.Animation = AnimationMode.Timeline;
        Require(!old.HasCustomAnimation);
    }

    private static void StartupBounds()
    {
        Require(JsonSerializer.Deserialize<Preferences>("{}")?.WindowMode == StartupWindowMode.VideoAspect);
        var area = new WindowsApp.Rect(-1920, 24, 0, 1040);
        var landscape = area.Fit(16d / 9);
        var portrait = area.Fit(9d / 16);
        Require(Math.Abs((double)landscape.Width / landscape.Height - 16d / 9) < .005);
        Require(Math.Abs((double)portrait.Width / portrait.Height - 9d / 16) < .005);
        foreach (var bounds in new[] { landscape, portrait })
        {
            Require(bounds.Left > area.Left && bounds.Right < area.Right);
            Require(bounds.Top > area.Top && bounds.Bottom < area.Bottom);
        }
    }

    private static void MissingPictures()
    {
        var settings = new Preferences { WallpaperEnabled = true };
        settings.For(Region.Window).File = "//example.com/folder/photo.png";
        var result = Theme.Build(settings);
        Require(!result.HasAssets && result.MissingImages && result.Json == "{}");
        settings.WallpaperEnabled = false;
        result = Theme.Build(settings);
        Require(!result.HasAssets && !result.MissingImages);
    }

    private static void MissingWallpaperVideo()
    {
        var settings = JsonSerializer.Deserialize<Preferences>("{}")!;
        Require(settings.WindowBackground == WindowBackgroundKind.Picture && !settings.WallpaperVideoSound);
        settings.WallpaperEnabled = true;
        settings.For(Region.Window).File = "https://example.com/background.png";
        settings.WallpaperVideoFile = "https://example.com/background.mp4";
        var picture = Theme.Build(settings);
        Require(!picture.HasAssets && picture.MissingImages && !picture.MissingVideo);
        settings.WindowBackground = WindowBackgroundKind.Video;
        var video = Theme.Build(settings);
        Require(!video.HasAssets && !video.MissingImages && video.MissingVideo && video.Json == "{}");
    }

    private static void EndpointIdentity()
    {
        static bool Check(string page, string address, string id = "example")
        {
            using var target = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                type = "page", url = page, id, webSocketDebuggerUrl = address
            }));
            return Theme.TargetSocket(target.RootElement, 19335, out _);
        }
        Require(Check("app://./index.html", "ws://127.0.0.1:19335/devtools/page/example"));
        Require(!Check("https://example.com", "ws://127.0.0.1:19335/devtools/page/example"));
        Require(!Check("app://./index.html", "ws://example.com:19335/devtools/page/example"));
        Require(!Check("app://./index.html", "ws://127.0.0.1:19335/devtools/page/example?extra=true"));
        Require(!Check("app://./index.html", "ws://127.0.0.1:19335/devtools/page/example", "other"));
        Require(!Check("app://./index.html", "ws://127.0.0.1:19336/devtools/page/example"));
    }

    private static void PortOwner()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var owner = WindowsApp.LoopbackListenerOwner(port);
        if (owner != Environment.ProcessId)
        {
            var error = ProbeTcpTable(port);
            throw new InvalidOperationException($"Port {port}, expected {Environment.ProcessId}, actual {owner}. {error}");
        }
        Require(ThemeSession.FromPort(port) == null);
    }

    private static string ProbeTcpTable(int port)
    {
        int size = 0;
        var sizing = GetExtendedTcpTable(0, ref size, false, 2, 3, 0);
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(Math.Max(size, 4));
        try
        {
            var query = GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0);
            if (query != 0) return $"Sizing {sizing}; query {query}; length {size}.";
            var count = System.Runtime.InteropServices.Marshal.ReadInt32(buffer);
            for (int i = 0; i < count; i++)
            {
                int row = 4 + 24 * i;
                int raw = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, row + 8);
                if (((raw & 255) << 8 | ((raw >> 8) & 255)) == port)
                    return $"Table has {count} rows; matching address {System.Runtime.InteropServices.Marshal.ReadInt32(buffer, row + 4):X8}, raw port {raw:X8}.";
            }
            return $"Table has {count} rows but the test listener is absent.";
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref int length, bool order, int family, int type, uint reserved);

    private static void ProcessLifetime()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, "--short-lived")
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        using var process = Process.Start(start)!;
        var ticks = Stopwatch.StartNew();
        var ran = false;
        var canceled = false;
        Theme.WatchUntilExit(process, async token =>
        {
            ran = true;
            try { await Task.Delay(TimeSpan.FromSeconds(8), token); }
            catch (OperationCanceledException) { canceled = true; throw; }
        }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Require(ran && canceled && process.HasExited && ticks.Elapsed < TimeSpan.FromSeconds(5));
    }

    private static void OfficialIcon(string output)
    {
        var file = Path.Combine(output, "official-test-" + Guid.NewGuid().ToString("N") + ".ico");
        WindowsApp.SaveOfficialIcon(file);
        var bytes = File.ReadAllBytes(file);
        Require(bytes.Length > 30 && bytes[2] == 1 && bytes[4] == 1);
    }

    private static void Capture(FrameworkElement art, string path)
    {
        art.UpdateLayout();
        var target = new RenderTargetBitmap((int)art.ActualWidth, (int)art.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(art);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var result = File.Create(path);
        encoder.Save(result);
        Require(result.Length > 1000);
    }

    private static int SettingsVisuals(string output, string? video = null)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                var preferences = new Preferences { WallpaperEnabled = true, WallpaperVideoFile = video is null ? "" : Path.GetFullPath(video) };
                var picture = Path.Combine(output, "settings-backgrounds.png");
                preferences.For(Region.Window).File = Path.GetFullPath(picture);
                var window = new SettingsWindow(preferences)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -2200, Top = -1200
                };
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var surface = (FrameworkElement)window.Content;
                Capture(surface, picture);
                if (video is not null)
                {
                    Click(window, "VideoBackground");
                    Require(((Button)window.FindName("PickAsset")).Content.Equals("选择视频"));
                    Require(((CheckBox)window.FindName("VideoSound")).IsChecked == false);
                    var theme = Theme.Build(preferences);
                    Require(theme.HasAssets && !theme.MissingVideo && theme.VideoFile == Path.GetFullPath(video));
                    Require(!theme.Json.Contains(Path.GetFileName(video), StringComparison.Ordinal));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Capture(surface, Path.Combine(output, "settings-video.png"));
                    Click(window, "PictureBackground");
                    Require(preferences.For(Region.Window).File == Path.GetFullPath(picture));
                    Require(preferences.WallpaperVideoFile == Path.GetFullPath(video));
                }
                Click(window, "RegionRight");
                Require(((TextBlock)window.FindName("RegionTitle")).Text == "右侧面板");
                Click(window, "AnimationTab");
                Require(!((Button)window.FindName("PreviewButton")).IsEnabled);
                Require(((RadioButton)window.FindName("VideoAspectWindow")).IsChecked == true);
                Require(((RadioButton)window.FindName("MaximizedWindow")).IsChecked == false);
                Require(((Button)window.FindName("SettingsShortcut")).IsVisible);
                Capture(surface, Path.Combine(output, "settings-animation.png"));
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Click(window, "ModeTimeline");
                Require(!((Slider)window.FindName("WaitMarker")).IsEnabled);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Capture(surface, Path.Combine(output, "settings-compact.png"));
                window.Close();
                Console.WriteLine("PASS rendered both settings pages and compact mode; empty animation cannot be previewed");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL settings visual checks — " + ex);
                app.Shutdown(1);
            }
        };
        return app.Run();

        static void Click(SettingsWindow window, string name)
        {
            var button = (System.Windows.Controls.Primitives.ButtonBase)window.FindName(name);
            if (button is RadioButton radio) radio.IsChecked = true;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
        }
    }

    private static int MediaPlayback(string clip)
    {
        clip = Path.GetFullPath(clip);
        Require(Math.Abs(Mp4.Duration(clip) - 1.2) < .01);
        Require(Mp4.AspectRatio(clip) > 0);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                var settings = new SettingsWindow(new Preferences());
                settings.Close();
                var empty = new AnimationWindow();
                var created = false;
                empty.SourceInitialized += (_, _) => created = true;
                await empty.Play(new Preferences(), _ => Task.FromResult(true));
                Require(!created);
                var view = new AnimationWindow(preview: true)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -2000, Top = -2000
                };
                var timer = Stopwatch.StartNew();
                var preferences = new Preferences { Animation = AnimationMode.Timeline, Timeline = Path.GetFullPath(clip), WaitFrom = .5, RevealFrom = .9 };
                await view.Play(preferences, _ => Task.FromResult(true));
                if (view.MediaIssue is { } issue) throw new InvalidOperationException(issue);
                if (timer.Elapsed.TotalSeconds is < .70 or > 6)
                    throw new InvalidOperationException($"Elapsed {timer.Elapsed.TotalSeconds:F2}s; expected the MP4 to play with the waiting segment skipped.");
                Console.WriteLine($"PASS settings loads and MP4 uses opening and reveal segments ({timer.Elapsed.TotalSeconds:F2}s)");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL WPF media playback — " + ex.Message);
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private static int WindowHandoff(string clip, bool maximizedOnly = false, bool loadingOnly = false)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                var path = Path.GetFullPath(clip);
                var aspect = Mp4.AspectRatio(path);
                Require(aspect > 0);
                var cases = new[]
                {
                    (StartupWindowMode.VideoAspect, "normal"), (StartupWindowMode.VideoAspect, "minimum-width"),
                    (StartupWindowMode.Maximized, "normal"), (StartupWindowMode.Maximized, "inset-maximize"),
                    (StartupWindowMode.Maximized, "reject-maximize"), (StartupWindowMode.Maximized, "restore"),
                    (StartupWindowMode.Maximized, "disappear"), (StartupWindowMode.Maximized, "loading")
                };
                foreach (var (mode, behavior) in cases)
                {
                    if (maximizedOnly && (mode != StartupWindowMode.Maximized || behavior != "normal")) continue;
                    if (loadingOnly != (behavior == "loading")) continue;
                    WindowsApp.WorkAreas(out var area);
                    var view = new AnimationWindow();
                    var prefs = new Preferences { Animation = AnimationMode.Clips, Opening = path, WindowMode = mode };
                    if (behavior is "inset-maximize" or "loading")
                    {
                        prefs.Animation = AnimationMode.Timeline;
                        prefs.Timeline = path;
                        prefs.WaitFrom = .25;
                        prefs.RevealFrom = behavior == "loading" ? .9 : .6;
                    }
                    nint clientHandle = 0;
                    Process? client = null;
                    var expected = mode == StartupWindowMode.Maximized ? area : area.Fit(aspect);
                    var rejected = false;
                    var center = new Point { X = area.Left + area.Width / 2, Y = area.Top + area.Height / 2 };
                    var corner = new Point { X = area.Left + 8, Y = area.Top + 8 };
                    Exception? renderFailure = null;
                    var transparentBeforePlayback = false;
                    WindowsApp.Rect? lastAnimationBounds = null;
                    Task<bool>? completedWaitingCycle = null;
                    view.Closing += (_, _) =>
                    {
                        if (WindowsApp.Bounds(new WindowInteropHelper(view).Handle, out var bounds)) lastAnimationBounds = bounds;
                    };
                    view.ContentRendered += (_, _) =>
                    {
                        try
                        {
                            var animation = new WindowInteropHelper(view).Handle;
                            Require(((Grid)view.Content).Opacity == 0);
                            if (WindowFromPoint(center) == animation || client is not null)
                                throw new InvalidOperationException("Animation captured the center point or launched the client before its fade-in.");
                            transparentBeforePlayback = true;
                        }
                        catch (Exception ex) { renderFailure = ex; }
                    };
                    try
                    {
                        await view.Play(prefs, async _ =>
                        {
                            var animation = new WindowInteropHelper(view).Handle;
                            if (renderFailure is not null) throw renderFailure;
                            if (!transparentBeforePlayback || !WindowsApp.OverlayMatches(view, expected))
                                throw new InvalidOperationException($"Pre-playback layout mismatch ({mode}, {behavior}), initial paint seen: {transparentBeforePlayback}, media: {view.MediaIssue}");
                            Require(((Grid)view.Content).Opacity == 1);
                            Require(((Grid)view.Content).Children.OfType<MediaElement>().Single().Stretch == Stretch.UniformToFill);
                            Require(WindowFromPoint(center) == animation);
                            Require((WindowFromPoint(corner) == animation) == (mode == StartupWindowMode.Maximized));
                            var command = new ProcessStartInfo(Environment.ProcessPath!)
                            {
                                UseShellExecute = false, CreateNoWindow = true,
                                RedirectStandardOutput = true, RedirectStandardInput = true
                            };
                            command.ArgumentList.Add("--handoff-client");
                            command.ArgumentList.Add(mode.ToString());
                            command.ArgumentList.Add(behavior);
                            client = Process.Start(command) ?? throw new InvalidOperationException("Could not start test client");
                            var output = await client.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(6));
                            Require(nint.TryParse(output, out clientHandle) && clientHandle != 0);
                            Require(WindowFromPoint(center) == animation);
                            if (behavior == "disappear")
                            {
                                client.StandardInput.Close();
                                clientHandle = 0;
                            }
                            if (behavior == "restore")
                            {
                                var clock = Stopwatch.StartNew();
                                while (WindowsApp.VisibleFrame(clientHandle)?.Maximized != true && clock.ElapsedMilliseconds < 3500)
                                    await Task.Delay(30);
                                Require(WindowsApp.VisibleFrame(clientHandle)?.Maximized == true);
                                client.StandardInput.WriteLine("restore");
                                Require(await client.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) == "restored");
                            }
                            if (behavior == "loading")
                            {
                                var media = ((Grid)view.Content).Children.OfType<MediaElement>().Single();
                                Require(!await Task.Run(() => WindowsApp.ContentReady(clientHandle)));
                                var watch = Stopwatch.StartNew();
                                var cycles = 0;
                                var lastPosition = 0d;
                                while (watch.Elapsed < TimeSpan.FromSeconds(5.2))
                                {
                                    var position = media.Position.TotalSeconds;
                                    if (lastPosition - position > .25) cycles++;
                                    Require(position < prefs.RevealFrom + .06 && WindowFromPoint(center) == animation);
                                    lastPosition = position;
                                    await Task.Delay(25);
                                }
                                Require(cycles >= 2 && !await Task.Run(() => WindowsApp.ContentReady(clientHandle)));
                                client.StandardInput.WriteLine("ready");
                                Require(await client.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) == "ready");
                                Require(await Task.Run(() => WindowsApp.ContentReady(clientHandle)));
                                watch.Restart();
                                while (media.Position.TotalSeconds is < .32 or > .48 && watch.Elapsed < TimeSpan.FromSeconds(2))
                                    await Task.Delay(12);
                                Require(media.Position.TotalSeconds is >= .30 and < .52);
                                completedWaitingCycle = ReachesWaitingBoundary(media, prefs.RevealFrom);
                            }
                            return true;
                        }, () => clientHandle, _ =>
                        {
                            rejected = true;
                            Require(WindowFromPoint(center) == new WindowInteropHelper(view).Handle);
                        });
                        if (renderFailure is not null) throw renderFailure;
                        Require(transparentBeforePlayback && view.MediaIssue is null);
                        if (completedWaitingCycle is not null) Require(await completedWaitingCycle);
                        if (behavior is "disappear" or "reject-maximize") Require(rejected && !view.HandoffSucceeded);
                        else
                        {
                            Require(!rejected && view.HandoffSucceeded && view.HandoffIssue is null);
                            var frame = WindowsApp.VisibleFrame(clientHandle);
                            if (frame is null || frame.Value.Bounds != lastAnimationBounds
                                || frame.Value.Maximized != (mode == StartupWindowMode.Maximized))
                                throw new InvalidOperationException($"Final playback bounds {lastAnimationBounds} differ from {mode}/{behavior} client frame {frame}.");
                            if ((frame.Value.Bounds != expected) != (behavior is "minimum-width" or "inset-maximize"))
                                throw new InvalidOperationException($"Unexpected client placement for {mode}/{behavior}: {frame}. Initial playback bounds: {expected}.");
                            Require(WindowFromPoint(center) == clientHandle);
                        }
                    }
                    finally
                    {
                        if (client is not null)
                        {
                            using (client)
                            {
                                client.StandardInput.Close();
                                await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
                            }
                        }
                    }
                }
                Console.WriteLine(loadingOnly ? "PASS a loading window stays covered beyond five seconds and hands off after its editor is ready and the waiting cycle finishes"
                    : maximizedOnly ? "PASS selected MP4 fades in and hands off to the maximized test client at identical bounds"
                    : "PASS edge-to-edge playback, exact windowed and maximized reveals including constrained windows, and blocked reveals for lost or incompatible clients");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL window handoff — " + ex);
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private static async Task<bool> ReachesWaitingBoundary(MediaElement media, double revealFrom)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            await Task.Delay(12);
            var position = media.Position.TotalSeconds;
            if (position >= revealFrom) return false;
            if (position >= revealFrom - .12) return true;
        }
        return false;
    }

    private static int HandoffClient(StartupWindowMode mode, string behavior)
    {
        var app = new Application();
        app.Startup += async (_, _) =>
        {
            var client = new Window
            {
                Title = "CodexStart handoff smoke", Width = 600, Height = 460, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowState = mode == StartupWindowMode.VideoAspect ? WindowState.Maximized : WindowState.Normal
            };
            if (behavior == "reject-maximize")
            {
                client.StateChanged += (_, _) =>
                {
                    if (client.WindowState == WindowState.Maximized)
                        client.Dispatcher.BeginInvoke(() => client.WindowState = WindowState.Normal);
                };
            }
            if (behavior == "minimum-width") client.MinWidth = SystemParameters.WorkArea.Width * .95;
            if (behavior == "inset-maximize") client.MaxWidth = SystemParameters.WorkArea.Width * .96;
            var editor = new TextBox { IsEnabled = false, Width = 270 };
            if (behavior == "loading") client.Content = new TestDocument
            {
                Children = { new Button { Content = "Cancel", Width = 120 }, editor }
            };
            client.Show();
            Console.WriteLine(new WindowInteropHelper(client).Handle);
            var command = await Task.Run(Console.ReadLine);
            if (command == "restore")
            {
                client.WindowState = WindowState.Normal;
                Console.WriteLine("restored");
                await Task.Run(Console.ReadLine);
            }
            if (command == "ready")
            {
                editor.IsEnabled = true;
                Console.WriteLine("ready");
                await Task.Run(Console.ReadLine);
            }
            client.Close();
        };
        return app.Run();
    }

    private sealed class TestDocument : StackPanel
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new DocumentPeer(this);

        private sealed class DocumentPeer(TestDocument owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
}
