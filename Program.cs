namespace MakarovPhysicsSandbox;

internal static class Program
{
    // The app runs on a native Win32 window with its own message loop (no Application.Run). STA is
    // kept because the GDI+ image codecs (WIC) prefer it; there are no WinForms dialogs any more.
    [System.STAThread]
    static void Main(string[] args)
    {
        // Must run before any window is created so the GL surface is crisp on high-DPI displays.
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        new MakarovPhysicsSandbox(LaunchOptions.Parse(args)).Run();

        GdiPlusImage.Shutdown();
    }
}
