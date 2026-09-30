using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using VidArchiverGui.Core.Services;

namespace VidArchiverGui.App.Services;

/// <summary>
/// Shows a desktop notification without extra packages: a tray balloon on Windows (shown as a toast on 10/11),
/// osascript on macOS, and notify-send or gdbus on Linux. Failures are ignored; a notification is never essential.
/// </summary>
public sealed class SystemNotifier(Window window)
{
    private const string AppName = "Vid Archiver GUI";

    /// <summary>The desktop entry and icon name (see packaging/flatpak).</summary>
    private const string AppId = "io.github.disibio.vid-archiver-gui";

    public void Show(string title, string message)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ShowWindows(title, message);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Run("osascript", ["-e", $"display notification {AppleScriptString(message)} with title {AppleScriptString(title)}"]);
            }
            else if (!Run("notify-send", ["--app-name=" + AppName, "--icon=" + AppId, title, message]))
            {
                // gdbus ships with GLib, so it's there on desktops (and in the Flatpak runtime) without libnotify's tools.
                Run("gdbus", ["call", "--session", "--dest=org.freedesktop.Notifications", "--object-path=/org/freedesktop/Notifications",
                    "--method=org.freedesktop.Notifications.Notify", AppName, "0", AppId, title, message, "[]", "{}", "-1"]);
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static bool Run(string program, IEnumerable<string> args)
    {
        if (ProcessHelper.FindOnPath(program) is not { } exe)
        {
            return false;
        }

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var _ = Process.Start(psi);
        return true;
    }

    private static string AppleScriptString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ---------- Windows: a temporary tray icon's balloon, which Windows 10/11 shows as a toast ----------

    private IntPtr _icon;
    private DispatcherTimer? _removeTimer;

    private void ShowWindows(string title, string message)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_icon == IntPtr.Zero && Environment.ProcessPath is { } exe)
        {
            _icon = ExtractIconW(IntPtr.Zero, exe, 0);
        }

        var data = NewData(hwnd);
        data.uFlags = NifIcon | NifTip | NifInfo;
        data.hIcon = _icon;
        data.szTip = AppName;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = _icon != IntPtr.Zero ? NiifUser | NiifLargeIcon : 0;
        data.hBalloonIcon = _icon;
        if (!Shell_NotifyIconW(NimModify, ref data))
        {
            Shell_NotifyIconW(NimAdd, ref data);
        }

        // Take the tray icon away again once the toast has had time to show.
        _removeTimer?.Stop();
        _removeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _removeTimer.Tick += (_, _) =>
        {
            _removeTimer.Stop();
            RemoveWindowsIcon();
        };
        _removeTimer.Start();
    }

    /// <summary>Removes the tray icon, if one is showing (called on exit so it doesn't linger).</summary>
    public void RemoveWindowsIcon()
    {
        if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
        {
            var data = NewData(hwnd);
            Shell_NotifyIconW(NimDelete, ref data);
        }
    }

    private static NotifyIconData NewData(IntPtr hwnd) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = 1, szTip = "", szInfo = "", szInfoTitle = "",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private const int NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const int NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const int NiifUser = 0x4, NiifLargeIcon = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIconW(IntPtr instance, string exeFileName, int iconIndex);
}
