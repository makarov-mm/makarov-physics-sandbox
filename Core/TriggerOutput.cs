namespace MakarovPhysicsSandbox.Core;

public sealed class TriggerOutput
{
    public string TargetId = string.Empty;
    public string TargetName = string.Empty;
    public TriggerActionKind Action = TriggerActionKind.Explosion;
    public float Delay;
    public float Radius = 5.0f;
    public float Strength = 10.0f;
    public bool Enabled = true;
}
