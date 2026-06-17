namespace MakarovPhysicsSandbox;

public sealed class PhysicObjectMenuItem(string text, string icon, string shortcut, Action callback)
{
    public string Text { get; init; } = text;
    public string Icon { get; init; } = icon;
    public string Shortcut { get; init; } = shortcut;
    public Action Callback { get; init; } = callback;
}
