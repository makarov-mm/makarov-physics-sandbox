using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MakarovPhysicsSandbox;

// Minimal immediate-mode 2D UI renderer in pure OpenGL. It owns one dynamic quad batch and a
// bitmap-font atlas baked once from a system font via GDI+ (the same one-off rasterisation trick
// the icon bitmaps use). Every per-frame call is plain GL with no WinForms controls. Coordinates
// are screen pixels with the origin at the top-left; the vertex shader converts them to NDC.
//
// A single shader draws both solid rectangles and text: each vertex carries a "use texture" flag,
// so coloured panels and glyph quads can share one draw call and keep painter's-order layering.
internal sealed class UiRenderer
{
    private uint _program, _vao, _vbo, _atlas;
    private int _uScreenW, _uScreenH, _uTex;
    private readonly List<float> _verts = new(8192);
    private int _screenW = 1, _screenH = 1;

    private const int FirstChar = 32, LastChar = 126;
    private const int AtlasCols = 16;
    private const int FloatsPerVertex = 9; // pos2, uv2, rgba4, useTex1

    private int _cellW, _cellH, _atlasW, _atlasH, _glyphH;
    private readonly float[] _glyphAdvance = new float[LastChar - FirstChar + 1];
    private readonly float[] _glyphW = new float[LastChar - FirstChar + 1];

    // Natural (scale 1.0) line height in pixels.
    public float LineHeight => _glyphH;

