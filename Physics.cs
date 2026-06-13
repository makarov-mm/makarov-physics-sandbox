using System.Numerics;

namespace MakarovPhysicsSandbox;

internal enum ShapeType { Sphere, Box, Capsule }

/// <summary>Tiny 3x3 matrix. System.Numerics has no Matrix3x3 and we need one for inertia tensors.</summary>
internal struct Mat3
{
    public float M00, M01, M02, M10, M11, M12, M20, M21, M22;

    public static readonly Mat3 Zero = new();

    public static Mat3 Diagonal(Vector3 d) => new() { M00 = d.X, M11 = d.Y, M22 = d.Z };

    public static Mat3 FromQuaternion(Quaternion q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        return new Mat3
        {
            M00 = 1 - 2 * (y * y + z * z),
            M01 = 2 * (x * y - w * z),
            M02 = 2 * (x * z + w * y),
            M10 = 2 * (x * y + w * z),
            M11 = 1 - 2 * (x * x + z * z),
            M12 = 2 * (y * z - w * x),
            M20 = 2 * (x * z - w * y),
            M21 = 2 * (y * z + w * x),
            M22 = 1 - 2 * (x * x + y * y),
        };
    }

    public readonly Vector3 Transform(Vector3 v) => new(
        M00 * v.X + M01 * v.Y + M02 * v.Z,
        M10 * v.X + M11 * v.Y + M12 * v.Z,
        M20 * v.X + M21 * v.Y + M22 * v.Z);

    public readonly Mat3 Transposed() => new()
    {
        M00 = M00,
        M01 = M10,
        M02 = M20,
        M10 = M01,
        M11 = M11,
        M12 = M21,
        M20 = M02,
        M21 = M12,
        M22 = M22,
    };

    public static Mat3 Multiply(in Mat3 a, in Mat3 b) => new()
    {
        M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
        M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
        M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
        M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
        M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
        M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
        M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22,
    };

    public static Mat3 Add(in Mat3 a, in Mat3 b) => new()
    {
        M00 = a.M00 + b.M00,
        M01 = a.M01 + b.M01,
        M02 = a.M02 + b.M02,
        M10 = a.M10 + b.M10,
        M11 = a.M11 + b.M11,
        M12 = a.M12 + b.M12,
        M20 = a.M20 + b.M20,
        M21 = a.M21 + b.M21,
        M22 = a.M22 + b.M22,
    };

    public readonly Mat3 Inverse()
    {
        // adjugate / determinant; inertia tensors are symmetric positive definite,
        // so det is never anywhere near zero for a sane body
        float c00 = M11 * M22 - M12 * M21;
        float c01 = M12 * M20 - M10 * M22;
        float c02 = M10 * M21 - M11 * M20;
        float det = M00 * c00 + M01 * c01 + M02 * c02;
        float inv = 1f / det;
        return new Mat3
        {
            M00 = c00 * inv,
            M01 = (M02 * M21 - M01 * M22) * inv,
            M02 = (M01 * M12 - M02 * M11) * inv,
            M10 = c01 * inv,
            M11 = (M00 * M22 - M02 * M20) * inv,
            M12 = (M02 * M10 - M00 * M12) * inv,
            M20 = c02 * inv,
            M21 = (M01 * M20 - M00 * M21) * inv,
            M22 = (M00 * M11 - M01 * M10) * inv,
        };
    }
}

internal static class Quat
{
    // System.Numerics' q1*q2 operator multiplies in the opposite order to what
    // the textbook Hamilton product gives you, and it has bitten us before.
    // This is the version consistent with Vector3.Transform's  q v q*  sandwich:
    // Transform(v, Mul(p, c)) == Transform(Transform(v, c), p).
    public static Quaternion Mul(Quaternion a, Quaternion b)
    {
        var av = new Vector3(a.X, a.Y, a.Z);
        var bv = new Vector3(b.X, b.Y, b.Z);
        var v = a.W * bv + b.W * av + Vector3.Cross(av, bv);
        return new Quaternion(v.X, v.Y, v.Z, a.W * b.W - Vector3.Dot(av, bv));
    }
}

/// <summary>One collision shape of a body, in the body's local frame. Plain bodies have exactly one.</summary>
internal struct ChildShape
{
    public ShapeType Shape;
    public Vector3 LocalPos;
    public Quaternion LocalRot;
    public float Radius;        // sphere / capsule
    public Vector3 HalfExtents; // box
    public float HalfHeight;    // capsule

    public static ChildShape Sphere(float radius, Vector3 offset = default) => new()
    { Shape = ShapeType.Sphere, Radius = radius, LocalPos = offset, LocalRot = Quaternion.Identity };

    public static ChildShape Box(Vector3 halfExtents, Vector3 offset = default, Quaternion rot = default) => new()
    {
        Shape = ShapeType.Box,
        HalfExtents = halfExtents,
        LocalPos = offset,
        LocalRot = rot == default ? Quaternion.Identity : rot
    };

    public static ChildShape Capsule(float radius, Vector3 offset = default, Quaternion rot = default) => new()
    {
        Shape = ShapeType.Capsule,
        Radius = radius,
        HalfHeight = radius * RigidBody.CapsuleHalfHeightPerRadius,
        LocalPos = offset,
        LocalRot = rot == default ? Quaternion.Identity : rot
    };

    public readonly float BoundingRadius => Shape switch
    {
        ShapeType.Sphere => Radius,
        ShapeType.Capsule => HalfHeight + Radius,
        _ => HalfExtents.Length(),
    };

    public readonly (float mass, Vector3 inertia) MassProperties(float density)
    {
        switch (Shape)
        {
            case ShapeType.Sphere:
                {
                    float m = density * 4f / 3f * MathF.PI * Radius * Radius * Radius;
                    return (m, new Vector3(0.4f * m * Radius * Radius));
                }
            case ShapeType.Capsule:
                {
                    float r = Radius, h = HalfHeight;
                    float mCyl = density * MathF.PI * r * r * (2f * h);
                    float mCaps = density * 4f / 3f * MathF.PI * r * r * r;
                    float iy = mCyl * r * r * 0.5f + mCaps * 0.4f * r * r;
                    float ixz = mCyl * (h * h / 3f + r * r * 0.25f)
                              + mCaps * (0.4f * r * r + h * h + 0.75f * h * r);
                    return (mCyl + mCaps, new Vector3(ixz, iy, ixz));
                }
            default:
                {
                    var e = HalfExtents;
                    float m = density * 8f * e.X * e.Y * e.Z;
                    return (m, new Vector3(
                        m / 3f * (e.Y * e.Y + e.Z * e.Z),
                        m / 3f * (e.X * e.X + e.Z * e.Z),
                        m / 3f * (e.X * e.X + e.Y * e.Y)));
                }
        }
    }
}

/// <summary>A child shape resolved to world space. The narrow phase works on these,
/// so it never has to care whether the shape belongs to a plain body or a compound.</summary>
internal struct ShapeProxy
{
    public RigidBody Owner;
    public ShapeType Shape;
    public Vector3 Position;
    public Quaternion Rotation;
    public float Radius;
    public Vector3 HalfExtents;
    public float HalfHeight;
    public float BoundingRadius;

    public readonly Vector3 Axis(int i)
    {
        var v = i switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
        return Vector3.Transform(v, Rotation);
    }

    public readonly void CapsuleSegment(out Vector3 p0, out Vector3 p1)
    {
        var axis = Vector3.Transform(Vector3.UnitY, Rotation);
        p0 = Position - axis * HalfHeight;
        p1 = Position + axis * HalfHeight;
    }
}

internal sealed class RigidBody
{
    /// <summary>The render mesh for capsules is baked with this proportion, so physics must match it.</summary>
    public const float CapsuleHalfHeightPerRadius = 1.6f;

    public ChildShape[] Children = [];
    public ShapeProxy[] Proxies = [];

    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 Velocity;
    public Vector3 AngularVelocity;

    // pseudo-velocities for split-impulse position correction; they push penetrating
    // bodies apart during integration but never enter the real velocity, so position
    // fixes don't add kinetic energy and don't pollute the warm-start cache
    public Vector3 BiasVelocity;
    public Vector3 BiasAngularVelocity;

    public float Mass;
    public float InvMass;
    public float Density = 1f;   // kept around so buoyancy can recover the volume (V = m / rho)
    public Mat3 InvInertiaLocal;
    public Mat3 InvInertiaWorld;

    public float Restitution = 0.3f;
    public float Friction = 0.5f;
    public float BoundingRadius;

    public Vector3 Color = new(0.8f, 0.8f, 0.8f);
    // Arena walls/floor are not user-editable. User objects remain selectable even if frozen/static.
    public bool UserObject = true;

    // Optional gameplay back-reference (e.g. the RagdollBone this body belongs to). Physics never reads it.
    public object? Tag;

    // ---- thermal state (read/written by HeatSystem; the solver ignores all of this) ----
    public float Temperature = 20f;   // °C-ish; ambient is 20
    public bool Burning;              // currently on fire
    public float Flammability = 0.7f; // 0 = will never ignite. Also gated by density (metal/stone resist).

    // Optional toy destruction. Kept deliberately simple: fragile bodies are replaced
    // by a few smaller pieces when they take a hard enough impact.
    public bool Breakable;
    public float BreakThreshold = 7.5f;
    public int BreakPieces = 8;

