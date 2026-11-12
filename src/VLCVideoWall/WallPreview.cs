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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        int pad = 16;
        var area = new Rectangle(pad, pad, Width - 2 * pad, Height - 2 * pad);
        if (area.Width < 60 || area.Height < 60) return;

        // доска в пропорциях полотна, по центру
        double target = CanvasH > 0 ? (double)CanvasW / CanvasH : 16.0 / 9.0;
        int bw = area.Width;
        int bh = (int)(bw / target);
        if (bh > area.Height) { bh = area.Height; bw = (int)(bh * target); }
        var board = new Rectangle(area.X + (area.Width - bw) / 2, area.Y + (area.Height - bh) / 2, bw, bh);

        using (var bg = new SolidBrush(Color.FromArgb(226, 233, 243)))
        using (var pen = new Pen(Color.FromArgb(198, 209, 226), 2))
        using (var path = Round(board, 14))
        {
            g.FillPath(bg, path);
            g.DrawPath(pen, path);
        }

        int cols = Math.Max(1, Cols);
        int rows = Math.Max(1, Rows);
        int gap = board.Width > 500 ? 10 : 6;
        int cw = (board.Width - gap * (cols - 1)) / cols;
        int ch = (board.Height - gap * (rows - 1)) / rows;

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
