namespace MakarovPhysicsSandbox.Campaign;

/// <summary>
/// A campaign level: which challenge it runs, the scene that builds it, and how the
/// 0..3 star rating is computed from the attempt. The star rule is a delegate rather than
/// data because the catalog is code, not something we serialize - only the player's earned
/// stars get saved.
/// </summary>
public sealed class LevelDef
{
    public required string Id { get; init; }            // stable key used in the save file
    public required string Title { get; init; }
    public required string Goal { get; init; }
    public required ChallengeKind Kind { get; init; }
    public required string SceneName { get; init; }     // passed to LoadChallengeScene
    public required string StarHint { get; init; }      // shown in the UI: how to earn 3 stars
    public required Func<ChallengeResult, int> StarRule { get; init; }
}
