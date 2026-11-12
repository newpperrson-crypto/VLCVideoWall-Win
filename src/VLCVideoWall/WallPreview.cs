using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VLCVideoWall;

/// <summary>Визуальная сетка стены: занятые камерой окна закрашены, свободные — пунктиром.</summary>
public class WallPreview : Control
{
    public int Cols = 2;
    public int Rows = 2;
    public int CanvasW = 1920;
    public int CanvasH = 1080;
    public string[] Cells = new string[0];

    /// <summary>Поддержка звука включена — значки рисуются у всех плиток.</summary>
    public bool AudioSupport;

    /// <summary>Звук включён у плитки (по индексу окна).</summary>
    public bool[] AudioOn = new bool[0];

    /// <summary>Есть ли у камеры звуковая дорожка (по индексу окна).</summary>
    public AudioTrackState[] AudioTrack = new AudioTrackState[0];

    /// <summary>Идёт переключение звука (по индексу окна).</summary>
    public bool[] AudioBusy = new bool[0];

    /// <summary>Клик по плитке; аргумент — порядковый номер окна.</summary>
    public event Action<int> TileClicked;

    public WallPreview()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(247, 250, 254);
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d > Math.Min(r.Width, r.Height)) d = Math.Min(r.Width, r.Height);
        if (d < 2) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Геометрия сетки: доска, число ячеек и размер шага.</summary>
    private bool GetLayout(out Rectangle board, out int cols, out int rows, out int gap, out int cw, out int ch)
    {
        board = Rectangle.Empty; cols = 1; rows = 1; gap = 6; cw = 0; ch = 0;

        int pad = 16;
        var area = new Rectangle(pad, pad, Width - 2 * pad, Height - 2 * pad);
        if (area.Width < 60 || area.Height < 60) return false;

        // доска в пропорциях полотна, по центру
        double target = CanvasH > 0 ? (double)CanvasW / CanvasH : 16.0 / 9.0;
        int bw = area.Width;
        int bh = (int)(bw / target);
        if (bh > area.Height) { bh = area.Height; bw = (int)(bh * target); }
        board = new Rectangle(area.X + (area.Width - bw) / 2, area.Y + (area.Height - bh) / 2, bw, bh);

        cols = Math.Max(1, Cols);
        rows = Math.Max(1, Rows);
        gap = board.Width > 500 ? 10 : 6;
        cw = (board.Width - gap * (cols - 1)) / cols;
        ch = (board.Height - gap * (rows - 1)) / rows;
        return cw > 0 && ch > 0;
    }

    private static void DrawSpeaker(Graphics g, Rectangle cell, AudioBadgeState state)
    {
        int size = Math.Min(26, Math.Min(cell.Width, cell.Height) / 3);
        if (size < 14) return;
        var box = new Rectangle(cell.Right - size - 6, cell.Y + 6, size, size);

        using (var back = new SolidBrush(Color.FromArgb(180, 20, 24, 30)))
        using (var path = Round(box, 6))
            g.FillPath(back, path);

        Color color;
        switch (state)
        {
            case AudioBadgeState.On: color = Color.FromArgb(134, 239, 172); break;
            case AudioBadgeState.Busy: color = Color.FromArgb(226, 232, 240); break;
            case AudioBadgeState.NoTrack: color = Color.FromArgb(120, 128, 140); break;
            default: color = Color.FromArgb(150, 164, 184); break;
        }
        int bx = box.X + (int)(size * 0.22);
        int by = box.Y + (int)(size * 0.34);
        int unit = Math.Max(2, size / 10);

        using (var brush = new SolidBrush(color))
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
        using (var pen = new Pen(color, Math.Max(1.4f, size / 14f)))
        {
            if (state == AudioBadgeState.On)
            {
                g.DrawArc(pen, box.X + (int)(size * 0.52), box.Y + (int)(size * 0.3),
                               (int)(size * 0.24), (int)(size * 0.4), -55, 110);
                g.DrawArc(pen, box.X + (int)(size * 0.58), box.Y + (int)(size * 0.2),
                               (int)(size * 0.34), (int)(size * 0.6), -50, 100);
            }
            else if (state == AudioBadgeState.NoTrack)
            {
                // маленькая перечёркнутая нота: дорожки нет
                g.DrawLine(pen, box.X + (int)(size * 0.58), box.Y + (int)(size * 0.34),
                                box.X + (int)(size * 0.8), box.Y + (int)(size * 0.6));
                g.DrawEllipse(pen, box.X + (int)(size * 0.6), box.Y + (int)(size * 0.52),
                                   (int)(size * 0.16), (int)(size * 0.12));
            }
            else if (state == AudioBadgeState.Busy)
            {
                for (int i = 0; i < 3; i++)
                    g.FillEllipse(Brushes.White, box.X + (int)(size * 0.54) + i * (int)(size * 0.11),
                                  box.Y + (int)(size * 0.46), (int)(size * 0.06), (int)(size * 0.06));
            }
            else
            {
                g.DrawLine(pen, box.X + (int)(size * 0.6), box.Y + (int)(size * 0.34),
                                box.X + (int)(size * 0.82), box.Y + (int)(size * 0.66));
            }
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Rectangle board;
        int cols, rows, gap, cw, ch;
        if (!GetLayout(out board, out cols, out rows, out gap, out cw, out ch)) return;
        if (!board.Contains(e.Location)) return;

        int c = (e.X - board.X) / Math.Max(1, cw + gap);
        int r = (e.Y - board.Y) / Math.Max(1, ch + gap);
        if (c < 0 || c >= cols || r < 0 || r >= rows) return;

        var cell = new Rectangle(board.X + c * (cw + gap), board.Y + r * (ch + gap), cw, ch);
        if (!cell.Contains(e.Location)) return;   // попали в промежуток между окнами

        int pos = r * cols + c;
        var handler = TileClicked;
        if (handler != null) handler(pos);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        Rectangle board;
        int cols, rows, gap, cw, ch;
        if (!GetLayout(out board, out cols, out rows, out gap, out cw, out ch)) return;

        using (var bg = new SolidBrush(Color.FromArgb(226, 233, 243)))
        using (var pen = new Pen(Color.FromArgb(198, 209, 226), 2))
        using (var path = Round(board, 14))
        {
            g.FillPath(bg, path);
            g.DrawPath(pen, path);
        }

        using (var font = new Font("Segoe UI", board.Width > 600 ? 11f : 9f))
        using (var fontNum = new Font("Segoe UI", 7.5f))
        using (var filled = new SolidBrush(Color.FromArgb(31, 111, 235)))
        using (var freeBg = new SolidBrush(Color.White))
        using (var white = new SolidBrush(Color.White))
        using (var grey = new SolidBrush(Color.FromArgb(150, 160, 176)))
        using (var numBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 170)))
        using (var freePen = new Pen(Color.FromArgb(186, 199, 219), 1) { DashStyle = DashStyle.Dash })
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })

        for (int c = 0; c < cols; c++)
        {
            for (int r = 0; r < rows; r++)
            {
                // VLC заполняет окна слева-направо, сверху вниз
                int pos = r * cols + c;
                var cell = new Rectangle(board.X + c * (cw + gap), board.Y + r * (ch + gap), cw, ch);
                string name = pos < Cells.Length ? Cells[pos] : null;
                bool busy = !string.IsNullOrEmpty(name);

                using (var path = Round(cell, cell.Width > 120 ? 8 : 5))
                {
                    if (busy)
                    {
                        g.FillPath(filled, path);
                        g.DrawString(name, font, white, cell, sf);
                    }
                    else
                    {
                        g.FillPath(freeBg, path);
                        g.DrawPath(freePen, path);
                        g.DrawString("свободно", font, grey, cell, sf);
                    }
                }

                // значок звука у всех плиток, когда поддержка звука включена
                if (AudioSupport && busy)
                {
                    var state = AudioBadgeState.Off;
                    if (pos < AudioBusy.Length && AudioBusy[pos]) state = AudioBadgeState.Busy;
                    else if (pos < AudioTrack.Length && AudioTrack[pos] == AudioTrackState.Absent) state = AudioBadgeState.NoTrack;
                    else if (pos < AudioOn.Length && AudioOn[pos]) state = AudioBadgeState.On;
                    DrawSpeaker(g, cell, state);
                }

                // номер окна в углу (показывает порядок заполнения)
                if (cell.Width > 90 && cell.Height > 50)
                {
                    var nr = new Rectangle(cell.X + 6, cell.Y + 4, 30, 16);
                    g.DrawString((pos + 1).ToString(), fontNum, busy ? numBrush : grey, nr);
                }
            }
        }
    }
}
