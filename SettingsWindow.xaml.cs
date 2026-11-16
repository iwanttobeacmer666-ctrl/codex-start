using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CodexStart;

public partial class SettingsWindow : Window
{
    private readonly Preferences _preferences;
    private bool _updating = true;
    private bool _previewing;
    private double? _duration;
    private Region _region;
    private readonly Button[] _regionButtons;
    private readonly Button[] _modeButtons;
    private readonly Border[] _previewRegions;
    private readonly Dictionary<string, ImageSource?> _previewImages = [];
    private static readonly Color[] PreviewSurfaceColors =
    [Color.FromRgb(16, 17, 19), Color.FromRgb(31, 34, 38), Color.FromRgb(24, 26, 29),
        Color.FromRgb(40, 43, 48), Color.FromRgb(31, 34, 38)];

    internal SettingsWindow(Preferences preferences)
    {
        _preferences = preferences;
        InitializeComponent();
        _regionButtons = [RegionWindow, RegionSidebar, RegionConversation, RegionComposer, RegionRight];
        _modeButtons = [ModeClips, ModeTimeline, ModeOff];
        _previewRegions = [PreviewFrame, PreviewSidebar, PreviewConversation, PreviewComposer, PreviewRight];
        SourceInitialized += (_, _) => WindowsApp.DarkFrame(this);
        PreviewFrame.SizeChanged += (_, _) => DrawLayout();
        WallpaperEnabled.IsChecked = preferences.WallpaperEnabled;
        MaximizedWindow.IsChecked = preferences.WindowMode == StartupWindowMode.Maximized;
        VideoAspectWindow.IsChecked = MaximizedWindow.IsChecked != true;
        if (!Enum.IsDefined(preferences.Animation)) preferences.Animation = AnimationMode.Off;
        WaitMarker.Maximum = RevealMarker.Maximum = Math.Max(60, Math.Clamp(preferences.RevealFrom, 0, 3600));
        WaitMarker.Value = double.IsFinite(preferences.WaitFrom) ? preferences.WaitFrom : 3;
        RevealMarker.Value = double.IsFinite(preferences.RevealFrom) ? preferences.RevealFrom : 6;
        _updating = false;
        ReadRegion();
        UpdateMode();
        UpdateLabels();
        ShowPage(animation: false);
        try { Status.Text = File.ReadAllText(Path.Combine(Preferences.Folder, "last-launch.txt")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void MinimizeWindow(object sender, RoutedEventArgs args) => SystemCommands.MinimizeWindow(this);
    private void ToggleWindowSize(object sender, RoutedEventArgs args)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindow(object sender, RoutedEventArgs args) => Close();

    private void OpenBackgrounds(object sender, RoutedEventArgs args) => ShowPage(animation: false);
    private void OpenAnimation(object sender, RoutedEventArgs args) => ShowPage(animation: true);

    private void ShowPage(bool animation)
    {
        BackgroundPage.Visibility = animation ? Visibility.Collapsed : Visibility.Visible;
        AnimationPage.Visibility = animation ? Visibility.Visible : Visibility.Collapsed;
        BackgroundTab.Style = ButtonStyle(animation ? "Ghost" : "Selected");
        AnimationTab.Style = ButtonStyle(animation ? "Selected" : "Ghost");
        if (animation && _preferences.Animation == AnimationMode.Timeline) ProbeDuration();
    }

    private Style ButtonStyle(string name) => (Style)Resources[name];

    private void SelectRegion(object sender, RoutedEventArgs args)
    {
        if (!Enum.TryParse((string)((Button)sender).Tag, out Region selected)) return;
        _region = selected;
        ReadRegion();
    }

    private void ReadRegion()
    {
        if (_updating) return;
        var background = _preferences.For(_region);
        _updating = true;
        Inherit.Visibility = _region == Region.Window ? Visibility.Collapsed : Visibility.Visible;
        Inherit.IsChecked = _region != Region.Window && background.Inherit;
        Strength.Value = background.Strength;
        Veil.Value = background.Veil;
        _updating = false;
        RegionTitle.Text = (string)_regionButtons[(int)_region].Content;
        for (var index = 0; index < _regionButtons.Length; index++)
            _regionButtons[index].Style = ButtonStyle(index == (int)_region ? "ChipActive" : "Chip");
        RefreshBackground();
    }

    private void ChangeAppearance(object sender, RoutedEventArgs args)
    {
        _preferences.For(_region).Inherit = Inherit.IsChecked == true;
        RefreshBackground();
    }

    private bool UsesWindowVideo => _preferences.WindowBackground == WindowBackgroundKind.Video;

    private void SelectBackgroundKind(object sender, RoutedEventArgs args)
    {
        _preferences.WindowBackground = VideoBackground.IsChecked == true ? WindowBackgroundKind.Video : WindowBackgroundKind.Picture;
        RefreshBackground();
    }

    private void ChangeVideoSound(object sender, RoutedEventArgs args) =>
        _preferences.WallpaperVideoSound = VideoSound.IsChecked == true;

    private void ChooseWallpaper(object sender, RoutedEventArgs args)
    {
        var video = _region == Region.Window && UsesWindowVideo;
        var selection = PickFile(video ? "MP4 视频 (*.mp4)|*.mp4" : "图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg");
        if (selection is null) return;
        if (video)
        {
            if (Theme.CheckVideo(selection) is { } issue)
            {
                Status.Text = issue;
                return;
            }
            _preferences.WallpaperVideoFile = selection;
            RefreshBackground();
            return;
        }
        _preferences.For(_region).File = selection;
        _previewImages.Remove(selection);
        RefreshBackground();
        if (PreviewImage(selection) is null) Status.Text = "无法读取所选照片。支持本机 PNG 和 JPEG。";
    }

    private void ClearWallpaper(object sender, RoutedEventArgs args)
    {
        if (_region == Region.Window && UsesWindowVideo) _preferences.WallpaperVideoFile = "";
        else _preferences.For(_region).File = "";
        RefreshBackground();
    }

    private void Slide(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (_updating) return;
        var background = _preferences.For(_region);
        background.Strength = (int)Math.Round(Strength.Value);
        background.Veil = (int)Math.Round(Veil.Value);
        UpdateTint();
    }

    private void RefreshBackground()
    {
        var background = _preferences.For(_region);
        var wholeWindow = _region == Region.Window;
        var inherit = !wholeWindow && background.Inherit;
        var video = (wholeWindow || inherit) && UsesWindowVideo;
        var file = video ? _preferences.WallpaperVideoFile : inherit ? _preferences.For(Region.Window).File : background.File;
        var hasFile = !string.IsNullOrWhiteSpace(file);
        BackgroundKind.Visibility = wholeWindow ? Visibility.Visible : Visibility.Collapsed;
        PictureBackground.IsChecked = !UsesWindowVideo;
        VideoBackground.IsChecked = UsesWindowVideo;
        PickAsset.Content = video ? "选择视频" : "选择图片";
        PickAsset.IsEnabled = !inherit;
        RemoveAsset.IsEnabled = !inherit && hasFile;
        AssetName.Text = hasFile ? Path.GetFileName(file) : video ? "未选择视频" : "未选择图片";
        VideoSound.Visibility = wholeWindow && video ? Visibility.Visible : Visibility.Collapsed;
        VideoSound.IsChecked = _preferences.WallpaperVideoSound;
        VideoHint.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        VideoHint.Text = video && hasFile && Theme.CheckVideo(file) is { } issue ? issue
            : inherit ? "视频强度和遮罩使用整窗设置。" : "使用 H.264 MP4，保存后在主窗口播放。";
        Strength.IsEnabled = Veil.IsEnabled = !(video && inherit);
        UpdateTint();
    }

    private void UpdateTint()
    {
        StrengthLabel.Text = $"{Strength.Value:0}%";
        VeilLabel.Text = $"{Veil.Value:0}%";
        DrawLayout();
    }

    private ImageSource? PreviewImage(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        if (_previewImages.TryGetValue(file, out var image)) return image;
        var data = Theme.LoadImage(file);
        if (data is not null)
        {
            using var bytes = new MemoryStream(Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]));
            image = BitmapFrame.Create(bytes, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
        }
        _previewImages[file] = image;
        return image;
    }

    private void DrawLayout()
    {
        var whole = UsesWindowVideo ? null : PreviewImage(_preferences.For(Region.Window).File);
        for (var index = 0; index < _previewRegions.Length; index++)
        {
            var region = (Region)index;
            var surface = _previewRegions[index];
            var settings = _preferences.For(region);
            var opacity = Math.Clamp(settings.Strength, 0, 100) / 100d * (1 - Math.Clamp(settings.Veil, 0, 100) / 100d);
            var tint = PreviewSurfaceColors[index];
            var inherits = index > 0 && settings.Inherit && whole is not null;
            if (inherits) tint.A = (byte)(255 * (1 - opacity));
            var solid = new SolidColorBrush(tint);
            var usesVideo = UsesWindowVideo && (index == 0 || settings.Inherit);
            var image = inherits || usesVideo ? null : PreviewImage(settings.File);
            var bounds = new Rect(0, 0, Math.Max(1, surface.ActualWidth), Math.Max(1, surface.ActualHeight));
            if (image is not null)
            {
                var drawing = new DrawingGroup();
                var area = new RectangleGeometry(bounds);
                drawing.Children.Add(new GeometryDrawing(solid, null, area));
                drawing.Children.Add(new GeometryDrawing(new ImageBrush(image) { Stretch = Stretch.UniformToFill, Opacity = opacity }, null, area));
                surface.Background = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
            }
            else surface.Background = solid;
            surface.BorderBrush = region == _region ? (Brush)Resources["Accent"] : (Brush)Resources["Rule"];
        }
    }

    private void SelectMode(object sender, RoutedEventArgs args)
    {
        if (!Enum.TryParse((string)((Button)sender).Tag, out AnimationMode mode)) return;
        _preferences.Animation = mode;
        UpdateMode();
    }

    private void UpdateMode()
    {
        var mode = _preferences.Animation;
        Clips.Visibility = mode == AnimationMode.Clips ? Visibility.Visible : Visibility.Collapsed;
        Timeline.Visibility = mode == AnimationMode.Timeline ? Visibility.Visible : Visibility.Collapsed;
        AnimationOff.Visibility = mode == AnimationMode.Off ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in _modeButtons)
            button.Style = ButtonStyle((string)button.Tag == mode.ToString() ? "ChipActive" : "Chip");
        UpdateFileNames();
        if (mode == AnimationMode.Timeline && AnimationPage.IsVisible) ProbeDuration();
    }

    private void ChooseClip(object sender, RoutedEventArgs args)
    {
        var slot = ((Button)sender).Tag as string;
        var file = PickFile(slot == "Hold" ? "视频或照片|*.mp4;*.png;*.jpg;*.jpeg" : "MP4 视频 (*.mp4)|*.mp4");
        if (file is null) return;
        switch (slot)
        {
            case "Intro": _preferences.Opening = file; break;
            case "Hold": _preferences.Hold = file; break;
            case "Reveal": _preferences.Reveal = file; break;
            case "Timeline": _preferences.Timeline = file; ProbeDuration(resetMarkers: true); break;
        }
        UpdateFileNames();
    }

    private void RemoveClip(object sender, RoutedEventArgs args)
    {
        switch (((Button)sender).Tag as string)
        {
            case "Intro": _preferences.Opening = ""; break;
            case "Hold": _preferences.Hold = ""; break;
            case "Reveal": _preferences.Reveal = ""; break;
            case "Timeline": _preferences.Timeline = ""; ProbeDuration(); break;
        }
        UpdateFileNames();
    }

    private void ProbeDuration(bool resetMarkers = false)
    {
        var seconds = Mp4.Duration(_preferences.Timeline);
        _duration = seconds > 0 ? seconds : null;
        WaitMarker.IsEnabled = RevealMarker.IsEnabled = _duration is not null;
        if (_duration is null) { UpdateLabels(); return; }
        WaitMarker.Maximum = RevealMarker.Maximum = seconds;
        if (resetMarkers && !_preferences.HasValidTimeline(seconds))
        {
            WaitMarker.Value = seconds / 3;
            RevealMarker.Value = seconds * 2 / 3;
        }
        UpdateLabels();
    }

    private void UpdateFileNames()
    {
        static string Display(string path) => string.IsNullOrWhiteSpace(path) ? "未选择素材" : Path.GetFileName(path);
        IntroFile.Text = Display(_preferences.Opening);
        HoldFile.Text = Display(_preferences.Hold);
        RevealFile.Text = Display(_preferences.Reveal);
        TimelineFile.Text = Display(_preferences.Timeline);
        RemoveIntro.IsEnabled = !string.IsNullOrWhiteSpace(_preferences.Opening);
        RemoveHold.IsEnabled = !string.IsNullOrWhiteSpace(_preferences.Hold);
        RemoveReveal.IsEnabled = !string.IsNullOrWhiteSpace(_preferences.Reveal);
        RemoveTimeline.IsEnabled = !string.IsNullOrWhiteSpace(_preferences.Timeline);
        PreviewButton.IsEnabled = !_previewing && _preferences.HasCustomAnimation;
    }

    private void ReadMarkers(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (_updating) return;
        _preferences.WaitFrom = Math.Round(WaitMarker.Value, 2);
        _preferences.RevealFrom = Math.Round(RevealMarker.Value, 2);
        UpdateLabels();
        PreviewButton.IsEnabled = !_previewing && _preferences.HasCustomAnimation;
    }

    private void UpdateLabels()
    {
        WaitLabel.Text = $"{WaitMarker.Value:0.00} 秒";
        RevealLabel.Text = $"{RevealMarker.Value:0.00} 秒" + (_duration is { } length ? $" / {length:0.00} 秒" : "");
    }

    private string? PickFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return null;
        if (!Preferences.IsLocalFile(dialog.FileName))
        {
            Status.Text = "请选择本机磁盘上的素材文件。";
            return null;
        }
        return dialog.FileName;
    }

