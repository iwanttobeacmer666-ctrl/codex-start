using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexStart;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (args.Length > 0 && args[0] == "--watch")
        {
            try
            {
                if (args.Length == 4 && int.TryParse(args[1], out var pid) && pid > 0 &&
                    long.TryParse(args[2], out var start) && int.TryParse(args[3], out var port) && port is >= 1024 and <= 65535)
                    await Theme.Watch(new ThemeSession(pid, start, port));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or Win32Exception) { }
            finally { Shutdown(); }
            return;
        }

        try
        {
            if (args.FirstOrDefault() == "--settings" || (args.Length == 0 && !File.Exists(Preferences.SettingsFile)))
            {
                if (new SettingsWindow(Preferences.Load()).ShowDialog() != true) return;
            }
            using var gate = new Mutex(false, @"Local\CodexStart.Launch");
            bool acquired;
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            try { await Launch(args.FirstOrDefault() == "--maximized"); }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or COMException or InvalidOperationException)
        {
            StartupFailureDialog(fatal: true);
        }
        finally { Shutdown(); }
    }

    internal static void Report(string status)
    {
        try
        {
            Directory.CreateDirectory(Preferences.Folder);
            File.WriteAllText(Path.Combine(Preferences.Folder, "last-launch.txt"), status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task Launch(bool maximize)
    {
        while (true)
        {
            var existing = WindowsApp.Running();
            if (existing.Length > 0)
            {
                var window = WindowsApp.MainWindow(existing);
                if (window != 0)
                {
                    WindowsApp.Focus(window);
                    if (maximize) await MaximizeClient(window);
                }
                else await Task.Run(() => WindowsApp.Activate());
                return;
            }

            var preferences = Preferences.Load();
            if (maximize) preferences.WindowMode = StartupWindowMode.Maximized;
            nint target = 0;
            Task<LaunchResult>? ready = null;
            AnimationWindow? animation = null;
            if (preferences.HasCustomAnimation)
            {
                var stage = new AnimationWindow();
                animation = stage;
                await stage.Play(preferences,
                    token => Successful(ready = Prepare(preferences, value => target = value, token)),
                    CurrentClient,
                    message => MessageBox.Show(stage, message + " 点击确定后按当前窗口尺寸打开。", "ChatGPT",
                        MessageBoxButton.OK, MessageBoxImage.Information));
                if (stage.Canceled) return;
            }
            LaunchResult result;
            try { result = await (ready ?? Prepare(preferences, value => target = value)); }
            catch (OperationCanceledException) { return; }
            var status = animation?.HandoffIssue is { } issue ? result.Status + " " + issue : result.Status;
            if (animation?.PlaybackGeometry is { } geometry)
                status += " " + geometry + (animation.HandoffSucceeded ? "，与揭幕时的 Codex 窗口一致。" : "。");
            Report(status);
            if (result.Ready)
            {
                if (animation is null && preferences.WindowMode == StartupWindowMode.Maximized) await MaximizeClient(result.Window);
                if (result.Session is { } session)
                {
                    try { session.StartWorker(); }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                    {
                        Report(status + " 后台换肤进程启动失败，界面重载后需重新启动。");
                    }
                }
                if (animation?.HandoffSucceeded != true) WindowsApp.Focus(result.Window);
                return;
            }
            var selection = StartupFailureDialog();
            if (selection == 1) continue;
            if (selection == 2) await Task.Run(() => WindowsApp.Activate());
            return;

            nint CurrentClient()
            {
                if (ready is { IsCompletedSuccessfully: true } && ready.Result is { Ready: true, Window: var confirmed })
                    return confirmed;
                if (target == 0 || WindowsApp.VisibleFrame(target) is not null) return target;
                var replacement = WindowsApp.MainWindow(WindowsApp.Running());
                if (replacement != 0) target = replacement;
                return replacement;
            }
        }
    }

    private static async Task MaximizeClient(nint window)
    {
        WindowsApp.WorkAreas(out var area);
        if (window != 0 && area.Width > 0 && area.Height > 0)
            await WindowsApp.PlaceForReveal(() => window, area, StartupWindowMode.Maximized);
    }

    private static async Task<bool> Successful(Task<LaunchResult> launched) => (await launched).Ready;

    private static async Task<LaunchResult> Prepare(Preferences preferences, Action<nint> foundWindow,
        CancellationToken cancellationToken = default)
    {
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            var theme = await Task.Run(() => Theme.Build(preferences));
            var port = 0;
            var activation = 0;
            if (theme.HasAssets)
            {
                try
                {
                    port = Theme.ReservePort();
                    activation = await Task.Run(() => WindowsApp.Activate(
                        $"--remote-debugging-address=127.0.0.1 --remote-debugging-port={port} --user-data-dir=\"{Preferences.Profile}\""));
                }
                catch (Exception ex) when (ex is COMException or Win32Exception or System.Net.Sockets.SocketException)
                {
                    port = 0;
                }
            }
            if (activation == 0) await Task.Run(() => WindowsApp.Activate());

            var time = Stopwatch.StartNew();
            var lastProcessSeen = TimeSpan.Zero;
            var contentReadySince = TimeSpan.Zero;
            nint readyWindow = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var official = WindowsApp.Running();
                if (official.Length > 0) lastProcessSeen = time.Elapsed;
                else if (time.Elapsed - lastProcessSeen >= TimeSpan.FromSeconds(30))
                    return new(false, null, "30 秒内未发现运行中的官方客户端。");

                var mainWindow = WindowsApp.MainWindow(official);
                if (mainWindow != 0) foundWindow(mainWindow);
                var contentReady = mainWindow != 0 && await Task.Run(() => WindowsApp.ContentReady(mainWindow), cancellationToken)
                    .WaitAsync(cancellationToken);
                if (!contentReady) readyWindow = 0;
                else if (readyWindow != mainWindow)
                {
                    readyWindow = mainWindow;
                    contentReadySince = time.Elapsed;
                }
                else
                {
                    if (port == 0) return new(true, null, Status(theme), mainWindow);
                    var listener = ThemeSession.FromPort(port);
                    ThemeSession? session = listener is { } active && official.Contains(active.Pid) ? active : null;
                    if (session is { } current)
                    {
                        var result = await Theme.Apply(current, theme, cancellationToken);
                        if (result == ThemeResult.Applied)
                            return new(true, current, "背景已应用。" + AssetIssue(theme), mainWindow);
                        if (result == ThemeResult.VideoFailed)
                            return new(true, current, "视频背景无法播放，请在设置中重新选择 H.264 MP4。", mainWindow);
                    }
                    if (time.Elapsed - contentReadySince >= TimeSpan.FromSeconds(5))
                        return new(true, session, "官方窗口已打开，本次未能确认换肤。请检查 Store 版兼容性或在设置中检查背景文件。", mainWindow);
                }

                await Task.Delay(350, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or Win32Exception or InvalidOperationException)
        {
            return new(false, null, "Windows 未能打开官方 ChatGPT/Codex。");
        }
    }

    private static string Status(ThemePayload theme) =>
        (theme.HasAssets ? "官方窗口已打开，本次无法启用换肤。" : "官方窗口已打开。") + AssetIssue(theme);

    private static string AssetIssue(ThemePayload theme) =>
        (theme.MissingVideo ? " 视频背景文件无法读取，请在设置中重新选择。" : "")
        + (theme.MissingImages ? " 部分图片无法读取。" : "");

    private static int StartupFailureDialog(bool fatal = false)
    {
        var ink = new SolidColorBrush(Color.FromRgb(241, 242, 244));
        var dialog = new Window
        {
            Title = "ChatGPT", SizeToContent = SizeToContent.Height, Width = 440,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None, Foreground = ink, Background = new SolidColorBrush(Color.FromRgb(21, 22, 25))
        };
        dialog.SourceInitialized += (_, _) => WindowsApp.DarkFrame(dialog);
        var result = 0;
        var body = new StackPanel { Margin = new Thickness(24) };
        var title = new TextBlock { Text = "无法打开 ChatGPT", FontWeight = FontWeights.SemiBold, FontSize = 18 };
        title.MouseLeftButtonDown += (_, _) => dialog.DragMove();
        body.Children.Add(title);
        body.Children.Add(new TextBlock
        {
            Text = fatal ? "请检查官方 Windows 版 ChatGPT/Codex 是否已安装。你也可以从开始菜单打开。"
                : "暂未检测到可用的官方 ChatGPT/Codex 窗口。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 18)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        AddButton("关闭", 0, defaultAction: fatal);
        if (!fatal)
        {
            AddButton("正常打开", 2);
            AddButton("重试", 1, defaultAction: true);
        }
        dialog.KeyDown += (_, args) => { if (args.Key == Key.Escape) dialog.Close(); };
        body.Children.Add(buttons);
        dialog.Content = body;
        dialog.ShowDialog();
        return result;

        void AddButton(string label, int code, bool defaultAction = false)
        {
            var button = new Button
            {
                Content = label, Foreground = ink, BorderBrush = Brushes.Transparent,
                Background = new SolidColorBrush(Color.FromRgb(42, 44, 48)), Padding = new Thickness(13, 7, 13, 7),
                Margin = new Thickness(6, 0, 0, 0), IsDefault = defaultAction
            };
            button.Click += (_, _) => { result = code; dialog.Close(); };
            buttons.Children.Add(button);
        }
    }

    private sealed record LaunchResult(bool Ready, ThemeSession? Session, string Status, nint Window = 0);
}
