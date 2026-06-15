using System.Text.Json;

namespace MakarovPhysicsSandbox.Campaign;

/// <summary>
/// The player's earned stars per level, plus the unlock rule. A level is unlocked once the
/// previous one has been completed (at least one star). Persisted as a small JSON file in
/// the user's AppData; failures to read/write are swallowed because progress is non-critical.
/// </summary>
internal sealed class CampaignProgress
{
    // serialized member
    public Dictionary<string, int> StarsById { get; set; } = new();

    public int BestStars(string id) => StarsById.GetValueOrDefault(id, 0);
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
