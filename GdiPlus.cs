using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MakarovPhysicsSandbox;

// Image loading via the raw GDI+ flat API (gdiplus.dll), so no managed System.Drawing dependency
// is needed. This is part of making the app Native-AOT compatible: System.Drawing.Common is not
// AOT-supported, so the few places that decode PNGs go through these P/Invokes instead.
//
// GDI+ must be initialised once per process with GdiplusStartup before any Gdip* call. The startup
// is reference counted, so during the migration it can safely coexist with System.Drawing's own
// internal startup (still used by the font atlas); once that is gone this is the only initialiser.
internal static class GdiPlusImage
{
    // GDI+ pixel format and lock-mode constants (from gdipluspixelformats.h / gdiplusimaging.h).
    private const int PixelFormat32bppARGB = 0x0026200A;
    private const uint ImageLockModeRead = 1;
    private const int InterpolationModeHighQualityBicubic = 7;
    private const int FontStyleRegular = 0;
    private const int UnitPixel = 2;
    private const int TextRenderingHintAntiAlias = 4;

    private static readonly object Gate = new();
    private static bool _started;
    private static IntPtr _token;

    internal static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started) return;
            var input = new GdiplusStartupInput { GdiplusVersion = 1 };
            // status 0 == Ok; if it fails we leave _started false and the Gdip* calls below will throw.
            _started = GdiplusStartup(out _token, ref input, IntPtr.Zero) == 0;
        }
    }

    internal static void Shutdown()
    {
        lock (Gate)
        {
            if (!_started) return;
            GdiplusShutdown(_token);
            _started = false;
            _token = IntPtr.Zero;
        }
    }

    /// <summary>Decode an image file at its native size into tightly packed RGBA8 (top row first).</summary>
    internal static byte[] LoadRgba(string path, out int width, out int height)
    {
        EnsureStarted();
        if (GdipCreateBitmapFromFile(path, out IntPtr bmp) != 0 || bmp == IntPtr.Zero)
            throw new IOException($"GDI+ could not load image: {path}");
        try
        {
            GdipGetImageWidth(bmp, out uint w);
            GdipGetImageHeight(bmp, out uint h);
            width = (int)w;
            height = (int)h;
            return LockToRgba(bmp, width, height);
        }
        finally { GdipDisposeImage(bmp); }
    }

    /// <summary>Decode and scale an image file to dstW x dstH, returning tightly packed RGBA8.</summary>
    internal static byte[] LoadRgbaScaled(string path, int dstW, int dstH)
    {
        EnsureStarted();
        if (GdipCreateBitmapFromFile(path, out IntPtr src) != 0 || src == IntPtr.Zero)
            throw new IOException($"GDI+ could not load image: {path}");
        try
        {
            if (GdipCreateBitmapFromScan0(dstW, dstH, 0, PixelFormat32bppARGB, IntPtr.Zero, out IntPtr dst) != 0 || dst == IntPtr.Zero)
                throw new IOException("GDI+ could not create destination bitmap.");
            try
            {
                if (GdipGetImageGraphicsContext(dst, out IntPtr g) == 0 && g != IntPtr.Zero)
                {
                    GdipSetInterpolationMode(g, InterpolationModeHighQualityBicubic);
                    GdipDrawImageRectI(g, src, 0, 0, dstW, dstH);
                    GdipDeleteGraphics(g);
                }
                return LockToRgba(dst, dstW, dstH);
            }
            finally { GdipDisposeImage(dst); }
        }
        finally { GdipDisposeImage(src); }
    }

    /// <summary>
    /// Bake a monospace-friendly glyph atlas for chars [firstChar..lastChar] laid out in a grid of
    /// <paramref name="cols"/> columns, using the same GDI+ rasteriser (AntiAlias + typographic
    /// metrics) the managed System.Drawing path used. Returns the finished RGBA8 bytes (white glyphs,
    /// alpha = coverage) plus the cell/atlas geometry and per-glyph width/advance tables.
    /// </summary>
    internal static byte[] BakeFontAtlasRgba(string fontName, int fontPx, int firstChar, int lastChar, int cols,
        out int cellW, out int cellH, out int atlasW, out int atlasH, out int glyphH,
        out float[] glyphW, out float[] glyphAdvance)
    {
        EnsureStarted();

        int count = lastChar - firstChar + 1;
        int rows = (count + cols - 1) / cols;
        glyphW = new float[count];
        glyphAdvance = new float[count];

        Check(GdipCreateFontFamilyFromName(fontName, IntPtr.Zero, out IntPtr family), "CreateFontFamily");
        IntPtr font = IntPtr.Zero, fmt = IntPtr.Zero, probeBmp = IntPtr.Zero, probeG = IntPtr.Zero;
        try
        {
            Check(GdipCreateFont(family, fontPx, FontStyleRegular, UnitPixel, out font), "CreateFont");
            Check(GdipStringFormatGetGenericTypographic(out fmt), "GenericTypographic");

            // Tiny offscreen graphics just for measuring.
            Check(GdipCreateBitmapFromScan0(8, 8, 0, PixelFormat32bppARGB, IntPtr.Zero, out probeBmp), "probe bitmap");
            Check(GdipGetImageGraphicsContext(probeBmp, out probeG), "probe graphics");

            var widths = new float[count];
            float maxW = 0f, maxH = 0f;
            for (int i = 0; i < count; i++)
            {
                char c = (char)(firstChar + i);
                var layout = new RectF { X = 0f, Y = 0f, Width = 8192f, Height = 8192f };
                GdipMeasureString(probeG, c.ToString(), 1, font, ref layout, fmt, out RectF box, out _, out _);
                // MeasureString reports ~0 width for space; fall back to a quarter em (matches old code).
                float w = c == ' ' ? fontPx * 0.3f : box.Width;
                widths[i] = w;
                if (w > maxW) maxW = w;
                if (box.Height > maxH) maxH = box.Height;
            }

            cellW = (int)Math.Ceiling(maxW) + 2;
            cellH = (int)Math.Ceiling(maxH) + 2;
            glyphH = cellH - 2;
            atlasW = cellW * cols;
            atlasH = cellH * rows;

            Check(GdipCreateBitmapFromScan0(atlasW, atlasH, 0, PixelFormat32bppARGB, IntPtr.Zero, out IntPtr atlasBmp), "atlas bitmap");
            try
            {
                Check(GdipGetImageGraphicsContext(atlasBmp, out IntPtr g), "atlas graphics");
                Check(GdipCreateSolidFill(0xFFFFFFFFu, out IntPtr brush), "white brush");
                try
                {
                    GdipGraphicsClear(g, 0x00000000u);                 // transparent
                    GdipSetTextRenderingHint(g, TextRenderingHintAntiAlias);
                    for (int i = 0; i < count; i++)
                    {
                        int col = i % cols, row = i / cols;
                        char c = (char)(firstChar + i);
                        if (c != ' ')
                        {
                            var rect = new RectF { X = col * cellW + 1, Y = row * cellH + 1, Width = cellW, Height = cellH };
                            GdipDrawString(g, c.ToString(), 1, font, ref rect, fmt, brush);
                        }
                        glyphW[i] = widths[i];
                        glyphAdvance[i] = widths[i] + 1f;
                    }
                    GdipFlush(g, 1); // FlushIntentionSync, before reading the pixels back
                }
                finally
                {
                    GdipDeleteBrush(brush);
                    GdipDeleteGraphics(g);
                }

                return LockToRgba(atlasBmp, atlasW, atlasH, forceWhite: true);
            }
            finally { GdipDisposeImage(atlasBmp); }
        }
        finally
        {
            if (probeG != IntPtr.Zero) GdipDeleteGraphics(probeG);
            if (probeBmp != IntPtr.Zero) GdipDisposeImage(probeBmp);
            if (fmt != IntPtr.Zero) GdipDeleteStringFormat(fmt);
            if (font != IntPtr.Zero) GdipDeleteFont(font);
            GdipDeleteFontFamily(family);
        }
    }

    private static void Check(int status, string what)
    {
        if (status != 0) throw new IOException($"GDI+ {what} failed (status {status}).");
    }

    // Lock a 32bpp bitmap, copy its pixels and convert GDI+'s BGRA byte order to RGBA. When
    // forceWhite is set the colour is pinned to white and only the alpha (coverage) is kept — used
    // for the glyph atlas so the shader can tint text freely.
    private static byte[] LockToRgba(IntPtr bmp, int width, int height, bool forceWhite = false)
    {
        var rect = new GpRect { X = 0, Y = 0, Width = width, Height = height };
        if (GdipBitmapLockBits(bmp, ref rect, ImageLockModeRead, PixelFormat32bppARGB, out GpBitmapData data) != 0)
            throw new IOException("GDI+ LockBits failed.");

        var rgba = new byte[width * height * 4];
        try
        {
            int stride = data.Stride;                 // assume top-down (positive stride), as 32bpp always is here
            var raw = new byte[stride * height];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);

            for (int y = 0; y < height; y++)
            {
                int s = y * stride;
                int d = y * width * 4;
                for (int x = 0; x < width; x++, s += 4, d += 4)
                {
                    if (forceWhite)
                    {
                        rgba[d + 0] = 255;
                        rgba[d + 1] = 255;
                        rgba[d + 2] = 255;
                        rgba[d + 3] = raw[s + 3]; // coverage from alpha
                    }
                    else
                    {
                        rgba[d + 0] = raw[s + 2]; // R (GDI+ stores B,G,R,A)
                        rgba[d + 1] = raw[s + 1]; // G
                        rgba[d + 2] = raw[s + 0]; // B
                        rgba[d + 3] = raw[s + 3]; // A
                    }
                }
            }
        }
        finally { GdipBitmapUnlockBits(bmp, ref data); }

        return rgba;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpRect
    {
        public int X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectF
    {
        public float X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpBitmapData
    {
        public int Width;
        public int Height;
        public int Stride;
        public int PixelFormat;
        public IntPtr Scan0;
        public IntPtr Reserved;
    }

    [DllImport("gdiplus.dll")]
    private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

    [DllImport("gdiplus.dll")]
    private static extern void GdiplusShutdown(IntPtr token);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipCreateBitmapFromFile(string filename, out IntPtr bitmap);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan0, out IntPtr bitmap);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetInterpolationMode(IntPtr graphics, int interpolationMode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDrawImageRectI(IntPtr graphics, IntPtr image, int x, int y, int width, int height);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteGraphics(IntPtr graphics);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageWidth(IntPtr image, out uint width);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageHeight(IntPtr image, out uint height);

    [DllImport("gdiplus.dll")]
    private static extern int GdipBitmapLockBits(IntPtr bitmap, ref GpRect rect, uint flags, int format, out GpBitmapData lockedBitmapData);

    [DllImport("gdiplus.dll")]
    private static extern int GdipBitmapUnlockBits(IntPtr bitmap, ref GpBitmapData lockedBitmapData);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDisposeImage(IntPtr image);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipCreateFontFamilyFromName(string name, IntPtr fontCollection, out IntPtr fontFamily);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteFontFamily(IntPtr fontFamily);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateFont(IntPtr fontFamily, float emSize, int style, int unit, out IntPtr font);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteFont(IntPtr font);

    [DllImport("gdiplus.dll")]
    private static extern int GdipStringFormatGetGenericTypographic(out IntPtr format);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteStringFormat(IntPtr format);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateSolidFill(uint argb, out IntPtr brush);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteBrush(IntPtr brush);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetTextRenderingHint(IntPtr graphics, int mode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGraphicsClear(IntPtr graphics, uint argb);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipMeasureString(IntPtr graphics, string str, int length, IntPtr font,
        ref RectF layoutRect, IntPtr stringFormat, out RectF boundingBox, out int codepointsFitted, out int linesFilled);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipDrawString(IntPtr graphics, string str, int length, IntPtr font,
        ref RectF layoutRect, IntPtr stringFormat, IntPtr brush);

    [DllImport("gdiplus.dll")]
    private static extern int GdipFlush(IntPtr graphics, int intention);
}
