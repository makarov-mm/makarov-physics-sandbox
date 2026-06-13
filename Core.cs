using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MakarovPhysicsSandbox;

internal enum ActiveForceFieldKind { None, Attractor, Repeller, Wind }

internal enum PendingSceneActionKind { None, SpawnBody, BowlingPins, Chain, Explosion, Attractor, Repeller }

/// <summary>
/// A WinForms control that owns an OpenGL context on its OWN window handle and runs the
/// whole physics sandbox inside it. The form just hosts this panel, wires its toolbar and
/// menu to the public action methods, and calls RenderFrame() from the idle loop. There is
/// no separate Win32 window and no blocking message pump anymore - that was the bug in the
/// first integration (the engine's old Run() opened a second window and froze the form).
/// </summary>
internal sealed class GlPanel : Control
{
    // ---- WGL extension delegates (resolved through a throwaway context) ----
    private delegate bool WglChoosePixelFormatARBDel(
        IntPtr hdc, int[] attribsI, float[]? attribsF, uint maxFormats, int[] formats, out uint numFormats);
    private delegate IntPtr WglCreateContextAttribsARBDel(IntPtr hdc, IntPtr shareContext, int[] attribs);
    private delegate bool WglSwapIntervalEXTDel(int interval);

    private static WglChoosePixelFormatARBDel? _wglChoosePixelFormatARB;
    private static WglCreateContextAttribsARBDel? _wglCreateContextAttribsARB;
    private static WglSwapIntervalEXTDel? _wglSwapIntervalEXT;

    // the dummy class's WndProc must stay alive while we set the context up
    private static Win32.WndProcDelegate? _dummyProc;
    private static int _dummyClassSeq;

    /// <summary>Raised about twice a second with a status line for the StatusStrip.</summary>
    public event Action<string>? StatusUpdated;
    public event Action? StateChanged;
    public event Action? HelpRequested;

    public bool IsPaused => _paused;
    public bool IsSlowMo => _slowMo;
    public bool IsZeroGravity => _zeroG;
    public bool IsWaterOn => _waterOn;
    public ActiveForceFieldKind ActiveForceField => _world.Fields.Count == 1
        ? _world.Fields[0].Type switch
        {
            ForceField.Kind.Attract => ActiveForceFieldKind.Attractor,
            ForceField.Kind.Repel => ActiveForceFieldKind.Repeller,
            ForceField.Kind.Wind => ActiveForceFieldKind.Wind,
            _ => ActiveForceFieldKind.None,
        }
        : ActiveForceFieldKind.None;

    public PendingSceneActionKind PendingSceneAction => _pendingSceneAction;
    public ActiveForceFieldKind PendingForceField => _pendingSceneAction switch
    {
        PendingSceneActionKind.Attractor => ActiveForceFieldKind.Attractor,
        PendingSceneActionKind.Repeller => ActiveForceFieldKind.Repeller,
        _ => ActiveForceFieldKind.None,
    };

    private IntPtr _hwnd, _hdc, _hglrc;
    private int _width = 1, _height = 1;
    private bool _initialized;

    // ---- rendering ----
    private uint _mainProgram, _depthProgram;
    private Mesh _cubeMesh = null!, _sphereMesh = null!, _capsuleMesh = null!, _planeMesh = null!;
    private uint _texFloor, _texCrate, _texStripes, _texMetal, _texConcrete;
    private uint _shadowFbo, _shadowTex;
    private const int ShadowSize = 2048;

    private int _uModel, _uView, _uProj, _uLightVP, _uColor, _uLightDir, _uCamPos, _uShadowMap, _uAlbedo, _uUvScale, _uAlpha, _uEmissive;
    private int _dModel, _dLightVP;

    // ---- camera (orbit) ----
    private float _camYaw = 0.6f, _camPitch = 0.42f, _camDist = 16f;
    private readonly Vector3 _camTarget = new(0, 2, 0);
    private Vector3 _camPos;
    private Matrix4x4 _view, _proj;

    // ---- input state ----
    private bool _rmbDown;
    private int _lastMouseX, _lastMouseY;
    private float _dragPlaneDist;

    // ---- sandbox state ----
    private bool _paused;
    private bool _slowMo;
    private bool _zeroG;
    private bool _stepOnce;
    private PendingSceneActionKind _pendingSceneAction;
    private int _pendingSpawnKind;
    private Vector3 _aimPoint;
    private bool _aimValid;
    private int _lastSpawnKind = 2;
    private const float MuzzleSpeed = 26f;
    private static readonly Vector3 DefaultGravity = new(0, -9.81f, 0);

    // ---- effects (sparks + trails) ----
    private struct Particle
    {
        public Vector3 Pos, Vel, Color;
        public float Life, MaxLife, Size;
        public bool Gravity;
    }
    private readonly List<Particle> _particles = new(512);
    private const int MaxParticles = 600;

    private bool _waterOn;

    // ---- physics ----
    private readonly PhysicsWorld _world = new();
    private readonly Random _rng = new(12345);
    private const int MaxBodies = 360;
    private const float ArenaHalf = 12f;
    private const float WallHeight = 2.8f;

    private static readonly Vector3[] Palette =
    {
        new(0.86f, 0.32f, 0.28f), new(0.30f, 0.62f, 0.88f), new(0.95f, 0.74f, 0.25f),
        new(0.42f, 0.78f, 0.45f), new(0.72f, 0.48f, 0.85f), new(0.92f, 0.55f, 0.35f),
        new(0.35f, 0.80f, 0.78f), new(0.88f, 0.45f, 0.65f),
    };

    // ---- frame timing ----
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private double _prev, _fpsTimer;
    private int _frames;