    public bool Sleeping;
    public float SleepTimer;
    public bool Touching;   // set each substep if this body had a contact - used for rolling resistance
    public float RippleCooldown; // small per-body timer so a bobbing body doesn't spam water ripples

    /// <summary>True for a plain single-sphere body (a ball), which gets rolling resistance.</summary>
    public bool IsBall => Children.Length == 1 && Children[0].Shape == ShapeType.Sphere;

    public bool IsStatic => InvMass == 0f;
    public bool Inactive => IsStatic || Sleeping;

    public void Wake()
    {
        Sleeping = false;
        SleepTimer = 0f;
    }

    public static RigidBody CreateSphere(Vector3 pos, float radius, float density = 1f)
    {
        var b = FromChildren([ChildShape.Sphere(radius)], pos, density);
        b.Restitution = 0.45f;
        b.Friction = 0.45f;
        return b;
    }

    public static RigidBody CreateBox(Vector3 pos, Vector3 halfExtents, float density = 1f)
    {
        var b = FromChildren([ChildShape.Box(halfExtents)], pos, density);
        b.Restitution = 0.15f;
        b.Friction = 0.55f;
        return b;
    }

    public static RigidBody CreateCapsule(Vector3 pos, float radius, float density = 1f)
    {
        var b = FromChildren([ChildShape.Capsule(radius)], pos, density);
        b.Restitution = 0.3f;
        b.Friction = 0.5f;
        return b;
    }

    public static RigidBody CreateCompound(Vector3 pos, ChildShape[] children, float density = 1f)
    {
        var b = FromChildren(children, pos, density);
        b.Restitution = 0.2f;
        b.Friction = 0.55f;
        return b;
    }

    public static RigidBody CreateStaticBox(Vector3 pos, Vector3 halfExtents)
    {
        var b = new RigidBody
        {
            Children = [ChildShape.Box(halfExtents)],
            Position = pos,
            InvMass = 0f,
            InvInertiaLocal = Mat3.Zero,
            InvInertiaWorld = Mat3.Zero,
            Restitution = 0.1f,
            Friction = 0.6f,
            BoundingRadius = halfExtents.Length(),
            UserObject = false,
        };
        b.Proxies = new ShapeProxy[1];
        b.RefreshProxies();
        return b;
    }

    private static RigidBody FromChildren(ChildShape[] children, Vector3 pos, float density)
    {
        float totalMass = 0f;
        var com = Vector3.Zero;
        foreach (ref var c in children.AsSpan())
        {
            var (m, _) = c.MassProperties(density);
            totalMass += m;
            com += c.LocalPos * m;
        }
        com /= totalMass;

        // The solver assumes the body origin sits at the center of mass (torque arms are
        // measured from Position). So we move the origin to the COM and shift every child
        // the other way. Without this a lopsided compound would orbit a wrong point.
        var inertia = Mat3.Zero;
        for (int i = 0; i < children.Length; i++)
        {
            children[i].LocalPos -= com;

            var (m, iDiag) = children[i].MassProperties(density);
            var r = Mat3.FromQuaternion(children[i].LocalRot);
            var iChild = Mat3.Multiply(Mat3.Multiply(r, Mat3.Diagonal(iDiag)), r.Transposed());

            // parallel axis theorem: I += m * (|d|^2 E - d dT)
            var d = children[i].LocalPos;
            float dd = d.LengthSquared();
            var shift = new Mat3
            {
                M00 = m * (dd - d.X * d.X),
                M01 = -m * d.X * d.Y,
                M02 = -m * d.X * d.Z,
                M10 = -m * d.Y * d.X,
                M11 = m * (dd - d.Y * d.Y),
                M12 = -m * d.Y * d.Z,
                M20 = -m * d.Z * d.X,
                M21 = -m * d.Z * d.Y,
                M22 = m * (dd - d.Z * d.Z),
            };
            inertia = Mat3.Add(inertia, Mat3.Add(iChild, shift));
        }

        var b = new RigidBody
        {
            Children = children,
            Position = pos + com,
            Mass = totalMass,
            InvMass = 1f / totalMass,
            Density = density,
            InvInertiaLocal = inertia.Inverse(),
        };
        float br = 0f;
        foreach (ref var c in children.AsSpan())
            br = MathF.Max(br, c.LocalPos.Length() + c.BoundingRadius);
        b.BoundingRadius = br;

        b.Proxies = new ShapeProxy[children.Length];
        b.UpdateDerived();
        return b;
    }


    public void SetStatic(bool isStatic)
    {
        if (isStatic)
        {
            Mass = 0f;
            InvMass = 0f;
            InvInertiaLocal = Mat3.Zero;
            InvInertiaWorld = Mat3.Zero;
            Velocity = Vector3.Zero;
            AngularVelocity = Vector3.Zero;
            Sleeping = true;
            RefreshProxies();
            return;
        }

        RecomputeMass(MathF.Max(Density, 0.001f));
        Wake();
    }

    public void RecomputeMass(float density)
    {
        Density = MathF.Max(density, 0.001f);
        float totalMass = 0f;
        var inertia = Mat3.Zero;

        foreach (ref var c in Children.AsSpan())
        {
            var (m, iDiag) = c.MassProperties(Density);
            totalMass += m;

            var r = Mat3.FromQuaternion(c.LocalRot);
            var iChild = Mat3.Multiply(Mat3.Multiply(r, Mat3.Diagonal(iDiag)), r.Transposed());
            var d = c.LocalPos;
            float dd = d.LengthSquared();
            var shift = new Mat3
            {
                M00 = m * (dd - d.X * d.X),
                M01 = -m * d.X * d.Y,
                M02 = -m * d.X * d.Z,
                M10 = -m * d.Y * d.X,
                M11 = m * (dd - d.Y * d.Y),
                M12 = -m * d.Y * d.Z,
                M20 = -m * d.Z * d.X,
                M21 = -m * d.Z * d.Y,
                M22 = m * (dd - d.Z * d.Z),
            };
            inertia = Mat3.Add(inertia, Mat3.Add(iChild, shift));
        }

        Mass = MathF.Max(totalMass, 0.001f);
        InvMass = 1f / Mass;
        InvInertiaLocal = inertia.Inverse();

        float br = 0f;
        foreach (ref var c in Children.AsSpan())
            br = MathF.Max(br, c.LocalPos.Length() + c.BoundingRadius);
        BoundingRadius = br;
        UpdateDerived();
    }

    public void ScaleUniform(float factor)
    {
        factor = Math.Clamp(factor, 0.05f, 20f);
        for (int i = 0; i < Children.Length; i++)
        {
            Children[i].LocalPos *= factor;
            Children[i].Radius *= factor;
            Children[i].HalfExtents *= factor;
            Children[i].HalfHeight *= factor;
        }

        float br = 0f;
        foreach (ref var c in Children.AsSpan())
            br = MathF.Max(br, c.LocalPos.Length() + c.BoundingRadius);
        BoundingRadius = br;

        if (IsStatic) RefreshProxies();
        else RecomputeMass(Density);
        Wake();
    }

    public RigidBody Clone(Vector3 offset)
    {
        var clone = new RigidBody
        {
            Children = Children.ToArray(),
            Position = Position + offset,
            Rotation = Rotation,
            Velocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
            Density = Density,
            Restitution = Restitution,
            Friction = Friction,
            Color = Color,
            UserObject = true,
            Breakable = Breakable,
            BreakThreshold = BreakThreshold,
            BreakPieces = BreakPieces,
            Sleeping = false,
        };
        clone.Proxies = new ShapeProxy[clone.Children.Length];
        if (IsStatic) clone.SetStatic(true);
        else clone.RecomputeMass(Density);
        return clone;
    }

    public void UpdateDerived()
    {
        if (!IsStatic)
        {
            var r = Mat3.FromQuaternion(Rotation);
            InvInertiaWorld = Mat3.Multiply(Mat3.Multiply(r, InvInertiaLocal), r.Transposed());
        }
        RefreshProxies();
    }

    public void RefreshProxies()
    {
        for (int i = 0; i < Children.Length; i++)
        {
            ref var c = ref Children[i];
            Proxies[i] = new ShapeProxy
            {
                Owner = this,
                Shape = c.Shape,
                Position = Position + Vector3.Transform(c.LocalPos, Rotation),
                Rotation = Quat.Mul(Rotation, c.LocalRot),
                Radius = c.Radius,
                HalfExtents = c.HalfExtents,
                HalfHeight = c.HalfHeight,
                BoundingRadius = c.BoundingRadius,
            };
        }
    }

    public Vector3 VelocityAt(Vector3 worldPoint)
        => Velocity + Vector3.Cross(AngularVelocity, worldPoint - Position);

    public void ApplyImpulse(Vector3 impulse, Vector3 worldPoint)
    {
        if (IsStatic) return;
        Velocity += impulse * InvMass;
        AngularVelocity += InvInertiaWorld.Transform(Vector3.Cross(worldPoint - Position, impulse));
    }
}

internal sealed class Contact
{
    public RigidBody A = null!, B = null!;
    public Vector3 Point;
    public Vector3 Normal;       // from A to B
    public float Penetration;

