using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace CodexStart;

internal static class WindowsApp
{
    private const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    private const string AppId = PackageFamily + "!App";
    private const uint NoActivate = 0x0010, NoZOrder = 0x0004, AsyncWindowPos = 0x4000;
    private const int Restore = 9, Maximize = 3;

    public static int Activate(string? arguments = null)
    {
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true)!;
        var manager = (IApplicationActivationManager)Activator.CreateInstance(type)!;
        try
        {
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(AppId, arguments, 0, out var pid));
            return pid;
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    public static int[] Running()
    {
        var result = new List<int>();
        foreach (var name in new[] { "ChatGPT", "Codex" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                    if (IsOfficial(process)) result.Add(process.Id);
            }
        }
        return [.. result];
    }

    public static bool IsOfficial(Process process)
    {
        try
        {
            using var handle = OpenProcess(0x1000, false, process.Id);
            if (handle.IsInvalid) return false;
            uint length = 0;
            if (GetPackageFamilyName(handle, ref length, null) != 122) return false;
            var name = new StringBuilder((int)length);
            return GetPackageFamilyName(handle, ref length, name) == 0 && name.ToString() == PackageFamily;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public static nint MainWindow(IEnumerable<int> pids)
    {
        var allowed = pids.ToHashSet();
        nint result = 0;
        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out var pid);
            if (!allowed.Contains(pid) || !IsWindowVisible(candidate) || IsHungAppWindow(candidate) || GetWindow(candidate, 4) != 0)
                return true;
            if (!Bounds(candidate, out var rect) || rect.Right - rect.Left < 400 || rect.Bottom - rect.Top < 260)
                return true;
            result = candidate;
            return false;
        }, 0);
        return result;
    }

    public static bool ContentReady(nint window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var document = root?.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
            if (document is null) return false;

            var available = new AndCondition(
                new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
                new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true));
            var editors = document.FindAll(TreeScope.Descendants, new AndCondition(available,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true)));
            foreach (AutomationElement editor in editors)
                if (editor.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && !((ValuePattern)pattern).Current.IsReadOnly)
                    return true;

            var signInNames = new[] { "Sign in", "Log in", "Sign in to ChatGPT", "Sign in to Codex", "登录", "登入" };
            var signIn = new OrCondition(signInNames.Select(name => new PropertyCondition(
                AutomationElement.NameProperty, name, PropertyConditionFlags.IgnoreCase)).ToArray());
            return document.FindFirst(TreeScope.Descendants, new AndCondition(available, signIn,
                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink)))) is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ElementNotAvailableException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Focus(nint window)
    {
        if (window == 0) return;
        if (IsIconic(window)) ShowWindowAsync(window, Restore);
        SetForegroundWindow(window);
    }

    public static bool Bounds(nint window, out Rect bounds) =>
        DwmGetWindowAttribute(window, 9, out bounds, Marshal.SizeOf<Rect>()) == 0 || GetWindowRect(window, out bounds);

    public static List<Rect> WorkAreas(out Rect launchArea)
    {
        GetCursorPos(out var cursor);
        var selected = MonitorFromPoint(cursor, 2);
        var primary = MonitorFromPoint(default, 1);
        var workAreas = new List<Rect>();
        var selectedArea = default(Rect);
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                workAreas.Add(info.Work);
                if (monitor == selected || monitor == primary && selectedArea.Width == 0) selectedArea = info.Work;
            }
            return true;
        }, 0);
        launchArea = selectedArea.Width > 0 ? selectedArea : workAreas.FirstOrDefault();
        return workAreas;
    }

    public static void ShowAt(Window overlay, Rect bounds)
    {
        new WindowInteropHelper(overlay).EnsureHandle();
        MoveOverlay(overlay, bounds);
        overlay.Show();
        MoveOverlay(overlay, bounds);
    }

    public static void MoveOverlay(Window overlay, Rect bounds) => SetWindowPos(new WindowInteropHelper(overlay).Handle,
        new nint(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, NoActivate);

    internal readonly record struct WindowFrame(nint Handle, Rect Bounds, bool Maximized);

    public static WindowFrame? VisibleFrame(nint window) =>
        window != 0 && IsWindowVisible(window) && !IsIconic(window) && Bounds(window, out var bounds)
        && bounds.Width > 0 && bounds.Height > 0 ? new WindowFrame(window, bounds, IsZoomed(window)) : null;

    public static bool OverlayMatches(Window overlay, Rect expected) =>
        Bounds(new WindowInteropHelper(overlay).Handle, out var bounds) && bounds == expected;

    public static async Task<WindowFrame?> PlaceForReveal(Func<nint> client, Rect wanted, StartupWindowMode mode)
    {
        var monitor = MonitorFromPoint(new Point { X = wanted.Left + wanted.Width / 2, Y = wanted.Top + wanted.Height / 2 }, 2);
        var maximize = mode == StartupWindowMode.Maximized;
        var clock = Stopwatch.StartNew();
        WindowFrame? previous = null;
        long stableSince = 0, nextAttempt = 0;
        while (clock.ElapsedMilliseconds < 2200)
        {
            var elapsed = clock.ElapsedMilliseconds;
            var window = client();
            var frame = VisibleFrame(window);
            var onMonitor = monitor == MonitorFromWindow(window, 2);
            var placed = onMonitor && frame is { } current && current.Maximized == maximize
                && (maximize || current.Bounds == wanted);
            if (previous != frame)
            {
                previous = frame;
                stableSince = elapsed;
            }

            if (placed && elapsed - stableSince >= 200) return frame;
            if (window != 0 && !placed && elapsed >= nextAttempt && elapsed < 1750)
            {
                nextAttempt = elapsed + 220;
                if (IsIconic(window) || frame is { Maximized: true }) ShowWindowAsync(window, Restore);
                else if (maximize && onMonitor) ShowWindowAsync(window, Maximize);
                else if (frame is { Bounds: var actual } && GetWindowRect(window, out var outer))
                {
                    // Convert the desired visible rectangle to the native rectangle including resize borders.
                    SetWindowPos(window, 0, wanted.Left + outer.Left - actual.Left, wanted.Top + outer.Top - actual.Top,
                        wanted.Width + outer.Width - actual.Width, wanted.Height + outer.Height - actual.Height,
                        NoZOrder | NoActivate | AsyncWindowPos);
                }
            }
            await Task.Delay(40);
        }
        // Codex can enforce a minimum size. Use its settled visible bounds after trying the requested size.
        return !maximize && previous is { Maximized: false } final && clock.ElapsedMilliseconds - stableSince >= 200
            && MonitorFromWindow(final.Handle, 2) == monitor && VisibleFrame(client()) == final ? final : null;
    }

    public static void FlushComposition() => DwmFlush();

    public static void DarkFrame(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var dark = 1u;
        var noBorder = 0xFFFFFFFEu;
        DwmSetWindowAttribute(handle, 20, in dark, sizeof(uint));
        DwmSetWindowAttribute(handle, 34, in noBorder, sizeof(uint));
    }

    public static void CreateShortcut(string path, bool settings)
    {
        if (File.Exists(path)) throw new IOException("快捷方式已存在，请选择另一个名称。");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("找不到启动器路径。");
        var icon = settings ? Path.Combine(AppContext.BaseDirectory, "Assets", "settings.ico")
            : Path.Combine(Preferences.Folder, "official-codex.ico");
        if (!settings && !File.Exists(icon))
        {
            Directory.CreateDirectory(Preferences.Folder);
            SaveOfficialIcon(icon);
        }
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
        try
        {
            dynamic link = ((dynamic)shell).CreateShortcut(path);
            try
            {
                link.TargetPath = executable;
                link.Arguments = settings ? "--settings" : "";
                link.WorkingDirectory = Path.GetDirectoryName(executable);
                link.IconLocation = icon + ",0";
                link.Description = settings ? "ChatGPT 启动设置" : "ChatGPT 自定义启动快捷方式";
                link.Save();
            }
            finally { Marshal.ReleaseComObject((object)link); }
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    internal static void SaveOfficialIcon(string path)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(@"shell:AppsFolder\" + AppId, 0, ref iid, out var item));
        try
        {
            Marshal.ThrowExceptionForHR(item.GetImage(new Size { Width = 256, Height = 256 }, 4, out var bitmap));
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var png = new MemoryStream();
                encoder.Save(png);
                using var output = new BinaryWriter(new FileStream(path, FileMode.CreateNew));
                output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)1);
                output.Write((byte)(source.PixelWidth % 256)); output.Write((byte)(source.PixelHeight % 256));
                output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)32);
                output.Write((int)png.Length); output.Write(22);
                png.WriteTo(output.BaseStream);
            }
            finally { DeleteObject(bitmap); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    public static int LoopbackListenerOwner(int port)
    {
        int length = 0;
        GetExtendedTcpTable(0, ref length, false, 2, 3, 0);
        if (length <= 4) return 0;
        var table = Marshal.AllocHGlobal(length);
        try
        {
            if (GetExtendedTcpTable(table, ref length, false, 2, 3, 0) != 0) return 0;
            int count = Marshal.ReadInt32(table);
            int owner = 0;
            for (int i = 0; i < count; i++)
            {
                int offset = 4 + i * 24;
                int localPort = Marshal.ReadByte(table, offset + 8) * 256 + Marshal.ReadByte(table, offset + 9);
                if (localPort != port) continue;
                if (Marshal.ReadInt32(table, offset + 4) != 0x0100007f) return 0;
                owner = Marshal.ReadInt32(table, offset + 20);
            }
            return owner;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public Rect Fit(double aspect)
        {
            var width = Math.Max(1, (int)Math.Round(Math.Min(Width * .90, Height * .90 * aspect)));
            var height = Math.Max(1, (int)Math.Round(Math.Min(Height * .90, Width * .90 / aspect)));
            var left = Left + (Width - width) / 2;
            var top = Top + (Height - height) / 2;
            return new(left, top, left + width, top + height);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Screen, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int Width, Height; }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string? args, int options, out int pid);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, int flags, out nint bitmap);
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    private delegate bool EnumMonitorsCallback(nint monitor, nint dc, nint bounds, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, EnumMonitorsCallback callback, nint parameter);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out Rect output, int length);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, in uint value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(
        uint access, bool inheritHandle, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetPackageFamilyName(
        Microsoft.Win32.SafeHandles.SafeProcessHandle process, ref uint length, StringBuilder? name);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref int length, bool order, int family, int type, uint reserved);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int SHCreateItemFromParsingName(
        string path, nint bindContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint bitmap);
}
