using System.Numerics;

namespace MakarovPhysicsSandbox.Physics;

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
    private Matrix3x3 _kInv;           // effective-mass matrix for the Point constraint
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
            var k = Matrix3x3.Diagonal(new Vector3(InvMassSum()));
            k = Matrix3x3.Add(k, SkewInertiaTerm(A, _rA));
            if (B is { IsStatic: false }) k = Matrix3x3.Add(k, SkewInertiaTerm(B, _rB));
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

    private static Matrix3x3 SkewInertiaTerm(RigidBody b, Vector3 r)
    {
        // -skew(r) * Iinv * skew(r), built column by column
        var c0 = SkewMul(r, b.InvInertiaWorld.Transform(SkewMul(r, Vector3.UnitX)));
        var c1 = SkewMul(r, b.InvInertiaWorld.Transform(SkewMul(r, Vector3.UnitY)));
        var c2 = SkewMul(r, b.InvInertiaWorld.Transform(SkewMul(r, Vector3.UnitZ)));
        return new Matrix3x3
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