    public Vector3 RA, RB;
    public Vector3 Tangent1, Tangent2;
    public float MassNormal, MassTangent1, MassTangent2;
    public float VelocityBias;   // restitution only
    public float PositionBias;   // Baumgarte term, solved against pseudo-velocities

    public float Pn, Pt1, Pt2;   // accumulated impulses
    public float Pnb;            // accumulated position-correction impulse
    public float ImpactSpeed;    // closing speed at first touch, for spark effects
}

/// <summary>
/// A localized force region. Attract/Repel pull or push toward a point; Wind blows in a
/// fixed direction. All three are applied as accelerations (independent of mass, like
/// gravity) with a linear falloff to the edge of the radius.
/// </summary>
internal sealed class ForceField
{
    public enum Kind { Attract, Repel, Wind }

    public Kind Type;
    public Vector3 Position;
    public float Radius = 6f;
    public float Strength = 20f;
    public Vector3 WindDir = Vector3.UnitX;

    public void Apply(RigidBody b, float h)
    {
        var d = Position - b.Position;
        float dist = d.Length();
        if (dist > Radius) return;
        float falloff = 1f - dist / Radius;

        Vector3 accel = Type switch
        {
            Kind.Attract => (dist > 1e-3f ? d / dist : Vector3.Zero) * Strength,
            Kind.Repel => (dist > 1e-3f ? -d / dist : Vector3.UnitY) * Strength,
            _ => Vector3.Normalize(WindDir) * Strength,
        };
        b.Velocity += accel * (falloff * h);
    }
}

/// <summary>
/// A box of water sitting on the floor. Bodies below the surface get Archimedes buoyancy
/// plus heavy drag. Because we keep each body's density, the buoyant acceleration works
/// out to g * submergedFraction * (waterDensity / bodyDensity): lighter-than-water bodies
/// bob up, denser ones sink - no per-shape volume bookkeeping needed.
/// </summary>
internal sealed class WaterVolume
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
    public const int MaxRipples = 24;
    private const float RippleLife = 1.6f;       // seconds before a ring fades out
    private const float RippleRingSpeed = 3.2f;  // how fast the ring radius expands (units/s)
    private const float RippleWaveK = 6.0f;      // spatial frequency of the ring
    private const float RipplePhaseSpeed = 9.0f;

    /// <summary>Spawn an expanding ripple centered at (x,z). Strength scales its height.</summary>
    public void Disturb(float x, float z, float strength)
    {
        if (_ripples.Count >= MaxRipples) _ripples.RemoveAt(0); // drop the oldest
        _ripples.Add(new Ripple { X = x, Z = z, Start = Time, Strength = Math.Clamp(strength, 0.02f, 0.6f) });
    }

    public int RippleCount => _ripples.Count;

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
        => MathF.Abs(p.X - Center.X) <= HalfX && MathF.Abs(p.Z - Center.Z) <= HalfZ;

    public void Step(float h)
    {
        Time += h;
        // prune dead ripples
        for (int i = _ripples.Count - 1; i >= 0; i--)
            if (Time - _ripples[i].Start > RippleLife) _ripples.RemoveAt(i);
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
            float horiz = MathF.Sqrt(b.Velocity.X * b.Velocity.X + b.Velocity.Z * b.Velocity.Z);
            float speed = MathF.Abs(b.Velocity.Y) + 0.35f * horiz;
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
    }
}

/// <summary>
/// A two-body constraint solved with sequential impulses, right next to the contacts.
/// Point keeps two anchor points coincident (a ball-socket - good for chains and ragdolls);
/// Distance holds them a fixed length apart (a rigid rod); Rope only resists stretching.
/// If body B is null the joint is pinned to a fixed point in the world.
/// </summary>
internal sealed class Joint
{
    public enum Kind { Point, Distance, Rope, Spring }

    public Kind Type;
    public RigidBody A = null!;
    public RigidBody? B;          // null => anchored to the world at AnchorB (world space)
    public Vector3 LocalA;        // attach point in A's local frame
    public Vector3 LocalB;        // in B's local frame, or world point if B is null
    public float Length;          // for Distance / Rope / Spring
    public float Stiffness = 18f;  // for Spring
    public float Damping = 2.2f;    // for Spring

    // recomputed each substep
    private Vector3 _rA, _rB, _worldA, _worldB;
    private Mat3 _kInv;           // effective-mass matrix for the Point constraint
    private Vector3 _axis;        // unit A->B direction for Distance / Rope
    private float _axisMass;

    public Vector3 WorldA => _worldA;
    public Vector3 WorldB => _worldB;

    private Vector3 VelA => A.Velocity + Vector3.Cross(A.AngularVelocity, _rA);
    private Vector3 VelB => B == null ? Vector3.Zero : B.Velocity + Vector3.Cross(B.AngularVelocity, _rB);

    public bool Involves(RigidBody body) => A == body || B == body;

    public void WakePair()
    {
        if (!A.IsStatic) A.Wake();
        if (B is { IsStatic: false }) B.Wake();
    }

    public void Presolve(float h)
    {
        _rA = Vector3.Transform(LocalA, A.Rotation);
        _worldA = A.Position + _rA;
        if (B != null)
        {
            _rB = Vector3.Transform(LocalB, B.Rotation);
            _worldB = B.Position + _rB;
        }
        else
        {
            _rB = Vector3.Zero;
            _worldB = LocalB; // world anchor
        }

        if (Type == Kind.Point)
        {
            // K = invMassSum*I - skew(rA) IinvA skew(rA) - skew(rB) IinvB skew(rB)
            var k = Mat3.Diagonal(new Vector3(InvMassSum()));
            k = Mat3.Add(k, SkewInertiaTerm(A, _rA));
            if (B is { IsStatic: false }) k = Mat3.Add(k, SkewInertiaTerm(B, _rB));
            _kInv = k.Inverse();
        }
        else
        {
            var d = _worldB - _worldA;
            float len = d.Length();
            _axis = len > 1e-5f ? d / len : Vector3.UnitY;
            _axisMass = 1f / EffMassAlong(_axis);
        }
    }

    public void Solve(float h)
    {
        const float beta = 0.2f;

        if (Type == Kind.Point)
        {
            var c = _worldB - _worldA;
            var vrel = VelB - VelA;
            var rhs = -(vrel + c * (beta / h));
            var impulse = _kInv.Transform(rhs);
            ApplyImpulse(impulse);
        }
        else
        {
            float c = (_worldB - _worldA).Length() - Length;
            float vrel = Vector3.Dot(VelB - VelA, _axis);

            if (Type == Kind.Spring)
            {
                // Soft distance link. Unlike Distance/Rope, it deliberately overshoots and oscillates.
                // _axisMass keeps heavy/light pairs from exploding too easily.
                float jn = -(Stiffness * c + Damping * vrel) * h * _axisMass;
                jn = Math.Clamp(jn, -2.5f, 2.5f);
                ApplyImpulse(_axis * jn);
                return;
            }

            if (Type == Kind.Rope && c < 0f) return; // slack rope does nothing

            float jnRigid = -(vrel + beta / h * c) * _axisMass;
            ApplyImpulse(_axis * jnRigid);
        }
    }

    private void ApplyImpulse(Vector3 p)
    {
        if (!A.IsStatic)
        {
            A.Velocity -= p * A.InvMass;
            A.AngularVelocity -= A.InvInertiaWorld.Transform(Vector3.Cross(_rA, p));
        }
        if (B is { IsStatic: false })
        {
            B.Velocity += p * B.InvMass;
            B.AngularVelocity += B.InvInertiaWorld.Transform(Vector3.Cross(_rB, p));
        }
    }

    private float InvMassSum()
        => (A.IsStatic ? 0f : A.InvMass) + (B is { IsStatic: false } ? B.InvMass : 0f);

    private float EffMassAlong(Vector3 n)
    {
        float k = 1e-6f;
        if (!A.IsStatic)
        {
            var rn = Vector3.Cross(_rA, n);
            k += A.InvMass + Vector3.Dot(Vector3.Cross(A.InvInertiaWorld.Transform(rn), _rA), n);
        }
        if (B is { IsStatic: false })
        {
            var rn = Vector3.Cross(_rB, n);
            k += B.InvMass + Vector3.Dot(Vector3.Cross(B.InvInertiaWorld.Transform(rn), _rB), n);
        }
        return k;
    }

    private static Mat3 SkewInertiaTerm(RigidBody b, Vector3 r)
    {
        // -skew(r) * Iinv * skew(r), built column by column
        var c0 = SkewMul(r, b.InvInertiaWorld.Transform(SkewMul(r, Vector3.UnitX)));
        var c1 = SkewMul(r, b.InvInertiaWorld.Transform(SkewMul(r, Vector3.UnitY)));
        var c2 = SkewMul(r, b.InvInertiaWorld.Transform(SkewMul(r, Vector3.UnitZ)));
        return new Mat3
        {
            M00 = -c0.X,
            M01 = -c1.X,
            M02 = -c2.X,
            M10 = -c0.Y,
            M11 = -c1.Y,
            M12 = -c2.Y,
            M20 = -c0.Z,
            M21 = -c1.Z,
            M22 = -c2.Z,
        };
    }

    // skew(r) * v == r x v
    private static Vector3 SkewMul(Vector3 r, Vector3 v) => Vector3.Cross(r, v);
}

