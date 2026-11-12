using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VLCVideoWall;

/// <summary>Вид значка звука.</summary>
public enum AudioBadgeState
{
    Off,        // звук камеры выключен
    On,         // звук включён
    Busy,       // команда принята, идёт переключение
    NoTrack     // в потоке камеры нет звуковой дорожки
}

/// <summary>
/// Значок звука — самостоятельное всплывающее окно (WS_POPUP) без TopMost,
/// владелец — окно стены (VLC). Координаты задаются в экранной системе.
/// Владелец держит значок над стеной; когда вперёд выходит другое приложение,
/// значок уходит назад вместе со стеной и чужих окон не перекрывает.
/// Дочернее окно (WS_CHILD) здесь не годится: VLC рисует видео поверх дочерних окон
/// своего окна, значок становится невидимым.
/// </summary>
public sealed class AudioBadgeWindow : IDisposable
{
    private const string ClassName = "VLCVideoWallAudioBadge";
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public Rectangle rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);


    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, int cursor);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);

    private static readonly Dictionary<IntPtr, AudioBadgeWindow> Map = new Dictionary<IntPtr, AudioBadgeWindow>();
    private static WndProcDelegate _proc;          // нельзя отдать GC
    private static bool _registered;

    public IntPtr Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public AudioBadgeState State { get; private set; } = AudioBadgeState.Off;
    public string CameraName = "";

    public event Action Clicked;

    /// <summary>Создать значок дочерним окном указанного окна (окно стены).</summary>
    public AudioBadgeWindow(IntPtr parent, int x, int y, int size = 38)
    {
        EnsureClass();

        Width = size;
        Height = size;
        // окно-владелец — окно стены: значок всегда над стеной, но НЕ над чужими окнами
        Handle = CreateWindowEx(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, ClassName, "",
            WS_POPUP | WS_VISIBLE, x, y, size, size, parent, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (Handle == IntPtr.Zero) return;
        Map[Handle] = this;
        Move(x, y);
    }

    private static void EnsureClass()
    {
        if (_registered) return;

        _proc = Proc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
            style = 0x0002,      // CS_HREDRAW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, 32512),   // IDC_ARROW
            lpszClassName = ClassName
        };
        wc.style |= 0x0001;      // CS_VREDRAW
        RegisterClassEx(ref wc);
        _registered = true;
    }

    private static IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        AudioBadgeWindow self;
        if (!Map.TryGetValue(hWnd, out self))
            return DefWindowProc(hWnd, msg, wParam, lParam);

        switch (msg)
        {
            case WM_PAINT:
                {
                    PAINTSTRUCT ps;
                    var hdc = BeginPaint(hWnd, out ps);
                    self.PaintBuffered(hdc);
                    EndPaint(hWnd, ref ps);
                    return IntPtr.Zero;
                }
            case WM_ERASEBKGND:
                return (IntPtr)1;
            case WM_LBUTTONDOWN:
                SetCapture(hWnd);
                return IntPtr.Zero;      // не отдаём клик окну стены
            case WM_LBUTTONUP:
            case WM_RBUTTONUP:
                ReleaseCapture();
                if (self.State == AudioBadgeState.NoTrack || self.State == AudioBadgeState.Busy) return IntPtr.Zero;

                // защита от двойного срабатывания при быстрой перерисовке
                long now = Environment.TickCount64;
                if (now - self._lastClickTicks < 300) return IntPtr.Zero;
                self._lastClickTicks = now;

                var handler = self.Clicked;
                if (handler != null) handler();
                return IntPtr.Zero;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    public void SetState(AudioBadgeState state)
    {
        if (State == state) return;
        State = state;
        Invalidate();
    }

    private int _x = int.MinValue, _y = int.MinValue;
    private bool _shown = true;
    private long _lastClickTicks;

    /// <summary>Скрыть значок, когда окно стены свёрнуто или невидимо.</summary>
    public void SetVisible(bool visible)
    {
        if (_shown == visible || Handle == IntPtr.Zero) return;
        _shown = visible;
        ShowWindow(Handle, visible ? 8 : 0);   // SW_SHOWNA / SW_HIDE
    }

    public void Move(int x, int y)
    {
        if (Handle == IntPtr.Zero) return;
        if (_x == x && _y == y) return;      // лишний SetWindowPos = мигание
        _x = x; _y = y;
        // SWP_SHOWWINDOW не ставим: иначе скрытый значок снова покажется на каждом сдвиге
        SetWindowPos(Handle, IntPtr.Zero, x, y, Width, Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Рисование через свой буфер: без мигания при перерисовке.</summary>
    private void PaintBuffered(IntPtr hdc)
    {
        using (var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height)))
        {
            using (var g = Graphics.FromImage(bmp)) Paint(g);
            using (var target = Graphics.FromHdc(hdc))
            {
                target.CompositingMode = CompositingMode.SourceCopy;
                target.DrawImageUnscaled(bmp, 0, 0);
            }
        }
    }

    private void Invalidate()
    {
        if (Handle == IntPtr.Zero) return;
        InvalidateRect(Handle, IntPtr.Zero, false);
    }

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    private void Paint(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(22, 26, 32));

        var body = new Rectangle(1, 1, Width - 3, Height - 3);
        using (var path = Round(body, 9))
        using (var fill = new SolidBrush(State == AudioBadgeState.Busy
                                            ? Color.FromArgb(64, 72, 84)
                                            : Color.FromArgb(40, 46, 56)))
        using (var pen = new Pen(Color.FromArgb(96, 106, 122), 1.2f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        Color fg;
        switch (State)
        {
            case AudioBadgeState.On: fg = Color.FromArgb(134, 239, 172); break;
            case AudioBadgeState.Busy: fg = Color.FromArgb(226, 232, 240); break;
            case AudioBadgeState.NoTrack: fg = Color.FromArgb(118, 126, 138); break;
            default: fg = Color.FromArgb(150, 164, 184); break;
        }

        int size = Height;
        int bx = (int)(size * 0.19), by = (int)(size * 0.30), unit = Math.Max(2, size / 11);

        using (var brush = new SolidBrush(fg))
        {
            g.FillRectangle(brush, bx, by + unit, unit * 2, unit * 4);
            g.FillPolygon(brush, new[]
            {
                new Point(bx + unit * 2, by + unit),
                new Point(bx + unit * 4, by - unit / 2),
                new Point(bx + unit * 4, by + unit * 6),
                new Point(bx + unit * 2, by + unit * 4)
            });
        }

        using (var pen = new Pen(fg, Math.Max(1.6f, size / 16f)))
        {
            if (State == AudioBadgeState.On)
            {
                g.DrawArc(pen, (int)(size * 0.50), (int)(size * 0.33), (int)(size * 0.17), (int)(size * 0.28), -55, 110);
                g.DrawArc(pen, (int)(size * 0.56), (int)(size * 0.25), (int)(size * 0.24), (int)(size * 0.44), -50, 100);
            }
            else if (State == AudioBadgeState.NoTrack)
            {
                // дорожки нет: динамик перечёркнут крестом
                g.DrawLine(pen, (int)(size * 0.52), (int)(size * 0.30), (int)(size * 0.80), (int)(size * 0.70));
                g.DrawLine(pen, (int)(size * 0.80), (int)(size * 0.30), (int)(size * 0.52), (int)(size * 0.70));
            }
            else if (State == AudioBadgeState.Busy)
            {
                for (int i = 0; i < 3; i++)
                    g.FillEllipse(Brushes.White, (int)(size * 0.54) + i * (int)(size * 0.11),
                                  (int)(size * 0.46), (int)(size * 0.06), (int)(size * 0.06));
            }
            // выключен: только динамик, без перечёркивания
        }
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d < 2 || r.Width <= 2 || r.Height <= 2) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        Map.Remove(Handle);
        try { DestroyWindow(Handle); } catch { }
        Handle = IntPtr.Zero;
    }
}
