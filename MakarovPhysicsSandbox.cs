using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;   // only for the Save/Load dialogs, MessageBox, and the MouseButtons/Keys enums

namespace MakarovPhysicsSandbox;

// Native Win32 host window. There is no WinForms Form: the window is created with raw Win32
// and the GL context lives directly on its HDC. WinForms is still referenced only for the
// Save/Load file dialogs, message boxes, and the MouseButtons/Keys/KeyEventArgs value types.
public partial class MakarovPhysicsSandbox
{
    private readonly LaunchOptions _launchOptions;
    private GlPanel _gl = null!;
    private IntPtr _hwnd;
    private Win32.WndProcDelegate _wndProc = null!;   // kept alive against the GC
    private bool _isFullscreen;
    private bool _running = true;
    private const string WindowClass = "WrecksmithWindow";

    public MakarovPhysicsSandbox() : this(LaunchOptions.Default) { }
    internal MakarovPhysicsSandbox(LaunchOptions launchOptions) { _launchOptions = launchOptions; }

    public void Run()
    {
        CreateHostWindow();

        _gl = new GlPanel();
        _gl.StateChanged += UpdateResultOverlay;
        _gl.HelpRequested += ShowHelp;
        _gl.SaveRequested += SaveSceneAs;
        _gl.LoadRequested += LoadSceneFromFile;
        _gl.MenuRequested += ShowPlayMenu;
        _gl.FullscreenRequested += ToggleFullscreen;

        Win32.GetClientRect(_hwnd, out var rc);
        _gl.Init(_hwnd, rc.Right - rc.Left, rc.Bottom - rc.Top);
        _isFullscreen = true;   // the window is created covering the screen

        BeginInitialLaunchMode();

        // Continuous render loop: drain pending messages, then draw one frame. vsync (set in the
        // GL panel) blocks SwapBuffers to the refresh rate, so this does not spin the CPU.
        var msg = default(Win32.MSG);
        while (_running)
        {
            while (Win32.PeekMessageW(out msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
            {
                if (msg.message == Win32.WM_QUIT) { _running = false; break; }
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessageW(ref msg);
            }
            if (!_running) break;
            _gl.RenderFrame();
        }
        _gl.Shutdown();
    }

    private void CreateHostWindow()
    {
        IntPtr hInst = Win32.GetModuleHandleW(null!);
        _wndProc = WndProc;
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = Win32.CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInst,
            hCursor = Win32.LoadCursorW(IntPtr.Zero, Win32.IDC_ARROW),
            lpszClassName = WindowClass,
        };
        Win32.RegisterClassExW(ref wc);

        int sw = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
        int sh = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
        _hwnd = Win32.CreateWindowExW(0, WindowClass, "Wrecksmith",
            Win32.WS_POPUP | Win32.WS_VISIBLE, 0, 0, sw, sh,
            IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
        Win32.SetFocus(_hwnd);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_SIZE:
                _gl?.Resize(LoWord(lParam), HiWord(lParam));
                return IntPtr.Zero;
            case Win32.WM_LBUTTONDOWN: Win32.SetCapture(hWnd); _gl?.HandleMouseDown(Mouse(MouseButtons.Left, lParam)); return IntPtr.Zero;
            case Win32.WM_RBUTTONDOWN: Win32.SetCapture(hWnd); _gl?.HandleMouseDown(Mouse(MouseButtons.Right, lParam)); return IntPtr.Zero;
            case Win32.WM_MBUTTONDOWN: _gl?.HandleMouseDown(Mouse(MouseButtons.Middle, lParam)); return IntPtr.Zero;
            case Win32.WM_LBUTTONUP: Win32.ReleaseCapture(); _gl?.HandleMouseUp(Mouse(MouseButtons.Left, lParam)); return IntPtr.Zero;
            case Win32.WM_RBUTTONUP: Win32.ReleaseCapture(); _gl?.HandleMouseUp(Mouse(MouseButtons.Right, lParam)); return IntPtr.Zero;
            case Win32.WM_MOUSEMOVE: _gl?.HandleMouseMove(Mouse(MouseButtons.None, lParam)); return IntPtr.Zero;
            case Win32.WM_MOUSEWHEEL: _gl?.HandleMouseWheel(Wheel(wParam, lParam)); return IntPtr.Zero;
            case Win32.WM_KEYDOWN: OnKey((int)(long)wParam); return IntPtr.Zero;
            case Win32.WM_CLOSE: Quit(); return IntPtr.Zero;
            case Win32.WM_DESTROY: Win32.PostQuitMessage(0); return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static int LoWord(IntPtr v) => (short)((long)v & 0xFFFF);
    private static int HiWord(IntPtr v) => (short)(((long)v >> 16) & 0xFFFF);
    private static MouseEventArgs Mouse(MouseButtons b, IntPtr lParam) => new(b, 1, LoWord(lParam), HiWord(lParam), 0);

    private MouseEventArgs Wheel(IntPtr wParam, IntPtr lParam)
    {
        int delta = (short)(((long)wParam >> 16) & 0xFFFF);
        var pt = new Win32.POINT { X = LoWord(lParam), Y = HiWord(lParam) };   // wheel coords are screen-relative
        Win32.ScreenToClient(_hwnd, ref pt);
        return new(MouseButtons.None, 0, pt.X, pt.Y, delta);
    }

    private void OnKey(int vk)
    {
        bool ctrl = (Win32.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
        if (vk == Win32.VK_ESCAPE)
        {
            if (_gl.PlayMenuOpen) { if (Confirm("Exit Wrecksmith?", "Exit")) Quit(); }
            else if (_gl.StartOpen) { _gl.HideOverlay(); _gl.Focus(); }
            else if (Confirm("Return to the main menu? The current sandbox will be reset.", "Return to menu")) ShowPlayMenu();
            else _gl.Focus();
            return;
        }
        if (ctrl && vk == Win32.VK_S) { SaveSceneAs(); return; }
        if (ctrl && vk == Win32.VK_O) { LoadSceneFromFile(); return; }
        if (vk == Win32.VK_F11) { ToggleFullscreen(); return; }
        if (vk == Win32.VK_F5) { ShowStartScreen(); return; }
        _gl.HandleKeyDown(new KeyEventArgs((Keys)vk));
    }

    private static bool Confirm(string text, string caption)
        => MessageBox.Show(text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private void Quit()
    {
        _running = false;
        Win32.DestroyWindow(_hwnd);
    }

    private void BeginInitialLaunchMode()
    {
        if (!string.IsNullOrWhiteSpace(_launchOptions.Preset))
            _gl.LoadPreset(_launchOptions.Preset);

        if (_launchOptions.ShowStartScreen) ShowStartScreen();
        else ShowPlayMenu();
    }

    // ---- GL overlay control ----
    private void ShowPlayMenu()
    {
        var buttons = new List<(string, bool, Action)>
        {
            ("Enter sandbox", true, () => EnterSandboxFromMenu(loadSandbox: true)),
            ("Choose a preset", false, () => EnterSandboxFromMenu(loadSandbox: false)),
            ("Exit", false, Quit),
        };
        _gl.ShowOverlay(OverlayKind.PlayMenu, "WRECKSMITH", "Build it. Wreck it. Repeat.", -1, buttons);
    }

    private void EnterSandboxFromMenu(bool loadSandbox)
    {
        _gl.HideOverlay();
        if (loadSandbox) _gl.Reset();
        _gl.Focus();
    }

    private void LoadStartPreset(string name)
    {
        _gl.LoadPreset(name);
        _gl.HideOverlay();
        _gl.Focus();
    }

    private void ShowStartScreen()
    {
        string subtitle = _isFullscreen
            ? "Play-mode shell - choose a preset, open the catalog, or continue the current scene"
            : "Physics sandbox prototype - dummies, vehicles, mechanisms, chain reactions";
        var buttons = new List<(string, bool, Action)>
        {
            ("Continue sandbox", true, () => { _gl.HideOverlay(); _gl.Focus(); }),
            ("Start vertical slice: Android Crash Test", false, () => { _gl.HideOverlay(); _gl.LoadVerticalSlice(); _gl.Focus(); }),
            ("Android Stress Chamber", false, () => LoadStartPreset("Android Stress Chamber")),
            ("Piston Crusher Lab", false, () => LoadStartPreset("Piston Crusher Lab")),
            ("Conveyor Chain Lab", false, () => LoadStartPreset("Conveyor Chain Lab")),
            ("Bridge Jump", false, () => LoadStartPreset("Bridge Jump")),
            ("Catapult Bridge Siege", false, () => LoadStartPreset("Catapult Bridge Siege")),
            ("Drone Target Range", false, () => LoadStartPreset("Drone Target Range")),
        };
        _gl.ShowOverlay(OverlayKind.Start, "WRECKSMITH", subtitle, -1, buttons);
    }

    private void UpdateResultOverlay()
    {
        if (!_gl.IsVerticalSliceFinished) return;
        var buttons = new List<(string, bool, Action)>
        {
            ("Retry test", true, () => { _gl.HideOverlay(); _gl.RetryVerticalSlice(); }),
            ("Back to title", false, () => { _gl.HideOverlay(); ShowStartScreen(); }),
            ("Continue sandbox", false, () => { _gl.HideOverlay(); _gl.DismissVerticalSliceResult(); _gl.Focus(); }),
        };
        _gl.ShowOverlay(OverlayKind.Result, _gl.VerticalSliceResultTitle, _gl.VerticalSliceResultDetail,
                        _gl.VerticalSliceStars, buttons);
    }

    // ---- file dialogs (still WinForms) ----
    private void SaveSceneAs()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save scene",
            Filter = "Wrecksmith scene (*.mpscene)|*.mpscene|JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "mpscene",
            AddExtension = true,
            FileName = "sandbox-scene.mpscene",
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try { _gl.SaveScene(dialog.FileName); _gl.Focus(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not save scene", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void LoadSceneFromFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Load scene",
            Filter = "Wrecksmith scene (*.mpscene)|*.mpscene|JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "mpscene",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try { _gl.LoadScene(dialog.FileName); _gl.Focus(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not load scene", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            Win32.SetWindowLongPtrW(_hwnd, Win32.GWL_STYLE, unchecked((IntPtr)(long)(Win32.WS_OVERLAPPEDWINDOW | Win32.WS_VISIBLE)));
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, 80, 60, 1180, 760, Win32.SWP_FRAMECHANGED | Win32.SWP_NOZORDER);
            _isFullscreen = false;
        }
        else
        {
            int sw = Win32.GetSystemMetrics(Win32.SM_CXSCREEN), sh = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
            Win32.SetWindowLongPtrW(_hwnd, Win32.GWL_STYLE, unchecked((IntPtr)(long)(Win32.WS_POPUP | Win32.WS_VISIBLE)));
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, sw, sh, Win32.SWP_FRAMECHANGED | Win32.SWP_NOZORDER);
            _isFullscreen = true;
        }
        _gl.Focus();
    }

    private void ShowHelp()
    {
        MessageBox.Show(
            "Mouse:\n" +
            "  Left mouse - select/grab an object, or use the active editor tool\n" +
            "  Right mouse + move - rotate the camera\n" +
            "  Mouse wheel - zoom; in Scale mode it scales the selected object\n" +
            "  Middle mouse - shoot a ball\n\n" +
            "Keyboard:\n" +
            "  1-8 objects, 9 bowling pins, L chain, 0 android\n" +
            "  Space/F shoot, E explosion, I ignite, D electrify\n" +
            "  Q select, M move, O rotate, S scale\n" +
            "  Z/X/U attractor/repeller/wind, V water, G gravity\n" +
            "  P pause, T slow motion, B single step\n" +
            "  Ctrl+S / Ctrl+O save/load, F11 fullscreen, F5 title, Esc menu\n" +
            "  C clear dynamic, R reset, H help",
            "Keyboard controls", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _gl.Focus();
    }
}
