using System.Numerics;

namespace MakarovPhysicsSandbox.Physics;

public sealed class Contact
{
    public RigidBody A = null!, B = null!;
    public Vector3 Point;
    public Vector3 Normal; // from A to B
    public float Penetration;
    public Vector3 RA, RB;
    public Vector3 Tangent1, Tangent2;
    public float MassNormal, MassTangent1, MassTangent2;
    public float VelocityBias; // restitution only
    public float PositionBias; // Baumgarte term, solved against pseudo-velocities
    public float Pn, Pt1, Pt2; // accumulated impulses
    public float Pnb; // accumulated position-correction impulse
    public float ImpactSpeed; // closing speed at first touch, for spark effects
    public bool SkipA, SkipB; // treat that body as immovable for this contact (one-way debris)
}
