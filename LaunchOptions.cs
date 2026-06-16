namespace MakarovPhysicsSandbox;

public sealed class LaunchOptions
{
    public bool PlayMode { get; init; }
    public bool ShowStartScreen { get; init; }
    public string? Preset { get; init; }

    public static LaunchOptions Default { get; } = new();

    public static LaunchOptions Parse(string[] args)
    {
        bool play = true;   // default: boot straight into PlayMode (the shipped experience); pass --editor to get the editor
        bool? start = null;
        string? preset = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].Trim();
            if (arg.Equals("--play", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--fullscreen", StringComparison.OrdinalIgnoreCase))
            {
                play = true;
            }
            else if (arg.Equals("--editor", StringComparison.OrdinalIgnoreCase))
            {
                play = false;   // dev opt-out: launch into the editor instead of PlayMode
            }
            else if (arg.Equals("--start", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("--title", StringComparison.OrdinalIgnoreCase))
            {
                start = true;
            }
            else if (arg.Equals("--no-start", StringComparison.OrdinalIgnoreCase))
            {
                start = false;
            }
            else if (arg.Equals("--preset", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                preset = args[++i];
            }
            else if (arg.StartsWith("--preset=", StringComparison.OrdinalIgnoreCase))
            {
                preset = arg["--preset=".Length..].Trim('"');
            }
        }

        return new LaunchOptions
        {
            PlayMode = play,
            ShowStartScreen = start ?? false,   // the PlayMode menu is the start screen; editor overlay only on explicit --start
            Preset = string.IsNullOrWhiteSpace(preset) ? null : preset,
        };
    }
}
