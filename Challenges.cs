using System.Text.Json;

namespace MakarovPhysicsSandbox;

// Lives here (not in the UI file) so the campaign framework can be unit-tested without
// pulling in WinForms. Core.cs and the challenge builders use the same enum.
internal enum ChallengeKind { None, HitTarget, TowerDestroyer, BridgeEndurance, Bowling, WaterSorting }

/// <summary>The measured outcome of one challenge attempt, fed to a level's star rule.</summary>
internal readonly struct ChallengeResult
{
    public bool Success { get; init; }
    public float TimeSeconds { get; init; }
    public int Shots { get; init; }     // balls fired during the attempt
    public int Score { get; init; }     // challenge-specific (pins down, objects sorted, ...)
    public int StartCount { get; init; }// how many relevant objects the level started with
}

/// <summary>
/// A campaign level: which challenge it runs, the scene that builds it, and how the
/// 0..3 star rating is computed from the attempt. The star rule is a delegate rather than
/// data because the catalog is code, not something we serialize - only the player's earned
/// stars get saved.
/// </summary>
internal sealed class LevelDef
{
    public required string Id { get; init; }            // stable key used in the save file
    public required string Title { get; init; }
    public required string Goal { get; init; }
    public required ChallengeKind Kind { get; init; }
    public required string SceneName { get; init; }     // passed to LoadChallengeScene
    public required string StarHint { get; init; }      // shown in the UI: how to earn 3 stars
    public required Func<ChallengeResult, int> StarRule { get; init; }
}

internal static class LevelCatalog
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

/// <summary>
/// The player's earned stars per level, plus the unlock rule. A level is unlocked once the
/// previous one has been completed (at least one star). Persisted as a small JSON file in
/// the user's AppData; failures to read/write are swallowed because progress is non-critical.
/// </summary>
internal sealed class CampaignProgress
{
    // serialized member
    public Dictionary<string, int> StarsById { get; set; } = new();

    public int BestStars(string id) => StarsById.TryGetValue(id, out int s) ? s : 0;
    public bool IsCompleted(string id) => BestStars(id) > 0;

    /// <summary>Stores the result if it beats the previous best. Returns true if it improved.</summary>
    public bool Record(string id, int stars)
    {
        if (stars <= 0) return false;
        if (stars <= BestStars(id)) return false;
        StarsById[id] = stars;
        return true;
    }

    public int TotalStars
    {
        get { int t = 0; foreach (var s in StarsById.Values) t += s; return t; }
    }

    public int CompletedCount
    {
        get { int n = 0; foreach (var s in StarsById.Values) if (s > 0) n++; return n; }
    }

    public bool IsUnlocked(int index)
    {
        if (index <= 0) return true;
        if (index >= LevelCatalog.Count) return false;
        return IsCompleted(LevelCatalog.At(index - 1).Id);
    }

    // ---- persistence ----

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string DefaultPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MakarovPhysicsSandbox");
            return Path.Combine(dir, "campaign-progress.json");
        }
    }

    public static CampaignProgress Load() => LoadFrom(DefaultPath);
    public void Save() => SaveTo(DefaultPath);

    // path-injectable variants keep this testable without touching the real AppData folder
    public static CampaignProgress LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return new CampaignProgress();
            var loaded = JsonSerializer.Deserialize<CampaignProgress>(File.ReadAllText(path));
            return loaded ?? new CampaignProgress();
        }
        catch
        {
            return new CampaignProgress(); // corrupt/unreadable -> start fresh, don't crash
        }
    }

    public void SaveTo(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            /* progress saving is best-effort */
        }
    }
}
