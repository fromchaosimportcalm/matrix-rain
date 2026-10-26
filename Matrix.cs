// Matrix digital rain screensaver. One borderless window per monitor.
// Build: csc /target:winexe /optimize /out:Matrix.scr Matrix.cs
// Install: right-click Matrix.scr -> Install.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "/s";
        if (mode.StartsWith("/p")) return; // skip the tiny preview monitor
        if (mode.StartsWith("/c"))
        {
            MessageBox.Show("Nothing to configure.\n\nThere is no spoon.", "Matrix");
            return;
        }

        // Real pixels on every monitor, even with mixed scaling.
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch (EntryPointNotFoundException) { SetProcessDPIAware(); }

        var glyphs = new Glyphs();
        var forms = new List<RainForm>();
        int seed = Environment.TickCount;
        foreach (var screen in Screen.AllScreens)
            forms.Add(new RainForm(screen.Bounds, glyphs, new Random(seed++)));

        var timer = new Timer { Interval = 33 };
        timer.Tick += (s, e) => { foreach (var f in forms) f.Step(); };

        Cursor.Hide();
        foreach (var f in forms) f.Show();
        timer.Start();
        Application.Run();
    }
}

// Every glyph pre-rendered (mirrored, on an opaque black cell) at each
// brightness level, plus a near-white "head" version.
class Glyphs
{
    public const int CellW = 14, CellH = 18, Levels = 16;
    public readonly Bitmap[][] ByLevel; // [level 1..Levels][glyph]; Levels+1 = head
    public readonly int Count;

    public Glyphs()
    {
        var chars = new List<char>();
        for (char c = 'ｦ'; c <= 'ﾝ'; c++) chars.Add(c); // half-width katakana
        foreach (char c in "0123456789Z:.=*+-<>|\"") chars.Add(c);
        Count = chars.Count;

        ByLevel = new Bitmap[Levels + 2][];
        using (var font = new Font("MS Gothic", 16, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            for (int level = 1; level <= Levels + 1; level++)
            {
                Color color = level > Levels ? Color.FromArgb(200, 255, 245) : Trail((float)level / Levels);
                ByLevel[level] = new Bitmap[Count];
                for (int i = 0; i < Count; i++)
                    ByLevel[level][i] = Render(chars[i], font, color, !char.IsDigit(chars[i]));
            }
        }
    }

    // Teal-green that fades to dark green, a touch of white at the top.
    static Color Trail(float t)
    {
        return Color.FromArgb(
            (int)(130 * t * t * t),
            (int)(40 + 215 * t),
            (int)(25 + 165 * Math.Pow(t, 1.5)));
    }

    static Bitmap Render(char ch, Font font, Color color, bool mirror)
    {
        var bmp = new Bitmap(CellW, CellH, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(color))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (mirror) { g.TranslateTransform(CellW, 0); g.ScaleTransform(-1, 1); }
            g.DrawString(ch.ToString(), font, brush, new RectangleF(0, 0, CellW, CellH), fmt);
        }
        return bmp;
    }
}

class Drop
{
    public float Pos, Speed, Decay;
}

class RainForm : Form
{
    const double SpawnChance = 0.03; // per column per frame
    const int MinGap = 6;            // rows between drops in one column

    readonly Glyphs glyphs;
    readonly Random rnd;
    readonly Bitmap buffer;
    readonly int cols, rows;
    readonly List<Drop>[] drops;
    // Per cell, row-major.
    readonly float[] bright, decay;
    readonly byte[] glyph, drawnGlyph;
    readonly int[] drawnLevel;
    readonly bool[] isHead;
    readonly List<int> heads = new List<int>();
    Point? mouseStart;

    public RainForm(Rectangle bounds, Glyphs glyphs, Random rnd)
    {
        this.glyphs = glyphs;
        this.rnd = rnd;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        DoubleBuffered = true;

        buffer = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(buffer)) g.Clear(Color.Black);

        cols = bounds.Width / Glyphs.CellW + 1;
        rows = bounds.Height / Glyphs.CellH + 1;
        int cells = cols * rows;
        bright = new float[cells]; decay = new float[cells];
        glyph = new byte[cells]; drawnGlyph = new byte[cells];
        drawnLevel = new int[cells];
        isHead = new bool[cells];
        drops = new List<Drop>[cols];
        for (int c = 0; c < cols; c++) drops[c] = new List<Drop>();

        // Start mid-rain rather than on an empty screen.
        for (int i = 0; i < rows * 2; i++) Simulate();
    }

    void Simulate()
    {
        foreach (int i in heads) isHead[i] = false;
        heads.Clear();

        for (int i = 0; i < bright.Length; i++)
        {
            if (bright[i] <= 0) continue;
            bright[i] = Math.Max(0, bright[i] - decay[i]);
            if (rnd.Next(50) == 0) glyph[i] = (byte)rnd.Next(glyphs.Count);
        }

        for (int c = 0; c < cols; c++)
        {
            var list = drops[c];
            for (int d = list.Count - 1; d >= 0; d--)
            {
                var drop = list[d];
                int before = (int)Math.Floor(drop.Pos);
                drop.Pos += drop.Speed;
                int after = (int)Math.Floor(drop.Pos);
                for (int r = Math.Max(0, before + 1); r <= after && r < rows; r++)
                {
                    int i = r * cols + c;
                    bright[i] = 1;
                    decay[i] = drop.Decay;
                    glyph[i] = (byte)rnd.Next(glyphs.Count);
                }
                if (after >= 0 && after < rows)
                {
                    int i = after * cols + c;
                    isHead[i] = true;
                    heads.Add(i);
                    if (rnd.Next(3) == 0) glyph[i] = (byte)rnd.Next(glyphs.Count);
                }
                if (after >= rows) list.RemoveAt(d);
            }

            bool roomAtTop = list.Count == 0 || list[list.Count - 1].Pos > MinGap;
            if (roomAtTop && rnd.NextDouble() < SpawnChance)
            {
                float speed = 0.3f + (float)rnd.NextDouble() * 0.7f;
                int trail = rnd.Next(10, 50);
                list.Add(new Drop { Pos = -1, Speed = speed, Decay = speed / trail });
            }
        }
    }

    public void Step()
    {
        Simulate();

        // Redraw only cells whose look changed.
        using (var g = Graphics.FromImage(buffer))
        using (var black = new SolidBrush(Color.Black))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            for (int i = 0; i < bright.Length; i++)
            {
                int level = isHead[i] ? Glyphs.Levels + 1 : (int)Math.Ceiling(bright[i] * Glyphs.Levels);
                if (level == drawnLevel[i] && (level == 0 || glyph[i] == drawnGlyph[i])) continue;

                int x = (i % cols) * Glyphs.CellW, y = (i / cols) * Glyphs.CellH;
                if (level == 0) g.FillRectangle(black, x, y, Glyphs.CellW, Glyphs.CellH);
                else g.DrawImageUnscaled(glyphs.ByLevel[level][glyph[i]], x, y);
                drawnLevel[i] = level;
                drawnGlyph[i] = glyph[i];
            }
        }
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.CompositingMode = CompositingMode.SourceCopy;
        e.Graphics.DrawImageUnscaled(buffer, 0, 0);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (mouseStart == null) { mouseStart = e.Location; return; }
        if (Math.Abs(e.X - mouseStart.Value.X) > 10 || Math.Abs(e.Y - mouseStart.Value.Y) > 10)
            Application.Exit();
    }

    protected override void OnMouseDown(MouseEventArgs e) { Application.Exit(); }
    protected override void OnKeyDown(KeyEventArgs e) { Application.Exit(); }
}
