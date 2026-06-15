using MakarovPhysicsSandbox.Physics;
using System.Numerics;
using MakarovPhysicsSandbox.Material;

namespace MakarovPhysicsSandbox.Dto;

public sealed class SceneBodyDto
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Velocity { get; set; }
    public Vector3 AngularVelocity { get; set; }
    public float Density { get; set; } = 1f;
    public string MaterialId { get; set; } = "Custom";
    public float Restitution { get; set; }
    public float Friction { get; set; }
    public bool IsStatic { get; set; }
    public Vector3 Color { get; set; } = new(0.8f);
    public bool Sleeping { get; set; }
    public bool Breakable { get; set; }
    public float BreakThreshold { get; set; } = 7.5f;
    public int BreakPieces { get; set; } = 8;
    public float Flammability { get; set; } = 0.7f;
    public float Conductivity { get; set; } = 0.05f;
    public float ExplosivePower { get; set; }
    public float Wetness { get; set; }
    public float Temperature { get; set; } = 20f;
    public List<SceneChildShapeDto> Children { get; set; } = [];

    public static SceneBodyDto FromBody(RigidBody b) => new()
    {
        Position = b.Position,
        Rotation = b.Rotation,
        Velocity = b.Velocity,
        AngularVelocity = b.AngularVelocity,
        Density = b.Density,
        MaterialId = b.MaterialId.ToString(),
        Restitution = b.Restitution,
        Friction = b.Friction,
        IsStatic = b.IsStatic,
        Color = b.Color,
        Sleeping = b.Sleeping,
        Breakable = b.Breakable,
        BreakThreshold = b.BreakThreshold,
        BreakPieces = b.BreakPieces,
        Flammability = b.Flammability,
        Conductivity = b.Conductivity,
        ExplosivePower = b.ExplosivePower,
        Wetness = b.Wetness,
        Temperature = b.Temperature,
        Children = b.Children.Select(SceneChildShapeDto.FromChild).ToList(),
    };

    public RigidBody ToBody()
    {
        var children = Children.Select(c => c.ToChild()).ToArray();
        if (children.Length == 0) children = [ChildShape.Box(new Vector3(0.5f))];

        var b = RigidBody.CreateCompound(Vector3.Zero, children, MathF.Max(Density, 0.001f));
        b.Position = Position;
        Quaternion q = Rotation;
        b.Rotation = Quaternion.Normalize(q == default ? Quaternion.Identity : q);
        b.Velocity = Velocity;
        b.AngularVelocity = AngularVelocity;
        b.Density = MathF.Max(Density, 0.001f);
        b.MaterialId = Materials.TryParse(MaterialId, out var materialId) ? materialId : Materials.GuessFromValues(b);
        b.Restitution = Restitution;
        b.Friction = Friction;
        if (IsStatic) b.SetStatic(true);
        b.UserObject = true;
        b.Breakable = Breakable;
        b.BreakThreshold = BreakThreshold <= 0 ? 7.5f : BreakThreshold;
        b.BreakPieces = BreakPieces <= 0 ? 8 : BreakPieces;
        b.Flammability = Flammability;
        b.Conductivity = Conductivity;
        b.ExplosivePower = ExplosivePower;
        b.Wetness = Wetness;
        b.Temperature = Temperature <= 0 ? 20f : Temperature;
        b.Color = Color;
        b.Sleeping = Sleeping;
        b.UpdateDerived();
        return b;
    }
}