internal sealed class PhysicsWorld
{
    public const float FixedStep = 1f / 120f;

    private const int SolverIterations = 10;
    private const float Beta = 0.2f;            // Baumgarte factor
    private const float Slop = 0.008f;
    private const float RestitutionThreshold = 1.0f;

    // rolling resistance for balls in contact (per second); tuned so a ball coasts to rest
    // in a couple of seconds instead of rolling indefinitely
    private const float RollAngularDamp = 1.6f;
    private const float RollLinearDamp = 0.9f;

    // sleeping: a body below both velocity thresholds for SleepDelay seconds nods off
    private const float SleepLinVelSq = 0.08f * 0.08f;
    private const float SleepAngVelSq = 0.30f * 0.30f;
    private const float SleepDelay = 0.6f;
    private const float WakeImpactSpeed = 0.30f;

    private const float WarmStartMatchDistSq = 0.04f * 0.04f;

    public readonly List<RigidBody> Bodies = [];
    public readonly List<Joint> Joints = [];
    public readonly List<ForceField> Fields = [];
    public readonly List<WaterVolume> Waters = [];

    // strong contacts this frame, drained by the renderer to spawn sparks
    public readonly List<(Vector3 point, Vector3 normal, float speed)> Impacts = [];
    public readonly RigidBody Ground = new()
    {
        InvMass = 0f,
        Restitution = 0.2f,
        Friction = 0.7f,
    };
    public Vector3 Gravity = new(0, -9.81f, 0);

    private readonly List<Contact> _contacts = [];
    private float _accumulator;
    private readonly Random _breakRng = new(24680);

    public RigidBody? Grabbed;
    public Vector3 GrabLocalAnchor;
    public Vector3 DragTarget;

    public int AwakeCount
    {
        get
        {
            int n = 0;
            foreach (var b in Bodies) if (!b.Inactive) n++;
            return n;
        }
    }

    // ---- warm starting cache ----
    // Impulses solved on the previous substep, keyed by body pair and re-anchored
    // in A's local frame. Feeding them back as the starting guess is what lets a
    // stack of boxes actually settle instead of buzzing forever: the solver only
    // has to correct the small per-frame change, not rediscover the whole load path
    // from zero in 10 iterations.
    private struct CachedImpulse
    {
        public Vector3 LocalA;
        public float Pn, Pt1, Pt2;
    }
    private Dictionary<(RigidBody, RigidBody), List<CachedImpulse>> _warmCache = new();
    private Dictionary<(RigidBody, RigidBody), List<CachedImpulse>> _warmCacheBack = new();

    public void Step(float dt)
    {
        Impacts.Clear();
        _accumulator += MathF.Min(dt, 0.05f);
        int steps = 0;
        while (_accumulator >= FixedStep && steps++ < 8)
        {
            SubStep(FixedStep);
            _accumulator -= FixedStep;
        }
        if (steps >= 8) _accumulator = 0f; // can't keep up - drop time instead of spiraling
    }

    private void SubStep(float h)
    {
        foreach (var w in Waters) w.Step(h);
        foreach (var b in Bodies)
        {
            if (b.Inactive) continue;
            b.Velocity += Gravity * h;
        }

        ApplyEnvironment(h);
        ApplyDragSpring(h);

        _contacts.Clear();
        foreach (var b in Bodies) b.Touching = false;
        GenerateContacts();
        foreach (var c in _contacts) { c.A.Touching = true; c.B.Touching = true; }

        // joints share the solver with contacts: wake the pairs first so a tug on one
        // end of a chain travels down it, then interleave the iterations
        foreach (var j in Joints) j.WakePair();
        foreach (var j in Joints) j.Presolve(h);

        foreach (var c in _contacts) Presolve(c, h);
        for (int i = 0; i < SolverIterations; i++)
        {
            foreach (var j in Joints) j.Solve(h);
            foreach (var c in _contacts) SolveContact(c);
        }
        for (int i = 0; i < SolverIterations; i++)
            foreach (var c in _contacts) SolvePosition(c);

        StoreWarmCache();
        CollectImpacts();
        BreakBodiesFromImpacts();

        foreach (var b in Bodies)
        {
            if (b.Inactive) continue;

            b.Position += (b.Velocity + b.BiasVelocity) * h;

            var w = b.AngularVelocity + b.BiasAngularVelocity;
            if (w.LengthSquared() > 1e-10f)
            {
                var dq = Quat.Mul(new Quaternion(w.X, w.Y, w.Z, 0f), b.Rotation);
                b.Rotation = Quaternion.Normalize(new Quaternion(
                    b.Rotation.X + dq.X * 0.5f * h,
                    b.Rotation.Y + dq.Y * 0.5f * h,
                    b.Rotation.Z + dq.Z * 0.5f * h,
                    b.Rotation.W + dq.W * 0.5f * h));
            }

            b.BiasVelocity = Vector3.Zero;
            b.BiasAngularVelocity = Vector3.Zero;
            if (b.RippleCooldown > 0f) b.RippleCooldown -= h;

            // mild damping keeps stacks from buzzing
            b.Velocity *= 1f / (1f + 0.02f * h);
            b.AngularVelocity *= 1f / (1f + 0.08f * h);

            // rolling resistance: a ball resting on a surface should coast to a stop, not
            // roll forever (wind-pushed balls, the domino striker, thrown bowling balls).
            // Only applied to balls that are actually touching something, so airborne or
            // floating spheres are unaffected.
            if (b.IsBall && b.Touching)
            {
                b.AngularVelocity *= 1f / (1f + RollAngularDamp * h);
                // shave the horizontal glide too; gravity-aligned (fall) component is left alone
                var v = b.Velocity;
                var up = Vector3.UnitY;
                var vUp = Vector3.Dot(v, up) * up;
                var vFlat = v - vUp;
                vFlat *= 1f / (1f + RollLinearDamp * h);
                b.Velocity = vFlat + vUp;
            }

            // sleep bookkeeping
            if (b.Velocity.LengthSquared() < SleepLinVelSq
                && b.AngularVelocity.LengthSquared() < SleepAngVelSq)
            {
                b.SleepTimer += h;
                if (b.SleepTimer >= SleepDelay && b != Grabbed)
                {
                    b.Sleeping = true;
                    b.Velocity = Vector3.Zero;
                    b.AngularVelocity = Vector3.Zero;
                }
            }
            else
            {
                b.SleepTimer = 0f;
            }

            b.UpdateDerived();
        }
    }

    private void ApplyEnvironment(float h)
    {
        if (Fields.Count == 0 && Waters.Count == 0) return;
        foreach (var b in Bodies)
        {
            if (b.IsStatic) continue;
            bool touched = false;
            foreach (var f in Fields) { f.Apply(b, h); touched = true; }
            foreach (var w in Waters) w.Apply(b, Gravity, h);
            // a field should be able to stir a sleeping body back to life
            if (touched && b.Sleeping) b.Wake();
        }
    }

    private const float ImpactSparkSpeed = 2.5f;

    private void CollectImpacts()
    {
        foreach (var c in _contacts)
            if (c.ImpactSpeed > ImpactSparkSpeed)
                Impacts.Add((c.Point, c.Normal, c.ImpactSpeed));
    }

    private void BreakBodiesFromImpacts()
    {
        if (_contacts.Count == 0 || Bodies.Count > 520) return;

        var toBreak = new HashSet<RigidBody>();
        foreach (var c in _contacts)
        {
            if (c.ImpactSpeed < 1f) continue;
            TryMarkBreakable(c.A, c.ImpactSpeed, toBreak);
            TryMarkBreakable(c.B, c.ImpactSpeed, toBreak);
        }

        foreach (var b in toBreak)
            BreakBody(b);
    }

    private static void TryMarkBreakable(RigidBody b, float impactSpeed, HashSet<RigidBody> toBreak)
    {
        if (b.IsStatic || !b.UserObject || !b.Breakable) return;
        if (b.BoundingRadius < 0.16f) return;
        if (impactSpeed >= MathF.Max(0.5f, b.BreakThreshold))
            toBreak.Add(b);
    }

    private void BreakBody(RigidBody b)
    {
        if (!Bodies.Contains(b)) return;

        var pieces = new List<RigidBody>(Math.Clamp(b.BreakPieces, 3, 18));
        foreach (var child in b.Children)
            CreateBreakPiecesForChild(b, child, pieces);

        if (pieces.Count == 0) return;

        var originalVel = b.Velocity;
        var originalAng = b.AngularVelocity;
        var color = b.Color;

        RemoveBody(b);

        foreach (var p in pieces)
        {
            p.Color = Vector3.Clamp(color * (0.75f + (float)_breakRng.NextDouble() * 0.35f), Vector3.Zero, Vector3.One);
            p.Friction = b.Friction;
            p.Restitution = MathF.Max(b.Restitution, 0.18f);
            p.Breakable = false; // one-level fracture keeps runaway debris under control
            p.BreakThreshold = b.BreakThreshold;
            p.Velocity = originalVel + RandomUnit() * (1.0f + (float)_breakRng.NextDouble() * 2.5f);
            p.AngularVelocity = originalAng + RandomUnit() * (2.0f + (float)_breakRng.NextDouble() * 5.0f);
            p.UserObject = true;
            p.Wake();
            Bodies.Add(p);
        }
    }

