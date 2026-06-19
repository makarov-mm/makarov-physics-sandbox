namespace MakarovPhysicsSandbox;

// Lightweight input types that replace the WinForms MouseButtons / MouseEventArgs / KeyEventArgs
// value types. The native host (WndProc) builds these from raw Win32 messages and hands them to
// GlPanel, so no System.Windows.Forms types appear in the code any more.
public enum MouseBtn { None, Left, Right, Middle }

// Field names (Button/X/Y/Delta) match the old MouseEventArgs so the handler bodies are unchanged.
public readonly struct MouseInput
{
    public readonly MouseBtn Button;
    public readonly int X, Y, Delta;

    public MouseInput(MouseBtn button, int x, int y, int delta)
    {
        Button = button;
        X = x;
        Y = y;
        Delta = delta;
    }
}
