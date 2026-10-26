// Matrix digital rain screensaver, GPU version with glow.
// One OpenGL window spanning every monitor. Simulation on the CPU, drawing
// and bloom in shaders.
// Build: csc /target:winexe /optimize /out:MatrixGlow.scr MatrixGlow.cs
// Install: right-click MatrixGlow.scr -> Install.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "/s";
        bool selfTest = mode.StartsWith("/selftest");
        if (!selfTest && mode.StartsWith("/p")) return; // skip the tiny preview monitor
        if (!selfTest && mode.StartsWith("/c"))
        {
            MessageBox.Show("Nothing to configure.\n\nThere is no spoon.", "Matrix");
            return;
        }

        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch (EntryPointNotFoundException) { SetProcessDPIAware(); }

        // Self-test: tiny off-screen window, render a few frames, report via exit code.
        Rectangle bounds = selfTest ? new Rectangle(-4000, -4000, 320, 240) : SystemInformation.VirtualScreen;
        GlowForm form;
        try
        {
            form = new GlowForm(bounds, new Random());
            form.Show();
            for (int i = 0; i < 3; i++) form.Step();
            Gl.CheckError("render");
        }
        catch (Exception ex)
        {
            if (selfTest)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "matrixglow-selftest.txt"), ex.ToString());
                Environment.Exit(1);
            }
            MessageBox.Show(ex.Message, "Matrix");
            return;
        }
        if (selfTest) Environment.Exit(0);

        var timer = new Timer { Interval = 33 };
        timer.Tick += (s, e) => form.Step();
        Cursor.Hide();
        form.Activate();
        timer.Start();
        Application.Run(form);
    }
}

// The rain itself: drops falling down columns, lighting cells that then fade.
class Drop
{
    public float Pos, Speed, Decay;
}

class Rain
{
    const double SpawnChance = 0.03; // per column per frame
    const int MinGap = 6;            // rows between drops in one column

    public readonly int Cols, Rows;
    readonly Random rnd;
    readonly int glyphCount;
    readonly List<Drop>[] drops;
    readonly float[] bright, decay;
    readonly byte[] glyph;
    readonly bool[] isHead;
    readonly List<int> heads = new List<int>();

    public Rain(int cols, int rows, int glyphCount, Random rnd)
    {
        Cols = cols; Rows = rows;
        this.glyphCount = glyphCount;
        this.rnd = rnd;
        int cells = cols * rows;
        bright = new float[cells]; decay = new float[cells];
        glyph = new byte[cells];
        isHead = new bool[cells];
        drops = new List<Drop>[cols];
        for (int c = 0; c < cols; c++) drops[c] = new List<Drop>();

        // Start mid-rain rather than on an empty screen.
        for (int i = 0; i < rows * 2; i++) Step();
    }

