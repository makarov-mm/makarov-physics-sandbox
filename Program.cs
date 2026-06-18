namespace MakarovPhysicsSandbox;

internal static class Program
{
    // STA is kept so the WinForms Save/Load dialogs and message boxes work; the app itself
    // runs on a native Win32 window with its own message loop (no Application.Run).
    [System.STAThread]
    static void Main(string[] args)
    {
        new MakarovPhysicsSandbox(LaunchOptions.Parse(args)).Run();
    }
}
