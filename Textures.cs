using MakarovPhysicsSandbox;

namespace MakarovPhysicsSandbox;

/// <summary>
/// Procedural textures, generated at startup. No image files, no loaders - a couple
/// of hash functions and some sine waves go a surprisingly long way. Everything is
/// kept light and mostly desaturated on purpose: the fragment shader multiplies the
/// texel by the per-body tint color, so the texture provides pattern and the tint
/// provides hue.
/// </summary>
internal static class Textures
{
    // integer hash -> [0,1). Wang-style avalanche; quality is plenty for noise.
    private static float Hash(int x, int y, int seed)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / 4294967296f;
    }

    // smooth value noise with bilinear interpolation between lattice hashes
    private static float Noise(float x, float y, int seed)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3f - 2f * fx); // smoothstep, otherwise the lattice grid shows
        fy = fy * fy * (3f - 2f * fy);

        float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed);
        float c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    private static float Fbm(float x, float y, int seed, int octaves = 4)
    {
        float sum = 0f, amp = 0.5f, freq = 1f;
        for (int i = 0; i < octaves; i++)
        {
            sum += amp * Noise(x * freq, y * freq, seed + i * 131);
            amp *= 0.5f;
            freq *= 2f;
        }
        return sum;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(v * 255f, 0f, 255f);

    private static byte[] Generate(int size, Func<float, float, (float r, float g, float b)> pixel)
    {
        var data = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var (r, g, b) = pixel((x + 0.5f) / size, (y + 0.5f) / size);
                int i = (y * size + x) * 4;
                data[i] = ToByte(r); data[i + 1] = ToByte(g); data[i + 2] = ToByte(b); data[i + 3] = 255;
            }
        return data;
    }

    private static uint Upload(byte[] pixels, int size)
    {
        uint tex = GL.GenTexture();
        GL.BindTexture(GL.TEXTURE_2D, tex);
        GL.TexImage2D(GL.TEXTURE_2D, 0, GL.RGBA8, size, size, 0, GL.RGBA, GL.UNSIGNED_BYTE, pixels);
        GL.GenerateMipmap(GL.TEXTURE_2D);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR_MIPMAP_LINEAR);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.REPEAT);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.REPEAT);
        return tex;
    }

    /// <summary>Floor: a 2x2 checker block (tiles seamlessly under REPEAT) with subtle grain.</summary>
    public static uint CreateCheckerFloor(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            bool dark = (u < 0.5f) ^ (v < 0.5f);
            float baseV = dark ? 0.62f : 0.92f;
            baseV += (Fbm(u * 14f, v * 14f, 7) - 0.5f) * 0.10f;

            // thin grout lines along the cell borders
            float du = MathF.Min(MathF.Abs(u - 0.5f), MathF.Min(u, 1f - u));
            float dv = MathF.Min(MathF.Abs(v - 0.5f), MathF.Min(v, 1f - v));
            if (MathF.Min(du, dv) < 0.008f) baseV *= 0.72f;

            return (baseV, baseV, baseV);
        }), size);
    }

    /// <summary>Wooden crate: horizontal planks, grain, darker frame around the edge.</summary>
    public static uint CreateCrate(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            const int planks = 4;
            float pv = v * planks;
            int plank = (int)pv;
            float inPlank = pv - plank;

            // each plank gets its own grain phase so they don't look like clones
            float phase = Hash(plank, 17, 3) * 40f;
            float grain = MathF.Sin((u * 26f + phase) + (Fbm(u * 5f, v * 18f, 21) - 0.5f) * 7f);
            float wood = 0.80f + grain * 0.07f + (Fbm(u * 50f, v * 50f, 33) - 0.5f) * 0.08f;

            if (inPlank < 0.05f || inPlank > 0.95f) wood *= 0.66f;  // plank seams
            float edge = MathF.Min(MathF.Min(u, 1f - u), MathF.Min(v, 1f - v));
            if (edge < 0.06f) wood *= 0.62f;                        // crate frame

            // a hint of warmth so it reads as wood even under a gray tint
            return (wood, wood * 0.88f, wood * 0.72f);
        }), size);
    }

    /// <summary>Spheres: soft latitude stripes plus speckle.</summary>
    public static uint CreateStripes(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            float band = 0.5f + 0.5f * MathF.Sin(v * MathF.PI * 10f);
            band = 0.78f + band * band * 0.22f;
            band += (Fbm(u * 30f, v * 30f, 51) - 0.5f) * 0.05f;
            return (band, band, band);
        }), size);
    }

    /// <summary>Capsules: brushed metal - stretched horizontal noise with faint streaks.</summary>
    public static uint CreateMetal(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            float brushed = Fbm(u * 4f, v * 90f, 77, 3);          // anisotropic: stretched in u
            float streaks = MathF.Sin(v * MathF.PI * 60f + brushed * 9f) * 0.03f;
            float m = 0.78f + (brushed - 0.5f) * 0.16f + streaks;
            return (m, m, m * 1.04f);                              // cold blue-ish cast
        }), size);
    }

    /// <summary>Walls: plain concrete with pores and faint blotches.</summary>
    public static uint CreateConcrete(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            float c = 0.82f + (Fbm(u * 9f, v * 9f, 91) - 0.5f) * 0.14f;
            if (Hash((int)(u * size), (int)(v * size), 13) > 0.985f) c *= 0.8f; // pores
            return (c, c, c);
        }), size);
    }
}
