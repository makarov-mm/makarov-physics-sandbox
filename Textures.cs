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



    /// <summary>Android shell: pale panels with seams and cool cyan circuitry accents.</summary>
    public static uint CreateAndroidPanel(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            float panel = 0.86f + (Fbm(u * 20f, v * 20f, 161) - 0.5f) * 0.06f;
            float seam = 1f;
            if (MathF.Abs(u - 0.50f) < 0.02f || MathF.Abs(v - 0.50f) < 0.02f) seam = 0.72f;
            float accent = 0f;
            if ((u > 0.18f && u < 0.82f && MathF.Abs(v - 0.24f) < 0.03f) ||
                (u > 0.18f && u < 0.82f && MathF.Abs(v - 0.76f) < 0.03f))
                accent = 1f;

            float r = panel * seam;
            float g = panel * seam;
            float b = MathF.Min(1f, panel * seam * 1.05f);
            if (accent > 0.5f)
            {
                r = 0.24f; g = 0.88f; b = 0.95f;
            }
            return (r, g, b);
        }), size);
    }

    /// <summary>Vehicle body paint: glossy paint with a darker windshield band and subtle grime.</summary>
    public static uint CreateVehiclePaint(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            float noise = (Fbm(u * 18f, v * 18f, 173) - 0.5f) * 0.05f;
            float highlight = 0.90f + 0.10f * MathF.Sin((u * 1.8f + v * 0.6f) * MathF.PI);
            float r = 0.88f * highlight + noise;
            float g = 0.18f * highlight + noise * 0.35f;
            float b = 0.14f * highlight + noise * 0.25f;
            if (v > 0.18f && v < 0.38f && u > 0.18f && u < 0.82f)
            {
                r = 0.10f; g = 0.13f; b = 0.17f;
            }
            return (Math.Clamp(r,0f,1f), Math.Clamp(g,0f,1f), Math.Clamp(b,0f,1f));
        }), size);
    }

    /// <summary>Tyre texture: dark rubber with sidewall ring and a hint of tread.</summary>
    public static uint CreateTire(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            float tread = 0.10f + 0.02f * MathF.Sin(v * MathF.PI * 40f);
            float ring = MathF.Abs(u - 0.50f) > 0.32f ? 0.05f : 0f;
            float val = 0.08f + tread + ring + (Fbm(u * 30f, v * 30f, 181) - 0.5f) * 0.03f;
            return (val, val, val + 0.01f);
        }), size);
    }

    /// <summary>Explosive barrel: painted drum body, dark ribs and a warning label stripe.</summary>
    public static uint CreateBarrel(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            float ribs = 1f;
            if (v < 0.12f || v > 0.88f) ribs = 0.48f;
            else if (MathF.Abs(v - 0.50f) < 0.07f) ribs = 0.72f;

            float baseR = 0.88f, baseG = 0.17f, baseB = 0.08f;
            float n = (Fbm(u * 16f, v * 28f, 123) - 0.5f) * 0.10f;
            float scratch = MathF.Sin(u * MathF.PI * 18f + v * 3f) * 0.02f;
            float shade = Math.Clamp(ribs + n + scratch, 0.30f, 1.15f);

            float r = baseR * shade;
            float g = baseG * shade;
            float b = baseB * shade;

            // center warning label with diagonal hazard pattern
            if (MathF.Abs(v - 0.50f) < 0.11f && u > 0.22f && u < 0.78f)
            {
                float stripe = MathF.Sin((u * 26f + v * 18f) * MathF.PI);
                bool black = stripe > 0f;
                r = black ? 0.08f : 0.96f;
                g = black ? 0.08f : 0.82f;
                b = black ? 0.08f : 0.15f;
            }

            // thin metallic seams
            if (MathF.Abs(v - 0.16f) < 0.01f || MathF.Abs(v - 0.84f) < 0.01f)
                r = g = b = 0.85f;

            return (r, g, b);
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