    public void Init(int fontPx = 26)
    {
        _program = Shaders.Build(Shaders.UiVertex, Shaders.UiFragment);
        _uScreenW = GL.GetUniformLocation(_program, "uScreenW");
        _uScreenH = GL.GetUniformLocation(_program, "uScreenH");
        _uTex = GL.GetUniformLocation(_program, "uTex");

        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);
        _vbo = GL.GenBuffer();
        GL.BindBuffer(GL.ARRAY_BUFFER, _vbo);
        int stride = FloatsPerVertex * sizeof(float);
        GL.VertexAttribPointer(0, 2, GL.FLOAT, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, GL.FLOAT, false, stride, 2 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 4, GL.FLOAT, false, stride, 4 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(3, 1, GL.FLOAT, false, stride, 8 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.BindVertexArray(0);

        BakeFontAtlas(fontPx);
    }

    private void BakeFontAtlas(int fontPx)
    {
        int count = LastChar - FirstChar + 1;
        int rows = (count + AtlasCols - 1) / AtlasCols;

        using var probe = new Bitmap(8, 8);
        using var pg = Graphics.FromImage(probe);
        using var font = new Font("Consolas", fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        var fmt = StringFormat.GenericTypographic;

        var widths = new float[count];
        float maxW = 0f, maxH = 0f;
        for (int i = 0; i < count; i++)
        {
            char c = (char)(FirstChar + i);
            var s = pg.MeasureString(c.ToString(), font, PointF.Empty, fmt);
            // MeasureString gives 0 width for space; fall back to a quarter em.
            float w = c == ' ' ? fontPx * 0.3f : s.Width;
            widths[i] = w;
            if (w > maxW) maxW = w;
            if (s.Height > maxH) maxH = s.Height;
        }

        _cellW = (int)Math.Ceiling(maxW) + 2;
        _cellH = (int)Math.Ceiling(maxH) + 2;
        _glyphH = _cellH - 2;
        _atlasW = _cellW * AtlasCols;
        _atlasH = _cellH * rows;

        var px = new byte[_atlasW * _atlasH * 4];
        using (var bmp = new Bitmap(_atlasW, _atlasH, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(0, 0, 0, 0));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                using var brush = new SolidBrush(Color.White);
                for (int i = 0; i < count; i++)
                {
                    int col = i % AtlasCols, row = i / AtlasCols;
                    char c = (char)(FirstChar + i);
                    if (c != ' ')
                        g.DrawString(c.ToString(), font, brush, col * _cellW + 1, row * _cellH + 1, fmt);
                    _glyphW[i] = widths[i];
                    _glyphAdvance[i] = widths[i] + 1f;
                }
            }

            var bits = bmp.LockBits(new Rectangle(0, 0, _atlasW, _atlasH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(bits.Scan0, px, 0, px.Length);
            bmp.UnlockBits(bits);
        }

        // GDI+ stores BGRA; we only need the coverage. Force RGB to white and keep alpha as the
        // glyph mask so the shader can tint freely.
        for (int i = 0; i < _atlasW * _atlasH; i++)
        {
            byte a = px[i * 4 + 3];
            px[i * 4 + 0] = 255;
            px[i * 4 + 1] = 255;
            px[i * 4 + 2] = 255;
            px[i * 4 + 3] = a;
        }

        _atlas = GL.GenTexture();
        GL.BindTexture(GL.TEXTURE_2D, _atlas);
        GL.TexImage2D(GL.TEXTURE_2D, 0, GL.RGBA8, _atlasW, _atlasH, 0, GL.RGBA, GL.UNSIGNED_BYTE, px);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_EDGE);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_EDGE);
        GL.BindTexture(GL.TEXTURE_2D, 0);
    }

    public void Begin(int screenW, int screenH)
    {
        _screenW = Math.Max(1, screenW);
        _screenH = Math.Max(1, screenH);
        _verts.Clear();
    }

    private void PushVertex(float x, float y, float u, float v, float r, float g, float b, float a, float useTex)
    {
        _verts.Add(x); _verts.Add(y);
        _verts.Add(u); _verts.Add(v);
        _verts.Add(r); _verts.Add(g); _verts.Add(b); _verts.Add(a);
        _verts.Add(useTex);
    }

    private void PushQuad(float x, float y, float w, float h, float u0, float v0, float u1, float v1,
                          float r, float g, float b, float a, float useTex)
    {
        PushVertex(x, y, u0, v0, r, g, b, a, useTex);
        PushVertex(x + w, y, u1, v0, r, g, b, a, useTex);
        PushVertex(x + w, y + h, u1, v1, r, g, b, a, useTex);
        PushVertex(x, y, u0, v0, r, g, b, a, useTex);
        PushVertex(x + w, y + h, u1, v1, r, g, b, a, useTex);
        PushVertex(x, y + h, u0, v1, r, g, b, a, useTex);
    }

    public void DrawRect(float x, float y, float w, float h, float r, float g, float b, float a)
        => PushQuad(x, y, w, h, 0f, 0f, 0f, 0f, r, g, b, a, 0f);

    public float MeasureText(string s, float scale)
    {
        float w = 0f;
        foreach (char c in s)
        {
            int i = (c < FirstChar || c > LastChar) ? ('?' - FirstChar) : (c - FirstChar);
            w += _glyphAdvance[i] * scale;
        }
        return w;
    }

    // Returns s, or a truncated "prefix..." that fits within maxWidth at the given scale.
    public string Ellipsize(string s, float maxWidth, float scale)
    {
        if (string.IsNullOrEmpty(s) || MeasureText(s, scale) <= maxWidth) return s;
        const string dots = "...";
        float dotsW = MeasureText(dots, scale);
        for (int len = s.Length - 1; len > 0; len--)
        {
            if (MeasureText(s.Substring(0, len), scale) + dotsW <= maxWidth)
                return s.Substring(0, len) + dots;
        }
        return dots;
    }

    // Draws text with the top-left of the line at (x, y).
    public void DrawText(float x, float y, string s, float r, float g, float b, float a, float scale = 1f)
    {
        float cx = x;
        float qh = _glyphH * scale;
        foreach (char c in s)
        {
            int i = (c < FirstChar || c > LastChar) ? ('?' - FirstChar) : (c - FirstChar);
            if (c != ' ')
            {
                int col = i % AtlasCols, row = i / AtlasCols;
                float gw = _glyphW[i];
                float u0 = (col * _cellW + 1) / (float)_atlasW;
                float v0 = (row * _cellH + 1) / (float)_atlasH;
                float u1 = (col * _cellW + 1 + gw) / (float)_atlasW;
                float v1 = (row * _cellH + 1 + _glyphH) / (float)_atlasH;
                PushQuad(cx, y, gw * scale, qh, u0, v0, u1, v1, r, g, b, a, 1f);
            }
            cx += _glyphAdvance[i] * scale;
        }
    }

    public void Flush()
    {
        if (_verts.Count == 0) return;
        GL.UseProgram(_program);
        GL.Uniform1(_uScreenW, (float)_screenW);
        GL.Uniform1(_uScreenH, (float)_screenH);
        GL.ActiveTexture(GL.TEXTURE0);
        GL.BindTexture(GL.TEXTURE_2D, _atlas);
        GL.Uniform1(_uTex, 0);

        GL.BindVertexArray(_vao);
        GL.BindBuffer(GL.ARRAY_BUFFER, _vbo);
        GL.BufferData(GL.ARRAY_BUFFER, _verts.ToArray(), GL.STATIC_DRAW);
        GL.DrawArrays(GL.TRIANGLES, 0, _verts.Count / FloatsPerVertex);
        GL.BindVertexArray(0);
        _verts.Clear();
    }

    // Full-colour icons live in their own GL textures (loaded once from PNG via GDI+). Each icon is a
    // self-contained single-quad draw, so it must be called after Flush(), not mixed into a batch.
    private readonly Dictionary<string, uint> _iconTextures = new();

    public void DrawIcon(float x, float y, float w, float h, string iconPath)
    {
        uint tex = GetIconTexture(iconPath);
        if (tex == 0) return;   // missing icon: row stays text-only

        _verts.Clear();
        PushQuad(x, y, w, h, 0f, 0f, 1f, 1f, 1f, 1f, 1f, 1f, 2f);   // useTex = 2 (full-colour)
        GL.UseProgram(_program);
        GL.Uniform1(_uScreenW, (float)_screenW);
        GL.Uniform1(_uScreenH, (float)_screenH);
        GL.ActiveTexture(GL.TEXTURE0);
        GL.BindTexture(GL.TEXTURE_2D, tex);
        GL.Uniform1(_uTex, 0);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(GL.ARRAY_BUFFER, _vbo);
        GL.BufferData(GL.ARRAY_BUFFER, _verts.ToArray(), GL.STATIC_DRAW);
        GL.DrawArrays(GL.TRIANGLES, 0, _verts.Count / FloatsPerVertex);
        GL.BindVertexArray(0);
        _verts.Clear();
    }

    private uint GetIconTexture(string path)
    {
        if (_iconTextures.TryGetValue(path, out uint cached)) return cached;
        uint tex = 0;
        try
        {
            if (System.IO.File.Exists(path))
            {
                using var src = new Bitmap(path);
                using var bmp = new Bitmap(src, new Size(64, 64));
                var bits = bmp.LockBits(new Rectangle(0, 0, 64, 64), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var px = new byte[64 * 64 * 4];
                Marshal.Copy(bits.Scan0, px, 0, px.Length);
                bmp.UnlockBits(bits);
                for (int i = 0; i < 64 * 64; i++)
                {
                    byte b = px[i * 4 + 0];
                    byte r = px[i * 4 + 2];
                    px[i * 4 + 0] = r;   // BGRA -> RGBA
                    px[i * 4 + 2] = b;
                }
                tex = GL.GenTexture();
                GL.BindTexture(GL.TEXTURE_2D, tex);
                GL.TexImage2D(GL.TEXTURE_2D, 0, GL.RGBA8, 64, 64, 0, GL.RGBA, GL.UNSIGNED_BYTE, px);
                GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR);
                GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
                GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_EDGE);
                GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_EDGE);
                GL.BindTexture(GL.TEXTURE_2D, 0);
            }
        }
        catch { tex = 0; }
        _iconTextures[path] = tex;
        return tex;
    }
}
