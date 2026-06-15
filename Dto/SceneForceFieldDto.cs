using MakarovPhysicsSandbox.Physics;
using System.Numerics;

namespace MakarovPhysicsSandbox.Dto;

public sealed class SceneForceFieldDto
{
    public string Type { get; set; } = nameof(ForceField.Kind.Attract);
    public Vector3 Position { get; set; }
    public float Radius { get; set; }
    public float Strength { get; set; }
    public Vector3 WindDir { get; set; } = Vector3.UnitX;

    public static SceneForceFieldDto FromField(ForceField f) => new()
    {
        Type = f.Type.ToString(),
        Position = f.Position,
        Radius = f.Radius,
        Strength = f.Strength,
        WindDir = f.WindDir,
    };
}
