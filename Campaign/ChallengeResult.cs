namespace MakarovPhysicsSandbox.Campaign;

/// <summary>The measured outcome of one challenge attempt, fed to a level's star rule.</summary>
public readonly struct ChallengeResult
{
    public bool Success { get; init; }
    public float TimeSeconds { get; init; }
    public int Shots { get; init; }     // balls fired during the attempt
    public int Score { get; init; }     // challenge-specific (pins down, objects sorted, ...)
    public int StartCount { get; init; }// how many relevant objects the level started with
}
