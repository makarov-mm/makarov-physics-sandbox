using MakarovPhysicsSandbox.Physics;
using System.Numerics;

namespace MakarovPhysicsSandbox.Dto;

public sealed class SceneJointDto
{
    public string Type { get; set; } = nameof(Joint.Kind.Distance);
    public int A { get; set; }
    public int? B { get; set; }
    public Vector3 LocalA { get; set; }
    public Vector3 LocalB { get; set; }
    public float Length { get; set; }
    public float Stiffness { get; set; }
    public float Damping { get; set; }
}
