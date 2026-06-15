using MakarovPhysicsSandbox.Physics;
using System.Numerics;

namespace MakarovPhysicsSandbox.Material;

public sealed record MaterialDefinition(
    MaterialId Id,
    string DisplayName,
    float Density,
    float Friction,
    float Restitution,
    Vector3 Color,
    bool Breakable,
    float BreakThreshold,
    int BreakPieces,
    float Flammability,
    float Conductivity,
    float ExplosivePower,
    bool Melts = false,
    bool Sticky = false)
{
    public void ApplyTo(RigidBody b, bool overwriteColor = true)
    {
        b.MaterialId = Id;
        b.Density = MathF.Max(0.001f, Density);
        b.Friction = Friction;
        b.Restitution = Restitution;
        if (overwriteColor) b.Color = Color;
        b.Breakable = Breakable;
        b.BreakThreshold = BreakThreshold;
        b.BreakPieces = BreakPieces;
        b.Flammability = Flammability;
        b.Conductivity = Conductivity;
        b.ExplosivePower = ExplosivePower;
        if (!b.IsStatic) b.RecomputeMass(b.Density);
        b.UpdateDerived();
        b.Wake();
    }
}
