using System.Numerics;

namespace MakarovPhysicsSandbox.Physics;

/// <summary>
/// A box of water sitting on the floor. Bodies below the surface get Archimedes buoyancy
/// plus heavy drag. Because we keep each body's density, the buoyant acceleration works
/// out to g * submergedFraction * (waterDensity / bodyDensity): lighter-than-water bodies
/// bob up, denser ones sink - no per-shape volume bookkeeping needed.
/// </summary>
public sealed class WaterVolume
{
    public Vector3 Center;       // center of the XZ footprint
    public float HalfX = 6f, HalfZ = 6f;
    public float SurfaceY = 1.5f;
    public float Density = 1.65f;
    public float LinearDrag = 2.5f;
    public float WaveAmplitude = 0.22f;
    public float Time;

    // ---- object-driven ripples (expanding rings spawned when bodies hit/cross the surface) ----
    private struct Ripple { public float X, Z, Start, Strength; }
    private readonly List<Ripple> _ripples = new(32);
    public const int MAX_RIPPLES = 24;
    private const float RippleLife = 1.6f;       // seconds before a ring fades out
    private const float RippleRingSpeed = 3.2f;  // how fast the ring radius expands (units/s)
    private const float RippleWaveK = 6.0f;      // spatial frequency of the ring
    private const float RipplePhaseSpeed = 9.0f;

    /// <summary>Spawn an expanding ripple centered at (x,z). Strength scales its height.</summary>
    public void Disturb(float x, float z, float strength)
    {
        if (_ripples.Count >= MAX_RIPPLES) _ripples.RemoveAt(0); // drop the oldest

        _ripples.Add(new Ripple
        {
            X = x, 
            Z = z, 
            Start = Time, 
            Strength = Math.Clamp(strength, 0.02f, 0.6f)
        });
    }

    /// <summary>Fills dst with active ripples as (x, z, age, strength) quads; returns the count written.</summary>
    public int FillRipples(float[] dst, int maxQuads)
    {
        int n = 0;

        foreach (Ripple rp in _ripples)
        {
            if (n >= maxQuads) break;
            float age = Time - rp.Start;
            if (age is < 0f or > RippleLife) continue;
            dst[n * 4 + 0] = rp.X;
            dst[n * 4 + 1] = rp.Z;
            dst[n * 4 + 2] = age;
            dst[n * 4 + 3] = rp.Strength;
            n++;
        }
        return n;
    }

    public bool ContainsColumn(Vector3 p)
    { 
        return MathF.Abs(p.X - Center.X) <= HalfX && MathF.Abs(p.Z - Center.Z) <= HalfZ;
    }

    public void Step(float h)
    {
        Time += h;

        // prune dead ripples
        for (int i = _ripples.Count - 1; i >= 0; i--)
        {
            if (Time - _ripples[i].Start > RippleLife)
            {
                _ripples.RemoveAt(i);
            }
        }
    }

    private float RippleHeightAt(float x, float z)
    {
        if (_ripples.Count == 0) return 0f;
        float sum = 0f;

        foreach (var rp in _ripples)
        {
            float age = Time - rp.Start;
            if (age < 0f || age > RippleLife) continue;
            float dx = x - rp.X, dz = z - rp.Z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            float fade = 1f - age / RippleLife;                       // overall decay
            float ringR = age * RippleRingSpeed;                      // current ring radius
            float band = MathF.Exp(-2.5f * MathF.Abs(dist - ringR));  // localize at the ring
            sum += rp.Strength * fade * band * MathF.Sin(dist * RippleWaveK - age * RipplePhaseSpeed);
        }

        return sum;
    }

    public float SurfaceAt(float x, float z)
    {
        float w1 = MathF.Sin(x * 1.15f + Time * 1.35f) * 0.55f;
        float w2 = MathF.Sin(z * 1.75f + Time * 1.85f) * 0.30f;
        float w3 = MathF.Sin((x + z) * 0.80f + Time * 1.10f) * 0.25f;
        return SurfaceY + WaveAmplitude * (w1 + w2 + w3) + RippleHeightAt(x, z);
    }

    public void Apply(RigidBody b, Vector3 gravity, float h)
    {
        if (!ContainsColumn(b.Position)) return;

        float r = b.BoundingRadius;
        float surfaceY = SurfaceAt(b.Position.X, b.Position.Z);

        // spawn a ripple when a body crosses / churns the surface (throttled per body)
        if (b.RippleCooldown <= 0f && MathF.Abs(b.Position.Y - surfaceY) < r + 0.15f)
        {
            float horizontal = MathF.Sqrt(b.Velocity.X * b.Velocity.X + b.Velocity.Z * b.Velocity.Z);
            float speed = MathF.Abs(b.Velocity.Y) + 0.35f * horizontal;

            if (speed > 1.0f)
            {
                Disturb(b.Position.X, b.Position.Z, 0.05f * MathF.Min(speed, 8f) * MathF.Min(2f * r, 1.5f));
                b.RippleCooldown = 0.10f;
            }
        }

        float bottom = b.Position.Y - r;
        if (bottom >= surfaceY) return; // fully above the surface

        // crude submerged fraction from the bounding sphere - smooth enough for bobbing
        float submerged = Math.Clamp((surfaceY - bottom) / (2f * r), 0f, 1f);

        float g = gravity.Length();
        if (g > 1e-4f)
        {
            float buoyAccel = g * submerged * (Density / MathF.Max(b.Density, 1e-3f));
            b.Velocity += -Vector3.Normalize(gravity) * (buoyAccel * h);
        }

        // drag scales with how deep we are; kills the bounce so things settle at the line
        float k = 1f / (1f + LinearDrag * submerged * h);
        b.Velocity *= k;
        b.AngularVelocity *= k;

        // Gameplay wetness: water volumes are not just visual/buoyancy. They also feed
        // the interaction matrix: wet bodies resist fire and conduct electricity better.
        b.Wetness = Math.Clamp(b.Wetness + submerged * h * 3.5f, 0f, 1f);

        if (submerged > 0.25f && b.Burning)
        {
            b.Burning = false;
            if (b.Tag is RagdollBone bone) bone.Burning = false;
            b.Temperature = MathF.Min(b.Temperature, 80f);
        }
    }
}
