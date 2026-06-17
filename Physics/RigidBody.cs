using System.Numerics;
using MakarovPhysicsSandbox.Material;

namespace MakarovPhysicsSandbox.Physics;

public sealed class RigidBody
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
    public MaterialId MaterialId = MaterialId.Custom; // engine-level gameplay material, not just UI values
    public Matrix3x3 InvInertiaLocal;
    public Matrix3x3 InvInertiaWorld;

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
    public float Flammability = 0.7f; // 0 = will never ignite. Also gated by density/wetness.
    public float Conductivity = 0.05f; // 0 = insulator, 1 = good conductor (wetness boosts this).
    public float ExplosivePower;        // 0 = inert, >0 detonation strength multiplier.
    public float Wetness;              // 0..1, raised by water volumes, dries over time.
    public float Charge;               // transient electricity charge/arc state for gameplay + VFX.

    // Optional toy destruction. Kept deliberately simple: fragile bodies are replaced
    // by a few smaller pieces when they take a hard enough impact.
    public bool Breakable;
    public float BreakThreshold = 7.5f;
    public int BreakPieces = 8;

    public bool Sleeping;
    public float SleepTimer;
    public float DebrisLife = -1f;   // >= 0 for fracture debris: seconds left before it is removed
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
            InvInertiaLocal = Matrix3x3.Zero,
            InvInertiaWorld = Matrix3x3.Zero,
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
        var inertia = Matrix3x3.Zero;
        for (int i = 0; i < children.Length; i++)
        {
            children[i].LocalPos -= com;

            var (m, iDiag) = children[i].MassProperties(density);
            var r = Matrix3x3.FromQuaternion(children[i].LocalRot);
            var iChild = Matrix3x3.Multiply(Matrix3x3.Multiply(r, Matrix3x3.Diagonal(iDiag)), r.Transposed());

            // parallel axis theorem: I += m * (|d|^2 E - d dT)
            var d = children[i].LocalPos;
            float dd = d.LengthSquared();
            var shift = new Matrix3x3
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
            inertia = Matrix3x3.Add(inertia, Matrix3x3.Add(iChild, shift));
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
            InvInertiaLocal = Matrix3x3.Zero;
            InvInertiaWorld = Matrix3x3.Zero;
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
        var inertia = Matrix3x3.Zero;

        foreach (ref var c in Children.AsSpan())
        {
            var (m, iDiag) = c.MassProperties(Density);
            totalMass += m;

            var r = Matrix3x3.FromQuaternion(c.LocalRot);
            var iChild = Matrix3x3.Multiply(Matrix3x3.Multiply(r, Matrix3x3.Diagonal(iDiag)), r.Transposed());
            var d = c.LocalPos;
            float dd = d.LengthSquared();
            var shift = new Matrix3x3
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
            inertia = Matrix3x3.Add(inertia, Matrix3x3.Add(iChild, shift));
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
            MaterialId = MaterialId,
            Restitution = Restitution,
            Friction = Friction,
            Color = Color,
            UserObject = true,
            Breakable = Breakable,
            BreakThreshold = BreakThreshold,
            BreakPieces = BreakPieces,
            Flammability = Flammability,
            Conductivity = Conductivity,
            ExplosivePower = ExplosivePower,
            Wetness = Wetness,
            Temperature = Temperature,
            Sleeping = false,
        };

        clone.Proxies = new ShapeProxy[clone.Children.Length];

        if (IsStatic) 
            clone.SetStatic(true);
        else 
            clone.RecomputeMass(Density);

        return clone;
    }

    public void UpdateDerived()
    {
        if (!IsStatic)
        {
            Matrix3x3 r = Matrix3x3.FromQuaternion(Rotation);
            InvInertiaWorld = Matrix3x3.Multiply(Matrix3x3.Multiply(r, InvInertiaLocal), r.Transposed());
        }
        RefreshProxies();
    }

    public void RefreshProxies()
    {
        for (int i = 0; i < Children.Length; i++)
        {
            ref ChildShape c = ref Children[i];

            Proxies[i] = new ShapeProxy
            {
                Owner = this,
                Shape = c.Shape,
                Position = Position + Vector3.Transform(c.LocalPos, Rotation),
                Rotation = Rotation.Multiply(c.LocalRot),
                Radius = c.Radius,
                HalfExtents = c.HalfExtents,
                HalfHeight = c.HalfHeight,
                BoundingRadius = c.BoundingRadius,
            };
        }
    }

    public Vector3 VelocityAt(Vector3 worldPoint)
    {
        return Velocity + Vector3.Cross(AngularVelocity, worldPoint - Position);
    }

    public void ApplyImpulse(Vector3 impulse, Vector3 worldPoint)
    {
        if (IsStatic) return;
        Velocity += impulse * InvMass;
        AngularVelocity += InvInertiaWorld.Transform(Vector3.Cross(worldPoint - Position, impulse));
    }
}
