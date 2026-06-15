using System.Numerics;

namespace MakarovPhysicsSandbox.Physics;

/// <summary>One collision shape of a body, in the body's local frame. Plain bodies have exactly one.</summary>
public struct ChildShape
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