    public void Step()
    {
        foreach (int i in heads) isHead[i] = false;
        heads.Clear();

        for (int i = 0; i < bright.Length; i++)
        {
            if (bright[i] <= 0) continue;
            bright[i] = Math.Max(0, bright[i] - decay[i]);
            if (rnd.Next(50) == 0) glyph[i] = (byte)rnd.Next(glyphCount);
        }

        for (int c = 0; c < Cols; c++)
        {
            var list = drops[c];
            for (int d = list.Count - 1; d >= 0; d--)
            {
                var drop = list[d];
                int before = (int)Math.Floor(drop.Pos);
                drop.Pos += drop.Speed;
                int after = (int)Math.Floor(drop.Pos);
                for (int r = Math.Max(0, before + 1); r <= after && r < Rows; r++)
                {
                    int i = r * Cols + c;
                    bright[i] = 1;
                    decay[i] = drop.Decay;
                    glyph[i] = (byte)rnd.Next(glyphCount);
                }
                if (after >= 0 && after < Rows)
                {
                    int i = after * Cols + c;
                    isHead[i] = true;
                    heads.Add(i);
                    if (rnd.Next(3) == 0) glyph[i] = (byte)rnd.Next(glyphCount);
                }
                if (after >= Rows) list.RemoveAt(d);
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

    // RGBA per cell for the GPU: glyph index, brightness, head flag.
    public void Fill(byte[] data)
    {
        for (int i = 0, o = 0; i < bright.Length; i++, o += 4)
        {
            data[o] = glyph[i];
            data[o + 1] = (byte)Math.Ceiling(bright[i] * 255);
            data[o + 2] = isHead[i] ? (byte)255 : (byte)0;
            data[o + 3] = 255;
        }
    }
}

// White glyphs on black in one strip, mirrored like the film (digits aren't).
static class Atlas
{
    public const int CellW = 14, CellH = 18;

    public static Bitmap Build(out int count)
    {
        var chars = new List<char>();
        for (char c = 'ｦ'; c <= 'ﾝ'; c++) chars.Add(c); // half-width katakana
        foreach (char c in "0123456789Z:.=*+-<>|\"") chars.Add(c);
        count = chars.Count;

        var bmp = new Bitmap(CellW * count, CellH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var font = new Font("MS Gothic", 16, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            for (int i = 0; i < count; i++)
            {
                g.ResetTransform();
                if (char.IsDigit(chars[i])) g.TranslateTransform(i * CellW, 0);
                else { g.TranslateTransform((i + 1) * CellW, 0); g.ScaleTransform(-1, 1); }
                g.DrawString(chars[i].ToString(), font, Brushes.White, new RectangleF(0, 0, CellW, CellH), fmt);
            }
        }
        return bmp;
    }
}

static class Shaders
{
    // Fullscreen triangle from gl_VertexID, no vertex buffers needed.
    public const string Vertex = @"#version 330
out vec2 uv;
void main() {
    vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    uv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

    public const string Scene = @"#version 330
uniform sampler2D cells;
uniform sampler2D atlas;
uniform vec2 cellSize;
uniform float screenH;
out vec4 color;
void main() {
    vec2 p = vec2(gl_FragCoord.x, screenH - gl_FragCoord.y); // top-left origin
    vec4 c = texelFetch(cells, ivec2(p / cellSize), 0);
    float b = c.g;
    if (b <= 0.0) { color = vec4(0.0, 0.0, 0.0, 1.0); return; }
    int g = int(c.r * 255.0 + 0.5);
    ivec2 local = ivec2(mod(p, cellSize));
    float a = texelFetch(atlas, ivec2(g * int(cellSize.x) + local.x, local.y), 0).r;
    vec3 trail = vec3(0.5 * b * b * b, 0.16 + 0.84 * b, 0.1 + 0.65 * pow(b, 1.5));
    vec3 head = vec3(0.8, 1.0, 0.96);
    color = vec4(mix(trail, head, c.b) * a, 1.0);
}";

    // Quarter-res downsample (4 bilinear taps cover a 4x4 block), squared so
    // the bright heads dominate the glow.
    public const string Bright = @"#version 330
uniform sampler2D src;
uniform vec2 texel;
in vec2 uv;
out vec4 color;
void main() {
    vec3 s = texture(src, uv + texel * vec2(-1.0, -1.0)).rgb
           + texture(src, uv + texel * vec2( 1.0, -1.0)).rgb
           + texture(src, uv + texel * vec2(-1.0,  1.0)).rgb
           + texture(src, uv + texel * vec2( 1.0,  1.0)).rgb;
    s *= 0.25;
    color = vec4(s * s, 1.0);
}";

    public const string Blur = @"#version 330
uniform sampler2D src;
uniform vec2 dir;
in vec2 uv;
out vec4 color;
const float w[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);
void main() {
    vec3 s = texture(src, uv).rgb * w[0];
    for (int i = 1; i < 5; i++) {
        s += texture(src, uv + dir * float(i)).rgb * w[i];
        s += texture(src, uv - dir * float(i)).rgb * w[i];
    }
    color = vec4(s, 1.0);
}";

    public const string Composite = @"#version 330
uniform sampler2D scene;
uniform sampler2D bloom;
uniform float strength;
in vec2 uv;
out vec4 color;
void main() {
    color = vec4(texture(scene, uv).rgb + texture(bloom, uv).rgb * strength, 1.0);
}";
}

class GlowForm : Form
{
    const float GlowStrength = 3.2f;

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);

    readonly Rain rain;
    readonly byte[] cellData;
    readonly int w, h, bw, bh;
    IntPtr hdc;
    uint cellsTex, atlasTex, sceneTex, sceneFbo, bloomA, bloomB, fboA, fboB;
    uint sceneProg, brightProg, blurProg, compProg;
    int uBlurDir;
    Point? mouseStart;

    public GlowForm(Rectangle bounds, Random rnd)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        w = bounds.Width; h = bounds.Height;
        bw = (w + 3) / 4; bh = (h + 3) / 4;
        int glyphCount;
        using (Atlas.Build(out glyphCount)) { }
        rain = new Rain(w / Atlas.CellW + 1, h / Atlas.CellH + 1, glyphCount, rnd);
        cellData = new byte[rain.Cols * rain.Rows * 4];
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20; return cp; } // CS_OWNDC
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        hdc = GetDC(Handle);
        Gl.CreateContext(hdc);

        uint vao;
        Gl.GenVertexArrays(1, out vao);
        Gl.BindVertexArray(vao);

        uint vs = Gl.Compile(Gl.VERTEX_SHADER, Shaders.Vertex);
        sceneProg = Gl.Link(vs, Gl.Compile(Gl.FRAGMENT_SHADER, Shaders.Scene));
        brightProg = Gl.Link(vs, Gl.Compile(Gl.FRAGMENT_SHADER, Shaders.Bright));
        blurProg = Gl.Link(vs, Gl.Compile(Gl.FRAGMENT_SHADER, Shaders.Blur));
        compProg = Gl.Link(vs, Gl.Compile(Gl.FRAGMENT_SHADER, Shaders.Composite));

        Gl.UseProgram(sceneProg);
        Gl.Uniform1i(Gl.GetUniformLocation(sceneProg, "cells"), 0);
        Gl.Uniform1i(Gl.GetUniformLocation(sceneProg, "atlas"), 1);
        Gl.Uniform2f(Gl.GetUniformLocation(sceneProg, "cellSize"), Atlas.CellW, Atlas.CellH);
        Gl.Uniform1f(Gl.GetUniformLocation(sceneProg, "screenH"), h);
        Gl.UseProgram(brightProg);
        Gl.Uniform1i(Gl.GetUniformLocation(brightProg, "src"), 0);
        Gl.Uniform2f(Gl.GetUniformLocation(brightProg, "texel"), 1f / w, 1f / h);
        Gl.UseProgram(blurProg);
        Gl.Uniform1i(Gl.GetUniformLocation(blurProg, "src"), 0);
        uBlurDir = Gl.GetUniformLocation(blurProg, "dir");
        Gl.UseProgram(compProg);
        Gl.Uniform1i(Gl.GetUniformLocation(compProg, "scene"), 0);
        Gl.Uniform1i(Gl.GetUniformLocation(compProg, "bloom"), 1);
        Gl.Uniform1f(Gl.GetUniformLocation(compProg, "strength"), GlowStrength);

        Gl.glPixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        cellsTex = Gl.Texture(rain.Cols, rain.Rows, Gl.RGBA8, Gl.UNSIGNED_BYTE, Gl.NEAREST);
        int count;
        using (var atlas = Atlas.Build(out count))
        {
            var data = atlas.LockBits(new Rectangle(0, 0, atlas.Width, atlas.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            atlasTex = Gl.Texture(atlas.Width, atlas.Height, Gl.RGBA8, Gl.UNSIGNED_BYTE, Gl.NEAREST);
            Gl.glTexSubImage2D(Gl.TEXTURE_2D, 0, 0, 0, atlas.Width, atlas.Height, Gl.BGRA, Gl.UNSIGNED_BYTE, data.Scan0);
            atlas.UnlockBits(data);
        }
        sceneTex = Gl.Texture(w, h, Gl.RGBA8, Gl.UNSIGNED_BYTE, Gl.NEAREST);
        bloomA = Gl.Texture(bw, bh, Gl.RGBA16F, Gl.FLOAT, Gl.LINEAR);
        bloomB = Gl.Texture(bw, bh, Gl.RGBA16F, Gl.FLOAT, Gl.LINEAR);
        sceneFbo = Gl.Framebuffer(sceneTex);
        fboA = Gl.Framebuffer(bloomA);
        fboB = Gl.Framebuffer(bloomB);
        Gl.CheckError("setup");
    }

    public void Step()
    {
        rain.Step();
        rain.Fill(cellData);

        Gl.Bind(0, cellsTex);
        Gl.glTexSubImage2D(Gl.TEXTURE_2D, 0, 0, 0, rain.Cols, rain.Rows, Gl.RGBA, Gl.UNSIGNED_BYTE, cellData);

        // 1. Characters at full resolution.
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, sceneFbo);
        Gl.glViewport(0, 0, w, h);
        Gl.UseProgram(sceneProg);
        Gl.Bind(1, atlasTex);
        Gl.glDrawArrays(Gl.TRIANGLES, 0, 3);

        // 2. Bright parts at quarter resolution.
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, fboA);
        Gl.glViewport(0, 0, bw, bh);
        Gl.UseProgram(brightProg);
        Gl.Bind(0, sceneTex);
        Gl.glDrawArrays(Gl.TRIANGLES, 0, 3);

        // 3. Blur, twice, the second pass wider.
        Gl.UseProgram(blurProg);
        for (int pass = 1; pass <= 2; pass++)
        {
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, fboB);
            Gl.Bind(0, bloomA);
            Gl.Uniform2f(uBlurDir, pass / (float)bw, 0);
            Gl.glDrawArrays(Gl.TRIANGLES, 0, 3);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, fboA);
            Gl.Bind(0, bloomB);
            Gl.Uniform2f(uBlurDir, 0, pass / (float)bh);
            Gl.glDrawArrays(Gl.TRIANGLES, 0, 3);
        }

        // 4. Characters plus glow, to the window.
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        Gl.glViewport(0, 0, w, h);
        Gl.UseProgram(compProg);
        Gl.Bind(0, sceneTex);
        Gl.Bind(1, bloomA);
        Gl.glDrawArrays(Gl.TRIANGLES, 0, 3);

        Gl.SwapBuffers(hdc);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (mouseStart == null) { mouseStart = e.Location; return; }
        if (Math.Abs(e.X - mouseStart.Value.X) > 10 || Math.Abs(e.Y - mouseStart.Value.Y) > 10)
            Application.Exit();
    }