    private bool CheckTimeline()
    {
        if (_preferences.Animation != AnimationMode.Timeline || string.IsNullOrWhiteSpace(_preferences.Timeline)) return true;
        if (Preferences.IsLocalFile(_preferences.Timeline) && File.Exists(_preferences.Timeline)
            && _preferences.HasValidTimeline(_duration ?? double.PositiveInfinity)) return true;
        Status.Text = "请选择完整视频，并让两个标记依次位于视频开始和结束之间。";
        return false;
    }

    private async void Preview(object sender, RoutedEventArgs args)
    {
        if (!_preferences.HasCustomAnimation || !CheckTimeline()) return;
        _previewing = true;
        PreviewButton.IsEnabled = false;
        var preview = new AnimationWindow(preview: true) { Owner = this };
        var options = new Preferences
        {
            Animation = _preferences.Animation, WindowMode = _preferences.WindowMode, Opening = _preferences.Opening, Hold = _preferences.Hold,
            Reveal = _preferences.Reveal, Timeline = _preferences.Timeline, WaitFrom = _preferences.WaitFrom,
            RevealFrom = _preferences.RevealFrom
        };
        try
        {
            await preview.Play(options);
            if (preview.MediaIssue is { } issue) Status.Text = issue;
        }
        finally { _previewing = false; PreviewButton.IsEnabled = _preferences.HasCustomAnimation; }
    }