    public GlPanel()
    {
        // GL paints every pixel of this control; keep WinForms from drawing the background
        SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.Selectable, true);
        DoubleBuffered = false;
        TabStop = true;
        BackColor = Color.Black;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (DesignMode || _initialized) return;
        InitContext();
        GL.LoadFunctions();
        InitGraphics();
        ResetScene();
        _initialized = true;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_initialized) return;
        _width = Math.Max(1, ClientSize.Width);
        _height = Math.Max(1, ClientSize.Height);
        Win32.wglMakeCurrent(_hdc, _hglrc);
        GL.Viewport(0, 0, _width, _height);
        Invalidate();
    }

    // GL owns the surface, so do nothing here (prevents flicker)
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { if (_initialized) RenderFrame(); }

    protected override void Dispose(bool disposing)
    {
        if (_hglrc != IntPtr.Zero)
        {
            Win32.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            Win32.wglDeleteContext(_hglrc);
            _hglrc = IntPtr.Zero;
        }
        if (_hdc != IntPtr.Zero) { Win32.ReleaseDC(_hwnd, _hdc); _hdc = IntPtr.Zero; }
        base.Dispose(disposing);
    }

    // ================= public actions (toolbar / menu) =================
    // Toolbar/menu actions that need a position in the scene are armed first.
    // The actual placement happens on the next left click inside the GL panel.
    // Keyboard shortcuts still execute immediately at the current aim point, because
    // pressing a key does not force the cursor to leave the scene.
    public void Spawn(int kind) { if (_initialized) { ArmSceneAction(PendingSceneActionKind.SpawnBody, kind); Focus(); } }
    public void SpawnPins()     { if (_initialized) { ArmSceneAction(PendingSceneActionKind.BowlingPins); Focus(); } }
    public void SpawnChain()    { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Chain); Focus(); } }
    public void Shoot()         { if (_initialized) { CancelPendingSceneAction(); ShootBall(); Focus(); } }
    public void Detonate()      { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Explosion); Focus(); } }
    public void Attractor()     { if (_initialized) { ToggleOrArmField(ForceField.Kind.Attract, PendingSceneActionKind.Attractor); Focus(); } }
    public void Repeller()      { if (_initialized) { ToggleOrArmField(ForceField.Kind.Repel, PendingSceneActionKind.Repeller); Focus(); } }
    public void Wind()          { if (_initialized) { CancelPendingSceneAction(); AddField(ForceField.Kind.Wind); Focus(); } }
    public void Water()         { if (_initialized) { CancelPendingSceneAction(); ToggleWater(); Focus(); } }
    public void Gravity()       { if (_initialized) { CancelPendingSceneAction(); ToggleGravity(); Focus(); } }
    public void Clear()         { if (_initialized) { CancelPendingSceneAction(); ClearDynamic(); Focus(); } }
    public void Reset()         { if (_initialized) { CancelPendingSceneAction(); ResetScene(); NotifyStateChanged(); Focus(); } }
    public void TogglePause()   { _paused = !_paused; NotifyStateChanged(); Focus(); }
    public void ToggleSlowMo()  { _slowMo = !_slowMo; NotifyStateChanged(); Focus(); }
    public void StepOnce()      { _stepOnce = true; NotifyStateChanged(); Focus(); }

    // ================= per-frame update (driven by the form's idle loop) =================
    public void RenderFrame()
    {
        if (!_initialized) return;
        Win32.wglMakeCurrent(_hdc, _hglrc);

        double now = _sw.Elapsed.TotalSeconds;
        float dt = (float)(now - _prev);
        _prev = now;
        if (dt > 0.25f) dt = 0.25f; // a stall (window drag etc.) shouldn't blow up the sim

        UpdateCamera();
        UpdateAim();
        if (_world.Grabbed != null)
            _world.DragTarget = ComputeDragTarget();

        if (_stepOnce) { _world.Step(PhysicsWorld.FixedStep); _stepOnce = false; }
        else if (!_paused) _world.Step(_slowMo ? dt * 0.2f : dt);

        SpawnEffectsFromWorld();
        UpdateParticles(_paused ? 0f : (_slowMo ? dt * 0.2f : dt));

        RenderShadowPass();
        RenderMainPass();
        Win32.SwapBuffers(_hdc);

        _frames++;
        _fpsTimer += dt;
        if (_fpsTimer >= 0.5)
        {
            string flags = (_paused ? " [paused]" : "") + (_slowMo ? " [slow motion]" : "")
                         + (_zeroG ? " [0g]" : "") + (_waterOn ? " [water]" : "")
                         + (_world.Fields.Count > 0 ? " [field]" : "")
                         + (_pendingSceneAction != PendingSceneActionKind.None ? $" [click scene: {PendingSceneActionLabel()}]" : "");
            StatusUpdated?.Invoke($"{_frames / _fpsTimer:F0} FPS    bodies: {_world.Bodies.Count}    active: {_world.AwakeCount}{flags}");
            _frames = 0;
            _fpsTimer = 0;
        }
    }

    // ================= context creation =================
    private void InitContext()
    {
        IntPtr hInstance = Win32.GetModuleHandleW(null!);

        // 1) throwaway window + legacy context, only to fetch the WGL ARB entry points
        string dummyName = "GlDummyClass" + System.Threading.Interlocked.Increment(ref _dummyClassSeq);
        _dummyProc = (h, m, w, l) => Win32.DefWindowProcW(h, m, w, l);
        var dummyClass = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = Win32.CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_dummyProc),
            hInstance = hInstance,
            lpszClassName = dummyName,
        };
        Win32.RegisterClassExW(ref dummyClass);
        IntPtr dummyWnd = Win32.CreateWindowExW(0, dummyName, "dummy", 0,
            0, 0, 16, 16, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        IntPtr dummyDc = Win32.GetDC(dummyWnd);

        var pfd = new Win32.PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<Win32.PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = Win32.PFD_DRAW_TO_WINDOW | Win32.PFD_SUPPORT_OPENGL | Win32.PFD_DOUBLEBUFFER,
            iPixelType = Win32.PFD_TYPE_RGBA,
            cColorBits = 32,
            cDepthBits = 24,
            cStencilBits = 8,
            iLayerType = Win32.PFD_MAIN_PLANE,
        };
        int fmt = Win32.ChoosePixelFormat(dummyDc, ref pfd);
        Win32.SetPixelFormat(dummyDc, fmt, ref pfd);
        IntPtr dummyRc = Win32.wglCreateContext(dummyDc);
        Win32.wglMakeCurrent(dummyDc, dummyRc);

        _wglChoosePixelFormatARB ??= LoadWgl<WglChoosePixelFormatARBDel>("wglChoosePixelFormatARB");
        _wglCreateContextAttribsARB ??= LoadWgl<WglCreateContextAttribsARBDel>("wglCreateContextAttribsARB");
        _wglSwapIntervalEXT ??= LoadWgl<WglSwapIntervalEXTDel>("wglSwapIntervalEXT");

        Win32.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        Win32.wglDeleteContext(dummyRc);
        Win32.ReleaseDC(dummyWnd, dummyDc);
        Win32.DestroyWindow(dummyWnd);

        // 2) the real surface is THIS control's window
        _hwnd = Handle;
        _hdc = Win32.GetDC(_hwnd);

        // 3) pick an MSAA pixel format on our own DC
        int pixelFormat = 0;
        if (_wglChoosePixelFormatARB != null)
        {
            int[] attribs =
            {
                Win32.WGL_DRAW_TO_WINDOW_ARB, 1,
                Win32.WGL_SUPPORT_OPENGL_ARB, 1,
                Win32.WGL_DOUBLE_BUFFER_ARB,  1,
                Win32.WGL_PIXEL_TYPE_ARB,     Win32.WGL_TYPE_RGBA_ARB,
                Win32.WGL_COLOR_BITS_ARB,     32,
                Win32.WGL_DEPTH_BITS_ARB,     24,
                Win32.WGL_STENCIL_BITS_ARB,   8,
                Win32.WGL_SAMPLE_BUFFERS_ARB, 1,
                Win32.WGL_SAMPLES_ARB,        4,
                0
            };
            var formats = new int[1];
            if (_wglChoosePixelFormatARB(_hdc, attribs, null, 1, formats, out uint n) && n > 0)
                pixelFormat = formats[0];
        }
        if (pixelFormat == 0)
            pixelFormat = Win32.ChoosePixelFormat(_hdc, ref pfd);

        var realPfd = new Win32.PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<Win32.PIXELFORMATDESCRIPTOR>()
        };
        Win32.DescribePixelFormat(_hdc, pixelFormat, realPfd.nSize, ref realPfd);
        Win32.SetPixelFormat(_hdc, pixelFormat, ref realPfd);

        // 4) core-profile context, newest version that will hand one out
        if (_wglCreateContextAttribsARB != null)
        {
            foreach ((int major, int minor) in new[] { (4, 6), (4, 5), (4, 3), (4, 1), (3, 3) })
            {
                int[] ctxAttribs =
                {
                    Win32.WGL_CONTEXT_MAJOR_VERSION_ARB, major,
                    Win32.WGL_CONTEXT_MINOR_VERSION_ARB, minor,
                    Win32.WGL_CONTEXT_PROFILE_MASK_ARB,  Win32.WGL_CONTEXT_CORE_PROFILE_BIT_ARB,
                    0
                };
                _hglrc = _wglCreateContextAttribsARB(_hdc, IntPtr.Zero, ctxAttribs);
                if (_hglrc != IntPtr.Zero) break;
            }
        }
        if (_hglrc == IntPtr.Zero)
            _hglrc = Win32.wglCreateContext(_hdc); // last-resort legacy context

        Win32.wglMakeCurrent(_hdc, _hglrc);
        _wglSwapIntervalEXT?.Invoke(1); // vsync

        _width = Math.Max(1, ClientSize.Width);
        _height = Math.Max(1, ClientSize.Height);
    }

    private static T? LoadWgl<T>(string name) where T : Delegate
    {
        IntPtr p = Win32.wglGetProcAddress(name);
        long v = p.ToInt64();
        return v is 0 or 1 or 2 or 3 or -1 ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    // ================= input (WinForms events instead of a WndProc) =================
    // claim the keys we use so they reach OnKeyDown instead of moving focus around
    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus(); // so the panel starts receiving key presses
        _lastMouseX = e.X;
        _lastMouseY = e.Y;
        if (e.Button == MouseButtons.Left)
        {
            if (_pendingSceneAction != PendingSceneActionKind.None)
            {
                UpdateAim();
                if (!ExecutePendingSceneAction())
                    StatusUpdated?.Invoke($"Click a valid point on the floor or on an object to place {PendingSceneActionLabel()}. Press Esc to cancel.");
                return;
            }
            TryGrab(e.X, e.Y);
        }
        else if (e.Button == MouseButtons.Middle) ShootBall();
        else if (e.Button == MouseButtons.Right) _rmbDown = true;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) _world.Grabbed = null;
        else if (e.Button == MouseButtons.Right) _rmbDown = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_rmbDown)
        {
            _camYaw -= (e.X - _lastMouseX) * 0.008f;
            _camPitch += (e.Y - _lastMouseY) * 0.008f;
        }
        _lastMouseX = e.X;
        _lastMouseY = e.Y;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _camDist *= e.Delta > 0 ? 0.9f : 1.1f;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        HandleKey((int)e.KeyCode);
        e.Handled = true;
    }

    private void HandleKey(int vk)
    {
        switch (vk)
        {
            case 0x1B: CancelPendingSceneAction(); break;  // Esc
            case 0x31: CancelPendingSceneAction(); SpawnBody(1); break;              // 1 sphere
            case 0x32: CancelPendingSceneAction(); SpawnBody(2); break;              // 2 box
            case 0x33: CancelPendingSceneAction(); SpawnBody(3); break;              // 3 capsule
            case 0x34: CancelPendingSceneAction(); SpawnBody(4); break;              // 4 plank
            case 0x35: CancelPendingSceneAction(); SpawnBody(5); break;              // 5 pillar
            case 0x36: CancelPendingSceneAction(); SpawnBody(6); break;              // 6 dumbbell
            case 0x37: CancelPendingSceneAction(); SpawnBody(7); break;              // 7 hammer
            case 0x38: CancelPendingSceneAction(); SpawnBody(8); break;              // 8 table
            case 0x39: CancelPendingSceneAction(); SpawnBowlingPins(); break;        // 9 bowling pins
            case 0x4C: CancelPendingSceneAction(); DropChain(); break;               // L  chain
            case 0x56: CancelPendingSceneAction(); ToggleWater(); break;             // V  water
            case 0x5A: CancelPendingSceneAction(); AddField(ForceField.Kind.Attract); break; // Z
            case 0x58: CancelPendingSceneAction(); AddField(ForceField.Kind.Repel); break;   // X
            case 0x55: CancelPendingSceneAction(); AddField(ForceField.Kind.Wind); break;    // U
            case 0x20: CancelPendingSceneAction(); ShootBall(); break;               // Space
            case 0x46: CancelPendingSceneAction(); ShootBall(); break;               // F
            case 0x45: CancelPendingSceneAction(); Explode(); break;                 // E
            case 0x47: CancelPendingSceneAction(); ToggleGravity(); break;           // G
            case 0x50: TogglePause(); break;             // P
            case 0x54: ToggleSlowMo(); break;            // T
            case 0x42: StepOnce(); break;                // B
            case 0x43: CancelPendingSceneAction(); ClearDynamic(); break;            // C
            case 0x52: CancelPendingSceneAction(); Reset(); break;                   // R
            case 0x48: HelpRequested?.Invoke(); break;   // H
        }
    }

    private void InitGraphics()
    {
        GL.Enable(GL.DEPTH_TEST);
        GL.DepthFunc(GL.LESS);
        GL.Enable(GL.CULL_FACE);
        GL.CullFace(GL.BACK);
        GL.FrontFace(GL.CCW);
        GL.Enable(GL.MULTISAMPLE);

        _mainProgram = Shaders.Build(Shaders.MainVertex, Shaders.MainFragment);
        _depthProgram = Shaders.Build(Shaders.DepthVertex, Shaders.DepthFragment);

        _uModel = GL.GetUniformLocation(_mainProgram, "uModel");
        _uView = GL.GetUniformLocation(_mainProgram, "uView");
        _uProj = GL.GetUniformLocation(_mainProgram, "uProj");
        _uLightVP = GL.GetUniformLocation(_mainProgram, "uLightVP");
        _uColor = GL.GetUniformLocation(_mainProgram, "uColor");
        _uLightDir = GL.GetUniformLocation(_mainProgram, "uLightDir");
        _uCamPos = GL.GetUniformLocation(_mainProgram, "uCamPos");
        _uAlbedo = GL.GetUniformLocation(_mainProgram, "uAlbedo");
        _uUvScale = GL.GetUniformLocation(_mainProgram, "uUvScale");
        _uAlpha = GL.GetUniformLocation(_mainProgram, "uAlpha");
        _uEmissive = GL.GetUniformLocation(_mainProgram, "uEmissive");
        _uShadowMap = GL.GetUniformLocation(_mainProgram, "uShadowMap");

        _dModel = GL.GetUniformLocation(_depthProgram, "uModel");
        _dLightVP = GL.GetUniformLocation(_depthProgram, "uLightVP");

        _texFloor = Textures.CreateCheckerFloor();
        _texCrate = Textures.CreateCrate();
        _texStripes = Textures.CreateStripes();
        _texMetal = Textures.CreateMetal();
        _texConcrete = Textures.CreateConcrete();

        _cubeMesh = Mesh.CreateCube();
        _sphereMesh = Mesh.CreateSphere();
        _capsuleMesh = Mesh.CreateCapsule();
        _planeMesh = Mesh.CreatePlane();

        // ---- shadow map FBO ----
        var tex = new uint[1];
        GL.GenTextures(1, tex);
        _shadowTex = tex[0];
        GL.BindTexture(GL.TEXTURE_2D, _shadowTex);
        GL.TexImage2D(GL.TEXTURE_2D, 0, (int)GL.DEPTH_COMPONENT24, ShadowSize, ShadowSize, 0,
            GL.DEPTH_COMPONENT, GL.FLOAT, IntPtr.Zero);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MIN_FILTER, (int)GL.NEAREST);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_MAG_FILTER, (int)GL.NEAREST);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_S, (int)GL.CLAMP_TO_BORDER);
        GL.TexParameteri(GL.TEXTURE_2D, GL.TEXTURE_WRAP_T, (int)GL.CLAMP_TO_BORDER);
        GL.TexParameterfv(GL.TEXTURE_2D, GL.TEXTURE_BORDER_COLOR, new float[] { 1, 1, 1, 1 });

        _shadowFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(GL.FRAMEBUFFER, _shadowFbo);
        GL.FramebufferTexture2D(GL.FRAMEBUFFER, GL.DEPTH_ATTACHMENT, GL.TEXTURE_2D, _shadowTex, 0);
        GL.DrawBuffer(GL.NONE);
        GL.ReadBuffer(GL.NONE);
        if (GL.CheckFramebufferStatus(GL.FRAMEBUFFER) != GL.FRAMEBUFFER_COMPLETE)
            throw new InvalidOperationException("Shadow framebuffer is incomplete.");
        GL.BindFramebuffer(GL.FRAMEBUFFER, 0);
    }

    // ================= scene =================

    private void ResetScene()
    {
        _world.Bodies.Clear();
        _world.Joints.Clear();
        _world.Fields.Clear();
        _world.Waters.Clear();
        _particles.Clear();
        _world.Grabbed = null;
        _zeroG = false;
        _waterOn = false;
        _world.Gravity = DefaultGravity;
        AddWalls();

        // a small stack of boxes
        for (int i = 0; i < 4; i++)
        {
            var b = RigidBody.CreateBox(new Vector3(-2.5f, 0.55f + i * 1.12f, 0), new Vector3(0.55f));
            b.Color = Palette[i % Palette.Length];
            _world.Bodies.Add(b);
        }

        // a couple of bigger boxes
        var big = RigidBody.CreateBox(new Vector3(2.2f, 0.8f, -1.5f), new Vector3(0.8f, 0.8f, 0.8f));
        big.Color = Palette[4];
        _world.Bodies.Add(big);

        // spheres dropping from above
        for (int i = 0; i < 3; i++)
        {
            var s = RigidBody.CreateSphere(new Vector3(0.4f * i - 0.4f, 5f + 1.6f * i, 1.2f), 0.55f);
            s.Color = Palette[(i + 5) % Palette.Length];
            _world.Bodies.Add(s);
        }

        // compound props
        var dumbbell = MakeDumbbell(new Vector3(3.5f, 2.2f, 2.5f));
        dumbbell.Color = Palette[1];
        _world.Bodies.Add(dumbbell);

        var hammer = MakeHammer(new Vector3(-3.5f, 3f, 2.5f));
        hammer.Rotation = Quaternion.CreateFromYawPitchRoll(0.7f, 0.9f, 0f);
        hammer.UpdateDerived();
        hammer.Color = Palette[6 % Palette.Length];
        _world.Bodies.Add(hammer);

        var table = MakeTable(new Vector3(0f, 1.2f, -3f));
        table.Color = Palette[3];
        _world.Bodies.Add(table);

        // a hanging chain, so the joint system shows up right away
        {
            var pin = new Vector3(-5.5f, 4.0f, -4.5f);
            const float gap = 0.5f, r = 0.18f;
            RigidBody? prev = null;
            for (int i = 0; i < 6; i++)
            {
                var link = RigidBody.CreateSphere(pin - new Vector3(0, (i + 1) * gap, 0), r);
                link.Color = Palette[2];
                _world.Bodies.Add(link);
                if (prev == null)
                    _world.Joints.Add(new Joint
                    {
                        Type = Joint.Kind.Point,
                        A = link,
                        B = null,
                        LocalA = new Vector3(0, gap / 2, 0),
                        LocalB = pin
                    });
                else
                    _world.Joints.Add(new Joint
                    {
                        Type = Joint.Kind.Point,
                        A = link,
                        B = prev,
                        LocalA = new Vector3(0, gap / 2, 0),
                        LocalB = new Vector3(0, -gap / 2, 0)
                    });
                prev = link;
            }
        }
    }

    private void AddWalls()
    {
        const float t = 0.3f;                 // wall half-thickness
        float hh = WallHeight * 0.5f;
        float c = ArenaHalf + t;              // wall center offset
        float len = ArenaHalf + 2f * t;       // half-length, overlaps the corners

        var wallColor = new Vector3(0.32f, 0.34f, 0.38f);
        var halfX = new Vector3(t, hh, len);
        var halfZ = new Vector3(len, hh, t);

        foreach (var w in new[]
        {
            RigidBody.CreateStaticBox(new Vector3( c, hh, 0), halfX),
            RigidBody.CreateStaticBox(new Vector3(-c, hh, 0), halfX),
            RigidBody.CreateStaticBox(new Vector3(0, hh,  c), halfZ),
            RigidBody.CreateStaticBox(new Vector3(0, hh, -c), halfZ),
        })
        {
            w.Color = wallColor;
            _world.Bodies.Add(w);
        }
    }

    private void SpawnBody(int kind)
    {
        EvictIfFull();
        _lastSpawnKind = kind;

        float J() => (float)_rng.NextDouble(); // jitter helper

        // drop the new object from above wherever the user is aiming; without a valid
        // aim point (mouse off the arena) fall back to a small random spot near the middle
        float ax = _aimValid ? _aimPoint.X : (float)(_rng.NextDouble() * 4 - 2);
        float az = _aimValid ? _aimPoint.Z : (float)(_rng.NextDouble() * 4 - 2);
        float lim = ArenaHalf - 1f;
        ax = Math.Clamp(ax + (J() - 0.5f), -lim, lim);
        az = Math.Clamp(az + (J() - 0.5f), -lim, lim);
        var pos = new Vector3(ax, 7.5f, az);

        RigidBody body = kind switch
        {
            1 => RigidBody.CreateSphere(pos, 0.35f + J() * 0.4f),
            3 => RigidBody.CreateCapsule(pos, 0.28f + J() * 0.18f),
            4 => RigidBody.CreateBox(pos, new Vector3(           // plank
                     0.75f + J() * 0.35f, 0.10f + J() * 0.05f, 0.45f + J() * 0.15f)),
            5 => RigidBody.CreateBox(pos, new Vector3(           // pillar
                     0.20f + J() * 0.08f, 0.75f + J() * 0.35f, 0.20f + J() * 0.08f)),
            6 => MakeDumbbell(pos, 0.8f + J() * 0.5f),
            7 => MakeHammer(pos, 0.9f + J() * 0.4f),
            8 => MakeTable(pos, 0.9f + J() * 0.3f),
            _ => RigidBody.CreateBox(pos, new Vector3(
                     0.3f + J() * 0.45f, 0.3f + J() * 0.45f, 0.3f + J() * 0.45f)),
        };

        if (body.Children.Length > 1 || body.Children[0].Shape != ShapeType.Sphere)
        {
            body.Rotation = Quaternion.CreateFromYawPitchRoll(
                J() * MathF.PI, J() * 0.6f, J() * 0.6f);
            body.UpdateDerived();
        }
        body.Color = Palette[_rng.Next(Palette.Length)];
        body.Velocity = new Vector3(0, -1.5f, 0);
        _world.Bodies.Add(body);
    }

    // compound bodies: a couple of multi-shape props to show off CreateCompound

    private static RigidBody MakeDumbbell(Vector3 pos, float k = 1f) =>
        RigidBody.CreateCompound(pos, [
            ChildShape.Box(new Vector3(0.50f * k, 0.085f * k, 0.085f * k)),
            ChildShape.Sphere(0.27f * k, new Vector3(-0.50f * k, 0, 0)),
            ChildShape.Sphere(0.27f * k, new Vector3( 0.50f * k, 0, 0)),
        ], density: 1.4f);

    private static RigidBody MakeHammer(Vector3 pos, float k = 1f) =>
        RigidBody.CreateCompound(pos, [
            ChildShape.Box(new Vector3(0.55f * k, 0.07f * k, 0.07f * k)),
            // the heavy head shifts the COM well off the handle's middle -
            // watch how it tumbles compared to a plain box
            ChildShape.Box(new Vector3(0.13f * k, 0.18f * k, 0.30f * k), new Vector3(0.58f * k, 0, 0)),
        ], density: 3f);

    // a little table: tabletop plus four legs. Five boxes welded into one rigid body -
    // a good stress test for the compound inertia math, and it stacks/topples nicely.
    private static RigidBody MakeTable(Vector3 pos, float k = 1f)
    {
        float top = 0.6f * k, th = 0.06f * k, legH = 0.4f * k, legR = 0.06f * k;
        float inset = top - legR - 0.04f * k;
        float legY = -legH - th;
        return RigidBody.CreateCompound(pos, [
            ChildShape.Box(new Vector3(top, th, top)),
            ChildShape.Box(new Vector3(legR, legH, legR), new Vector3( inset, legY,  inset)),
            ChildShape.Box(new Vector3(legR, legH, legR), new Vector3( inset, legY, -inset)),
            ChildShape.Box(new Vector3(legR, legH, legR), new Vector3(-inset, legY,  inset)),
            ChildShape.Box(new Vector3(legR, legH, legR), new Vector3(-inset, legY, -inset)),
        ], density: 1.0f);
    }

    // ================= sandbox actions =================

    /// <summary>
    /// Where is the user pointing? Cast the mouse ray at the bodies first, and if it
    /// misses everything fall back to the floor plane. This point drives the aim marker,
    /// where new objects drop, and the center of explosions.
    /// </summary>
    private void UpdateAim()
    {
        var (origin, dir) = MouseRay(_lastMouseX, _lastMouseY);

        var hitBody = _world.RayCast(origin, dir, out float t, out var hit);
        if (hitBody != null)
        {
            _aimPoint = hit;
            _aimValid = true;
            return;
        }

        // intersect the ground plane y = 0
        if (MathF.Abs(dir.Y) > 1e-4f)
        {
            float tg = -origin.Y / dir.Y;
            if (tg > 0f)
            {
                var p = origin + dir * tg;
                if (MathF.Abs(p.X) < ArenaHalf + 4f && MathF.Abs(p.Z) < ArenaHalf + 4f)
                {
                    _aimPoint = p;
                    _aimValid = true;
                    return;
                }
            }
        }
        _aimValid = false;
    }

    /// <summary>Fire a ball from the camera toward wherever the mouse is aiming.</summary>
    private void ShootBall()
    {
        var (origin, dir) = MouseRay(_lastMouseX, _lastMouseY);
        EvictIfFull();

        // spawn a little ahead of the near plane so it never appears behind the camera
        float r = 0.3f;
        var ball = RigidBody.CreateSphere(origin + dir * 1.0f, r, density: 4f);
        ball.Restitution = 0.5f;
        ball.Velocity = dir * MuzzleSpeed;
        ball.Color = new Vector3(0.95f, 0.95f, 0.97f); // pale "steel" shot
        _world.Bodies.Add(ball);
    }

    private void Explode()
    {
        if (!_aimValid) return;
        _world.ApplyExplosion(_aimPoint, radius: 5f, strength: 9f);
    }

    private void ToggleGravity()
    {
        _zeroG = !_zeroG;
        _world.Gravity = _zeroG ? Vector3.Zero : DefaultGravity;
        foreach (var b in _world.Bodies) b.Wake(); // so they react to the change
        NotifyStateChanged();
    }

    private void ArmSceneAction(PendingSceneActionKind action, int spawnKind = 0)
    {
        if (_pendingSceneAction == action && _pendingSpawnKind == spawnKind)
        {
            CancelPendingSceneAction();
            return;
        }

        _pendingSceneAction = action;
        _pendingSpawnKind = spawnKind;
        StatusUpdated?.Invoke($"Click inside the scene to place {PendingSceneActionLabel()}. Press Esc to cancel.");
        NotifyStateChanged();
    }

    private void CancelPendingSceneAction()
    {
        if (_pendingSceneAction == PendingSceneActionKind.None) return;
        _pendingSceneAction = PendingSceneActionKind.None;
        _pendingSpawnKind = 0;
        NotifyStateChanged();
    }

    private void ToggleOrArmField(ForceField.Kind fieldKind, PendingSceneActionKind actionKind)
    {
        if (_pendingSceneAction == actionKind)
        {
            CancelPendingSceneAction();
            return;
        }

        if (_world.Fields.Count == 1 && _world.Fields[0].Type == fieldKind)
        {
            _world.Fields.Clear();
            foreach (var b in _world.Bodies) b.Wake();
            NotifyStateChanged();
            return;
        }

        ArmSceneAction(actionKind);
    }

    private bool ExecutePendingSceneAction()
    {
        if (_pendingSceneAction == PendingSceneActionKind.None) return false;

        if (!_aimValid) return false;

        var action = _pendingSceneAction;
        var spawnKind = _pendingSpawnKind;
        _pendingSceneAction = PendingSceneActionKind.None;
        _pendingSpawnKind = 0;

        switch (action)
        {
            case PendingSceneActionKind.SpawnBody:
                SpawnBody(spawnKind);
                break;
            case PendingSceneActionKind.BowlingPins:
                SpawnBowlingPins();
                break;
            case PendingSceneActionKind.Chain:
                DropChain();
                break;
            case PendingSceneActionKind.Explosion:
                Explode();
                break;
            case PendingSceneActionKind.Attractor:
                AddField(ForceField.Kind.Attract);
                break;
            case PendingSceneActionKind.Repeller:
                AddField(ForceField.Kind.Repel);
                break;
            default:
                return false;
        }

        NotifyStateChanged();
        return true;
    }

    private string PendingSceneActionLabel() => _pendingSceneAction switch
    {
        PendingSceneActionKind.SpawnBody => _pendingSpawnKind switch
        {
            1 => "sphere",
            2 => "box",
            3 => "capsule",
            4 => "plank",
            5 => "pillar",
            6 => "dumbbell",
            7 => "hammer",
            8 => "table",
            _ => "object",
        },
        PendingSceneActionKind.BowlingPins => "bowling pins",
        PendingSceneActionKind.Chain => "chain",
        PendingSceneActionKind.Explosion => "explosion",
        PendingSceneActionKind.Attractor => "attractor",
        PendingSceneActionKind.Repeller => "repeller",
        _ => "tool",
    };

    private void ClearDynamic()
    {
        _world.Grabbed = null;
        _world.Joints.Clear();
        _world.Bodies.RemoveAll(b => !b.IsStatic);
        _particles.Clear();
    }

    private void EvictIfFull()
    {
        if (_world.Bodies.Count < MaxBodies) return;
        var oldest = _world.Bodies.FirstOrDefault(b => !b.IsStatic && b != _world.Grabbed);
        if (oldest != null) _world.RemoveBody(oldest);
    }

    // ---- effects ----

    /// <summary>
    /// Turn this frame's physics events into particles: orange sparks at hard contacts,
    /// and faint trails behind anything moving fast. The trail dots inherit the body's
    /// color, so you get streaks that match what's flying around.
    /// </summary>
    private void SpawnEffectsFromWorld()
    {
        // sparks from impacts (the solver flagged contacts with a high closing speed)
        foreach (var (point, normal, speed) in _world.Impacts)
        {
            if (_particles.Count >= MaxParticles) break;
            int count = Math.Clamp((int)(speed * 0.8f), 2, 10);
            for (int i = 0; i < count; i++)
            {
                // fan out around the contact normal
                var dir = Vector3.Normalize(normal + RandomUnit() * 0.8f);
                float sp = speed * (0.3f + (float)_rng.NextDouble() * 0.7f);
                _particles.Add(new Particle
                {
                    Pos = point,
                    Vel = dir * sp,
                    Color = new Vector3(1.0f, 0.7f + (float)_rng.NextDouble() * 0.3f, 0.2f),
                    Life = 0.4f,
                    MaxLife = 0.4f,
                    Size = 0.05f,
                    Gravity = true,
                });
            }
        }

        // trails behind fast bodies
        foreach (var b in _world.Bodies)
        {
            if (b.IsStatic || b.Sleeping) continue;
            if (b.Velocity.LengthSquared() < 9f) continue;      // ~3 m/s threshold
            if (_particles.Count >= MaxParticles) break;
            _particles.Add(new Particle
            {
                Pos = b.Position,
                Vel = Vector3.Zero,
                Color = b.Color,
                Life = 0.35f,
                MaxLife = 0.35f,
                Size = 0.08f,
                Gravity = false,
            });
        }
    }

    private void UpdateParticles(float dt)
    {
        if (dt <= 0f) return;
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Life -= dt;
            if (p.Life <= 0f) { _particles.RemoveAt(i); continue; }
            if (p.Gravity) p.Vel += DefaultGravity * dt;
            p.Pos += p.Vel * dt;
            _particles[i] = p;
        }
    }

    private Vector3 RandomUnit()
    {
        // a roughly uniform direction; good enough for spark spray
        var v = new Vector3(
            (float)_rng.NextDouble() * 2 - 1,
            (float)_rng.NextDouble() * 2 - 1,
            (float)_rng.NextDouble() * 2 - 1);
        return v.LengthSquared() > 1e-4f ? Vector3.Normalize(v) : Vector3.UnitY;
    }

    // ---- scene props ----

    private void SpawnBowlingPins()
    {
        if (!_aimValid) return;
        // classic 10-pin triangle, pointing away from the camera
        var fwd = Vector3.Normalize(new Vector3(_aimPoint.X - _camPos.X, 0, _aimPoint.Z - _camPos.Z));
        var right = Vector3.Cross(Vector3.UnitY, fwd);
        const float gap = 0.55f;
        for (int row = 0; row < 4; row++)
            for (int k = 0; k <= row; k++)
            {
                EvictIfFull();
                var p = _aimPoint + fwd * (row * gap * 0.87f) + right * ((k - row * 0.5f) * gap);
                var pin = RigidBody.CreateCapsule(new Vector3(p.X, 0.42f, p.Z), 0.13f);
                pin.Color = new Vector3(0.95f, 0.95f, 0.95f);
                _world.Bodies.Add(pin);
            }
    }

    /// <summary>Hang a chain of small spheres from a point in the air above the aim spot.</summary>
    private void DropChain()
    {
        if (!_aimValid) return;
        var pin = new Vector3(_aimPoint.X, 6.5f, _aimPoint.Z);
        const float gap = 0.5f, r = 0.18f;
        RigidBody? prev = null;
        var color = Palette[_rng.Next(Palette.Length)];
        for (int i = 0; i < 7; i++)
        {
            EvictIfFull();
            var link = RigidBody.CreateSphere(pin - new Vector3(0, (i + 1) * gap, 0), r);
            link.Color = color;
            _world.Bodies.Add(link);
            // rigid distance links between body centers: a chain of fixed-length rods
            // that swings as one piece (Length = the center-to-center spacing)
            if (prev == null)
                _world.Joints.Add(new Joint
                {
                    Type = Joint.Kind.Distance,
                    A = link,
                    B = null,
                    LocalA = Vector3.Zero,
                    LocalB = pin,
                    Length = gap
                });
            else
                _world.Joints.Add(new Joint
                {
                    Type = Joint.Kind.Distance,
                    A = link,
                    B = prev,
                    LocalA = Vector3.Zero,
                    LocalB = Vector3.Zero,
                    Length = gap
                });
            prev = link;
        }
    }

    private void ToggleWater()
    {
        _waterOn = !_waterOn;
        _world.Waters.Clear();
        if (_waterOn)
            _world.Waters.Add(new WaterVolume
            {
                Center = Vector3.Zero,
                HalfX = ArenaHalf,
                HalfZ = ArenaHalf,
                SurfaceY = 1.6f,
                Density = 1.0f,
                LinearDrag = 2.5f,
            });
        foreach (var b in _world.Bodies) b.Wake();
        NotifyStateChanged();
    }

    private void AddField(ForceField.Kind kind)
    {
        // keep it simple: one field at a time, toggle off if the same kind is re-added.
        // The same-kind toggle must work even when the mouse is not over a valid aim point.
        bool sameExists = _world.Fields.Count == 1 && _world.Fields[0].Type == kind;
        if (sameExists)
        {
            _world.Fields.Clear();
            foreach (var b in _world.Bodies) b.Wake();
            NotifyStateChanged();
            return;
        }

        if (!_aimValid && kind != ForceField.Kind.Wind) return;

        _world.Fields.Clear();
        if (kind == ForceField.Kind.Wind)
        {
            var dir = Vector3.Normalize(new Vector3(_camTarget.X - _camPos.X, 0, _camTarget.Z - _camPos.Z));
            _world.Fields.Add(new ForceField { Type = kind, Position = _camTarget, Radius = 40f, Strength = 9f, WindDir = dir });
        }
        else
        {
            _world.Fields.Add(new ForceField { Type = kind, Position = _aimPoint + new Vector3(0, 1.5f, 0), Radius = 7f, Strength = 18f });
        }
        foreach (var b in _world.Bodies) b.Wake();
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        StateChanged?.Invoke();
    }

    // ================= camera & picking =================

    private void UpdateCamera()
    {
        _camPitch = Math.Clamp(_camPitch, 0.05f, 1.45f);
        _camDist = Math.Clamp(_camDist, 4f, 45f);

        float cp = MathF.Cos(_camPitch), sp = MathF.Sin(_camPitch);
        var offset = new Vector3(
            cp * MathF.Sin(_camYaw),
            sp,
            cp * MathF.Cos(_camYaw)) * _camDist;
        _camPos = _camTarget + offset;

        _view = Matrix4x4.CreateLookAt(_camPos, _camTarget, Vector3.UnitY);
        _proj = GlPerspective(MathF.PI / 4f, _width / (float)_height, 0.1f, 200f);
    }

    /// <summary>Mouse position → world-space ray.</summary>
    private (Vector3 origin, Vector3 dir) MouseRay(int mx, int my)
    {
        float ndcX = 2f * mx / _width - 1f;
        float ndcY = 1f - 2f * my / _height;

        Matrix4x4.Invert(_view * _proj, out var inv);
        var near4 = Vector4.Transform(new Vector4(ndcX, ndcY, -1f, 1f), inv);
        var far4 = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), inv);
        var near3 = new Vector3(near4.X, near4.Y, near4.Z) / near4.W;
        var far3 = new Vector3(far4.X, far4.Y, far4.Z) / far4.W;
        return (near3, Vector3.Normalize(far3 - near3));
    }

    private void TryGrab(int mx, int my)
    {
        var (origin, dir) = MouseRay(mx, my);
        var body = _world.RayCast(origin, dir, out float t, out var hitPoint);
        if (body == null || body.IsStatic) return;

        body.Wake();
        _world.Grabbed = body;
        var invRot = Quaternion.Conjugate(body.Rotation);
        _world.GrabLocalAnchor = Vector3.Transform(hitPoint - body.Position, invRot);
        _world.DragTarget = hitPoint;

        // drag plane: perpendicular to the view direction, through the grab point
        var camFwd = Vector3.Normalize(_camTarget - _camPos);
        _dragPlaneDist = Vector3.Dot(hitPoint - _camPos, camFwd);
    }

    private Vector3 ComputeDragTarget()
    {
        var (origin, dir) = MouseRay(_lastMouseX, _lastMouseY);
        var camFwd = Vector3.Normalize(_camTarget - _camPos);
        float denom = Vector3.Dot(dir, camFwd);
        if (MathF.Abs(denom) < 1e-5f) return _world.DragTarget;

        float t = _dragPlaneDist / denom;
        var p = origin + dir * t;
        float lim = ArenaHalf - 0.6f;
        p.X = Math.Clamp(p.X, -lim, lim);
        p.Z = Math.Clamp(p.Z, -lim, lim);
        p.Y = Math.Clamp(p.Y, 0.2f, 9f);    // above the floor, below "orbit"
        return p;
    }

    // ================= rendering =================

    private (Matrix4x4 lightVP, Vector3 lightDir) LightMatrices()
    {
        var lightDir = Vector3.Normalize(new Vector3(-0.45f, -1f, -0.3f));
        var lightPos = -lightDir * 30f;
        var lightView = Matrix4x4.CreateLookAt(lightPos, Vector3.Zero, Vector3.UnitY);
        var lightProj = GlOrtho(-24, 24, -24, 24, 1f, 60f);
        return (lightView * lightProj, lightDir);
    }

    private void RenderShadowPass()
    {
        var (lightVP, _) = LightMatrices();

        GL.BindFramebuffer(GL.FRAMEBUFFER, _shadowFbo);
        GL.Viewport(0, 0, ShadowSize, ShadowSize);
        GL.Clear(GL.DEPTH_BUFFER_BIT);
        GL.UseProgram(_depthProgram);
        GL.UniformMatrix4(_dLightVP, ToArray(lightVP));
        GL.CullFace(GL.FRONT); // reduces peter-panning

        foreach (var b in _world.Bodies)
            foreach (ref var child in b.Children.AsSpan())
            {
                GL.UniformMatrix4(_dModel, ToArray(ModelMatrix(b, in child)));
                MeshFor(child.Shape).Draw();
            }

        GL.CullFace(GL.BACK);
        GL.BindFramebuffer(GL.FRAMEBUFFER, 0);
    }

    private void RenderMainPass()
    {
        var (lightVP, lightDir) = LightMatrices();

        GL.Viewport(0, 0, _width, _height);
        GL.ClearColor(0.07f, 0.09f, 0.12f, 1f);
        GL.Clear(GL.COLOR_BUFFER_BIT | GL.DEPTH_BUFFER_BIT);

        GL.UseProgram(_mainProgram);
        GL.UniformMatrix4(_uView, ToArray(_view));
        GL.UniformMatrix4(_uProj, ToArray(_proj));
        GL.UniformMatrix4(_uLightVP, ToArray(lightVP));
        GL.Uniform3(_uLightDir, lightDir.X, lightDir.Y, lightDir.Z);
        GL.Uniform3(_uCamPos, _camPos.X, _camPos.Y, _camPos.Z);

        GL.ActiveTexture(GL.TEXTURE0);
        GL.BindTexture(GL.TEXTURE_2D, _shadowTex);
        GL.Uniform1(_uShadowMap, 0);

        GL.ActiveTexture(GL.TEXTURE1);
        GL.Uniform1(_uAlbedo, 1);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uEmissive, 0f);

        // floor
        GL.BindTexture(GL.TEXTURE_2D, _texFloor);
        GL.Uniform1(_uUvScale, 20f);
        GL.Uniform3(_uColor, 0.62f, 0.64f, 0.68f);
        GL.UniformMatrix4(_uModel, ToArray(Matrix4x4.CreateScale(40f, 1f, 40f)));
        _planeMesh.Draw();

        // bodies
        GL.Uniform1(_uUvScale, 1f);
        foreach (var b in _world.Bodies)
        {
            // sleeping bodies dim slightly so you can see the engine actually sleeps
            var color = b == _world.Grabbed ? b.Color * 1.35f
                      : b.Sleeping ? b.Color * 0.72f
                      : b.Color;
            GL.Uniform3(_uColor, color.X, color.Y, color.Z);

            foreach (ref var child in b.Children.AsSpan())
            {
                GL.BindTexture(GL.TEXTURE_2D, TextureFor(b, child.Shape));
                if (b.IsStatic) GL.Uniform1(_uUvScale, 6f);
                GL.UniformMatrix4(_uModel, ToArray(ModelMatrix(b, in child)));
                MeshFor(child.Shape).Draw();
                if (b.IsStatic) GL.Uniform1(_uUvScale, 1f);
            }
        }

        DrawJointRods();
        DrawFieldMarkers();
        DrawAimMarker();
        DrawParticles();
        DrawWater();
    }

    /// <summary>Thin rods drawn between joint anchors so chains and ropes read as connected.</summary>
    private void DrawJointRods()
    {
        if (_world.Joints.Count == 0) return;
        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform3(_uColor, 0.7f, 0.7f, 0.74f);

        foreach (var j in _world.Joints)
        {
            var a = j.WorldA;
            var b = j.WorldB;
            var mid = (a + b) * 0.5f;
            var d = b - a;
            float len = d.Length();
            if (len < 1e-4f) continue;

            // orient a unit cube's Y axis along the rod, then squash it thin
            var dir = d / len;
            var rot = RotationFromTo(Vector3.UnitY, dir);
            var m = Matrix4x4.CreateScale(0.03f, len * 0.5f, 0.03f)
                  * Matrix4x4.CreateFromQuaternion(rot)
                  * Matrix4x4.CreateTranslation(mid);
            GL.UniformMatrix4(_uModel, ToArray(m));
            _cubeMesh.Draw();
        }
    }

    private void DrawFieldMarkers()
    {
        foreach (var f in _world.Fields)
        {
            if (f.Type == ForceField.Kind.Wind) continue; // wind has no single point
            var c = f.Type == ForceField.Kind.Attract ? new Vector3(0.3f, 0.6f, 1.0f)
                                                       : new Vector3(1.0f, 0.4f, 0.3f);
            GL.BindTexture(GL.TEXTURE_2D, _texMetal);
            GL.Uniform1(_uEmissive, 1f);
            GL.Uniform3(_uColor, c.X, c.Y, c.Z);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(0.25f) * Matrix4x4.CreateTranslation(f.Position)));
            _sphereMesh.Draw();
            GL.Uniform1(_uEmissive, 0f);
        }
    }

    private void DrawParticles()
    {
        if (_particles.Count == 0) return;
        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 1f); // particles glow regardless of lighting

        foreach (var p in _particles)
        {
            float fade = p.Life / p.MaxLife;
            GL.Uniform3(_uColor, p.Color.X * fade, p.Color.Y * fade, p.Color.Z * fade);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(p.Size * (0.4f + fade)) * Matrix4x4.CreateTranslation(p.Pos)));
            _sphereMesh.Draw();
        }
        GL.Uniform1(_uEmissive, 0f);
    }

    /// <summary>A single translucent quad at the water surface. Drawn last, blended, no depth write.</summary>
    private void DrawWater()
    {
        if (_world.Waters.Count == 0) return;
        var w = _world.Waters[0];

        GL.Enable(GL.BLEND);
        GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
        GL.DepthMask(0); // don't write depth: things underwater stay visible

        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 8f);
        GL.Uniform1(_uEmissive, 0.5f);
        GL.Uniform1(_uAlpha, 0.45f);
        GL.Uniform3(_uColor, 0.25f, 0.5f, 0.75f);
        var m = Matrix4x4.CreateScale(w.HalfX, 1f, w.HalfZ)
              * Matrix4x4.CreateTranslation(w.Center.X, w.SurfaceY, w.Center.Z);
        GL.UniformMatrix4(_uModel, ToArray(m));
        _planeMesh.Draw();

        GL.DepthMask(1);
        GL.Disable(GL.BLEND);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uEmissive, 0f);
    }

    // builds a rotation that takes unit vector `from` onto unit vector `to`
    private static Quaternion RotationFromTo(Vector3 from, Vector3 to)
    {
        float d = Vector3.Dot(from, to);
        if (d > 0.9999f) return Quaternion.Identity;
        if (d < -0.9999f)
        {
            // opposite: rotate 180 around any axis perpendicular to `from`
            var axis = Vector3.Cross(Vector3.UnitX, from);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitZ, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }

    private uint TextureFor(RigidBody b, ShapeType shape)
        => b.IsStatic ? _texConcrete
         : shape switch
         {
             ShapeType.Sphere => _texStripes,
             ShapeType.Capsule => _texMetal,
             _ => _texCrate,
         };

    /// <summary>A flat ring-ish marker on the ground showing where the mouse is aiming.</summary>
    private void DrawAimMarker()
    {
        if (!_aimValid || _world.Grabbed != null) return;

        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform3(_uColor, 1.0f, 0.85f, 0.2f); // bright amber, hard to miss

        // a thin flattened sphere = a little disc sitting just above the floor
        var m = Matrix4x4.CreateScale(0.35f, 0.04f, 0.35f)
              * Matrix4x4.CreateTranslation(_aimPoint + new Vector3(0, 0.02f, 0));
        GL.UniformMatrix4(_uModel, ToArray(m));
        _sphereMesh.Draw();
    }

    private Mesh MeshFor(ShapeType shape) => shape switch
    {
        ShapeType.Sphere => _sphereMesh,
        ShapeType.Capsule => _capsuleMesh,
        _ => _cubeMesh,
    };

    private static Matrix4x4 ModelMatrix(RigidBody b, in ChildShape child)
    {
        var scale = child.Shape switch
        {
            ShapeType.Sphere => Matrix4x4.CreateScale(child.Radius),
            // capsule mesh is baked with radius 0.5 -> uniform scale keeps the caps spherical
            ShapeType.Capsule => Matrix4x4.CreateScale(child.Radius * 2f),
            _ => Matrix4x4.CreateScale(child.HalfExtents),
        };
        // child -> body -> world; with row vectors the leftmost transform applies first
        return scale
             * Matrix4x4.CreateFromQuaternion(child.LocalRot)
             * Matrix4x4.CreateTranslation(child.LocalPos)
             * Matrix4x4.CreateFromQuaternion(b.Rotation)
             * Matrix4x4.CreateTranslation(b.Position);
    }

    private static float[] ToArray(in Matrix4x4 m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    };

    /// <summary>Right-handed perspective with GL clip z in [-1, 1] (row-vector convention).</summary>
    private static Matrix4x4 GlPerspective(float fovY, float aspect, float zn, float zf)
    {
        float f = 1f / MathF.Tan(fovY * 0.5f);
        var m = new Matrix4x4
        {
            M11 = f / aspect,
            M22 = f,
            M33 = (zf + zn) / (zn - zf),
            M34 = -1f,
            M43 = 2f * zf * zn / (zn - zf),
        };
        return m;
    }

    /// <summary>Right-handed ortho with GL clip z in [-1, 1] (row-vector convention).</summary>
    private static Matrix4x4 GlOrtho(float l, float r, float b, float t, float n, float f)
    {
        return new Matrix4x4
        {
            M11 = 2f / (r - l),
            M22 = 2f / (t - b),
            M33 = -2f / (f - n),
            M41 = -(r + l) / (r - l),
            M42 = -(t + b) / (t - b),
            M43 = -(f + n) / (f - n),
            M44 = 1f,
        };
    }

}
