using System.Numerics;

namespace MakarovPhysicsSandbox.Core;

public sealed class SelectedTriggerProperties
{
    public string Name { get; init; } = "Trigger";
    public Vector3 Position { get; init; }
    public Vector3 HalfExtents { get; init; }
    public TriggerActionKind Action { get; init; }
    public bool OneShot { get; init; }
    public bool Enabled { get; init; }
    public float Radius { get; init; }
    public float Strength { get; init; }
    public float CooldownSeconds { get; init; }
    public Vector3 TargetPosition { get; init; }
}
