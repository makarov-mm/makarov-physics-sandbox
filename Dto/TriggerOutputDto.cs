using MakarovPhysicsSandbox.Core;

namespace MakarovPhysicsSandbox.Dto;

public sealed class TriggerOutputDto
{
    public string TargetId { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public string Action { get; set; } = nameof(TriggerActionKind.Explosion);
    public float Delay { get; set; }
    public float Radius { get; set; } = 5.0f;
    public float Strength { get; set; } = 10.0f;
    public bool Enabled { get; set; } = true;

    public static TriggerOutputDto FromOutput(TriggerOutput output) => new()
    {
        TargetId = output.TargetId,
        TargetName = output.TargetName,
        Action = output.Action.ToString(),
        Delay = output.Delay,
        Radius = output.Radius,
        Strength = output.Strength,
        Enabled = output.Enabled,
    };

    public TriggerOutput ToOutput()
    {
        if (!Enum.TryParse<TriggerActionKind>(Action, out var action))
        {
            action = TriggerActionKind.Explosion;
        }

        return new TriggerOutput
        {
            TargetId = TargetId,
            TargetName = TargetName,
            Action = action,
            Delay = MathF.Max(0f, Delay),
            Radius = Radius <= 0 ? 5.0f : Radius,
            Strength = Strength <= 0 ? 10.0f : Strength,
            Enabled = Enabled,
        };
    }
}
