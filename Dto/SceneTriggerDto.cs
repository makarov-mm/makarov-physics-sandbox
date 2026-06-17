using System.Numerics;
using MakarovPhysicsSandbox.Core;

namespace MakarovPhysicsSandbox.Dto;

public sealed class SceneTriggerDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = "Trigger";
    public string DisplayName { get; set; } = string.Empty;
    public Vector3 Position { get; set; }
    public Vector3 HalfExtents { get; set; } = new(0.9f, 0.08f, 0.9f);
    public string Action { get; set; } = nameof(TriggerActionKind.Explosion);
    public bool OneShot { get; set; }
    public bool Enabled { get; set; } = true;
    public float Radius { get; set; } = 5.0f;
    public float Strength { get; set; } = 10.0f;
    public float CooldownSeconds { get; set; } = 1.0f;
    public Vector3? TargetOffset { get; set; }
    public List<TriggerOutputDto>? Outputs { get; set; }

    public static SceneTriggerDto FromTrigger(SceneTrigger trigger) => new()
    {
        Id = trigger.Id,
        Name = trigger.Name,
        DisplayName = trigger.DisplayName,
        Position = trigger.Position,
        HalfExtents = trigger.HalfExtents,
        Action = trigger.Action.ToString(),
        OneShot = trigger.OneShot,
        Enabled = trigger.Enabled,
        Radius = trigger.Radius,
        Strength = trigger.Strength,
        CooldownSeconds = trigger.CooldownSeconds,
        TargetOffset = trigger.TargetOffset,
        Outputs = trigger.Outputs.Select(TriggerOutputDto.FromOutput).ToList(),
    };
}
