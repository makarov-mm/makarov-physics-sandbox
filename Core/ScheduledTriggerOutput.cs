using System.Numerics;

namespace MakarovPhysicsSandbox.Core;

public sealed class ScheduledTriggerOutput
{
    public string SourceName = "Trigger";
    public TriggerOutput Output = new();
    public Vector3 FallbackTargetPosition;
    public float Remaining;
}