    private void CreateBreakPiecesForChild(RigidBody source, ChildShape child, List<RigidBody> pieces)
    {
        var childPos = source.Position + Vector3.Transform(child.LocalPos, source.Rotation);
        var childRot = Quat.Mul(source.Rotation, child.LocalRot);
        int maxPieces = Math.Clamp(source.BreakPieces, 3, 18);

        if (child.Shape == ShapeType.Box)
        {
            var he = child.HalfExtents;
            if (he.LengthSquared() < 0.03f) return;

            var smallHe = Vector3.Max(he * 0.46f, new Vector3(0.06f));
            int made = 0;
            for (int sx = -1; sx <= 1 && made < maxPieces; sx += 2)
            for (int sy = -1; sy <= 1 && made < maxPieces; sy += 2)
            for (int sz = -1; sz <= 1 && made < maxPieces; sz += 2)
            {
                var local = new Vector3(sx * he.X * 0.48f, sy * he.Y * 0.48f, sz * he.Z * 0.48f);
                var p = RigidBody.CreateBox(childPos + Vector3.Transform(local, childRot), smallHe, MathF.Max(source.Density, 0.001f));
                p.Rotation = childRot;
                p.UpdateDerived();
                pieces.Add(p);
                made++;
            }
            return;
        }

        if (child.Shape == ShapeType.Sphere)
        {
            float r = MathF.Max(child.Radius * 0.42f, 0.055f);
            int count = Math.Min(maxPieces, 7);
            for (int i = 0; i < count; i++)
            {
                var dir = RandomUnit();
                var p = RigidBody.CreateSphere(childPos + dir * child.Radius * 0.38f, r, MathF.Max(source.Density, 0.001f));
                pieces.Add(p);
            }
            return;
        }

        // Capsules are approximated by bead-like fragments.
        if (child.Shape == ShapeType.Capsule)
        {
            float r = MathF.Max(child.Radius * 0.38f, 0.05f);
            int count = Math.Min(maxPieces, 6);
            for (int i = 0; i < count; i++)
            {
                float u = count == 1 ? 0f : (i / (float)(count - 1) - 0.5f) * child.HalfHeight * 2f;
                var local = new Vector3(0, u, 0) + RandomUnit() * child.Radius * 0.2f;
                var p = RigidBody.CreateSphere(childPos + Vector3.Transform(local, childRot), r, MathF.Max(source.Density, 0.001f));
                pieces.Add(p);
            }
        }
    }

    private Vector3 RandomUnit()
    {
        var v = new Vector3(
            (float)_breakRng.NextDouble() * 2f - 1f,
            (float)_breakRng.NextDouble() * 2f - 1f,
            (float)_breakRng.NextDouble() * 2f - 1f);
        return v.LengthSquared() > 1e-6f ? Vector3.Normalize(v) : Vector3.UnitY;
    }

    /// <summary>Drops a body and any joints that referenced it. Keeps the world consistent.</summary>
    public void RemoveBody(RigidBody b)
    {
        Bodies.Remove(b);
        Joints.RemoveAll(j => j.Involves(b));
        if (Grabbed == b) Grabbed = null;
    }

    private void ApplyDragSpring(float h)
    {
        if (Grabbed == null || Grabbed.IsStatic) return;
        Grabbed.Wake();

        var anchor = Grabbed.Position + Vector3.Transform(GrabLocalAnchor, Grabbed.Rotation);
        var delta = DragTarget - anchor;
        float len = delta.Length();
        if (len > 4f) delta *= 4f / len; // limit spring stretch -> no explosions

        var velAtAnchor = Grabbed.VelocityAt(anchor);
        var force = Grabbed.Mass * (delta * 180f - velAtAnchor * 18f);
        Grabbed.ApplyImpulse(force * h, anchor);

        // extra angular damping while held, otherwise boxes spin like propellers
        Grabbed.AngularVelocity *= 1f / (1f + 4f * h);
    }

    // ================= contact generation =================

