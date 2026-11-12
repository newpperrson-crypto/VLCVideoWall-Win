using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VLCVideoWall;

/// <summary>
/// Значок звука поверх окна стены. Отдельное маленькое окно без рамки, всегда сверху:
/// VLC рисует мозаику одним полотном, поэтому «нарисовать внутри плитки» можно только
/// наложением — сам значок принадлежит приложению, а не видеопотоку.
/// Клик по значку включает и выключает звук.
/// </summary>
public sealed class AudioBadge : Form
{
    private bool _muted;

    /// <summary>Текущее состояние значка.</summary>
    public bool IsMuted { get { return _muted; } }

    /// <summary>Имя камеры — показывается во всплывающей подсказке.</summary>
    public string CameraName = "";

    /// <summary>Переключить вид значка (вызывается при обновлении состояния).</summary>
    public void SetMuted(bool muted)
    {
        if (_muted == muted) return;
        _muted = muted;
        Invalidate();
    }

    public event Action Clicked;

    public AudioBadge()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(46, 46);
        BackColor = Color.FromArgb(22, 26, 32);
        Opacity = 0.9;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE: не забирать фокус у стены
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW: не показываться в Alt+Tab
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(22, 26, 32));

        // фон значка
        var body = new Rectangle(1, 1, Width - 3, Height - 3);
        using (var path = Round(body, 10))
        using (var fill = new SolidBrush(Color.FromArgb(38, 44, 54)))
        using (var pen = new Pen(Color.FromArgb(120, 132, 150), 1.4f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        var fg = _muted ? Color.FromArgb(248, 113, 113) : Color.FromArgb(134, 239, 172);
        int cx = 12, cy = 16;
        using (var brush = new SolidBrush(fg))
        {
            // корпус динамика
            g.FillRectangle(brush, cx, cy + 5, 5, 9);
            // раструб
            var cone = new Point[]
            {
                new Point(cx + 5, cy + 5),
                new Point(cx + 12, cy),
                new Point(cx + 12, cy + 19),
                new Point(cx + 5, cy + 14)
            };
            g.FillPolygon(brush, cone);
        }

        if (_muted)
        {
            using (var pen = new Pen(Color.FromArgb(248, 113, 113), 2.4f))
            {
                g.DrawLine(pen, 27, 14, 37, 24);
                g.DrawLine(pen, 37, 14, 27, 24);
            }
        }
        else
        {
            using (var pen = new Pen(fg, 1.8f))
            {
                g.DrawArc(pen, 22, 13, 10, 12, -55, 110);
                g.DrawArc(pen, 25, 9, 16, 20, -50, 100);
            }
        }
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d < 2) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var handler = Clicked;
        if (handler != null) handler();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        var tip = string.IsNullOrEmpty(CameraName) ? "Звук" : "Звук: " + CameraName;
        var tooltip = new ToolTip();
        tooltip.SetToolTip(this, _muted ? tip + " — выключен" : tip + " — включён");
    }
}
