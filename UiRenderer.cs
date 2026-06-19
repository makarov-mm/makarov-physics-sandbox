using System;
using System.Collections.Generic;

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
        var px = GdiPlusImage.BakeFontAtlasRgba("Consolas", fontPx, FirstChar, LastChar, AtlasCols,
            out _cellW, out _cellH, out _atlasW, out _atlasH, out _glyphH,
            out var glyphW, out var glyphAdvance);
        Array.Copy(glyphW, _glyphW, _glyphW.Length);
        Array.Copy(glyphAdvance, _glyphAdvance, _glyphAdvance.Length);

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
                var px = GdiPlusImage.LoadRgbaScaled(path, 64, 64); // RGBA, 64x64
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
