namespace MakarovPhysicsSandbox
{
    internal sealed class LaunchOptions
    {
        public bool PlayMode { get; init; }
        public bool ShowStartScreen { get; init; }
        public string? Preset { get; init; }

        public static LaunchOptions Default { get; } = new();

        public static LaunchOptions Parse(string[] args)
        {
            bool play = false;
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
                ShowStartScreen = start ?? play,
                Preset = string.IsNullOrWhiteSpace(preset) ? null : preset,
            };
        }
    }

    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new MakarovPhysicsSandbox(LaunchOptions.Parse(args)));
        }
    }
}
