using System.Runtime.InteropServices;

namespace MakarovPhysicsSandbox;

/// <summary>Raw Win32 / GDI / WGL interop. No third-party libraries.</summary>
internal static class Win32
{
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public const uint CS_OWNDC = 0x0020;
    public const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    public const uint PFD_SUPPORT_OPENGL = 0x00000020;
    public const uint PFD_DOUBLEBUFFER = 0x00000001;
    public const byte PFD_TYPE_RGBA = 0;
    public const byte PFD_MAIN_PLANE = 0;
    public const int WGL_DRAW_TO_WINDOW_ARB = 0x2001;
    public const int WGL_SUPPORT_OPENGL_ARB = 0x2010;
    public const int WGL_DOUBLE_BUFFER_ARB = 0x2011;
    public const int WGL_PIXEL_TYPE_ARB = 0x2013;
    public const int WGL_TYPE_RGBA_ARB = 0x202B;
    public const int WGL_COLOR_BITS_ARB = 0x2014;
    public const int WGL_DEPTH_BITS_ARB = 0x2022;
    public const int WGL_STENCIL_BITS_ARB = 0x2023;
    public const int WGL_SAMPLE_BUFFERS_ARB = 0x2041;
    public const int WGL_SAMPLES_ARB = 0x2042;
    public const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    public const int WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    public const int WGL_CONTEXT_PROFILE_MASK_ARB = 0x9126;
    public const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB = 0x0001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift;
        public byte cAlphaBits, cAlphaShift;
        public byte cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte cDepthBits;
        public byte cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadLibraryW(string name);
    [DllImport("gdi32.dll")] public static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] public static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] public static extern int DescribePixelFormat(IntPtr hdc, int format, uint bytes, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] public static extern bool SwapBuffers(IntPtr hdc);
    [DllImport("opengl32.dll")] public static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] public static extern bool wglDeleteContext(IntPtr hglrc);
    [DllImport("opengl32.dll")] public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] public static extern IntPtr wglGetProcAddress(string name);
}
