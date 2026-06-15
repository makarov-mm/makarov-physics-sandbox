using System.Numerics;

namespace MakarovPhysicsSandbox.Physics;

/// <summary>A child shape resolved to world space. The narrow phase works on these,
/// so it never has to care whether the shape belongs to a plain body or a compound.</summary>
public struct ShapeProxy
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
        Vector3 v = i switch
        {
            0 => Vector3.UnitX, 
            1 => Vector3.UnitY, 
            _ => Vector3.UnitZ
        };

        return Vector3.Transform(v, Rotation);
    }

    public readonly void CapsuleSegment(out Vector3 p0, out Vector3 p1)
    {
        Vector3 axis = Vector3.Transform(Vector3.UnitY, Rotation);
        p0 = Position - axis * HalfHeight;
        p1 = Position + axis * HalfHeight;
    }
}
