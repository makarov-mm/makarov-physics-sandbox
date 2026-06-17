namespace MakarovPhysicsSandbox;

/// <summary>
/// Texture helpers. The project now prefers authored raster textures from the
/// Textures/ folder and keeps procedural generators as safe fallbacks. Bump maps are
/// loaded separately where available and sampled by the shader as height maps.
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
        {
            for (int x = 0; x < size; x++)
            {
                var (r, g, b) = pixel((x + 0.5f) / size, (y + 0.5f) / size);
                int i = (y * size + x) * 4;
                data[i] = ToByte(r); data[i + 1] = ToByte(g); data[i + 2] = ToByte(b); data[i + 3] = 255;
            }
        }

        return data;
    }

    private static uint Upload(byte[] pixels, int size) => Upload(pixels, size, size);

    private static uint Upload(byte[] pixels, int width, int height)
    {
        uint tex = GL.GenTexture();
        GL.BindTexture(GL.TEXTURE_2D, tex);
        GL.TexImage2D(GL.TEXTURE_2D, 0, GL.RGBA8, width, height, 0, GL.RGBA, GL.UNSIGNED_BYTE, pixels);
        GL.GenerateMipmap(GL.TEXTURE_2D);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.LINEAR_MIPMAP_LINEAR);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.LINEAR);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.REPEAT);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.REPEAT);
        return tex;
    }

    private static uint LoadTextureFile(string fileName, Func<uint> fallback)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Textures", fileName);
            if (!File.Exists(path))
                path = Path.Combine("Textures", fileName);
            if (!File.Exists(path)) return fallback();

            using var src = new Bitmap(path);
            using var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.DrawImage(src, 0, 0, src.Width, src.Height);

            var data = new byte[bmp.Width * bmp.Height * 4];
            int i = 0;
            for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                data[i++] = c.R;
                data[i++] = c.G;
                data[i++] = c.B;
                data[i++] = c.A;
            }
            return Upload(data, bmp.Width, bmp.Height);
        }
        catch
        {
            return fallback();
        }
    }

    public static uint LoadOrCreate(string fileName, Func<uint> fallback) => LoadTextureFile(fileName, fallback);

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

    /// <summary>Wooden crate/boards: warm procedural wood, planks, darker seams, diagonal braces and nail marks.</summary>
    public static uint CreateCrate(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            const int planks = 5;
            float pv = v * planks;
            int plank = (int)MathF.Floor(pv);
            float inPlank = pv - plank;

            float plankShift = (Hash(plank, 17, 3) - 0.5f) * 0.10f;
            float grainBase = Fbm(u * 15f + plankShift, v * 44f, 21, 5);
            float fine = MathF.Sin((u * 55f + grainBase * 10f + plankShift * 30f));
            float wood = 0.70f + (grainBase - 0.5f) * 0.22f + fine * 0.045f;

            bool seam = inPlank < 0.035f || inPlank > 0.965f;
            float edge = MathF.Min(MathF.Min(u, 1f - u), MathF.Min(v, 1f - v));
            bool frame = edge < 0.070f;
            bool braceA = MathF.Abs(u - v) < 0.030f;
            bool braceB = MathF.Abs(u + v - 1f) < 0.030f;
            bool nail = (Distance(u, v, 0.12f, 0.12f) < 0.016f) || (Distance(u, v, 0.88f, 0.12f) < 0.016f) ||
                        (Distance(u, v, 0.12f, 0.88f) < 0.016f) || (Distance(u, v, 0.88f, 0.88f) < 0.016f);

            if (seam) wood *= 0.42f;
            if (frame) wood *= 0.64f;
            if (braceA || braceB) wood *= 0.72f;
            if (nail) wood *= 0.20f;

            // Keep it close to real wood, not toy-coloured blocks.
            float r = wood * 0.86f;
            float g = wood * 0.55f;
            float b = wood * 0.30f;
            return (Math.Clamp(r,0f,1f), Math.Clamp(g,0f,1f), Math.Clamp(b,0f,1f));
        }), size);
    }

    private static float Distance(float x, float y, float cx, float cy)
    {
        float dx = x - cx, dy = y - cy;
        return MathF.Sqrt(dx * dx + dy * dy);
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

            if ((u is > 0.18f and < 0.82f && MathF.Abs(v - 0.24f) < 0.03f) ||
                (u is > 0.18f and < 0.82f && MathF.Abs(v - 0.76f) < 0.03f))
            {
                accent = 1f;
            }

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
            {
                r = g = b = 0.85f;
            }

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

    /// <summary>Gameplay balls: scuffed rubber/painted surface with subtle panel seams.</summary>
    public static uint CreateBall(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            float n = Fbm(u * 18f, v * 18f, 301, 5);
            float lat = MathF.Abs(MathF.Sin(v * MathF.PI * 6f));
            float lon = MathF.Abs(MathF.Sin(u * MathF.PI * 6f));
            float seam = (lat < 0.045f || lon < 0.040f) ? 0.72f : 1f;
            float scuff = Hash((int)(u * size), (int)(v * size), 313) > 0.985f ? 0.55f : 1f;
            float val = (0.78f + (n - 0.5f) * 0.18f) * seam * scuff;
            return (val, val * 0.96f, val * 0.86f);
        }), size);
    }

    /// <summary>Bowling pin texture: white lacquer with red neck stripes and slight grime.</summary>
    public static uint CreateBowlingPin(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            float n = (Fbm(u * 18f, v * 22f, 331) - 0.5f) * 0.08f;
            float r = 0.96f + n, g = 0.94f + n, b = 0.88f + n;
            if ((v > 0.23f && v < 0.29f) || (v > 0.32f && v < 0.36f))
            {
                r = 0.86f; g = 0.10f; b = 0.08f;
            }
            return (Math.Clamp(r,0f,1f), Math.Clamp(g,0f,1f), Math.Clamp(b,0f,1f));
        }), size);
    }

    /// <summary>Brick wall: staggered bricks, dark mortar and rough surface for shader bumping.</summary>
    public static uint CreateBrickWall(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            const float rows = 8f;
            float rowF = v * rows;
            int row = (int)MathF.Floor(rowF);
            float y = rowF - row;
            float cols = 8f;
            float xF = (u + (row % 2) * 0.5f / cols) * cols;
            float x = xF - MathF.Floor(xF);
            bool mortar = x < 0.045f || x > 0.955f || y < 0.060f || y > 0.940f;
            float rough = Fbm(u * 34f, v * 34f, 401, 5);
            float shade = 0.78f + (rough - 0.5f) * 0.24f;

            if (mortar)
            {
                float m = 0.34f + (rough - 0.5f) * 0.08f;
                return (m, m * 1.02f, m * 1.06f);
            }

            float r = 0.56f * shade;
            float g = 0.37f * shade;
            float b = 0.27f * shade;
            return (Math.Clamp(r,0f,1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f));
        }), size);
    }

    /// <summary>Breakable glass: blue-tinted panel with cracks and reflective bands.</summary>
    public static uint CreateGlassBlock(int size = 256)
    {
        return Upload(Generate(size, (u, v) =>
        {
            float tint = 0.70f + (Fbm(u * 10f, v * 10f, 211) - 0.5f) * 0.08f;
            float band = 0.08f * MathF.Max(0f, MathF.Sin((u + v) * MathF.PI * 5f));
            bool crack = MathF.Abs(u - 0.32f - (v - 0.5f) * 0.25f) < 0.012f && v > 0.18f && v < 0.82f;
            crack |= MathF.Abs(u - 0.68f + (v - 0.45f) * 0.38f) < 0.010f && v > 0.20f && v < 0.72f;
            float r = 0.55f * tint + band;
            float g = 0.82f * tint + band;
            float b = 1.00f * tint + band;
            if (crack) { r = 0.92f; g = 0.98f; b = 1.0f; }
            return (Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f));
        }), size);
    }

    /// <summary>Simple fake skybox texture: soft vertical gradient with sparse cloud noise.</summary>
    /// <summary>A soft round particle mask: white at the centre fading smoothly to transparent at
    /// the edge (stored in RGB; the particle shader reads the red channel as the alpha mask).</summary>
    public static uint SoftParticle(int size = 64)
    {
        return Upload(Generate(size, (u, v) =>
        {
            float dx = u - 0.5f, dy = v - 0.5f;
            float d = MathF.Min(1f, MathF.Sqrt(dx * dx + dy * dy) * 2f); // 0 centre .. 1 edge
            float m = 1f - d;
            m = m * m * (3f - 2f * m);  // smoothstep falloff
            return (m, m, m);
        }), size);
    }

    public static uint CreateSkybox(int size = 512)
    {
        return Upload(Generate(size, (u, v) =>
        {
            // Vertical gradient: pale at the horizon (v=0.5) -> rich blue at the zenith (v=1).
            // NOTE: this 2D image is mapped onto a cube, so we deliberately avoid a localised sun
            // disc (it would repeat on all six faces). A single real sun needs a direction-based
            // sky shader instead. Here we keep a clean gradient + soft layered clouds.
            float h = Math.Clamp((v - 0.5f) / 0.5f, 0f, 1f);
            float r = 0.62f + (0.22f - 0.62f) * h;
            float g = 0.78f + (0.44f - 0.78f) * h;
            float b = 0.95f + (0.82f - 0.95f) * h;

            // soft layered clouds above the horizon (two octaves of fbm, gently banded)
            float aboveH = Math.Clamp((v - 0.5f) * 3.5f, 0f, 1f);
            float c1 = Fbm(u * 4f, v * 3f, 241, 5);
            float c2 = Fbm(u * 9f + 11f, v * 6f, 613, 4);
            float cloud = Math.Clamp((c1 * 0.65f + c2 * 0.35f - 0.50f) * 2.8f, 0f, 1f) * aboveH;
            r += (1.0f - r) * cloud * 0.92f;
            g += (1.0f - g) * cloud * 0.92f;
            b += (1.0f - b) * cloud * 0.94f;

            return (Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f));
        }), size);
    }

    public static uint WoodCrateAlbedo() => LoadOrCreate("wood_crate_albedo.png", () => CreateCrate());
    public static uint WoodCrateBump() => LoadOrCreate("wood_crate_bump.png", () => CreateCrate());
    public static uint CartWoodAlbedo() => LoadOrCreate("cart_wood_albedo.png", () => CreateCrate());
    public static uint CartWoodBump() => LoadOrCreate("cart_wood_bump.png", () => CreateCrate());
    public static uint BrickWallAlbedo() => LoadOrCreate("brick_wall_albedo.png", () => CreateBrickWall());
    public static uint BrickWallBump() => LoadOrCreate("brick_wall_bump.png", () => CreateBrickWall());
    public static uint RustyMetalAlbedo() => LoadOrCreate("rusty_metal_albedo.png", () => CreateMetal());
    public static uint RustyMetalBump() => LoadOrCreate("rusty_metal_bump.png", () => CreateMetal());
    public static uint BallAlbedo() => LoadOrCreate("ball_albedo.png", () => CreateBall());
    public static uint BallBump() => LoadOrCreate("ball_bump.png", () => CreateBall());
    public static uint BowlingPinAlbedo() => LoadOrCreate("bowling_pin_albedo.png", () => CreateBowlingPin());
    public static uint BowlingPinBump() => LoadOrCreate("bowling_pin_bump.png", () => CreateBowlingPin());
    public static uint GlassAlbedo() => LoadOrCreate("glass_albedo.png", () => CreateGlassBlock());
    public static uint GlassBump() => LoadOrCreate("glass_bump.png", () => CreateGlassBlock());
    public static uint VehiclePaintAlbedo() => LoadOrCreate("vehicle_paint_albedo.png", () => CreateVehiclePaint());
    public static uint VehiclePaintBump() => LoadOrCreate("vehicle_paint_bump.png", () => CreateVehiclePaint());
    public static uint TireAlbedo() => LoadOrCreate("tire_albedo.png", () => CreateTire());
    public static uint TireBump() => LoadOrCreate("tire_bump.png", () => CreateTire());
}