    private void SaveSettings(object sender, RoutedEventArgs args) => Save();

    private void SaveAndLaunch(object sender, RoutedEventArgs args)
    {
        if (Save()) DialogResult = true;
    }

    private bool Save()
    {
        if (!CheckTimeline()) return false;
        _preferences.WallpaperEnabled = WallpaperEnabled.IsChecked == true;
        _preferences.WindowMode = MaximizedWindow.IsChecked == true ? StartupWindowMode.Maximized : StartupWindowMode.VideoAspect;
        try
        {
            _preferences.Save();
            Status.Text = "已保存。换肤会更新已启用的窗口，启动动画将在下次冷启动生效。";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "无法保存个性化设置。";
            return false;
        }
    }

    private void CreateShortcut(object sender, RoutedEventArgs args) => SaveShortcut(settings: false);

    private void CreateSettingsShortcut(object sender, RoutedEventArgs args) => SaveShortcut(settings: true);

    private void SaveShortcut(bool settings)
    {
        var purpose = settings ? "设置" : "启动";
        var save = new SaveFileDialog
        {
            Title = $"创建{purpose}快捷方式", Filter = "Windows 快捷方式 (*.lnk)|*.lnk", DefaultExt = ".lnk",
            FileName = settings ? "ChatGPT 启动设置.lnk" : "ChatGPT.lnk",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            OverwritePrompt = false
        };
        if (save.ShowDialog(this) != true) return;
        try
        {
            WindowsApp.CreateShortcut(save.FileName, settings);
            Status.Text = $"{purpose}快捷方式已创建。";
        }
        catch (Exception ex) when (ex is IOException or COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status.Text = "快捷方式创建失败。若名称已存在，请换一个名称。";
        }
    }
}
