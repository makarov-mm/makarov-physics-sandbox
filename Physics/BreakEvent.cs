using System.Numerics;
using MakarovPhysicsSandbox.Material;

namespace MakarovPhysicsSandbox.Physics;

internal readonly record struct BreakEvent(
    Vector3 Position,
    Vector3 Normal,
    float Speed,
    MaterialId MaterialId,
    Vector3 Color,
    int Pieces);
