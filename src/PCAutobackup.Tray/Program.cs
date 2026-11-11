using System.Runtime.InteropServices;
using Microsoft.Win32;
using PCAutobackup.Core;

namespace PCAutobackup.Tray;

internal static class Program
{
    private const uint CallbackMessage = 0x8001, OpenMessage = 0x8002;
    private static readonly WindowProcedure Procedure = WindowProc;
    private static string _configuration = "", _directory = "";
    private static string _language = "th";
    private static nint _window, _icon;
    private static uint _taskbarCreated;
    private static NotifyIconData _notification;

    [STAThread]
    private static int Main(string[] args)
    {
        var index = Array.IndexOf(args, "--config");
        if (index >= 0 && (index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]))) return 2;
        _configuration = DesktopRuntime.ConfigurationPath(index < 0 ? null : args[index + 1]);
        _directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var className = AppInstance.Identity("tray-window", _configuration);
        if (args.Contains("--exit"))
        {
            var existing = FindWindow(className, null);
            if (existing != 0) PostMessage(existing, 0x0010, 0, 0);
            return 0;
        }
        using var instance = new AppInstance("tray", _configuration);
        if (!instance.IsPrimary)
        {
            if (args.Contains("--open")) instance.ActivatePrimary();
            return 0;
        }
        var module = GetModuleHandle(null);
        var windowClass = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = module, Procedure = Procedure, ClassName = className };
        if (RegisterClassEx(ref windowClass) == 0) return 1;
        _window = CreateWindowEx(0, className, "PC AutoBackup background", 0, 0, 0, 0, 0, 0, 0, module, 0);
        if (_window == 0) return 1;
        _icon = LoadImage(0, Path.Combine(_directory, "Assets", "app.ico"), 1, 32, 32, 0x10);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        AddIcon();
        instance.Listen(() => PostMessage(_window, OpenMessage, 0, 0));
        // An in-place update can reopen the tray without requesting a catch-up run.
        if (!args.Contains("--skip-launch-check"))
            _ = DesktopRuntime.StartPendingBackupAsync(_directory, _configuration);
        if (args.Contains("--open")) OpenSettings();
        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        ShellNotifyIcon(2, ref _notification);
        if (_icon != 0) DestroyIcon(_icon);
        return 0;
    }

    private static void AddIcon()
    {
        RefreshLanguage();
        _notification = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1, Flags = 1 | 2 | 4 | 0x80, Callback = CallbackMessage, Icon = _icon, Tip = T("PC AutoBackup · เปิดเพื่อปรับการตั้งค่า"), Info = "", InfoTitle = "" };
        ShellNotifyIcon(0, ref _notification);
        _notification.Version = 4;
        ShellNotifyIcon(4, ref _notification);
    }

    private static string T(string text) => AppLocalization.Text(text, _language);

    private static void RefreshLanguage()
    {
        // Read on user interaction, not on a timer; language changes do not require restarting the resident tray.
        try { _language = ConfigurationStore.LoadAsync(_configuration).GetAwaiter().GetResult().Language; }
        // InvalidDataException (empty or unsupported configuration) is not an IOException; it must not end the tray from a window callback.
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }

    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (_taskbarCreated != 0 && message == _taskbarCreated) { AddIcon(); return 0; }
        if (message == OpenMessage) { OpenSettings(); return 0; }
        if (message == CallbackMessage)
        {
            var notification = (uint)((long)lParam & 0xffff);
            if (notification is 0x400 or 0x401 or 0x203) OpenSettings();
            else if (notification == 0x7b) ShowMenu();
            return 0;
        }
        if (message == 0x0011) return 1; // Allow normal Windows sign-out/shutdown.
        if (message == 0x0010) { DestroyWindow(window); return 0; }
        if (message == 0x0002) { PostQuitMessage(0); return 0; }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private static void OpenSettings()
    {
        try { using var process = DesktopRuntime.Start(Path.Combine(_directory, "PCAutobackup.exe"), "--config", _configuration); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { }
    }

    private static void ShowMenu()
    {
        RefreshLanguage();
        _notification.Tip = T("PC AutoBackup · เปิดเพื่อปรับการตั้งค่า");
        ShellNotifyIcon(1, ref _notification);
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, T("เปิด PC AutoBackup"));
            AppendMenu(menu, 0, 2, T("สำรองตอนนี้"));
            var customConfig = !string.Equals(_configuration, DesktopRuntime.ConfigurationPath(null), StringComparison.OrdinalIgnoreCase);
            AppendMenu(menu, (StartupEnabled() ? 8u : 0u) | (customConfig ? 1u : 0u), 3, T("เริ่มพร้อม Windows"));
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 4, T("ออกจากถาดระบบ (ตารางสำรองยังทำงาน)"));
            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            var selected = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, _window, 0);
            PostMessage(_window, 0, 0, 0);
            if (selected == 1) OpenSettings();
            if (selected == 2)
            {
                try { using var process = DesktopRuntime.Start(Path.Combine(_directory, "Worker", "PCAutobackup.Worker.exe"), "--config", _configuration); }
                catch (System.ComponentModel.Win32Exception) { }
            }
            if (selected == 3) SetStartup(!StartupEnabled());
            if (selected == 4) DestroyWindow(_window);
        }
        finally { DestroyMenu(menu); }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue("PCAutobackup") as string, StartupCommand(), StringComparison.OrdinalIgnoreCase);
    }
    private static string StartupCommand() => $"\"{Environment.ProcessPath}\" --background --config \"{_configuration}\"";
    private static void SetStartup(bool enabled)
    {
        // Test/custom configurations must not replace the user's installed startup entry.
        if (!string.Equals(_configuration, DesktopRuntime.ConfigurationPath(null), StringComparison.OrdinalIgnoreCase)) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("PCAutobackup", StartupCommand());
        else key.DeleteValue("PCAutobackup", false);
    }

    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style; public WindowProcedure Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
}