    private void GenerateContacts()
    {
        foreach (var body in Bodies)
        {
            if (body.Inactive) continue;
            foreach (ref var p in body.Proxies.AsSpan())
            {
                switch (p.Shape)
                {
                    case ShapeType.Sphere: SpherePlane(in p); break;
                    case ShapeType.Box: BoxPlane(in p); break;
                    case ShapeType.Capsule: CapsulePlane(in p); break;
                }
            }
        }

        // --- broad phase ---
        // Small bodies go into a uniform spatial hash so we only test nearby pairs instead
        // of every pair (the old O(n^2) loop is what made big collapses crawl). A few "huge"
        // bodies (the arena walls) would blow up the cell size, so they're tested separately
        // against everything - there are only a handful of them.
        int count = Bodies.Count;

        float maxR = 0f;
        for (int i = 0; i < count; i++)
        {
            float r = Bodies[i].BoundingRadius;
            if (r <= HugeRadius && r > maxR) maxR = r;
        }
        _cellSize = Math.Clamp(2f * maxR, 0.6f, 2f * HugeRadius);

        if (_next.Length < count) _next = new int[Math.Max(count, 64)];
        _cellHead.Clear();

        for (int i = 0; i < count; i++)
        {
            var b = Bodies[i];
            if (b.BoundingRadius > HugeRadius) continue; // huge bodies handled below
            long key = CellKey(b.Position);
            _next[i] = _cellHead.TryGetValue(key, out int head) ? head : -1;
            _cellHead[key] = i;
        }

        // small vs small: scan each body's 3x3x3 cell neighbourhood; j>i keeps each pair once
        for (int i = 0; i < count; i++)
        {
            var a = Bodies[i];
            if (a.BoundingRadius > HugeRadius) continue;
            (int cx, int cy, int cz) = CellCoords(a.Position);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!_cellHead.TryGetValue(PackKey(cx + dx, cy + dy, cz + dz), out int j)) continue;
                while (j != -1)
                {
                    if (j > i) NarrowPhase(a, Bodies[j]);
                    j = _next[j];
                }
            }
        }

        // huge bodies (walls) vs everything; keep A = lower index so warm-start keys are stable
        for (int p = 0; p < count; p++)
        {
            if (Bodies[p].BoundingRadius <= HugeRadius) continue;
            for (int q = 0; q < count; q++)
            {
                if (q == p) continue;
                bool qHuge = Bodies[q].BoundingRadius > HugeRadius;
                if (qHuge && q < p) continue; // huge-huge pair handled once
                int lo = Math.Min(p, q), hi = Math.Max(p, q);
                NarrowPhase(Bodies[lo], Bodies[hi]);
            }
        }
    }

    private const float HugeRadius = 4f;
    private readonly Dictionary<long, int> _cellHead = new();
    private int[] _next = [];
    private float _cellSize = 1f;

    private (int, int, int) CellCoords(Vector3 p)
        => ((int)MathF.Floor(p.X / _cellSize), (int)MathF.Floor(p.Y / _cellSize), (int)MathF.Floor(p.Z / _cellSize));

    private long CellKey(Vector3 p)
    {
        var (cx, cy, cz) = CellCoords(p);
        return PackKey(cx, cy, cz);
    }

    // exact (collision-free) packing of cell coords in +/-2^20 cells into one long
    private static long PackKey(int x, int y, int z)
        => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

    private void NarrowPhase(RigidBody a, RigidBody b)
    {
        if (a.Inactive && b.Inactive) return;

        float rr = a.BoundingRadius + b.BoundingRadius;
        if ((b.Position - a.Position).LengthSquared() > rr * rr) return;

        foreach (ref var pa in a.Proxies.AsSpan())
            foreach (ref var pb in b.Proxies.AsSpan())
            {
                float prr = pa.BoundingRadius + pb.BoundingRadius;
                if ((pb.Position - pa.Position).LengthSquared() > prr * prr) continue;
                Dispatch(in pa, in pb);
            }
    }

    private void Dispatch(in ShapeProxy a, in ShapeProxy b)
    {
        var (sa, sb) = (a.Shape, b.Shape);
        if (sa == ShapeType.Sphere && sb == ShapeType.Sphere) SphereSphere(in a, in b);
        else if (sa == ShapeType.Sphere && sb == ShapeType.Box) SphereBox(in a, in b, flip: false);
        else if (sa == ShapeType.Box && sb == ShapeType.Sphere) SphereBox(in b, in a, flip: true);
        else if (sa == ShapeType.Box && sb == ShapeType.Box) BoxBox(in a, in b);
        else if (sa == ShapeType.Capsule && sb == ShapeType.Capsule) CapsuleCapsule(in a, in b);
        else if (sa == ShapeType.Capsule && sb == ShapeType.Sphere) CapsuleSphere(in a, in b, flip: false);
        else if (sa == ShapeType.Sphere && sb == ShapeType.Capsule) CapsuleSphere(in b, in a, flip: true);
        else if (sa == ShapeType.Capsule && sb == ShapeType.Box) CapsuleBox(in a, in b, flip: false);
        else CapsuleBox(in b, in a, flip: true);
    }

    private void AddContact(RigidBody a, RigidBody b, Vector3 point, Vector3 normalAtoB, float penetration)
    {
        // a sleeping body only wakes if the thing touching it actually approaches with
        // some speed; a resting contact must NOT wake it, or stacks would keep each
        // other awake forever and sleeping would be useless
        if (a.Sleeping != b.Sleeping)
        {
            var sleeper = a.Sleeping ? a : b;
            var awake = a.Sleeping ? b : a;
            float approach = MathF.Abs(Vector3.Dot(
                awake.VelocityAt(point) - sleeper.VelocityAt(point), normalAtoB));
            if (approach > WakeImpactSpeed || awake == Grabbed)
                sleeper.Wake();
        }

        _contacts.Add(new Contact { A = a, B = b, Point = point, Normal = normalAtoB, Penetration = penetration });
    }

    // ---- vs ground plane (y = 0) ----

    private void SpherePlane(in ShapeProxy s)
    {
        float dist = s.Position.Y - s.Radius;
        if (dist < 0f)
            AddContact(Ground, s.Owner, s.Position - new Vector3(0, s.Radius, 0), Vector3.UnitY, -dist);
    }

    private void BoxPlane(in ShapeProxy b)
    {
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    var local = new Vector3(sx * b.HalfExtents.X, sy * b.HalfExtents.Y, sz * b.HalfExtents.Z);
                    var corner = b.Position + Vector3.Transform(local, b.Rotation);
                    if (corner.Y < 0f)
                        AddContact(Ground, b.Owner, corner, Vector3.UnitY, -corner.Y);
                }
    }

    private void CapsulePlane(in ShapeProxy c)
    {
        c.CapsuleSegment(out var p0, out var p1);
        foreach (var p in stackalloc[] { p0, p1 })
        {
            float dist = p.Y - c.Radius;
            if (dist < 0f)
                AddContact(Ground, c.Owner, p - new Vector3(0, c.Radius, 0), Vector3.UnitY, -dist);
        }
    }

    // ---- sphere pairs ----

    private void SphereSphere(in ShapeProxy a, in ShapeProxy b)
    {
        var d = b.Position - a.Position;
        float dist = d.Length();
        float sum = a.Radius + b.Radius;
        if (dist >= sum) return;

        var n = dist > 1e-6f ? d / dist : Vector3.UnitY;
        var point = a.Position + n * (a.Radius - (sum - dist) * 0.5f);
        AddContact(a.Owner, b.Owner, point, n, sum - dist);
    }

    private void SphereBox(in ShapeProxy s, in ShapeProxy box, bool flip)
    {
        if (!SphereObbCore(s.Position, s.Radius, in box,
                out var point, out var normalBoxToSphere, out float penetration))
            return;

        if (flip) AddContact(box.Owner, s.Owner, point, normalBoxToSphere, penetration);
        else AddContact(s.Owner, box.Owner, point, -normalBoxToSphere, penetration);
    }

    /// <summary>Sphere (center, radius) vs OBB. Handles the deep case (center inside the box).</summary>
    private static bool SphereObbCore(Vector3 center, float radius, in ShapeProxy box,
        out Vector3 point, out Vector3 normalBoxToSphere, out float penetration)
    {
        var invRot = Quaternion.Conjugate(box.Rotation);
        var local = Vector3.Transform(center - box.Position, invRot);
        var h = box.HalfExtents;
        var clamped = Vector3.Clamp(local, -h, h);

        if (clamped == local)
        {
            // center is inside the box: push out through the nearest face
            float dx = h.X - MathF.Abs(local.X);
            float dy = h.Y - MathF.Abs(local.Y);
            float dz = h.Z - MathF.Abs(local.Z);

            Vector3 nLocal;
            float depth;
            if (dx <= dy && dx <= dz) { nLocal = new Vector3(MathF.Sign(local.X), 0, 0); depth = dx; }
            else if (dy <= dz) { nLocal = new Vector3(0, MathF.Sign(local.Y), 0); depth = dy; }
            else { nLocal = new Vector3(0, 0, MathF.Sign(local.Z)); depth = dz; }
            if (nLocal == Vector3.Zero) nLocal = Vector3.UnitY;

            normalBoxToSphere = Vector3.Transform(nLocal, box.Rotation);
            point = center;
            penetration = radius + depth;
            return true;
        }

        var delta = local - clamped;
        float dist = delta.Length();
        if (dist >= radius)
        {
            point = default; normalBoxToSphere = default; penetration = 0f;
            return false;
        }

        normalBoxToSphere = Vector3.Transform(delta / dist, box.Rotation);
        point = box.Position + Vector3.Transform(clamped, box.Rotation);
        penetration = radius - dist;
        return true;
    }

    // ---- capsule pairs ----

    private void CapsuleSphere(in ShapeProxy cap, in ShapeProxy s, bool flip)
    {
        cap.CapsuleSegment(out var p0, out var p1);
        var closest = ClosestPtSegmentPoint(p0, p1, s.Position);

        var d = s.Position - closest;
        float dist = d.Length();
        float sum = cap.Radius + s.Radius;
        if (dist >= sum) return;

        var n = dist > 1e-6f ? d / dist : Vector3.UnitY;
        var point = closest + n * cap.Radius;

        if (flip) AddContact(s.Owner, cap.Owner, point, -n, sum - dist);
        else AddContact(cap.Owner, s.Owner, point, n, sum - dist);
    }

    private void CapsuleCapsule(in ShapeProxy a, in ShapeProxy b)
    {
        a.CapsuleSegment(out var a0, out var a1);
        b.CapsuleSegment(out var b0, out var b1);
        ClosestPtSegmentSegment(a0, a1, b0, b1, out var pA, out var pB);

        var d = pB - pA;
        float dist = d.Length();
        float sum = a.Radius + b.Radius;
        if (dist >= sum) return;

        var n = dist > 1e-6f ? d / dist : Vector3.UnitY;
        AddContact(a.Owner, b.Owner, (pA + n * a.Radius + pB - n * b.Radius) * 0.5f, n, sum - dist);
    }

    private void CapsuleBox(in ShapeProxy cap, in ShapeProxy box, bool flip)
    {
        cap.CapsuleSegment(out var p0, out var p1);

        var invRot = Quaternion.Conjugate(box.Rotation);
        var l0 = Vector3.Transform(p0 - box.Position, invRot);
        var l1 = Vector3.Transform(p1 - box.Position, invRot);
        var h = box.HalfExtents;

        // squared distance from a point of the segment to the box is convex in t,
        // so a plain ternary search finds the deepest spot - no special cases needed
        float DistSq(float t)
        {
            var p = Vector3.Lerp(l0, l1, t);
            return (p - Vector3.Clamp(p, -h, h)).LengthSquared();
        }

        float lo = 0f, hi = 1f;
        for (int i = 0; i < 48; i++)
        {
            float m1 = lo + (hi - lo) / 3f;
            float m2 = hi - (hi - lo) / 3f;
            if (DistSq(m1) <= DistSq(m2)) hi = m2; else lo = m1;
        }
        float tBest = (lo + hi) * 0.5f;

        // also probe both ends: a capsule lying flat on a box needs more than one
        // contact point or it would wobble like a seesaw
        float lastT = float.MinValue;
        foreach (float t in stackalloc[] { tBest, 0f, 1f })
        {
            if (MathF.Abs(t - lastT) < 0.05f) continue;

            var center = Vector3.Lerp(p0, p1, t);
            if (!SphereObbCore(center, cap.Radius, in box,
                    out var point, out var normalBoxToCap, out float pen))
                continue;

            lastT = t;
            if (flip) AddContact(box.Owner, cap.Owner, point, normalBoxToCap, pen);
            else AddContact(cap.Owner, box.Owner, point, -normalBoxToCap, pen);
        }
    }

    private static Vector3 ClosestPtSegmentPoint(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = b - a;
        float t = Vector3.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-9f);
        return a + ab * Math.Clamp(t, 0f, 1f);
    }

    /// <summary>Closest points between two segments (Ericson, Real-Time Collision Detection 5.1.9).</summary>
    private static void ClosestPtSegmentSegment(
        Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        float a = d1.LengthSquared();
        float e = d2.LengthSquared();
        float f = Vector3.Dot(d2, r);
        float s, t;

        if (a <= 1e-9f && e <= 1e-9f) { c1 = p1; c2 = p2; return; }

        if (a <= 1e-9f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-9f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom > 1e-9f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }

        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    // ---- OBB vs OBB: SAT over all 15 axes, then face clipping or edge-edge ----

    private void BoxBox(in ShapeProxy a, in ShapeProxy b)
    {
        Vector3[] axesA = [a.Axis(0), a.Axis(1), a.Axis(2)];
        Vector3[] axesB = [b.Axis(0), b.Axis(1), b.Axis(2)];
        var d = b.Position - a.Position;
        var ea = a.HalfExtents;
        var eb = b.HalfExtents;

        float bestPen = float.MaxValue;
        Vector3 bestAxis = Vector3.UnitY;
        int bestType = -1;                 // 0 = face of A, 1 = face of B, 2 = edge-edge
        int bestI = 0, bestJ = 0;

        bool Test(Vector3 axis, int type, int i, int j)
        {
            float lenSq = axis.LengthSquared();
            if (lenSq < 1e-8f) return true; // near-parallel edges, the cross product is junk
            axis /= MathF.Sqrt(lenSq);

            float ra = ea.X * MathF.Abs(Vector3.Dot(axesA[0], axis))
                     + ea.Y * MathF.Abs(Vector3.Dot(axesA[1], axis))
                     + ea.Z * MathF.Abs(Vector3.Dot(axesA[2], axis));
            float rb = eb.X * MathF.Abs(Vector3.Dot(axesB[0], axis))
                     + eb.Y * MathF.Abs(Vector3.Dot(axesB[1], axis))
                     + eb.Z * MathF.Abs(Vector3.Dot(axesB[2], axis));

            float proj = Vector3.Dot(d, axis);
            float pen = ra + rb - MathF.Abs(proj);
            if (pen < 0f) return false;    // found a separating axis, no collision

            // edge axes carry a 5% penalty: an edge-edge pair gives a single contact
            // point, a face gives a whole clipped manifold, so when penetrations are
            // close we'd much rather pick the face. (Getting the sign of this bias
            // wrong selects edges for plain stacked boxes - ask me how I know.)
            float effective = type == 2 ? pen * 1.05f + 1e-4f : pen;
            if (effective < bestPen)
            {
                bestPen = effective;
                bestAxis = proj < 0f ? -axis : axis; // orient from A toward B
                bestType = type;
                bestI = i; bestJ = j;
            }
            return true;
        }

        for (int i = 0; i < 3; i++) if (!Test(axesA[i], 0, i, 0)) return;
        for (int j = 0; j < 3; j++) if (!Test(axesB[j], 1, 0, j)) return;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (!Test(Vector3.Cross(axesA[i], axesB[j]), 2, i, j)) return;

        if (bestType == 2)
        {
            EdgeEdgeContact(in a, in b, axesA, axesB, bestAxis, bestI, bestJ);
            return;
        }

        bool refIsA = bestType == 0;
        ref readonly var refBox = ref refIsA ? ref a : ref b;
        ref readonly var incBox = ref refIsA ? ref b : ref a;
        var refNormal = refIsA ? bestAxis : -bestAxis; // points from refBox toward incBox
        int refAxisIdx = refIsA ? bestI : bestJ;

        // incident face = the face of the other box looking most directly back at us
        int incAxisIdx = 0;
        float minDot = float.MaxValue;
        float incSign = 1f;
        for (int i = 0; i < 3; i++)
        {
            float dot = Vector3.Dot(incBox.Axis(i), refNormal);
            if (dot < minDot) { minDot = dot; incAxisIdx = i; incSign = 1f; }
            if (-dot < minDot) { minDot = -dot; incAxisIdx = i; incSign = -1f; }
        }

        var incNormal = incBox.Axis(incAxisIdx) * incSign;
        int iu = (incAxisIdx + 1) % 3, iv = (incAxisIdx + 2) % 3;
        var incU = incBox.Axis(iu) * Comp(incBox.HalfExtents, iu);
        var incV = incBox.Axis(iv) * Comp(incBox.HalfExtents, iv);
        var incCenter = incBox.Position + incNormal * Comp(incBox.HalfExtents, incAxisIdx);

        var poly = new List<Vector3>(8)
        {
            incCenter + incU + incV,
            incCenter + incU - incV,
            incCenter - incU - incV,
            incCenter - incU + incV,
        };

        // Sutherland-Hodgman: shave the incident face down by the 4 side planes
        // of the reference face; whatever survives below the face is the manifold
        int ru = (refAxisIdx + 1) % 3, rv = (refAxisIdx + 2) % 3;
        var axisU = refBox.Axis(ru);
        var axisV = refBox.Axis(rv);
        ClipPolygon(poly, axisU, Vector3.Dot(refBox.Position, axisU) + Comp(refBox.HalfExtents, ru));
        ClipPolygon(poly, -axisU, -Vector3.Dot(refBox.Position, axisU) + Comp(refBox.HalfExtents, ru));
        ClipPolygon(poly, axisV, Vector3.Dot(refBox.Position, axisV) + Comp(refBox.HalfExtents, rv));
        ClipPolygon(poly, -axisV, -Vector3.Dot(refBox.Position, axisV) + Comp(refBox.HalfExtents, rv));

        float faceOffset = Vector3.Dot(refBox.Position, refNormal) + Comp(refBox.HalfExtents, refAxisIdx);
        var normalAtoB = refIsA ? refNormal : -refNormal;

        foreach (var p in poly)
        {
            float sep = Vector3.Dot(p, refNormal) - faceOffset;
            if (sep < 0f)
                AddContact(a.Owner, b.Owner, p - refNormal * (sep * 0.5f), normalAtoB, -sep);
        }
    }

    private void EdgeEdgeContact(in ShapeProxy a, in ShapeProxy b,
        Vector3[] axesA, Vector3[] axesB, Vector3 normalAtoB, int edgeAxisA, int edgeAxisB)
    {
        // pick the edge of each box closest to the other one: start at the center
        // and walk to the corner along the two non-edge axes, choosing signs that
        // move toward the contact
        var pA = a.Position;
        for (int k = 0; k < 3; k++)
        {
            if (k == edgeAxisA) continue;
            float s = Vector3.Dot(axesA[k], normalAtoB) > 0f ? 1f : -1f;
            pA += axesA[k] * (s * Comp(a.HalfExtents, k));
        }

        var pB = b.Position;
        for (int k = 0; k < 3; k++)
        {
            if (k == edgeAxisB) continue;
            float s = Vector3.Dot(axesB[k], -normalAtoB) > 0f ? 1f : -1f;
            pB += axesB[k] * (s * Comp(b.HalfExtents, k));
        }

        var d1 = axesA[edgeAxisA];
        var d2 = axesB[edgeAxisB];
        var r = pA - pB;
        float bDot = Vector3.Dot(d1, d2);
        float c = Vector3.Dot(d1, r);
        float f = Vector3.Dot(d2, r);
        float denom = 1f - bDot * bDot;
        if (MathF.Abs(denom) < 1e-6f) return; // parallel edges - the face case will pick this up

        float s1 = Math.Clamp((bDot * f - c) / denom, -Comp(a.HalfExtents, edgeAxisA), Comp(a.HalfExtents, edgeAxisA));
        float t1 = Math.Clamp((f - bDot * c) / denom, -Comp(b.HalfExtents, edgeAxisB), Comp(b.HalfExtents, edgeAxisB));

        var cA = pA + d1 * s1;
        var cB = pB + d2 * t1;

        float depth = Vector3.Dot(cA - cB, normalAtoB);
        if (depth < 0f) depth = 0.001f;

        AddContact(a.Owner, b.Owner, (cA + cB) * 0.5f, normalAtoB, depth);
    }

    private static float Comp(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    private static void ClipPolygon(List<Vector3> poly, Vector3 n, float offset)
    {
        if (poly.Count == 0) return;
        var output = new List<Vector3>(poly.Count + 2);

        for (int i = 0; i < poly.Count; i++)
        {
            var cur = poly[i];
            var next = poly[(i + 1) % poly.Count];
            float dCur = Vector3.Dot(cur, n) - offset;
            float dNext = Vector3.Dot(next, n) - offset;

            if (dCur <= 0f) output.Add(cur);
            if (dCur * dNext < 0f)
            {
                float t = dCur / (dCur - dNext);
                output.Add(cur + (next - cur) * t);
            }
        }

        poly.Clear();
        poly.AddRange(output);
    }

    // ================= solver =================

    private void Presolve(Contact c, float h)
    {
        c.RA = c.Point - c.A.Position;
        c.RB = c.Point - c.B.Position;
        var n = c.Normal;

        c.MassNormal = 1f / EffectiveMass(c, n);

        var t1 = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.X) > 0.7f ? Vector3.UnitY : Vector3.UnitX));
        var t2 = Vector3.Cross(n, t1);
        c.Tangent1 = t1;
        c.Tangent2 = t2;
        c.MassTangent1 = 1f / EffectiveMass(c, t1);
        c.MassTangent2 = 1f / EffectiveMass(c, t2);

        float vn = Vector3.Dot(RelativeVelocity(c), n);
        float restitution = MathF.Max(c.A.Restitution, c.B.Restitution);
        c.VelocityBias = vn < -RestitutionThreshold ? -restitution * vn : 0f;
        c.ImpactSpeed = vn < 0f ? -vn : 0f;

        c.PositionBias = MathF.Min(Beta / h * MathF.Max(0f, c.Penetration - Slop), 4f);
        c.Pn = c.Pt1 = c.Pt2 = c.Pnb = 0f;

        // warm start: if we solved (almost) this contact last substep, start from
        // last frame's impulses instead of zero
        if (_warmCache.TryGetValue((c.A, c.B), out var cached))
        {
            var localA = Vector3.Transform(c.Point - c.A.Position, Quaternion.Conjugate(c.A.Rotation));
            foreach (var w in cached)
            {
                if ((w.LocalA - localA).LengthSquared() > WarmStartMatchDistSq) continue;
                c.Pn = w.Pn;
                c.Pt1 = w.Pt1;
                c.Pt2 = w.Pt2;
                ApplyPair(c, n * c.Pn + t1 * c.Pt1 + t2 * c.Pt2);
                break;
            }
        }
    }

    private void StoreWarmCache()
    {
        (_warmCache, _warmCacheBack) = (_warmCacheBack, _warmCache);
        _warmCache.Clear();
        foreach (var c in _contacts)
        {
            if (c is { Pn: 0f, Pt1: 0f, Pt2: 0f }) continue;
            var key = (c.A, c.B);
            if (!_warmCache.TryGetValue(key, out var list))
                _warmCache[key] = list = [];
            list.Add(new CachedImpulse
            {
                LocalA = Vector3.Transform(c.Point - c.A.Position, Quaternion.Conjugate(c.A.Rotation)),
                Pn = c.Pn,
                Pt1 = c.Pt1,
                Pt2 = c.Pt2,
            });
        }
    }

    private static float EffectiveMass(Contact c, Vector3 dir)
    {
        float k = 1e-6f;
        if (!c.A.Inactive)
        {
            var raCrossD = Vector3.Cross(c.RA, dir);
            k += c.A.InvMass + Vector3.Dot(Vector3.Cross(c.A.InvInertiaWorld.Transform(raCrossD), c.RA), dir);
        }
        if (!c.B.Inactive)
        {
            var rbCrossD = Vector3.Cross(c.RB, dir);
            k += c.B.InvMass + Vector3.Dot(Vector3.Cross(c.B.InvInertiaWorld.Transform(rbCrossD), c.RB), dir);
        }
        return k;
    }

    private static Vector3 RelativeVelocity(Contact c)
        => (c.B.Velocity + Vector3.Cross(c.B.AngularVelocity, c.RB))
         - (c.A.Velocity + Vector3.Cross(c.A.AngularVelocity, c.RA));

    private static void SolveContact(Contact c)
    {
        var n = c.Normal;

        // normal impulse with accumulated clamping: individual iterations may go
        // negative, only the running total has to stay >= 0
        float vn = Vector3.Dot(RelativeVelocity(c), n);
        float dPn = c.MassNormal * (c.VelocityBias - vn);
        float oldPn = c.Pn;
        c.Pn = MathF.Max(oldPn + dPn, 0f);
        ApplyPair(c, n * (c.Pn - oldPn));

        // friction: Coulomb cone approximated by a box on two tangents
        float friction = MathF.Sqrt(c.A.Friction * c.B.Friction);
        float maxPt = friction * c.Pn;

        float vt1 = Vector3.Dot(RelativeVelocity(c), c.Tangent1);
        float oldPt1 = c.Pt1;
        c.Pt1 = Math.Clamp(oldPt1 + c.MassTangent1 * -vt1, -maxPt, maxPt);
        ApplyPair(c, c.Tangent1 * (c.Pt1 - oldPt1));

        float vt2 = Vector3.Dot(RelativeVelocity(c), c.Tangent2);
        float oldPt2 = c.Pt2;
        c.Pt2 = Math.Clamp(oldPt2 + c.MassTangent2 * -vt2, -maxPt, maxPt);
        ApplyPair(c, c.Tangent2 * (c.Pt2 - oldPt2));
    }

    private static void SolvePosition(Contact c)
    {
        if (c.PositionBias <= 0f) return;
        var n = c.Normal;

        var relBias = (c.B.BiasVelocity + Vector3.Cross(c.B.BiasAngularVelocity, c.RB))
                    - (c.A.BiasVelocity + Vector3.Cross(c.A.BiasAngularVelocity, c.RA));
        float vbn = Vector3.Dot(relBias, n);
        float dP = c.MassNormal * (c.PositionBias - vbn);
        float old = c.Pnb;
        c.Pnb = MathF.Max(old + dP, 0f);
        dP = c.Pnb - old;

        var impulse = n * dP;
        if (!c.A.Inactive)
        {
            c.A.BiasVelocity -= impulse * c.A.InvMass;
            c.A.BiasAngularVelocity -= c.A.InvInertiaWorld.Transform(Vector3.Cross(c.RA, impulse));
        }
        if (!c.B.Inactive)
        {
            c.B.BiasVelocity += impulse * c.B.InvMass;
            c.B.BiasAngularVelocity += c.B.InvInertiaWorld.Transform(Vector3.Cross(c.RB, impulse));
        }
    }

    private static void ApplyPair(Contact c, Vector3 impulse)
    {
        // sleeping bodies are treated as static here; if the hit was hard enough
        // to matter, AddContact has already woken them up
        if (!c.A.Inactive)
        {
            c.A.Velocity -= impulse * c.A.InvMass;
            c.A.AngularVelocity -= c.A.InvInertiaWorld.Transform(Vector3.Cross(c.RA, impulse));
        }
        if (!c.B.Inactive)
        {
            c.B.Velocity += impulse * c.B.InvMass;
            c.B.AngularVelocity += c.B.InvInertiaWorld.Transform(Vector3.Cross(c.RB, impulse));
        }
    }

    // ================= picking =================

    /// <summary>
    /// Radial impulse from a point - the sandbox "explosion". Strength falls off with
    /// distance (linear), and a small upward bias makes the debris hop instead of just
    /// sliding along the floor. Everything in range gets woken first, otherwise sleeping
    /// bodies would just ignore the blast.
    /// </summary>
    public void ApplyExplosion(Vector3 center, float radius, float strength)
    {
        foreach (var b in Bodies)
        {
            if (b.IsStatic) continue;
            var d = b.Position - center;
            float dist = d.Length();
            if (dist > radius) continue;

            b.Wake();
            var dir = dist > 1e-4f ? d / dist : Vector3.UnitY;
            dir = Vector3.Normalize(dir + new Vector3(0, 0.6f, 0)); // lift bias
            float falloff = 1f - dist / radius;
            b.ApplyImpulse(dir * (strength * falloff * b.Mass), b.Position);
        }
    }

    public RigidBody? RayCast(Vector3 origin, Vector3 dir, out float tHit, out Vector3 hitPoint)
    {
        tHit = float.MaxValue;
        hitPoint = default;
        RigidBody? best = null;

        foreach (var body in Bodies)
        {
            if (body.IsStatic) continue;
            foreach (ref var p in body.Proxies.AsSpan())
            {
                float t = p.Shape switch
                {
                    ShapeType.Sphere => RaySphere(origin, dir, p.Position, p.Radius),
                    ShapeType.Box => RayObb(origin, dir, in p),
                    ShapeType.Capsule => RayCapsule(origin, dir, in p),
                    _ => -1f,
                };
                if (t >= 0f && t < tHit)
                {
                    tHit = t;
                    best = body;
                }
            }
        }

        if (best != null) hitPoint = origin + dir * tHit;
        return best;
    }

    private static float RaySphere(Vector3 o, Vector3 d, Vector3 center, float r)
    {
        var m = o - center;
        float b = Vector3.Dot(m, d);
        float c = Vector3.Dot(m, m) - r * r;
        float disc = b * b - c;
        if (disc < 0f) return -1f;
        float t = -b - MathF.Sqrt(disc);
        return t >= 0f ? t : -1f;
    }

    private static float RayObb(Vector3 o, Vector3 d, in ShapeProxy box)
    {
        // classic slab test, done in the box's local frame
        var invRot = Quaternion.Conjugate(box.Rotation);
        var lo = Vector3.Transform(o - box.Position, invRot);
        var ld = Vector3.Transform(d, invRot);
        var h = box.HalfExtents;

        float tMin = 0f, tMax = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float oi = Comp(lo, i), di = Comp(ld, i), hi = Comp(h, i);
            if (MathF.Abs(di) < 1e-8f)
            {
                if (oi < -hi || oi > hi) return -1f;
                continue;
            }
            float inv = 1f / di;
            float t1 = (-hi - oi) * inv;
            float t2 = (hi - oi) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) return -1f;
        }
        return tMin;
    }

    private static float RayCapsule(Vector3 o, Vector3 d, in ShapeProxy c)
    {
        var invRot = Quaternion.Conjugate(c.Rotation);
        var lo = Vector3.Transform(o - c.Position, invRot);
        var ld = Vector3.Transform(d, invRot);
        float r = c.Radius, h = c.HalfHeight;

        float best = -1f;
        void Consider(float t) { if (t >= 0f && (best < 0f || t < best)) best = t; }

        // side: infinite cylinder x^2 + z^2 = r^2, accept hits within the segment span
        float a = ld.X * ld.X + ld.Z * ld.Z;
        if (a > 1e-9f)
        {
            float b = 2f * (lo.X * ld.X + lo.Z * ld.Z);
            float cc = lo.X * lo.X + lo.Z * lo.Z - r * r;
            float disc = b * b - 4f * a * cc;
            if (disc >= 0f)
            {
                float sq = MathF.Sqrt(disc);
                foreach (float t in stackalloc[] { (-b - sq) / (2f * a), (-b + sq) / (2f * a) })
                    if (MathF.Abs(lo.Y + ld.Y * t) <= h)
                        Consider(t);
            }
        }

        // the two sphere caps
        foreach (float cy in stackalloc[] { -h, h })
        {
            var co = lo - new Vector3(0, cy, 0);
            float b = 2f * Vector3.Dot(co, ld);
            float cc = co.LengthSquared() - r * r;
            float dlen = ld.LengthSquared();
            float disc = b * b - 4f * cc * dlen;
            if (disc < 0f) continue;
            float sq = MathF.Sqrt(disc);
            Consider((-b - sq) / (2f * dlen));
            Consider((-b + sq) / (2f * dlen));
        }

        return best;
    }
}
