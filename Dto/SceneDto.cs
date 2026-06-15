using System.Numerics;

namespace MakarovPhysicsSandbox.Dto;

public sealed class SceneDto
{
    public int Version { get; set; }
    public DateTime SavedAtUtc { get; set; }
    public bool ZeroGravity { get; set; }
    public bool WaterOn { get; set; }
    public Vector3 Gravity { get; set; } = new(0, -9.81f, 0);
    public List<SceneBodyDto>? Bodies { get; set; }
    public List<SceneJointDto>? Joints { get; set; }
    public List<SceneForceFieldDto>? Fields { get; set; }
    public List<SceneWaterDto>? Waters { get; set; }
    public List<SceneTriggerDto>? Triggers { get; set; }
    public List<SceneMechanismDto>? Mechanisms { get; set; }
}
