using System.Numerics;
using MakarovPhysicsSandbox.Material;

namespace MakarovPhysicsSandbox.Core;

public sealed class SelectedBodyProperties
{
    public bool IsStatic { get; init; }
    public MaterialId MaterialId { get; init; }
    public float Density { get; init; }
    public float Friction { get; init; }
    public float Restitution { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Velocity { get; init; }
    public Vector3 Color { get; init; }
    public bool Breakable { get; init; }
    public float BreakThreshold { get; init; }
    public float Flammability { get; init; }
    public float Conductivity { get; init; }
    public float ExplosivePower { get; init; }
}
