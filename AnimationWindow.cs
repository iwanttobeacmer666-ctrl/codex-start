using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexStart;

internal sealed class AnimationWindow : Window
{
    private readonly MediaElement _media = new()
    {
        LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.UniformToFill,
        ScrubbingEnabled = true, IsMuted = true, Visibility = Visibility.Hidden
    };
    private readonly Image _picture = new() { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
    private readonly Grid _layers = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly bool _preview;
    private readonly CancellationTokenSource _closed = new();
    private TaskCompletionSource<double>? _mediaOpened;
    private bool _mediaEnded;
    private bool _mediaFailed;
    private Func<Task>? _presentFirstFrame;
    private readonly TaskCompletionSource<bool> _previewReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? MediaIssue { get; private set; }
    public string? HandoffIssue { get; private set; }
    public string? PlaybackGeometry { get; private set; }
    public bool HandoffSucceeded { get; private set; }
    public bool Canceled { get; private set; }

    public AnimationWindow(bool preview = false)
    {
        _preview = preview;
        Title = preview ? "ChatGPT · 启动动画预览" : "ChatGPT";
        Background = Brushes.Transparent;
        Width = Math.Min(preview ? 810 : 1120, SystemParameters.WorkArea.Width * .90);
        Height = Math.Min(preview ? 525 : 740, SystemParameters.WorkArea.Height * .90);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = preview;
        Topmost = !preview;
        SourceInitialized += (_, _) => WindowsApp.DarkFrame(this);
        _layers.Opacity = preview ? 1 : 0;
        _layers.Children.Add(_picture);
        _layers.Children.Add(_media);
        Content = _layers;

        _media.MediaOpened += (_, _) => _mediaOpened?.TrySetResult(
            _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan.TotalSeconds : 0);
        _media.MediaEnded += (_, _) => _mediaEnded = true;
        _media.MediaFailed += (_, args) =>
        {
            _mediaFailed = true;
            MediaIssue = $"视频无法播放（0x{args.ErrorException.HResult:X8}）。";
            _mediaOpened?.TrySetResult(0);
        };
        Closed += (_, _) => { _closed.Cancel(); _media.Close(); };
        KeyDown += (_, args) => { if (args.Key == Key.Escape) Close(); };
        if (preview)
        {
            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
            };
            var ready = new Button { Content = "模拟就绪", Margin = new Thickness(0, 0, 8, 0) };
            var close = new Button { Content = "关闭预览" };
            ready.Click += (_, _) => { ready.Visibility = Visibility.Collapsed; _previewReady.TrySetResult(true); };
            close.Click += (_, _) => Close();
            controls.Children.Add(ready);
            controls.Children.Add(close);
            _layers.Children.Add(controls);
            Loaded += (_, _) =>
            {
                if (Owner?.TryFindResource("StudioButton") is Style style) ready.Style = close.Style = style;
            };
        }
    }

