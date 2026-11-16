using System.IO;
using System.Text.Json;

namespace CodexStart;

internal enum AnimationMode { Clips = 2, Timeline = 3, Off = 4 }
internal enum StartupWindowMode { VideoAspect, Maximized }
internal enum Region { Window, Sidebar, Conversation, Composer, RightPanel }
internal enum WindowBackgroundKind { Picture, Video }

internal sealed class Background
{
    public string File { get; set; } = "";
    public int Strength { get; set; } = 65;
    public int Veil { get; set; } = 45;
    public bool Inherit { get; set; } = true;
}

internal sealed class Preferences
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexStart");
    public static readonly string Profile = Path.Combine(Folder, "CodexProfile");
    public static readonly string SettingsFile = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AnimationMode Animation { get; set; } = AnimationMode.Clips;
    public StartupWindowMode WindowMode { get; set; }
    public string Opening { get; set; } = "";
    public string Hold { get; set; } = "";
    public string Reveal { get; set; } = "";
    public string Timeline { get; set; } = "";
    public double WaitFrom { get; set; } = 3;
    public double RevealFrom { get; set; } = 6;
    public bool WallpaperEnabled { get; set; }
    public WindowBackgroundKind WindowBackground { get; set; }
    public string WallpaperVideoFile { get; set; } = "";
    public bool WallpaperVideoSound { get; set; }
    public Dictionary<Region, Background> Backgrounds { get; set; } = [];

    public Background For(Region region)
    {
        if (!Backgrounds.TryGetValue(region, out var background) || background is null)
        {
            background = new Background { Inherit = region != Region.Window };
            Backgrounds[region] = background;
        }
        return background;
    }

    public bool WantsWallpaper => WallpaperEnabled && Enum.GetValues<Region>().Any(region =>
        region == Region.Window && WindowBackground == WindowBackgroundKind.Video
            ? !string.IsNullOrWhiteSpace(WallpaperVideoFile)
            : (region == Region.Window || !For(region).Inherit) && !string.IsNullOrWhiteSpace(For(region).File));

    public static Preferences Load()
    {
        try
        {
            using var file = new FileStream(SettingsFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var result = JsonSerializer.Deserialize<Preferences>(file) ?? new Preferences();
            result.Backgrounds ??= [];
            if (!Enum.IsDefined(result.Animation)) result.Animation = AnimationMode.Off;
            if (!Enum.IsDefined(result.WindowMode)) result.WindowMode = StartupWindowMode.VideoAspect;
            if (!Enum.IsDefined(result.WindowBackground)) result.WindowBackground = WindowBackgroundKind.Picture;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Preferences();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temporary = Path.Combine(Folder, Guid.NewGuid() + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporary, SettingsFile, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public bool HasValidTimeline(double duration = double.PositiveInfinity) =>
        double.IsFinite(WaitFrom) && double.IsFinite(RevealFrom)
        && WaitFrom >= .15 && RevealFrom - WaitFrom >= .15 && duration - RevealFrom >= .15;

    public bool HasCustomAnimation => Animation switch
    {
        AnimationMode.Clips => HasVideo(Opening) || HasVideo(Hold) || (IsPicture(Hold) && LocalAssetExists(Hold)) || HasVideo(Reveal),
        AnimationMode.Timeline => HasVideo(Timeline) && HasValidTimeline(),
        _ => false
    };

    public double VideoAspectRatio()
    {
        var videos = Animation == AnimationMode.Timeline ? new[] { Timeline } : new[] { Opening, Hold, Reveal };
        foreach (var path in videos)
        {
            var ratio = Mp4.AspectRatio(path);
            if (ratio > 0) return ratio;
        }
        return 16d / 9;
    }

    public static bool HasVideo(string? file) => LocalAssetExists(file) && string.Equals(Path.GetExtension(file), ".mp4", StringComparison.OrdinalIgnoreCase);

    public static bool LocalAssetExists(string? file) => IsLocalFile(file) && File.Exists(file);

    public static bool IsPicture(string? file) => Path.GetExtension(file)?.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";

    public static bool IsLocalFile(string? file) => file is { Length: >= 4 }
        && char.IsAsciiLetter(file[0]) && file[1] == ':' && file[2] is '\\' or '/' && Path.IsPathFullyQualified(file);
}
