namespace MakarovPhysicsSandbox.Campaign;

public static class LevelCatalog
{
    // Ordered easy -> hard. Adding a level is just appending here + a matching scene
    // builder in Core.cs (LoadChallengeScene already switches on SceneName).
    public static readonly IReadOnlyList<LevelDef> Levels = new List<LevelDef>
    {
        new()
        {
            Id = "hit-target", Title = "Hit the Target", Kind = ChallengeKind.HitTarget,
            SceneName = "Hit the Target",
            Goal = "Get an object into the target ring.",
            StarHint = "★★★: do it in a single shot.",
            // base star for success, faster/cheaper attempts earn more
            StarRule = r => !r.Success ? 0 : r.Shots <= 1 ? 3 : r.Shots <= 3 ? 2 : 1,
        },
        new()
        {
            Id = "bowling", Title = "Bowling", Kind = ChallengeKind.Bowling,
            SceneName = "Bowling Challenge",
            Goal = "Knock down at least 8 pins.",
            StarHint = "★★★: clear all 10 in one throw.",
            StarRule = r => !r.Success ? 0
                          : (r.Score >= 10 && r.Shots <= 1) ? 3
                          : r.Score >= 10 ? 2 : 1,
        },
        new()
        {
            Id = "destroy-tower", Title = "Destroy the Tower", Kind = ChallengeKind.TowerDestroyer,
            SceneName = "Destroy the Tower",
            Goal = "Topple at least 70% of the tower.",
            StarHint = "★★★: level it completely in ≤2 shots.",
            StarRule = r => !r.Success ? 0
                          : (r.StartCount > 0 && r.Score >= r.StartCount && r.Shots <= 2) ? 3
                          : (r.StartCount > 0 && r.Score >= r.StartCount * 0.85f) ? 2 : 1,
        },
        new()
        {
            Id = "float-or-sink", Title = "Float or Sink", Kind = ChallengeKind.WaterSorting,
            SceneName = "Float or Sink",
            Goal = "Let light objects float and heavy ones sink.",
            StarHint = "★★★: every object ends up correctly sorted.",
            StarRule = r => !r.Success ? 0
                          : (r.StartCount > 0 && r.Score >= r.StartCount) ? 3 : 2,
        },
        new()
        {
            Id = "bridge-endurance", Title = "Bridge Endurance", Kind = ChallengeKind.BridgeEndurance,
            SceneName = "Bridge Endurance",
            Goal = "Keep the cargo up for 10 seconds.",
            StarHint = "Hold the full load to the end.",
            // pure pass/fail endurance - surviving the timer is full marks
            StarRule = r => r.Success ? 3 : 0,
        },
    };

    public static int Count => Levels.Count;
    public static LevelDef At(int index) => Levels[index];

    public static int IndexOf(string id)
    {
        for (int i = 0; i < Levels.Count; i++)
            if (Levels[i].Id == id) return i;
        return -1;
    }
}
