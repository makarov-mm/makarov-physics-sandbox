using System.Numerics;

namespace MakarovPhysicsSandbox.Core;

public sealed class SceneTrigger
{
    public string Id = SceneId.New("trigger");
    public string Name = "Trigger";
    public readonly List<TriggerOutput> Outputs = new();
    public Vector3 Position;
    public Vector3 HalfExtents = new(0.9f, 0.08f, 0.9f);
    public TriggerActionKind Action = TriggerActionKind.Explosion;
    public bool OneShot;
    public bool Enabled = true;
    public bool WasPressed;
    public float Cooldown;
    public float CooldownSeconds = 1.0f;
    public float Pulse;
    public float Radius = 5.0f;
    public float Strength = 10.0f;
    public Vector3 TargetOffset;

    public string DisplayName
    {
        get => string.IsNullOrWhiteSpace(Name) ? Id : Name;
        set => Name = value;
    }

    public Vector3 TargetPosition
    {
        get => Position + TargetOffset;
        set => TargetOffset = value - Position;
    }
}
