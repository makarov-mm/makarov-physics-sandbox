using MakarovPhysicsSandbox.Physics;
using System.Numerics;

namespace MakarovPhysicsSandbox.Dto;

public sealed class SceneChildShapeDto
{
    public string Shape { get; set; } = nameof(ShapeType.Box);
    public Vector3 LocalPos { get; set; }
    public Quaternion LocalRot { get; set; } = Quaternion.Identity;
    public float Radius { get; set; }
    public Vector3 HalfExtents { get; set; }
    public float HalfHeight { get; set; }

    public static SceneChildShapeDto FromChild(ChildShape c) => new()
    {
        Shape = c.Shape.ToString(),
        LocalPos = c.LocalPos,
        LocalRot = c.LocalRot,
        Radius = c.Radius,
        HalfExtents = c.HalfExtents,
        HalfHeight = c.HalfHeight,
    };

    public ChildShape ToChild()
    {
        if (!Enum.TryParse<ShapeType>(Shape, out var shape)) shape = ShapeType.Box;
        Quaternion q = LocalRot;
        if (q == default) q = Quaternion.Identity;

        return new ChildShape
        {
            Shape = shape,
            LocalPos = LocalPos,
            LocalRot = Quaternion.Normalize(q),
            Radius = Radius,
            HalfExtents = HalfExtents,
            HalfHeight = HalfHeight,
        };
    }
}
