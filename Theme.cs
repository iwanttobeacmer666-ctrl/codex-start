using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace CodexStart;

internal readonly record struct ThemeSession(int Pid, long Start, int Port)
{
    public static ThemeSession? FromPort(int port)
    {
        var pid = WindowsApp.LoopbackListenerOwner(port);
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            return WindowsApp.IsOfficial(process) ? new(pid, process.StartTime.ToUniversalTime().Ticks, port) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    public void StartWorker()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("找不到换肤进程的文件。");
        using var worker = Process.Start(new ProcessStartInfo(executable, $"--watch {Pid} {Start} {Port}")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
}

internal enum ThemeResult { Unavailable, VideoFailed, Applied }

internal sealed record ThemePayload(string Json, string Revision, string? VideoFile, bool HasAssets,
    bool MissingImages, bool MissingVideo);

internal static class Theme
{
    private const string Marker = "__codexStartWallpaper";
    private const string Upload = "__codexStartVideoUpload";
    private const int MaxVideoBytes = 128 * 1024 * 1024;
    private const string ShellSelector = "[data-app-shell-frame], [data-app-shell-active-page], [data-testid=app-shell], main";
    private const string State = $"[globalThis.{Marker}?.revision || '', globalThis.{Marker}?.rendered === true, globalThis.{Marker}?.videoFailed === true]";
    private static readonly JsonSerializerOptions PayloadJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromMilliseconds(800)
    }) { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 1024 * 1024 };
    private static readonly HttpMessageInvoker SocketHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(1)
    });
    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexStart.wallpaper.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static ThemePayload Build(Preferences preferences)
    {
        var images = new Dictionary<string, string?>();
        var regions = new Dictionary<string, object>();
        var missingImages = false;
        var configuredVideo = SelectedVideo(preferences);
        var videoStamp = VideoStamp(configuredVideo);
        var missingVideo = configuredVideo is not null && CheckVideo(configuredVideo) is not null;
        var videoFile = missingVideo ? null : configuredVideo;
        if (preferences.WallpaperEnabled)
        {
            foreach (var region in Enum.GetValues<Region>())
            {
                var settings = preferences.For(region);
                string? image = null;
                bool inherit = region != Region.Window && settings.Inherit;
                bool videoMode = region == Region.Window && preferences.WindowBackground == WindowBackgroundKind.Video;
                if (!inherit && !videoMode && !string.IsNullOrWhiteSpace(settings.File))
                {
                    if (!images.TryGetValue(settings.File, out image))
                    {
                        image = LoadImage(settings.File);
                        images.Add(settings.File, image);
                    }
                    missingImages |= image is null;
                }
                regions[region.ToString()] = new
                {
                    Image = image,
                    Video = videoMode && videoFile is not null,
                    Sound = videoMode && preferences.WallpaperVideoSound,
                    Inherit = inherit,
                    Strength = Math.Clamp(settings.Strength, 0, 100),
                    Veil = Math.Clamp(settings.Veil, 0, 100)
                };
            }
        }
        var hasAssets = videoFile is not null || images.Values.Any(image => image is not null);
        var json = hasAssets ? JsonSerializer.Serialize(regions, PayloadJson) : "{}";
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{json}\n{videoFile}\n{videoStamp.Ticks}")));
        return new(json, revision, videoFile, hasAssets, missingImages, missingVideo);
    }

    private static string? SelectedVideo(Preferences preferences) => preferences.WallpaperEnabled
        && preferences.WindowBackground == WindowBackgroundKind.Video && !string.IsNullOrWhiteSpace(preferences.WallpaperVideoFile)
        ? preferences.WallpaperVideoFile : null;

    private static DateTime VideoStamp(string? file)
    {
        if (!Preferences.IsLocalFile(file)) return default;
        try { return File.GetLastWriteTimeUtc(file!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return default; }
    }

    internal static string? CheckVideo(string path)
    {
        try
        {
            if (!Preferences.HasVideo(path)) return "找不到本机 MP4 视频，请重新选择。";
            if (new FileInfo(path).Length > MaxVideoBytes) return "视频超过 128 MB，请选择较小的 MP4。";
            if (Mp4.AspectRatio(path) > 0) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return "无法读取视频，请重新选择 H.264 MP4。";
    }

    internal static string? LoadImage(string path)
    {
        if (!Preferences.IsPicture(path) || !Preferences.IsLocalFile(path)) return null;
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length > 32 * 1024 * 1024) return null;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = input;
            bitmap.DecodePixelWidth = 1600;
            bitmap.EndInit();
            if (bitmap.PixelHeight > 2400) return null;
            BitmapEncoder encoder;
            string type;
            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                encoder = new PngBitmapEncoder();
                type = "png";
            }
            else
            {
                encoder = new JpegBitmapEncoder { QualityLevel = 86 };
                type = "jpeg";
            }
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = new MemoryStream();
            encoder.Save(image);
            return "data:image/" + type + ";base64," + Convert.ToBase64String(image.GetBuffer(), 0, (int)image.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException or FormatException
            or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public static async Task<ThemeResult> Apply(ThemeSession session, ThemePayload theme, CancellationToken token)
    {
        if (ThemeSession.FromPort(session.Port) != session) return ThemeResult.Unavailable;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(theme.VideoFile is null ? 3 : 15));
        try
        {
            using var targets = JsonDocument.Parse(await Http.GetByteArrayAsync($"http://127.0.0.1:{session.Port}/json/list", limit.Token));
            if (targets.RootElement.ValueKind != JsonValueKind.Array) return ThemeResult.Unavailable;
            var result = ThemeResult.Unavailable;
            foreach (var target in targets.RootElement.EnumerateArray())
            {
                if (!TargetSocket(target, session.Port, out var uri)) continue;
                if (ThemeSession.FromPort(session.Port) != session) return ThemeResult.Unavailable;
                try
                {
                    var rendered = await ApplyTarget(uri!, theme, limit.Token);
                    if (rendered > result) result = rendered;
                }
                catch (WebSocketException) { }
            }
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            return ThemeResult.Unavailable;
        }
    }

    internal static bool TargetSocket(JsonElement target, int port, out Uri? socket)
    {
        socket = null;
        if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || !type.ValueEquals("page") || !target.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
            !url.GetString()!.StartsWith("app://", StringComparison.Ordinal) ||
            !target.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !target.TryGetProperty("webSocketDebuggerUrl", out var address) || address.ValueKind != JsonValueKind.String)
            return false;

        if (!Uri.TryCreate(address.GetString(), UriKind.Absolute, out var candidate) || candidate.Scheme != "ws" ||
            candidate.Host != "127.0.0.1" || candidate.Port != port || candidate.UserInfo.Length != 0 ||
            candidate.Query.Length != 0 || candidate.Fragment.Length != 0 ||
            candidate.AbsolutePath != "/devtools/page/" + id.GetString()) return false;

        socket = candidate;
        return true;
    }

    private static async Task<ThemeResult> ApplyTarget(Uri uri, ThemePayload theme, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri, SocketHttp, token);
        var rendered = await Evaluate(socket, 1, State, token);
        if (ReadState(rendered, theme.Revision) is { } current) return current;

        if (theme.VideoFile is { } videoFile && !await UploadVideo(socket, videoFile, token))
            return ThemeResult.Unavailable;
        var script = $$"""
            (async () => {
                const chunks = globalThis.{{Upload}};
                delete globalThis.{{Upload}};
                const clip = chunks ? new Blob(chunks, {type:'video/mp4'}) : null;
                await ({{Template.Value}})({{theme.Json}},'{{theme.Revision}}',clip);
                return {{State}};
            })()
            """;
        var result = await Evaluate(socket, 4, script, token, userGesture: theme.VideoFile is not null);
        return ReadState(result, theme.Revision) ?? ThemeResult.Unavailable;
    }

    private static ThemeResult? ReadState(JsonElement? value, string revision)
    {
        if (value is not { ValueKind: JsonValueKind.Array } state || state.GetArrayLength() != 3
            || state[0].ValueKind != JsonValueKind.String || !state[0].ValueEquals(revision)) return null;
        if (state[2].ValueKind == JsonValueKind.True) return ThemeResult.VideoFailed;
        return state[1].ValueKind == JsonValueKind.True ? ThemeResult.Applied : ThemeResult.Unavailable;
    }

    private static async Task<bool> UploadVideo(ClientWebSocket socket, string path, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is <= 0 or > MaxVideoBytes) return false;
        var ready = await Evaluate(socket, 2, $$"""
            (() => {
                if (location.protocol !== 'app:' || !document.querySelector('{{ShellSelector}}')) return false;
                const chunks = globalThis.{{Upload}} = [];
                setTimeout(() => { if (globalThis.{{Upload}} === chunks) delete globalThis.{{Upload}}; }, 20000);
                return true;
            })()
            """, token);
        if (ready is not { ValueKind: JsonValueKind.True }) return false;

        var bytes = new byte[512 * 1024];
        var remaining = file.Length;
        while (remaining > 0)
        {
            var length = (int)Math.Min(remaining, bytes.Length);
            await file.ReadExactlyAsync(bytes.AsMemory(0, length), token);
            var base64 = Convert.ToBase64String(bytes, 0, length);
            var added = await Evaluate(socket, 3, $$"""
                (() => {
                    const chunks = globalThis.{{Upload}};
                    if (!chunks) return false;
                    const decoded = atob('{{base64}}');
                    const bytes = new Uint8Array(decoded.length);
                    for (let i = 0; i < bytes.length; i++) bytes[i] = decoded.charCodeAt(i);
                    chunks.push(bytes);
                    return true;
                })()
                """, token);
            if (added is not { ValueKind: JsonValueKind.True }) return false;
            remaining -= length;
        }
        return true;
    }

    private static async Task<JsonElement?> Evaluate(ClientWebSocket socket, int id, string expression, CancellationToken token,
        bool userGesture = false)
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id, method = "Runtime.evaluate", @params = new { expression, returnByValue = true, awaitPromise = true, userGesture }
        });
        await socket.SendAsync(request, WebSocketMessageType.Text, true, token);
        var buffer = new byte[4096];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult packet;
            do
            {
                packet = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (packet.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, packet.Count);
                if (message.Length > 65536) return null;
            } while (!packet.EndOfMessage);

            using var response = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            if (!response.RootElement.TryGetProperty("id", out var reply) || !reply.TryGetInt32(out var replyId) || replyId != id)
                continue;
            return response.RootElement.TryGetProperty("result", out var outer)
                && outer.TryGetProperty("result", out var inner) && inner.TryGetProperty("value", out var value)
                ? value.Clone() : null;
        }
    }

    public static async Task Watch(ThemeSession session)
    {
        if (ThemeSession.FromPort(session.Port) != session) return;
        using var process = Process.GetProcessById(session.Pid);
        DateTime? stamp = null, videoStamp = null;
        Preferences preferences = new();
        ThemePayload theme = Build(preferences);
        string? videoIssue = null;
        await WatchUntilExit(process, async token =>
        {
            var current = File.GetLastWriteTimeUtc(Preferences.SettingsFile);
            if (stamp != current)
            {
                preferences = Preferences.Load();
                videoStamp = null;
                stamp = current;
            }
            var currentVideo = VideoStamp(SelectedVideo(preferences));
            if (currentVideo != videoStamp)
            {
                theme = Build(preferences);
                videoStamp = currentVideo;
            }
            var result = await Apply(session, theme, token);
            var issue = theme.MissingVideo ? "视频背景文件无法读取，请在设置中重新选择。"
                : result == ThemeResult.VideoFailed ? "视频背景无法播放，请在设置中重新选择 H.264 MP4。" : null;
            if (issue is not null && issue != videoIssue) App.Report(issue);
            else if (issue is null && videoIssue is not null && result == ThemeResult.Applied) App.Report("背景已应用。");
            if (issue is not null || result == ThemeResult.Applied) videoIssue = issue;
        });
    }

    internal static async Task WatchUntilExit(Process process, Func<CancellationToken, Task> refresh)
    {
        using var cancel = new CancellationTokenSource();
        var exit = StopAtExit();
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await refresh(cancel.Token);
                await Task.Delay(1250, cancel.Token);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        finally { await cancel.CancelAsync(); await exit; }

        async Task StopAtExit()
        {
            try { await process.WaitForExitAsync(cancel.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await cancel.CancelAsync();
        }
    }
}