    protected override void OnMouseDown(MouseEventArgs e) { Application.Exit(); }
    protected override void OnKeyDown(KeyEventArgs e) { Application.Exit(); }
}

// Just enough OpenGL. GL 1.1 comes straight from opengl32.dll; anything newer
// is looked up with wglGetProcAddress once a context exists.
static class Gl
{
    public const uint TEXTURE_2D = 0x0DE1, TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800,
        TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803, CLAMP_TO_EDGE = 0x812F,
        NEAREST = 0x2600, LINEAR = 0x2601, RGBA = 0x1908, BGRA = 0x80E1, RGBA8 = 0x8058, RGBA16F = 0x881A,
        UNSIGNED_BYTE = 0x1401, FLOAT = 0x1406, UNPACK_ALIGNMENT = 0x0CF5, TRIANGLES = 0x0004,
        FRAGMENT_SHADER = 0x8B30, VERTEX_SHADER = 0x8B31, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82,
        FRAMEBUFFER = 0x8D40, COLOR_ATTACHMENT0 = 0x8CE0, FRAMEBUFFER_COMPLETE = 0x8CD5, TEXTURE0 = 0x84C0;

    [StructLayout(LayoutKind.Sequential)]
    struct PixelFormatDescriptor
    {
        public ushort nSize, nVersion;
        public uint dwFlags;
        public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift,
            cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
            cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(IntPtr hdc, ref PixelFormatDescriptor pfd);
    [DllImport("gdi32.dll")] static extern bool SetPixelFormat(IntPtr hdc, int format, ref PixelFormatDescriptor pfd);
    [DllImport("gdi32.dll")] public static extern bool SwapBuffers(IntPtr hdc);
    [DllImport("opengl32.dll")] static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll")] static extern IntPtr wglGetProcAddress(string name);

