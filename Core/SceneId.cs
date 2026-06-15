namespace MakarovPhysicsSandbox.Core;

public static class SceneId
{
    public static string New(string prefix)
    {
        return $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 1 + 12, prefix.Length + 33)];
    }
}