    public async Task Play(Preferences preferences, Func<CancellationToken, Task<bool>>? start = null,
        Func<nint>? client = null, Action<string>? handoffFailed = null)
    {
        var animated = preferences.HasCustomAnimation;
        WindowsApp.Rect? startupBounds = null;
        TaskCompletionSource<Task<bool>>? started = null;
        Task<WindowsApp.WindowFrame?>? arranging = null;
        try
        {
            if (_preview)
            {
                var workArea = SystemParameters.WorkArea;
                var ratio = preferences.WindowMode == StartupWindowMode.Maximized ? workArea.Width / workArea.Height : preferences.VideoAspectRatio();
                var width = Math.Min(Width, Height * ratio);
                Height = Math.Min(Height, Width / ratio);
                Width = width;
                Show();
            }
            else if (animated && client is not null && start is not null)
            {
                WindowsApp.WorkAreas(out var area);
                if (area.Width <= 0 || area.Height <= 0)
                {
                    await start(_closed.Token);
                    return;
                }
                var bounds = preferences.WindowMode == StartupWindowMode.Maximized ? area : area.Fit(preferences.VideoAspectRatio());
                WindowsApp.ShowAt(this, bounds);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (!WindowsApp.OverlayMatches(this, bounds))
                {
                    HandoffIssue = "未能设置动画窗口尺寸，本次直接打开 Codex。";
                    await start(_closed.Token);
                    return;
                }
                startupBounds = bounds;
                started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _presentFirstFrame = async () =>
                {
                    await FadeIn();
                    StartClient();
                    arranging ??= ArrangeClient(client, started.Task.Unwrap(), bounds, preferences.WindowMode);
                    PlaybackGeometry ??= $"播放区域 {bounds.Width}×{bounds.Height} 像素，左上角 ({bounds.Left}, {bounds.Top})";
                };
            }

            var ready = started is null ? start?.Invoke(_closed.Token) ?? _previewReady.Task : ReadyForReveal(started.Task.Unwrap());
            if (animated)
            {
                if (preferences.Animation == AnimationMode.Timeline) await PlayTimeline(preferences, ready);
                else await PlayClips(preferences, ready, StartClient);
            }
            StartClient();
            if (await WaitUntilReady(ready) && IsVisible && _presentFirstFrame is null)
            {
                if (startupBounds is { } bounds && client is not null)
                {
                    HandoffSucceeded = await RevealClient(bounds, preferences.WindowMode, client);
                    _closed.Token.ThrowIfCancellationRequested();
                    if (!HandoffSucceeded)
                    {
                        HandoffIssue = "Codex 的窗口未能对齐动画。";
                        handoffFailed?.Invoke(HandoffIssue);
                    }
                }
                else await Dissolve();
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { Canceled = true; }
        finally
        {
            if (!_closed.IsCancellationRequested) Close();
            _closed.Dispose();
        }

        void StartClient()
        {
            if (started is not null && !started.Task.IsCompleted) started.SetResult(start!(_closed.Token));
        }

        async Task<bool> ReadyForReveal(Task<bool> launched)
        {
            if (!await WaitUntilReady(launched)) return false;
            if (startupBounds is { } bounds && client is not null)
            {
                arranging ??= ArrangeClient(client, launched, bounds, preferences.WindowMode);
                if (await arranging is { } frame && WindowsApp.VisibleFrame(client()) == frame && await AlignOverlay(frame.Bounds))
                    startupBounds = frame.Bounds;
            }
            return true;
        }
    }

    private async Task PlayTimeline(Preferences settings, Task<bool> ready)
    {
        var duration = await OpenVideo(settings.Timeline);
        if (!settings.HasValidTimeline(duration))
        {
            MediaIssue ??= $"两个标记必须在可播放的视频时长内（读取到 {duration:0.00} 秒）。";
            HideVideo();
            return;
        }
        if (!await PlayRange(0, settings.WaitFrom)) { HideVideo(); return; }
        while (!ready.IsCompleted)
        {
            if (!await PlayRange(settings.WaitFrom, settings.RevealFrom))
            {
                HideVideo();
                return;
            }
        }
        if (await ready && !await PlayRange(settings.RevealFrom, duration)) HideVideo();
    }

    private async Task PlayClips(Preferences settings, Task<bool> ready, Action startClient)
    {
        var duration = await OpenVideo(settings.Opening);
        if (duration > 0)
        {
            if (await PlayRange(0, duration)) _media.Position = TimeSpan.FromSeconds(Math.Max(0, duration - .06));
            else HideVideo();
        }
        if (!ready.IsCompleted && !string.IsNullOrWhiteSpace(settings.Hold))
        {
            if (Preferences.IsPicture(settings.Hold))
            {
                if (ShowPicture(settings.Hold)) await PresentFirstFrame();
                else MediaIssue ??= "等待照片无法读取。";
            }
            else if (await OpenVideo(settings.Hold, ready) is > 0 and var waitDuration)
            {
                while (!ready.IsCompleted)
                {
                    if (await PlayRange(0, waitDuration, ready)) continue;
                    HideVideo();
                    break;
                }
            }
        }
        startClient();
        if (!await WaitUntilReady(ready)) return;
        duration = await OpenVideo(settings.Reveal);
        if (duration > 0 && !await PlayRange(0, duration)) HideVideo();
    }

    private async Task<double> OpenVideo(string path, Task? interrupt = null)
    {
        if (!Preferences.HasVideo(path))
        {
            if (!string.IsNullOrWhiteSpace(path)) MediaIssue = "找不到所选视频。";
            return 0;
        }
        if (interrupt?.IsCompleted == true) return 0;
        if (!IsVisible) Show();
        _picture.Visibility = Visibility.Collapsed;
        _mediaOpened = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediaFailed = false;
        _media.Close();
        _media.Source = new Uri(path, UriKind.Absolute);
        _media.Visibility = Visibility.Visible;
        _media.Play();
        _media.Pause();
        var timeout = Task.Delay(TimeSpan.FromSeconds(4), _closed.Token);
        var opened = _mediaOpened.Task;
        var finished = await Task.WhenAny(opened, timeout, interrupt ?? timeout);
        _closed.Token.ThrowIfCancellationRequested();
        _mediaOpened = null;
        if (finished == opened)
        {
            var reported = await opened;
            var exact = _mediaFailed ? 0 : Mp4.Duration(path);
            var duration = exact > 0 ? exact : reported;
            if (duration > 0) return duration;
        }
        if (finished == timeout) MediaIssue = "等待视频解码超时。";
        HideVideo();
        return 0;
    }

    private async Task<bool> PlayRange(double start, double end, Task? interrupt = null)
    {
        _mediaEnded = false;
        _media.Position = TimeSpan.FromSeconds(start);
        _media.Play();
        var clock = Stopwatch.StartNew();
        while (!_mediaEnded && !_mediaFailed && _media.Position.TotalSeconds < end - .015 && !(interrupt?.IsCompleted ?? false))
        {
            if (_presentFirstFrame is not null && _media.Position.TotalSeconds > start + .015) await PresentFirstFrame();
            if (clock.Elapsed.TotalSeconds > end - start + 4)
            {
                _media.Pause();
                MediaIssue = "视频停止推进。";
                return false;
            }
            await Task.Delay(20, _closed.Token);
        }
        _media.Pause();
        return !_mediaFailed;
    }

    private bool ShowPicture(string path)
    {
        if (!Preferences.IsLocalFile(path) || !File.Exists(path)) return false;
        try
        {
            using var bytes = File.OpenRead(path);
            _picture.Source = BitmapFrame.Create(bytes, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            if (!IsVisible) Show();
            _media.Close();
            _media.Visibility = Visibility.Hidden;
            _picture.Visibility = Visibility.Visible;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { return false; }
    }

    private void HideVideo()
    {
        _media.Close();
        _media.Visibility = Visibility.Hidden;
        _picture.Visibility = Visibility.Collapsed;
    }

    private Task<bool> WaitUntilReady(Task<bool> ready) => ready.WaitAsync(_closed.Token);

    private async Task PresentFirstFrame()
    {
        if (_presentFirstFrame is not { } present) return;
        _presentFirstFrame = null;
        await present();
    }

    private async Task FadeIn()
    {
        const double duration = 160;
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < duration)
        {
            var progress = clock.ElapsedMilliseconds / duration;
            _layers.Opacity = progress * (2 - progress);
            await Task.Delay(16, _closed.Token);
        }
        _layers.Opacity = 1;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        WindowsApp.FlushComposition();
    }

    private async Task<WindowsApp.WindowFrame?> ArrangeClient(Func<nint> client, Task<bool> ready, WindowsApp.Rect bounds, StartupWindowMode mode)
    {
        var token = _closed.Token;
        while (client() == 0 && !ready.IsCompleted)
        {
            await Task.WhenAny(ready, Task.Delay(40, token));
            token.ThrowIfCancellationRequested();
        }
        return client() == 0 ? null : await WindowsApp.PlaceForReveal(client, bounds, mode);
    }

    private async Task<bool> RevealClient(WindowsApp.Rect bounds, StartupWindowMode mode, Func<nint> client)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            WindowsApp.Focus(client());
            var settled = await WindowsApp.PlaceForReveal(client, bounds, mode);
            if (settled is { } frame && await AlignOverlay(frame.Bounds) && await Dissolve(
                () => WindowsApp.VisibleFrame(client()) == frame && WindowsApp.OverlayMatches(this, frame.Bounds))) return true;
        }
        return false;
    }

    private async Task<bool> AlignOverlay(WindowsApp.Rect bounds)
    {
        if (!WindowsApp.OverlayMatches(this, bounds))
        {
            WindowsApp.MoveOverlay(this, bounds);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!WindowsApp.OverlayMatches(this, bounds)) return false;
        }
        PlaybackGeometry = $"播放区域 {bounds.Width}×{bounds.Height} 像素，左上角 ({bounds.Left}, {bounds.Top})";
        return true;
    }

    private async Task<bool> Dissolve(Func<bool>? matches = null)
    {
        const double duration = .28;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < duration)
        {
            if (matches?.Invoke() == false) break;
            var remaining = Math.Clamp(1 - clock.Elapsed.TotalSeconds / duration, 0, 1);
            _layers.Opacity = remaining * remaining;
            await Task.Delay(16, _closed.Token);
        }
        if (matches?.Invoke() != false && clock.Elapsed.TotalSeconds >= duration) return true;
        _layers.Opacity = 1;
        return false;
    }
}