    [DllImport("opengl32.dll")] public static extern void glViewport(int x, int y, int w, int h);
    [DllImport("opengl32.dll")] public static extern void glDrawArrays(uint mode, int first, int count);
    [DllImport("opengl32.dll")] public static extern void glPixelStorei(uint pname, int param);
    [DllImport("opengl32.dll")] static extern uint glGetError();
    [DllImport("opengl32.dll")] static extern void glGenTextures(int n, out uint tex);
    [DllImport("opengl32.dll")] static extern void glBindTexture(uint target, uint tex);
    [DllImport("opengl32.dll")] static extern void glTexParameteri(uint target, uint pname, int param);
    [DllImport("opengl32.dll")] static extern void glTexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, IntPtr data);
    [DllImport("opengl32.dll")] public static extern void glTexSubImage2D(uint target, int level, int x, int y, int w, int h, uint format, uint type, IntPtr data);
    [DllImport("opengl32.dll")] public static extern void glTexSubImage2D(uint target, int level, int x, int y, int w, int h, uint format, uint type, byte[] data);

    public delegate uint CreateShaderFn(uint type);
    public delegate void ShaderSourceFn(uint shader, int count, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] src, int[] lengths);
    public delegate void UintFn(uint id);
    public delegate void GetivFn(uint id, uint pname, out int value);
    public delegate void GetLogFn(uint id, int max, out int length, StringBuilder log);
    public delegate uint CreateProgramFn();
    public delegate void AttachShaderFn(uint program, uint shader);
    public delegate int GetUniformLocationFn(uint program, string name);
    public delegate void Uniform1iFn(int loc, int v);
    public delegate void Uniform1fFn(int loc, float v);
    public delegate void Uniform2fFn(int loc, float x, float y);
    public delegate void GenFn(int n, out uint id);
    public delegate void BindFramebufferFn(uint target, uint fbo);
    public delegate void FramebufferTexture2DFn(uint target, uint attachment, uint textarget, uint tex, int level);
    public delegate uint CheckFramebufferStatusFn(uint target);
    public delegate bool SwapIntervalFn(int interval);

    public static CreateShaderFn CreateShader;
    public static ShaderSourceFn ShaderSource;
    public static UintFn CompileShader, LinkProgram, UseProgram, ActiveTexture, BindVertexArray;
    public static GetivFn GetShaderiv, GetProgramiv;
    public static GetLogFn GetShaderInfoLog, GetProgramInfoLog;
    public static CreateProgramFn CreateProgram;
    public static AttachShaderFn AttachShader;
    public static GetUniformLocationFn GetUniformLocation;
    public static Uniform1iFn Uniform1i;
    public static Uniform1fFn Uniform1f;
    public static Uniform2fFn Uniform2f;
    public static GenFn GenFramebuffers, GenVertexArrays;
    public static BindFramebufferFn BindFramebuffer;
    public static FramebufferTexture2DFn FramebufferTexture2D;
    public static CheckFramebufferStatusFn CheckFramebufferStatus;

    public static void CreateContext(IntPtr hdc)
    {
        var pfd = new PixelFormatDescriptor
        {
            nSize = (ushort)Marshal.SizeOf(typeof(PixelFormatDescriptor)),
            nVersion = 1,
            dwFlags = 0x04 | 0x20 | 0x01, // draw to window, OpenGL, double buffered
            cColorBits = 32,
        };
        int format = ChoosePixelFormat(hdc, ref pfd);
        if (format == 0 || !SetPixelFormat(hdc, format, ref pfd)) throw new Exception("No usable OpenGL pixel format.");
        IntPtr ctx = wglCreateContext(hdc);
        if (ctx == IntPtr.Zero || !wglMakeCurrent(hdc, ctx)) throw new Exception("Couldn't create an OpenGL context.");

        CreateShader = Load<CreateShaderFn>("glCreateShader");
        ShaderSource = Load<ShaderSourceFn>("glShaderSource");
        CompileShader = Load<UintFn>("glCompileShader");
        GetShaderiv = Load<GetivFn>("glGetShaderiv");
        GetShaderInfoLog = Load<GetLogFn>("glGetShaderInfoLog");
        CreateProgram = Load<CreateProgramFn>("glCreateProgram");
        AttachShader = Load<AttachShaderFn>("glAttachShader");
        LinkProgram = Load<UintFn>("glLinkProgram");
        GetProgramiv = Load<GetivFn>("glGetProgramiv");
        GetProgramInfoLog = Load<GetLogFn>("glGetProgramInfoLog");
        UseProgram = Load<UintFn>("glUseProgram");
        GetUniformLocation = Load<GetUniformLocationFn>("glGetUniformLocation");
        Uniform1i = Load<Uniform1iFn>("glUniform1i");
        Uniform1f = Load<Uniform1fFn>("glUniform1f");
        Uniform2f = Load<Uniform2fFn>("glUniform2f");
        ActiveTexture = Load<UintFn>("glActiveTexture");
        GenFramebuffers = Load<GenFn>("glGenFramebuffers");
        BindFramebuffer = Load<BindFramebufferFn>("glBindFramebuffer");
        FramebufferTexture2D = Load<FramebufferTexture2DFn>("glFramebufferTexture2D");
        CheckFramebufferStatus = Load<CheckFramebufferStatusFn>("glCheckFramebufferStatus");
        GenVertexArrays = Load<GenFn>("glGenVertexArrays");
        BindVertexArray = Load<UintFn>("glBindVertexArray");

        // Vsync on, so frames don't tear.
        IntPtr swap = wglGetProcAddress("wglSwapIntervalEXT");
        if (swap != IntPtr.Zero)
            ((SwapIntervalFn)Marshal.GetDelegateForFunctionPointer(swap, typeof(SwapIntervalFn)))(1);
    }

    static T Load<T>(string name)
    {
        IntPtr p = wglGetProcAddress(name);
        long v = p.ToInt64();
        if (v >= -1 && v <= 3) throw new Exception("This graphics driver lacks " + name + " (needs OpenGL 3.3).");
        return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
    }

    public static uint Compile(uint type, string source)
    {
        uint s = CreateShader(type);
        ShaderSource(s, 1, new[] { source }, null);
        CompileShader(s);
        int ok;
        GetShaderiv(s, COMPILE_STATUS, out ok);
        if (ok == 0)
        {
            var log = new StringBuilder(4096);
            int len;
            GetShaderInfoLog(s, log.Capacity, out len, log);
            throw new Exception("Shader compile failed:\n" + log);
        }
        return s;
    }

    public static uint Link(uint vs, uint fs)
    {
        uint p = CreateProgram();
        AttachShader(p, vs);
        AttachShader(p, fs);
        LinkProgram(p);
        int ok;
        GetProgramiv(p, LINK_STATUS, out ok);
        if (ok == 0)
        {
            var log = new StringBuilder(4096);
            int len;
            GetProgramInfoLog(p, log.Capacity, out len, log);
            throw new Exception("Shader link failed:\n" + log);
        }
        return p;
    }

    public static uint Texture(int w, int h, uint internalFormat, uint type, uint filter)
    {
        uint t;
        glGenTextures(1, out t);
        glBindTexture(TEXTURE_2D, t);
        glTexParameteri(TEXTURE_2D, TEXTURE_MIN_FILTER, (int)filter);
        glTexParameteri(TEXTURE_2D, TEXTURE_MAG_FILTER, (int)filter);
        glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_S, (int)CLAMP_TO_EDGE);
        glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_T, (int)CLAMP_TO_EDGE);
        glTexImage2D(TEXTURE_2D, 0, (int)internalFormat, w, h, 0, RGBA, type, IntPtr.Zero);
        return t;
    }

    public static uint Framebuffer(uint tex)
    {
        uint fbo;
        GenFramebuffers(1, out fbo);
        BindFramebuffer(FRAMEBUFFER, fbo);
        FramebufferTexture2D(FRAMEBUFFER, COLOR_ATTACHMENT0, TEXTURE_2D, tex, 0);
        if (CheckFramebufferStatus(FRAMEBUFFER) != FRAMEBUFFER_COMPLETE) throw new Exception("Framebuffer incomplete.");
        return fbo;
    }

    public static void Bind(int unit, uint tex)
    {
        ActiveTexture(TEXTURE0 + (uint)unit);
        glBindTexture(TEXTURE_2D, tex);
    }

    public static void CheckError(string where)
    {
        uint err = glGetError();
        if (err != 0) throw new Exception("OpenGL error 0x" + err.ToString("X") + " during " + where + ".");
    }
}
