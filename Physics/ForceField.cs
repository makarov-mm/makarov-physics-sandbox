using System.Numerics;

namespace MakarovPhysicsSandbox.Physics;

/// <summary>
/// A localized force region. Attract/Repel pull or push toward a point; Wind blows in a
/// fixed direction. All three are applied as accelerations (independent of mass, like
/// gravity) with a linear falloff to the edge of the radius.
/// </summary>
public sealed class ForceField
{
    public enum Kind { Attract, Repel, Wind }

    public Kind Type;
    public Vector3 Position;
    public float Radius = 6f;
    public float Strength = 20f;
    public Vector3 WindDir = Vector3.UnitX;

    public void Apply(RigidBody b, float h)
    {
        Vector3 d = Position - b.Position;
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
