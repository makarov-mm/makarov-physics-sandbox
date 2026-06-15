namespace MakarovPhysicsSandbox.Campaign;

// Lives here (not in the UI file) so the campaign framework can be unit-tested without
// pulling in WinForms. Core.cs and the challenge builders use the same enum.
public enum ChallengeKind
{ 
    None,
    HitTarget,
    TowerDestroyer,
    BridgeEndurance,
    Bowling,
    WaterSorting,
    AndroidCrashTest
}
