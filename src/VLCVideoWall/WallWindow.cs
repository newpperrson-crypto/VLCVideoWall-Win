using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace VLCVideoWall;

/// <summary>
/// Поиск окна видеовыхода VLC своей стены. Нужен, чтобы положить значок звука
/// ровно над нужной плиткой: VLC не сообщает координаты плиток, но окно вывода
/// найти можно и пересчитать сетку относительно его клиентской области.
/// </summary>
public static class WallWindow
{
    private const int GWL_EXSTYLE = -20;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    /// <summary>
    /// Клиентская область окна видеовыхода нашего VLC в экранных координатах.
    /// false — если VLC не запущен или окно ещё не создано.
    /// </summary>
    public static bool TryGetClientOnScreen(int pid, out Rectangle client)
    {
        client = Rectangle.Empty;
        IntPtr found = IntPtr.Zero;

        EnumWindows((h, l) =>
        {
            uint windowPid;
            GetWindowThreadProcessId(h, out windowPid);
            if (windowPid != (uint)pid) return true;

            var sb = new StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString().StartsWith("VLC video", StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) return false;

        RECT rc;
        if (!GetClientRect(found, out rc)) return false;
        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(found, ref origin)) return false;

        client = new Rectangle(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
        return client.Width > 0 && client.Height > 0;
    }

    /// <summary>Процесс стены по vlc.pid: PID и время старта должны совпадать.</summary>
    public static bool TryGetWallProcess(int pid, out Process process)
    {
        process = null;
        try
        {
            var p = Process.GetProcessById(pid);
            if (!string.Equals(p.ProcessName, "vlc", StringComparison.OrdinalIgnoreCase))
            {
                p.Dispose();
                return false;
            }
            process = p;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
