namespace MakarovPhysicsSandbox.Core;

public sealed class SelectedTriggerOutputSnapshot
{
    public int Index { get; init; }
    public string TargetId { get; init; } = "";
    public string TargetName { get; init; } = "";
    public TriggerActionKind Action { get; init; }
    public float Delay { get; init; }
    public float Radius { get; init; }
    public float Strength { get; init; }
    public bool Enabled { get; init; }

    public override string ToString()
    {
        string target = string.IsNullOrWhiteSpace(TargetName) ? (string.IsNullOrWhiteSpace(TargetId) ? "legacy target" : TargetId) : TargetName;
        string delay = Delay > 0.01f ? $" +{Delay:0.##}s" : "";
        string state = Enabled ? "" : " [off]";
        return $"{Index + 1}. {Action} -> {target}{delay}{state}";
    }
}
