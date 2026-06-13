using System.Diagnostics;
using System.Media;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakarovPhysicsSandbox;

internal enum ActiveForceFieldKind { None, Attractor, Repeller, Wind }

internal enum PendingSceneActionKind { None, SpawnBody, BowlingPins, Chain, Explosion, Attractor, Repeller, Wind, Connect, Spring, Disconnect }


internal enum TriggerActionKind { Explosion, Wind, ToggleGravity, ToggleAttractor, ToggleRepeller }

internal enum EditorToolMode { Select, Move, Rotate, Scale }

internal sealed class SelectedTriggerSnapshot
{
    public string Name { get; init; } = "Trigger";
    public Vector3 Position { get; init; }
    public Vector3 HalfExtents { get; init; }
    public TriggerActionKind Action { get; init; }
    public bool OneShot { get; init; }
    public bool Enabled { get; init; }
    public float Radius { get; init; }
    public float Strength { get; init; }
    public float CooldownSeconds { get; init; }
    public Vector3 TargetPosition { get; init; }
}

internal sealed class SelectedTriggerProperties
{
    public string Name { get; init; } = "Trigger";
    public Vector3 Position { get; init; }
    public Vector3 HalfExtents { get; init; }
    public TriggerActionKind Action { get; init; }
    public bool OneShot { get; init; }
    public bool Enabled { get; init; }
    public float Radius { get; init; }
    public float Strength { get; init; }
    public float CooldownSeconds { get; init; }
    public Vector3 TargetPosition { get; init; }
}

internal sealed class SceneTrigger
{
    public string Name = "Trigger";
    public Vector3 Position;
    public Vector3 HalfExtents = new(0.9f, 0.08f, 0.9f);
    public TriggerActionKind Action = TriggerActionKind.Explosion;
    public bool OneShot;
    public bool Enabled = true;
    public bool WasPressed;
    public float Cooldown;
    public float CooldownSeconds = 1.0f;
    public float Pulse;
    public float Radius = 5.0f;
    public float Strength = 10.0f;
    public Vector3 TargetOffset;

    public Vector3 TargetPosition
    {
        get => Position + TargetOffset;
        set => TargetOffset = value - Position;
    }
}

internal sealed class SelectedBodySnapshot
{
    public bool IsStatic { get; init; }
    public int ChildCount { get; init; }
    public float Mass { get; init; }
    public float Density { get; init; }
    public float Friction { get; init; }
    public float Restitution { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Velocity { get; init; }
    public Vector3 Color { get; init; }
    public bool Breakable { get; init; }
    public float BreakThreshold { get; init; }
}

internal sealed class SelectedBodyProperties
{
    public bool IsStatic { get; init; }
    public float Density { get; init; }
    public float Friction { get; init; }
    public float Restitution { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Velocity { get; init; }
    public Vector3 Color { get; init; }
    public bool Breakable { get; init; }
    public float BreakThreshold { get; init; }
}

/// <summary>
/// A WinForms control that owns an OpenGL context on its OWN window handle and runs the
/// whole physics sandbox inside it. The form just hosts this panel, wires its toolbar and
/// menu to the public action methods, and calls RenderFrame() from the idle loop. There is
/// no separate Win32 window and no blocking message pump anymore - that was the bug in the
/// first integration (the engine's old Run() opened a second window and froze the form).
/// </summary>
internal sealed partial class GlPanel : Control
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
    public event Action<SelectedBodySnapshot?>? SelectionChanged;
    public event Action<SelectedTriggerSnapshot?>? TriggerSelectionChanged;

    public bool IsPaused => _paused;
    public bool IsSlowMo => _slowMo;
    public bool IsZeroGravity => _zeroG;
    public bool IsWaterOn => _waterOn;
    public bool IsSoundOn => _soundEnabled;
    public EditorToolMode ActiveEditorTool => _editorTool;
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
        PendingSceneActionKind.Wind => ActiveForceFieldKind.Wind,
        _ => ActiveForceFieldKind.None,
    };

    private IntPtr _hwnd, _hdc, _hglrc;
    private int _width = 1, _height = 1;
    private bool _initialized;

    // ---- rendering ----
    private uint _mainProgram, _depthProgram;
    private Mesh _cubeMesh = null!, _sphereMesh = null!, _capsuleMesh = null!, _planeMesh = null!, _waterMesh = null!;
    private uint _texFloor, _texCrate, _texStripes, _texMetal, _texConcrete;
    private uint _shadowFbo, _shadowTex;
    private const int ShadowSize = 2048;

    private int _uModel, _uView, _uProj, _uLightVP, _uColor, _uLightDir, _uCamPos, _uShadowMap, _uAlbedo, _uUvScale, _uAlpha, _uEmissive, _uTime, _uWaterWaveAmp, _uRippleCount, _uRipples;
    private readonly float[] _rippleBuffer = new float[WaterVolume.MaxRipples * 4];
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
    private RigidBody? _jointFirstBody;
    private Vector3 _jointFirstLocal;
    private Vector3 _jointFirstWorld;
    private RigidBody? _selectedBody;
    private SceneTrigger? _selectedTrigger;
    private EditorToolMode _editorTool = EditorToolMode.Select;
    private bool _toolDragging;
    private int _toolDragStartX, _toolDragStartY;
    private Vector3 _toolStartPos;
    private Quaternion _toolStartRot;
    private float _toolLastScaleFactor = 1f;
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

    // ---- lightweight feedback sounds / water-entry tracking ----
    private bool _soundEnabled = false;
    private double _nextImpactSound, _nextSplashSound, _nextExplosionSound;
    private readonly Dictionary<RigidBody, bool> _waterTouchState = new();

    // ---- interactive triggers / pressure plates ----
    private readonly List<SceneTrigger> _triggers = new();

    private bool _waterOn;

    // ---- challenge mode ----
    private ChallengeKind _challengeKind = ChallengeKind.None;
    private string _challengeTitle = "";
    private string _challengeGoal = "";
    private string _challengeMessage = "";
    private float _challengeTimer;
    private bool _challengeSuccess;
    private bool _challengeFailed;
    private Vector3 _challengeTarget;
    private float _challengeTargetRadius;
    private int _challengeStartCount;
    private int _challengeScore;
    private readonly List<RigidBody> _challengeBodies = [];

    // ---- campaign (levels + stars + progress) ----
    private CampaignProgress _campaign = new();
    private int _levelIndex = -1;          // -1 = not in campaign (free challenge / sandbox)
    private int _shotsThisLevel;
    public event Action<int>? LevelCompleted; // fired with the level index when it is beaten

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
        _campaign = CampaignProgress.Load();
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
    public void Wind()          { if (_initialized) { ToggleOrArmField(ForceField.Kind.Wind, PendingSceneActionKind.Wind); Focus(); } }
    public void Connect()       { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Connect); Focus(); } }
    public void Spring()        { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Spring); Focus(); } }
    public void Disconnect()    { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Disconnect); Focus(); } }
    public void Water()         { if (_initialized) { CancelPendingSceneAction(); ToggleWater(); Focus(); } }
    public void Gravity()       { if (_initialized) { CancelPendingSceneAction(); ToggleGravity(); Focus(); } }
    public void ToggleSound()   { _soundEnabled = !_soundEnabled; NotifyStateChanged(); Focus(); }
    public void Clear()         { if (_initialized) { CancelPendingSceneAction(); ClearDynamic(); Focus(); } }
    public void Reset()         { if (_initialized) { CancelPendingSceneAction(); ResetScene(); NotifyStateChanged(); Focus(); } }
    public void TogglePause()   { _paused = !_paused; NotifyStateChanged(); Focus(); }
    public void ToggleSlowMo()  { _slowMo = !_slowMo; NotifyStateChanged(); Focus(); }
    public void StepOnce()      { _stepOnce = true; NotifyStateChanged(); Focus(); }
    public void LoadPreset(string presetName)
    {
        if (!_initialized) return;
        CancelPendingSceneAction();
        SelectBody(null);
        SelectTrigger(null);
        LoadPresetScene(presetName);
        NotifyStateChanged();
        Focus();
    }

    public void LoadChallenge(string challengeName)
    {
        if (!_initialized) return;
        CancelPendingSceneAction();
        SelectBody(null);
        SelectTrigger(null);
        _levelIndex = -1; // free challenge: not part of the campaign, no stars recorded
        LoadChallengeScene(challengeName);
        NotifyStateChanged();
        Focus();
    }

    public void ApplySelectedBodyProperties(SelectedBodyProperties props)
    {
        if (_selectedBody == null) return;
        var b = _selectedBody;
        b.Position = props.Position;
        b.Velocity = props.IsStatic ? Vector3.Zero : props.Velocity;
        b.Color = Vector3.Clamp(props.Color, Vector3.Zero, Vector3.One);
        b.Friction = Math.Clamp(props.Friction, 0f, 3f);
        b.Restitution = Math.Clamp(props.Restitution, 0f, 2f);
        b.Density = Math.Clamp(props.Density, 0.001f, 100f);
        b.Breakable = props.Breakable;
        b.BreakThreshold = Math.Clamp(props.BreakThreshold, 1f, 50f);
        b.SetStatic(props.IsStatic);
        if (!props.IsStatic) b.RecomputeMass(b.Density);
        b.UpdateDerived();
        b.Wake();
        NotifySelectionChanged();
        Focus();
    }

    public void ScaleSelectedBody(float factor)
    {
        if (_selectedBody == null) return;
        _selectedBody.ScaleUniform(factor);
        NotifySelectionChanged();
        Focus();
    }

    public void DeleteSelectedBody()
    {
        if (_selectedBody == null) return;
        var b = _selectedBody;
        _world.Joints.RemoveAll(j => j.Involves(b));
        _world.RemoveBody(b);
        SelectBody(null);
        NotifyStateChanged();
        Focus();
    }

    public void DuplicateSelectedBody()
    {
        if (_selectedBody == null) return;
        EvictIfFull();
        var copy = _selectedBody.Clone(new Vector3(0.8f, 0.4f, 0.8f));
        _world.Bodies.Add(copy);
        SelectBody(copy);
        NotifyStateChanged();
        Focus();
    }
    public void ApplySelectedTriggerProperties(SelectedTriggerProperties props)
    {
        if (_selectedTrigger == null) return;
        var tr = _selectedTrigger;
        tr.Name = string.IsNullOrWhiteSpace(props.Name) ? "Trigger" : props.Name.Trim();
        tr.Position = props.Position;
        tr.HalfExtents = new Vector3(
            Math.Clamp(props.HalfExtents.X, 0.15f, 8f),
            Math.Clamp(props.HalfExtents.Y, 0.02f, 2f),
            Math.Clamp(props.HalfExtents.Z, 0.15f, 8f));
        tr.Action = props.Action;
        tr.OneShot = props.OneShot;
        tr.Enabled = props.Enabled;
        tr.Radius = Math.Clamp(props.Radius, 0.5f, 40f);
        tr.Strength = Math.Clamp(props.Strength, 0.1f, 80f);
        tr.CooldownSeconds = Math.Clamp(props.CooldownSeconds, 0.05f, 20f);
        tr.TargetPosition = props.TargetPosition;
        tr.WasPressed = false;
        tr.Pulse = 0.35f;
        NotifyTriggerSelectionChanged();
        NotifyStateChanged();
        Focus();
    }

    public void DeleteSelectedTrigger()
    {
        if (_selectedTrigger == null) return;
        _triggers.Remove(_selectedTrigger);
        SelectTrigger(null);
        NotifyStateChanged();
        Focus();
    }

    public void DuplicateSelectedTrigger()
    {
        if (_selectedTrigger == null) return;
        var t = _selectedTrigger;
        var copy = new SceneTrigger
        {
            Name = t.Name + " copy",
            Position = t.Position + new Vector3(1.4f, 0f, 1.4f),
            HalfExtents = t.HalfExtents,
            Action = t.Action,
            OneShot = t.OneShot,
            Enabled = t.Enabled,
            Radius = t.Radius,
            Strength = t.Strength,
            CooldownSeconds = t.CooldownSeconds,
            TargetOffset = t.TargetOffset,
        };
        _triggers.Add(copy);
        SelectTrigger(copy);
        NotifyStateChanged();
        Focus();
    }


    public void SetEditorTool(EditorToolMode tool)
    {
        if (!_initialized) return;
        CancelPendingSceneAction();
        _world.Grabbed = null;
        _toolDragging = false;
        _editorTool = tool;
        StatusUpdated?.Invoke(tool switch
        {
            EditorToolMode.Select => "Tool: Select — click an object to select or drag it.",
            EditorToolMode.Move => "Tool: Move — click/drag the selected object on the floor plane.",
            EditorToolMode.Rotate => "Tool: Rotate — click/drag horizontally to rotate around Y.",
            EditorToolMode.Scale => "Tool: Scale — drag up/down or use mouse wheel on the selected object.",
            _ => "Tool changed."
        });
        NotifyStateChanged();
        Focus();
    }


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

        float simDt = _paused ? 0f : (_slowMo ? dt * 0.2f : dt);
        UpdateTriggers(simDt);
        SpawnEffectsFromWorld();
        UpdateParticles(simDt);
        UpdateChallenge(simDt);

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
                         + (_triggers.Count > 0 ? $" [triggers: {_triggers.Count}]" : "")
                         + (_pendingSceneAction != PendingSceneActionKind.None ? $" [click scene: {PendingSceneActionLabel()}]" : "");
            var challenge = ChallengeStatusText();
            StatusUpdated?.Invoke($"{_frames / _fpsTimer:F0} FPS    bodies: {_world.Bodies.Count}    active: {_world.AwakeCount}{flags}{challenge}");
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
                {
                    bool bodyTool = _pendingSceneAction is PendingSceneActionKind.Connect or PendingSceneActionKind.Spring or PendingSceneActionKind.Disconnect;
                    StatusUpdated?.Invoke(bodyTool
                        ? PendingSceneActionInstruction()
                        : $"Click a valid point on the floor or on an object to place {PendingSceneActionLabel()}. Press Esc to cancel.");
                }
                return;
            }
            if (_editorTool == EditorToolMode.Select)
                TryGrab(e.X, e.Y);
            else
                TryStartEditorToolDrag(e.X, e.Y);
        }
        else if (e.Button == MouseButtons.Middle) ShootBall();
        else if (e.Button == MouseButtons.Right) _rmbDown = true;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) { _world.Grabbed = null; _toolDragging = false; }
        else if (e.Button == MouseButtons.Right) _rmbDown = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_toolDragging)
            UpdateEditorToolDrag(e.X, e.Y);
        else if (_rmbDown)
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
        if (_editorTool == EditorToolMode.Scale && _selectedBody != null)
        {
            ScaleSelectedBody(e.Delta > 0 ? 1.08f : 0.92f);
            return;
        }
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
            case 0x4A: ArmSceneAction(PendingSceneActionKind.Connect); break;         // J connect two bodies
            case 0x4B: ArmSceneAction(PendingSceneActionKind.Spring); break;          // K spring-link two bodies
            case 0x2E: ArmSceneAction(PendingSceneActionKind.Disconnect); break;      // Delete disconnect body
            case 0x20: CancelPendingSceneAction(); ShootBall(); break;               // Space
            case 0x46: CancelPendingSceneAction(); ShootBall(); break;               // F
            case 0x45: CancelPendingSceneAction(); Explode(); break;                 // E
            case 0x47: CancelPendingSceneAction(); ToggleGravity(); break;           // G
            case 0x50: TogglePause(); break;             // P
            case 0x54: ToggleSlowMo(); break;            // T
            case 0x42: StepOnce(); break;                // B
            case 0x43: CancelPendingSceneAction(); ClearDynamic(); break;            // C
            case 0x52: CancelPendingSceneAction(); Reset(); break;                   // R
            case 0x51: SetEditorTool(EditorToolMode.Select); break; // Q
            case 0x4D: SetEditorTool(EditorToolMode.Move); break;   // M
            case 0x4F: SetEditorTool(EditorToolMode.Rotate); break; // O
            case 0x53: SetEditorTool(EditorToolMode.Scale); break;  // S
            case 0x48: HelpRequested?.Invoke(); break;   // H
            case 0x4E: NextLevel(); break;               // N  next campaign level
            case 0x59: RetryLevel(); break;              // Y  retry campaign level
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
        _uTime = GL.GetUniformLocation(_mainProgram, "uTime");
        _uWaterWaveAmp = GL.GetUniformLocation(_mainProgram, "uWaterWaveAmp");
        _uRippleCount = GL.GetUniformLocation(_mainProgram, "uRippleCount");
        _uRipples = GL.GetUniformLocation(_mainProgram, "uRipples");
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
        _waterMesh = Mesh.CreateGridPlane(64);

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
        SelectBody(null);
        SelectTrigger(null);
        _world.Bodies.Clear();
        _world.Joints.Clear();
        _world.Fields.Clear();
        _world.Waters.Clear();
        _particles.Clear();
        _triggers.Clear();
        _world.Grabbed = null;
        _zeroG = false;
        _waterOn = false;
        _world.Gravity = DefaultGravity;
        ClearChallenge();
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

        // two different towers to knock over right away
        AddBrickTower(new Vector3(6.5f, 0f, -3.5f), levels: 5, perRow: 3, bw: 0.42f);
        AddPyramidTower(new Vector3(-6.5f, 0f, 4.5f), levels: 4, bw: 0.5f);

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

    // --- tower builders (used by the default scene) ---
    private void AddBrickTower(Vector3 baseCenter, int levels, int perRow, float bw)
    {
        // rows of bricks with alternating offset, like a little wall - topples satisfyingly
        for (int level = 0; level < levels; level++)
        {
            float offset = (level % 2 == 0) ? 0f : bw;
            for (int i = 0; i < perRow; i++)
            {
                float x = baseCenter.X + (i - (perRow - 1) / 2f) * (bw * 2f) + offset;
                float y = baseCenter.Y + bw + level * (bw * 2f);
                var b = RigidBody.CreateBox(new Vector3(x, y, baseCenter.Z), new Vector3(bw, bw, bw * 1.5f));
                b.Color = Palette[(level + i) % Palette.Length];
                _world.Bodies.Add(b);
            }
        }
    }

    private void AddPyramidTower(Vector3 baseCenter, int levels, float bw)
    {
        // classic tapering stack: wide base, single block on top
        for (int level = 0; level < levels; level++)
        {
            int n = levels - level;
            for (int i = 0; i < n; i++)
            {
                float x = baseCenter.X + (i - (n - 1) / 2f) * (bw * 2.05f);
                float y = baseCenter.Y + bw + level * (bw * 2f);
                var b = RigidBody.CreateBox(new Vector3(x, y, baseCenter.Z), new Vector3(bw));
                b.Color = Palette[(level * 2 + i) % Palette.Length];
                _world.Bodies.Add(b);
            }
        }
    }

    private void ResetToEmptyScene(bool zeroGravity = false, bool water = false)
    {
        SelectBody(null);
        _world.Bodies.Clear();
        _world.Joints.Clear();
        _world.Fields.Clear();
        _world.Waters.Clear();
        _particles.Clear();
        _triggers.Clear();
        _world.Grabbed = null;
        _jointFirstBody = null;
        _zeroG = zeroGravity;
        _waterOn = false;
        _world.Gravity = zeroGravity ? Vector3.Zero : DefaultGravity;
        ClearChallenge();
        AddWalls();
        if (water) ToggleWater();
    }

    private void LoadPresetScene(string presetName)
    {
        switch (presetName)
        {
            case "Domino Run":
                BuildDominoRun();
                break;
            case "Tower Collapse":
                BuildTowerCollapse();
                break;
            case "Bridge Test":
                BuildBridgeTest();
                break;
            case "Catapult":
                BuildCatapult();
                break;
            case "Newton Cradle":
                BuildNewtonCradle();
                break;
            case "Zero-G Chaos":
                BuildZeroGChaos();
                break;
            case "Water Playground":
                BuildWaterPlayground();
                break;
            case "Trigger Playground":
                BuildTriggerPlayground();
                break;
            default:
                ResetScene();
                break;
        }
    }

    private void LoadChallengeScene(string challengeName)
    {
        switch (challengeName)
        {
            case "Hit the Target":
                BuildHitTargetChallenge();
                break;
            case "Destroy the Tower":
                BuildDestroyTowerChallenge();
                break;
            case "Bridge Endurance":
                BuildBridgeEnduranceChallenge();
                break;
            case "Bowling Challenge":
                BuildBowlingChallenge();
                break;
            case "Float or Sink":
                BuildWaterSortingChallenge();
                break;
            default:
                ClearChallenge();
                break;
        }
    }

    private void StartChallenge(ChallengeKind kind, string title, string goal, Vector3 target = default, float targetRadius = 0f)
    {
        _challengeKind = kind;
        _challengeTitle = title;
        _challengeGoal = goal;
        _challengeMessage = goal;
        _challengeTarget = target;
        _challengeTargetRadius = targetRadius;
        _challengeTimer = 0f;
        _challengeSuccess = false;
        _challengeFailed = false;
        _challengeScore = 0;
        _challengeStartCount = _challengeBodies.Count;
        StatusUpdated?.Invoke($"Challenge: {title} — {goal}");
    }

    private void ClearChallenge()
    {
        _challengeKind = ChallengeKind.None;
        _challengeTitle = "";
        _challengeGoal = "";
        _challengeMessage = "";
        _challengeTimer = 0f;
        _challengeSuccess = false;
        _challengeFailed = false;
        _challengeTarget = Vector3.Zero;
        _challengeTargetRadius = 0f;
        _challengeStartCount = 0;
        _challengeScore = 0;
        _challengeBodies.Clear();
        _levelIndex = -1;
        _shotsThisLevel = 0;
    }

    private void CompleteChallenge(string message)
    {
        if (_challengeSuccess || _challengeFailed) return;
        _challengeSuccess = true;
        _challengeMessage = "SUCCESS: " + message;
        StatusUpdated?.Invoke($"Challenge complete: {_challengeTitle} — {message}");

        // campaign scoring: rate the attempt, store the best result, unlock the next level
        if (_levelIndex >= 0 && _levelIndex < LevelCatalog.Count)
        {
            var def = LevelCatalog.At(_levelIndex);
            var result = new ChallengeResult
            {
                Success = true,
                TimeSeconds = _challengeTimer,
                Shots = _shotsThisLevel,
                Score = _challengeScore,
                StartCount = _challengeStartCount,
            };
            int stars = def.StarRule(result);
            if (_campaign.Record(def.Id, stars)) _campaign.Save();
            _challengeMessage = $"LEVEL COMPLETE  {StarString(stars)}  —  {message}";
            StatusUpdated?.Invoke($"Level complete: {def.Title}  {StarString(stars)}");
            LevelCompleted?.Invoke(_levelIndex);
            NotifyStateChanged();
        }
    }

    private static string StarString(int stars)
    {
        stars = Math.Clamp(stars, 0, 3);
        return new string('★', stars) + new string('☆', 3 - stars);
    }

    // ================= campaign API (level select / progression) =================
    public int CampaignLevelCount => LevelCatalog.Count;
    public int CurrentLevelIndex => _levelIndex;
    public int CampaignTotalStars => _campaign.TotalStars;
    public bool IsLevelUnlocked(int index) => _campaign.IsUnlocked(index);
    public int LevelStars(int index)
        => index >= 0 && index < LevelCatalog.Count ? _campaign.BestStars(LevelCatalog.At(index).Id) : 0;
    public string LevelTitle(int index)
        => index >= 0 && index < LevelCatalog.Count ? LevelCatalog.At(index).Title : "";
    public string LevelGoal(int index)
        => index >= 0 && index < LevelCatalog.Count ? LevelCatalog.At(index).Goal : "";
    public string LevelStarHint(int index)
        => index >= 0 && index < LevelCatalog.Count ? LevelCatalog.At(index).StarHint : "";

    public void StartCampaignLevel(int index)
    {
        if (!_initialized) return;
        if (index < 0 || index >= LevelCatalog.Count) return;
        if (!_campaign.IsUnlocked(index)) return;

        CancelPendingSceneAction();
        SelectBody(null);
        SelectTrigger(null);
        _levelIndex = index;        // set before building so CompleteChallenge can score it
        _shotsThisLevel = 0;
        LoadChallengeScene(LevelCatalog.At(index).SceneName);
        NotifyStateChanged();
        Focus();
    }

    public void NextLevel()
    {
        if (_levelIndex < 0) return;
        int next = _levelIndex + 1;
        if (next < LevelCatalog.Count && _campaign.IsUnlocked(next))
            StartCampaignLevel(next);
    }

    public void RetryLevel()
    {
        if (_levelIndex >= 0) StartCampaignLevel(_levelIndex);
    }

    private void FailChallenge(string message)
    {
        if (_challengeSuccess || _challengeFailed) return;
        _challengeFailed = true;
        _challengeMessage = "FAILED: " + message;
        StatusUpdated?.Invoke($"Challenge failed: {_challengeTitle} — {message}");
    }

    private string ChallengeStatusText()
    {
        if (_challengeKind == ChallengeKind.None) return "";
        string state = _challengeSuccess ? "success" : _challengeFailed ? "failed" : $"{_challengeTimer:0.0}s";
        return $"    challenge: {_challengeTitle} [{state}] {_challengeMessage}";
    }

    private void UpdateChallenge(float dt)
    {
        if (_challengeKind == ChallengeKind.None || _challengeSuccess || _challengeFailed) return;
        if (dt > 0f) _challengeTimer += dt;

        switch (_challengeKind)
        {
            case ChallengeKind.HitTarget:
                _challengeScore = _world.Bodies.Count(b => b.UserObject && !b.IsStatic && Vector3.Distance(b.Position, _challengeTarget) <= _challengeTargetRadius);
                _challengeMessage = $"Move any object into the target zone. Objects inside: {_challengeScore}.";
                if (_challengeScore > 0) CompleteChallenge("an object reached the target zone");
                break;

            case ChallengeKind.TowerDestroyer:
                int fallen = _challengeBodies.Count(b => b.Position.Y < 0.75f || MathF.Abs(b.Position.X) > 2.2f || MathF.Abs(b.Position.Z) > 2.2f);
                _challengeScore = fallen;
                _challengeMessage = $"Destroy at least 70% of the tower. Fallen/scattered: {fallen}/{_challengeStartCount}.";
                if (_challengeStartCount > 0 && fallen >= _challengeStartCount * 0.70f) CompleteChallenge("the tower is mostly destroyed");
                break;

            case ChallengeKind.BridgeEndurance:
                bool cargoDropped = _challengeBodies.Any(b => b.Position.Y < 0.75f);
                _challengeMessage = $"Keep the cargo on the bridge for 10 seconds. Time: {_challengeTimer:0.0}/10.0.";
                if (cargoDropped) FailChallenge("cargo fell below the bridge");
                else if (_challengeTimer >= 10f) CompleteChallenge("the bridge held the load for 10 seconds");
                break;

            case ChallengeKind.Bowling:
                int knocked = _challengeBodies.Count(b => b.Position.Y < 0.55f || Vector3.Dot(Vector3.Transform(Vector3.UnitY, b.Rotation), Vector3.UnitY) < 0.65f);
                _challengeScore = knocked;
                _challengeMessage = $"Knock down at least 8 pins. Knocked: {knocked}/{_challengeStartCount}.";
                if (knocked >= 8) CompleteChallenge("enough pins were knocked down");
                break;

            case ChallengeKind.WaterSorting:
                int floatersOk = _challengeBodies.Count(b => b.Density < 1f && b.Position.Y > 1.15f);
                int sinkersOk = _challengeBodies.Count(b => b.Density >= 1f && b.Position.Y < 1.0f);
                _challengeScore = floatersOk + sinkersOk;
                _challengeMessage = $"Wait until light objects float and heavy objects sink: {_challengeScore}/{_challengeStartCount}.";
                if (_challengeTimer > 8f && _challengeScore >= Math.Max(1, _challengeStartCount - 1)) CompleteChallenge("objects sorted themselves by density");
                break;
        }
    }

    private void BuildHitTargetChallenge()
    {
        ResetToEmptyScene();
        var target = new Vector3(7.0f, 1.0f, 0f);
        for (int i = 0; i < 5; i++)
        {
            var ball = RigidBody.CreateSphere(new Vector3(-5f + i * 0.6f, 1.0f + i * 0.25f, -1.5f + i * 0.7f), 0.35f, density: 1.2f);
            ball.Restitution = 0.55f;
            AddBody(ball, Palette[i % Palette.Length]);
        }
        var ramp = RigidBody.CreateStaticBox(new Vector3(0.5f, 0.28f, 0f), new Vector3(2.2f, 0.14f, 1.2f));
        ramp.Rotation = Quaternion.CreateFromYawPitchRoll(0f, 0f, -0.18f);
        ramp.UpdateDerived();
        ramp.Color = new Vector3(0.35f, 0.35f, 0.38f);
        _world.Bodies.Add(ramp);
        StartChallenge(ChallengeKind.HitTarget, "Hit the Target", "Move any object into the glowing target zone.", target, 1.15f);
    }

    private void BuildDestroyTowerChallenge()
    {
        ResetToEmptyScene();
        _challengeBodies.Clear();
        const int levels = 7;
        for (int y = 0; y < levels; y++)
        {
            for (int x = -2; x <= 2; x++)
            {
                for (int z = -2; z <= 2; z++)
                {
                    if (Math.Abs(x) == 2 || Math.Abs(z) == 2 || y % 2 == 0)
                    {
                        var b = MakeBreakable(RigidBody.CreateBox(new Vector3(x * 0.55f, 0.28f + y * 0.56f, z * 0.55f), new Vector3(0.25f), density: 1.0f), threshold: 5.8f);
                        b.Friction = 0.65f;
                        AddBody(b, Palette[Math.Abs(x + z + y + 20) % Palette.Length]);
                        _challengeBodies.Add(b);
                    }
                }
            }
        }
        var ball = RigidBody.CreateSphere(new Vector3(-8.0f, 2.6f, 0f), 0.75f, density: 7f);
        ball.Velocity = new Vector3(10.5f, 0.1f, 0f);
        AddBody(ball, new Vector3(0.15f, 0.15f, 0.18f));
        StartChallenge(ChallengeKind.TowerDestroyer, "Destroy the Tower", "Destroy at least 70% of the tower blocks.");
    }

    private void BuildBridgeEnduranceChallenge()
    {
        BuildBridgeTest();
        _challengeBodies.Clear();
        foreach (var b in _world.Bodies.Where(b => b.UserObject && !b.IsStatic && b.Position.Y > 2.0f))
            _challengeBodies.Add(b);
        StartChallenge(ChallengeKind.BridgeEndurance, "Bridge Endurance", "Keep the cargo above the bridge for 10 seconds.");
    }

    private void BuildBowlingChallenge()
    {
        ResetToEmptyScene();
        _challengeBodies.Clear();
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col <= row; col++)
            {
                float x = (col - row * 0.5f) * 0.55f;
                float z = 3.8f + row * 0.55f;
                var pin = RigidBody.CreateCapsule(new Vector3(x, 0.65f, z), 0.18f, density: 0.55f);
                pin.Restitution = 0.35f;
                AddBody(pin, new Vector3(0.95f, 0.92f, 0.85f));
                _challengeBodies.Add(pin);
            }
        }
        var ball = RigidBody.CreateSphere(new Vector3(0f, 0.55f, -5.0f), 0.45f, density: 6f);
        ball.Velocity = new Vector3(0f, 0f, 8.5f);
        ball.Restitution = 0.35f;
        AddBody(ball, new Vector3(0.12f, 0.12f, 0.14f));
        StartChallenge(ChallengeKind.Bowling, "Bowling Challenge", "Knock down at least 8 of 10 pins.");
    }

    private void BuildWaterSortingChallenge()
    {
        ResetToEmptyScene(water: true);
        _challengeBodies.Clear();
        for (int i = 0; i < 8; i++)
        {
            bool light = i % 2 == 0;
            var box = RigidBody.CreateBox(new Vector3(-3.5f + i, 3.2f + i * 0.2f, -0.6f), new Vector3(0.33f), density: light ? 0.35f : 1.75f);
            box.Friction = 0.35f;
            AddBody(box, light ? new Vector3(0.25f, 0.75f, 0.35f) : new Vector3(0.55f, 0.35f, 0.18f));
            _challengeBodies.Add(box);
        }
        StartChallenge(ChallengeKind.WaterSorting, "Float or Sink", "Light objects should float while heavy objects sink.");
    }

    private void AddBody(RigidBody body, Vector3 color)
    {
        body.Color = color;
        _world.Bodies.Add(body);
    }

    private static RigidBody MakeBreakable(RigidBody body, float threshold = 6.5f, int pieces = 8)
    {
        body.Breakable = true;
        body.BreakThreshold = threshold;
        body.BreakPieces = pieces;
        return body;
    }

    private static Joint MakePointJoint(RigidBody a, RigidBody? b, Vector3 localA, Vector3 localBOrWorld)
        => new() { Type = Joint.Kind.Point, A = a, B = b, LocalA = localA, LocalB = localBOrWorld };

    private static Joint MakeSpring(RigidBody a, RigidBody b, float stiffness = 22f, float damping = 2.4f)
    {
        float len = Vector3.Distance(a.Position, b.Position);
        return new Joint
        {
            Type = Joint.Kind.Spring,
            A = a,
            B = b,
            LocalA = Vector3.Zero,
            LocalB = Vector3.Zero,
            Length = len,
            Stiffness = stiffness,
            Damping = damping,
        };
    }

    private void BuildDominoRun()
    {
        ResetToEmptyScene();

        const int count = 34;
        for (int i = 0; i < count; i++)
        {
            float u = i / (float)(count - 1);
            float x = -8.5f + u * 17f;
            float z = MathF.Sin(u * MathF.PI * 2.0f) * 2.2f;
            float dx = 17f / (count - 1);
            float dz = MathF.Cos(u * MathF.PI * 2.0f) * 2.2f * MathF.PI * 2.0f / (count - 1);
            float yaw = MathF.Atan2(dx, dz) + MathF.PI * 0.5f;
            var d = RigidBody.CreateBox(new Vector3(x, 0.8f, z), new Vector3(0.075f, 0.8f, 0.32f), density: 0.75f);
            d.Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);
            d.Friction = 0.75f;
            d.Restitution = 0.1f;
            d.UpdateDerived();
            AddBody(d, Palette[i % Palette.Length]);
        }

        var striker = RigidBody.CreateSphere(new Vector3(-10.2f, 0.45f, 0f), 0.42f, density: 5f);
        striker.Velocity = new Vector3(8.0f, 0f, 0.8f);
        striker.Restitution = 0.25f;
        AddBody(striker, new Vector3(1.0f, 0.35f, 0.2f));
    }

    private void BuildTowerCollapse()
    {
        ResetToEmptyScene();

        const int levels = 8;
        for (int y = 0; y < levels; y++)
        {
            for (int x = -2; x <= 2; x++)
            {
                for (int z = -2; z <= 2; z++)
                {
                    if (Math.Abs(x) == 2 || Math.Abs(z) == 2 || y % 2 == 0)
                    {
                        var b = MakeBreakable(RigidBody.CreateBox(new Vector3(x * 0.55f, 0.28f + y * 0.56f, z * 0.55f), new Vector3(0.25f, 0.25f, 0.25f), density: 1.1f), threshold: 5.8f);
                        b.Friction = 0.65f;
                        b.Restitution = 0.08f;
                        AddBody(b, Palette[Math.Abs(x + z + y + 20) % Palette.Length]);
                    }
                }
            }
        }

        var wreckingBall = RigidBody.CreateSphere(new Vector3(-8f, 3.0f, 0f), 0.75f, density: 8f);
        wreckingBall.Velocity = new Vector3(12.5f, 0.1f, 0f);
        wreckingBall.Restitution = 0.2f;
        AddBody(wreckingBall, new Vector3(0.15f, 0.15f, 0.18f));
    }

    private void BuildBridgeTest()
    {
        ResetToEmptyScene();

        var left = RigidBody.CreateStaticBox(new Vector3(-4.8f, 0.45f, 0f), new Vector3(0.6f, 0.45f, 1.5f));
        left.Color = new Vector3(0.25f, 0.27f, 0.30f);
        _world.Bodies.Add(left);
        var right = RigidBody.CreateStaticBox(new Vector3(4.8f, 0.45f, 0f), new Vector3(0.6f, 0.45f, 1.5f));
        right.Color = new Vector3(0.25f, 0.27f, 0.30f);
        _world.Bodies.Add(right);

        var planks = new List<RigidBody>();
        const int n = 9;
        for (int i = 0; i < n; i++)
        {
            float x = -3.6f + i * 0.9f;
            var p = RigidBody.CreateBox(new Vector3(x, 1.15f, 0f), new Vector3(0.43f, 0.09f, 1.05f), density: 0.7f);
            p.Friction = 0.85f;
            p.Restitution = 0.05f;
            AddBody(p, new Vector3(0.55f, 0.34f, 0.17f));
            planks.Add(p);
        }

        // pin both ends to the abutments at both edges so the deck can't twist out
        const float zEdge = 0.9f;
        foreach (float z in new[] { -zEdge, zEdge })
        {
            _world.Joints.Add(MakePointJoint(planks[0],  null, new Vector3(-0.43f, 0f, z), new Vector3(-4.05f, 1.15f, z)));
            _world.Joints.Add(MakePointJoint(planks[^1], null, new Vector3( 0.43f, 0f, z), new Vector3( 4.05f, 1.15f, z)));
        }

        // rigid hinge links between adjacent planks, two per seam so the deck stays flat.
        // (Springs let it sag and pull apart under load - that's why it used to collapse.)
        for (int i = 0; i < planks.Count - 1; i++)
            foreach (float z in new[] { -zEdge, zEdge })
                _world.Joints.Add(MakePointJoint(planks[i], planks[i + 1], new Vector3(0.43f, 0f, z), new Vector3(-0.43f, 0f, z)));

        // cargo to carry - lighter than before so a real bridge can actually hold it
        for (int i = 0; i < 5; i++)
        {
            var load = RigidBody.CreateSphere(new Vector3(-2.0f + i * 1.0f, 3.0f + i * 0.25f, 0f), 0.35f, density: 2.4f);
            AddBody(load, new Vector3(0.25f, 0.45f, 0.85f));
        }
    }

    private void BuildCatapult()
    {
        ResetToEmptyScene();

        var baseBox = RigidBody.CreateStaticBox(new Vector3(-2.0f, 0.22f, 0f), new Vector3(1.3f, 0.22f, 0.75f));
        baseBox.Color = new Vector3(0.28f, 0.24f, 0.20f);
        _world.Bodies.Add(baseBox);

        var arm = RigidBody.CreateBox(new Vector3(-2.0f, 0.95f, 0f), new Vector3(2.2f, 0.10f, 0.22f), density: 1.0f);
        arm.Rotation = Quaternion.CreateFromYawPitchRoll(0f, 0f, -0.25f);
        arm.Friction = 0.7f;
        arm.UpdateDerived();
        AddBody(arm, new Vector3(0.55f, 0.34f, 0.18f));

        _world.Joints.Add(MakePointJoint(arm, null, new Vector3(-0.35f, 0f, 0f), new Vector3(-2.35f, 0.72f, 0f)));

        // counterweight: moderate mass and NO initial velocity. A density-8 weight with a
        // downward kick on a long single-pivot lever overwhelmed the solver and flung apart.
        var weight = RigidBody.CreateBox(new Vector3(-4.0f, 1.45f, 0f), new Vector3(0.45f), density: 2.5f);
        AddBody(weight, new Vector3(0.18f, 0.18f, 0.18f));
        _world.Joints.Add(MakePointJoint(weight, arm, Vector3.Zero, new Vector3(-1.8f, 0f, 0f)));

        var projectile = RigidBody.CreateSphere(new Vector3(-0.15f, 1.55f, 0f), 0.32f, density: 1.2f);
        projectile.Restitution = 0.3f;
        AddBody(projectile, new Vector3(1.0f, 0.82f, 0.22f));

        var target = RigidBody.CreateBox(new Vector3(5.5f, 0.7f, 0f), new Vector3(0.25f, 0.7f, 1.1f), density: 0.65f);
        AddBody(target, new Vector3(0.8f, 0.2f, 0.2f));
    }

    private void BuildNewtonCradle()
    {
        ResetToEmptyScene();

        var frame = RigidBody.CreateStaticBox(new Vector3(0f, 4.3f, 0f), new Vector3(3.3f, 0.12f, 0.12f));
        frame.Color = new Vector3(0.35f, 0.35f, 0.38f);
        _world.Bodies.Add(frame);

        const int n = 5;
        for (int i = 0; i < n; i++)
        {
            float x = (i - 2) * 0.62f;
            var ball = RigidBody.CreateSphere(new Vector3(x, 2.15f, 0f), 0.30f, density: 6f);
            ball.Restitution = 0.95f;
            ball.Friction = 0.15f;
            if (i == 0)
            {
                ball.Position += new Vector3(-1.25f, 0.55f, 0f);
                ball.Velocity = new Vector3(2.2f, 0f, 0f);
            }
            AddBody(ball, i == 0 ? new Vector3(1f, 0.55f, 0.25f) : new Vector3(0.75f, 0.80f, 0.86f));
            _world.Joints.Add(new Joint
            {
                Type = Joint.Kind.Distance,
                A = ball,
                B = null,
                LocalA = Vector3.Zero,
                LocalB = new Vector3(x, 4.05f, 0f),
                Length = Vector3.Distance(ball.Position, new Vector3(x, 4.05f, 0f)),
            });
        }
    }

    private void BuildZeroGChaos()
    {
        ResetToEmptyScene(zeroGravity: true);

        for (int i = 0; i < 32; i++)
        {
            float x = (float)(_rng.NextDouble() * 14 - 7);
            float y = (float)(_rng.NextDouble() * 5 + 1.2);
            float z = (float)(_rng.NextDouble() * 14 - 7);
            RigidBody b = i % 3 == 0
                ? RigidBody.CreateSphere(new Vector3(x, y, z), 0.25f + (float)_rng.NextDouble() * 0.35f)
                : RigidBody.CreateBox(new Vector3(x, y, z), new Vector3(0.25f + (float)_rng.NextDouble() * 0.35f));
            b.Velocity = new Vector3((float)_rng.NextDouble() * 5f - 2.5f, (float)_rng.NextDouble() * 5f - 2.5f, (float)_rng.NextDouble() * 5f - 2.5f);
            b.AngularVelocity = new Vector3((float)_rng.NextDouble() * 4f, (float)_rng.NextDouble() * 4f, (float)_rng.NextDouble() * 4f);
            b.Restitution = 0.75f;
            AddBody(b, Palette[i % Palette.Length]);
        }

        _world.Fields.Add(new ForceField { Type = ForceField.Kind.Attract, Position = new Vector3(0f, 2.6f, 0f), Radius = 8.0f, Strength = 8.5f });
    }

    private void BuildWaterPlayground()
    {
        ResetToEmptyScene(water: true);

        for (int i = 0; i < 10; i++)
        {
            float x = -4.5f + i;
            var floater = RigidBody.CreateBox(new Vector3(x, 4.0f + i * 0.2f, -1.2f), new Vector3(0.35f, 0.25f, 0.35f), density: i % 2 == 0 ? 0.35f : 1.8f);
            floater.Restitution = 0.25f;
            floater.Friction = 0.35f;
            AddBody(floater, i % 2 == 0 ? new Vector3(0.25f, 0.75f, 0.35f) : new Vector3(0.55f, 0.35f, 0.18f));
        }

        var ball = RigidBody.CreateSphere(new Vector3(0f, 6.0f, 2.3f), 0.7f, density: 0.55f);
        ball.Restitution = 0.6f;
        AddBody(ball, new Vector3(0.9f, 0.9f, 0.25f));
    }

    private void BuildTriggerPlayground()
    {
        ResetToEmptyScene();

        // One clear left-to-right lane along z = 0 so cause and effect are obvious:
        // a ball rolls down the ramp, the WIND plate scatters light crates out of its path,
        // then the EXPLOSION plate (aimed at the wall) demolishes the brick wall ahead.

        var ramp = RigidBody.CreateStaticBox(new Vector3(-6.0f, 1.3f, 0f), new Vector3(2.2f, 0.12f, 0.7f));
        ramp.Rotation = Quaternion.CreateFromYawPitchRoll(0f, 0f, -0.22f); // high on the left, rolls toward +X
        ramp.UpdateDerived();
        ramp.Color = new Vector3(0.42f, 0.42f, 0.48f);
        _world.Bodies.Add(ramp);

        // heavy balls that roll the length of the lane, one after another
        for (int i = 0; i < 3; i++)
        {
            var ball = RigidBody.CreateSphere(new Vector3(-7.3f, 2.9f + i * 0.7f, 0f), 0.3f, density: 2.5f);
            ball.Restitution = 0.25f;
            AddBody(ball, new Vector3(0.95f, 0.85f, 0.25f));
        }

        // light crates sitting in the lane, to be blown aside when the wind plate fires
        for (int i = 0; i < 5; i++)
        {
            var crate = RigidBody.CreateBox(new Vector3(-1.8f + (i % 2) * 0.5f, 0.3f + (i / 2) * 0.55f, 0f), new Vector3(0.26f), density: 0.3f);
            crate.Friction = 0.4f;
            AddBody(crate, new Vector3(0.3f, 0.7f, 0.45f));
        }

        // brick wall facing the lane, to be demolished by the explosion plate
        for (int y = 0; y < 4; y++)
        for (int z = -2; z <= 2; z++)
        {
            var block = MakeBreakable(RigidBody.CreateBox(new Vector3(2.4f, 0.25f + y * 0.5f, z * 0.5f), new Vector3(0.22f, 0.24f, 0.22f), density: 1.0f), threshold: 5.0f);
            AddBody(block, new Vector3(0.82f, 0.35f, 0.25f));
        }

        _triggers.Add(new SceneTrigger
        {
            Name = "Wind plate",
            Position = new Vector3(-3.5f, 0.08f, 0f),
            HalfExtents = new Vector3(0.8f, 0.06f, 0.9f),
            Action = TriggerActionKind.Wind,
            Strength = 14f,
        });
        _triggers.Add(new SceneTrigger
        {
            Name = "Explosion plate",
            Position = new Vector3(0.4f, 0.08f, 0f),
            HalfExtents = new Vector3(0.8f, 0.06f, 0.9f),
            Action = TriggerActionKind.Explosion,
            TargetPosition = new Vector3(2.4f, 0.7f, 0f), // aim the blast at the wall, not the plate
            OneShot = true,
        });

        StatusUpdated?.Invoke("Trigger Playground: the ball rolls right - the wind plate scatters the crates, then the explosion plate blows up the wall.");
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
        if (_levelIndex >= 0) _shotsThisLevel++; // counts toward the level's star rating
    }

    private void Explode()
    {
        if (!_aimValid) return;
        _world.ApplyExplosion(_aimPoint, radius: 5f, strength: 9f);
        SpawnExplosionFeedback(_aimPoint, 5f);
        PlayExplosionSound();
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
        _jointFirstBody = null;
        _jointFirstLocal = Vector3.Zero;
        _jointFirstWorld = Vector3.Zero;
        StatusUpdated?.Invoke(PendingSceneActionInstruction());
        NotifyStateChanged();
    }

    private void CancelPendingSceneAction()
    {
        if (_pendingSceneAction == PendingSceneActionKind.None) return;
        _pendingSceneAction = PendingSceneActionKind.None;
        _pendingSpawnKind = 0;
        _jointFirstBody = null;
        _jointFirstLocal = Vector3.Zero;
        _jointFirstWorld = Vector3.Zero;
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

        if (_pendingSceneAction is PendingSceneActionKind.Connect or PendingSceneActionKind.Spring or PendingSceneActionKind.Disconnect)
            return ExecuteJointTool(_pendingSceneAction);

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
            case PendingSceneActionKind.Wind:
                AddField(ForceField.Kind.Wind);
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
        PendingSceneActionKind.Wind => "wind",
        PendingSceneActionKind.Connect => "connect",
        PendingSceneActionKind.Spring => "spring",
        PendingSceneActionKind.Disconnect => "disconnect",
        _ => "tool",
    };

    private string PendingSceneActionInstruction() => _pendingSceneAction switch
    {
        PendingSceneActionKind.Connect => "Connect: click first object, then click second object. Esc cancels.",
        PendingSceneActionKind.Spring => "Spring: click first object, then click second object. Esc cancels.",
        PendingSceneActionKind.Disconnect => "Disconnect: click an object to remove all its links/springs. Esc cancels.",
        _ => $"Click inside the scene to place {PendingSceneActionLabel()}. Press Esc to cancel.",
    };

    private bool ExecuteJointTool(PendingSceneActionKind action)
    {
        var (origin, dir) = MouseRay(_lastMouseX, _lastMouseY);
        var body = _world.RayCast(origin, dir, out _, out var hitPoint);
        if (body == null)
        {
            StatusUpdated?.Invoke(action == PendingSceneActionKind.Disconnect
                ? "Disconnect: click an object with links/springs. Esc cancels."
                : $"{PendingSceneActionLabel()}: click an object first. Esc cancels.");
            return false;
        }

        if (action == PendingSceneActionKind.Disconnect)
        {
            int removed = _world.Joints.RemoveAll(j => j.Involves(body));
            if (removed > 0) body.Wake();
            StatusUpdated?.Invoke(removed == 0 ? "This object has no links/springs." : $"Removed {removed} link(s)/spring(s).");
            _pendingSceneAction = PendingSceneActionKind.None;
            _jointFirstBody = null;
            NotifyStateChanged();
            return true;
        }

        if (_jointFirstBody == null)
        {
            _jointFirstBody = body;
            _jointFirstWorld = hitPoint;
            _jointFirstLocal = Vector3.Transform(hitPoint - body.Position, Quaternion.Conjugate(body.Rotation));
            body.Wake();
            StatusUpdated?.Invoke($"{PendingSceneActionLabel()}: first object selected. Click the second object. Esc cancels.");
            NotifyStateChanged();
            return true;
        }

        if (body == _jointFirstBody)
        {
            StatusUpdated?.Invoke($"{PendingSceneActionLabel()}: click a different second object. Esc cancels.");
            return false;
        }

        var localB = Vector3.Transform(hitPoint - body.Position, Quaternion.Conjugate(body.Rotation));
        float length = Vector3.Distance(_jointFirstWorld, hitPoint);
        if (length < 0.25f) length = Vector3.Distance(_jointFirstBody.Position, body.Position);
        if (length < 0.25f) length = 0.25f;

        _world.Joints.Add(new Joint
        {
            Type = action == PendingSceneActionKind.Spring ? Joint.Kind.Spring : Joint.Kind.Distance,
            A = _jointFirstBody,
            B = body,
            LocalA = _jointFirstLocal,
            LocalB = localB,
            Length = length,
            Stiffness = action == PendingSceneActionKind.Spring ? 22f : 18f,
            Damping = action == PendingSceneActionKind.Spring ? 2.6f : 2.2f,
        });

        _jointFirstBody.Wake();
        body.Wake();
        StatusUpdated?.Invoke(action == PendingSceneActionKind.Spring ? "Spring created." : "Objects connected.");
        _pendingSceneAction = PendingSceneActionKind.None;
        _jointFirstBody = null;
        NotifyStateChanged();
        return true;
    }

    private void ClearDynamic()
    {
        SelectBody(null);
        SelectTrigger(null);
        _world.Grabbed = null;
        _world.Joints.Clear();
        _world.Bodies.RemoveAll(b => !b.IsStatic);
        _particles.Clear();
        _waterTouchState.Clear();
        _triggers.Clear();
        ClearChallenge();
    }

    private void EvictIfFull()
    {
        if (_world.Bodies.Count < MaxBodies) return;
        var oldest = _world.Bodies.FirstOrDefault(b => !b.IsStatic && b != _world.Grabbed);
        if (oldest != null) _world.RemoveBody(oldest);
    }

    // ---- triggers / sensors ----

    private void UpdateTriggers(float dt)
    {
        if (dt <= 0f || _triggers.Count == 0) return;

        foreach (var tr in _triggers)
        {
            if (!tr.Enabled) continue;
            if (tr.Cooldown > 0f) tr.Cooldown -= dt;
            if (tr.Pulse > 0f) tr.Pulse = MathF.Max(0f, tr.Pulse - dt * 1.8f);

            bool pressed = IsTriggerPressed(tr);
            if (pressed && !tr.WasPressed && tr.Cooldown <= 0f)
                FireTrigger(tr);
            tr.WasPressed = pressed;
        }
    }

    private bool IsTriggerPressed(SceneTrigger tr)
    {
        foreach (var b in _world.Bodies)
        {
            if (b.IsStatic || !b.UserObject) continue;
            var p = b.Position;
            float margin = MathF.Max(0.15f, MathF.Min(0.7f, b.BoundingRadius * 0.35f));
            if (p.X < tr.Position.X - tr.HalfExtents.X - margin || p.X > tr.Position.X + tr.HalfExtents.X + margin) continue;
            if (p.Z < tr.Position.Z - tr.HalfExtents.Z - margin || p.Z > tr.Position.Z + tr.HalfExtents.Z + margin) continue;
            if (p.Y > tr.Position.Y + 1.3f + b.BoundingRadius) continue;
            return true;
        }
        return false;
    }

    private void FireTrigger(SceneTrigger tr)
    {
        tr.Cooldown = tr.CooldownSeconds;
        tr.Pulse = 1.0f;
        if (tr.OneShot) tr.Enabled = false;

        switch (tr.Action)
        {
            case TriggerActionKind.Explosion:
                ApplyExplosionAt(tr.TargetPosition + new Vector3(0, 0.25f, 0), radius: tr.Radius, strength: tr.Strength);
                StatusUpdated?.Invoke($"Triggered: {tr.Name} -> explosion");
                break;
            case TriggerActionKind.Wind:
                _world.Fields.Clear();
                _world.Fields.Add(new ForceField
                {
                    Type = ForceField.Kind.Wind,
                    Position = tr.TargetPosition + new Vector3(0, 1.6f, 0),
                    Radius = tr.Radius,
                    Strength = tr.Strength,
                    WindDir = Vector3.UnitX,
                });
                foreach (var b in _world.Bodies) b.Wake();
                StatusUpdated?.Invoke($"Triggered: {tr.Name} -> wind field");
                break;
            case TriggerActionKind.ToggleGravity:
                ToggleGravity();
                StatusUpdated?.Invoke($"Triggered: {tr.Name} -> gravity toggled");
                break;
            case TriggerActionKind.ToggleAttractor:
                _world.Fields.Clear();
                _world.Fields.Add(new ForceField
                {
                    Type = ForceField.Kind.Attract,
                    Position = tr.TargetPosition + new Vector3(0, 1.5f, 0),
                    Radius = tr.Radius,
                    Strength = tr.Strength,
                });
                foreach (var b in _world.Bodies) b.Wake();
                StatusUpdated?.Invoke($"Triggered: {tr.Name} -> attractor");
                break;
            case TriggerActionKind.ToggleRepeller:
                _world.Fields.Clear();
                _world.Fields.Add(new ForceField
                {
                    Type = ForceField.Kind.Repel,
                    Position = tr.TargetPosition + new Vector3(0, 1.5f, 0),
                    Radius = tr.Radius,
                    Strength = tr.Strength,
                });
                foreach (var b in _world.Bodies) b.Wake();
                StatusUpdated?.Invoke($"Triggered: {tr.Name} -> repeller");
                break;
        }
        NotifyStateChanged();
    }

    private void ApplyExplosionAt(Vector3 center, float radius, float strength)
    {
        _world.ApplyExplosion(center, radius, strength);
        SpawnExplosionFeedback(center, radius);
        PlayExplosionSound();
        foreach (var b in _world.Bodies) b.Wake();
    }

    // ---- effects ----

    /// <summary>
    /// Turn this frame's physics events into particles: orange sparks at hard contacts,
    /// and faint trails behind anything moving fast. The trail dots inherit the body's
    /// color, so you get streaks that match what's flying around.
    /// </summary>
    private void SpawnEffectsFromWorld()
    {
        float loudestImpact = 0f;

        // sparks + dust from impacts (the solver flagged contacts with a high closing speed)
        foreach (var (point, normal, speed) in _world.Impacts)
        {
            loudestImpact = MathF.Max(loudestImpact, speed);
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

            // gray dust puffs make heavy hits read better than sparks alone
            int dust = Math.Clamp((int)(speed * 0.35f), 1, 5);
            for (int i = 0; i < dust && _particles.Count < MaxParticles; i++)
            {
                var dir = Vector3.Normalize(normal * 0.7f + RandomUnit() * 0.9f);
                _particles.Add(new Particle
                {
                    Pos = point + normal * 0.04f,
                    Vel = dir * (0.5f + (float)_rng.NextDouble() * 1.8f),
                    Color = new Vector3(0.55f, 0.54f, 0.50f),
                    Life = 0.65f,
                    MaxLife = 0.65f,
                    Size = 0.10f,
                    Gravity = false,
                });
            }
        }
        if (loudestImpact >= 3.5f) PlayImpactSound(loudestImpact);

        SpawnWaterSplashEffects();

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

    private void SpawnWaterSplashEffects()
    {
        if (_world.Waters.Count == 0)
        {
            _waterTouchState.Clear();
            return;
        }

        var w = _world.Waters[0];
        var alive = new HashSet<RigidBody>(_world.Bodies);
        foreach (var key in _waterTouchState.Keys.ToArray())
            if (!alive.Contains(key)) _waterTouchState.Remove(key);

        foreach (var b in _world.Bodies)
        {
            if (b.IsStatic) continue;
            float surface = w.SurfaceAt(b.Position.X, b.Position.Z);
            bool intersectsSurface = w.ContainsColumn(b.Position)
                && b.Position.Y - b.BoundingRadius < surface
                && b.Position.Y + b.BoundingRadius > surface;

            bool wasTouching = _waterTouchState.TryGetValue(b, out var prev) && prev;
            _waterTouchState[b] = intersectsSurface;

            if (!intersectsSurface || wasTouching) continue;
            if (b.Velocity.Y > -0.25f && b.Velocity.LengthSquared() < 4f) continue;

            var pos = new Vector3(b.Position.X, surface + 0.03f, b.Position.Z);
            SpawnSplash(pos, MathF.Min(2.8f, 0.45f + b.Velocity.Length() * 0.12f + b.BoundingRadius * 0.35f));
            PlaySplashSound();
        }
    }

    private void SpawnSplash(Vector3 pos, float power)
    {
        int count = Math.Clamp((int)(power * 12f), 6, 28);
        for (int i = 0; i < count && _particles.Count < MaxParticles; i++)
        {
            var horizontal = new Vector3((float)_rng.NextDouble() * 2f - 1f, 0f, (float)_rng.NextDouble() * 2f - 1f);
            if (horizontal.LengthSquared() < 1e-4f) horizontal = Vector3.UnitX;
            horizontal = Vector3.Normalize(horizontal);
            float speed = power * (0.8f + (float)_rng.NextDouble() * 1.8f);
            _particles.Add(new Particle
            {
                Pos = pos,
                Vel = horizontal * speed + Vector3.UnitY * (power * (0.8f + (float)_rng.NextDouble() * 1.5f)),
                Color = new Vector3(0.45f, 0.75f, 1.0f),
                Life = 0.55f + (float)_rng.NextDouble() * 0.35f,
                MaxLife = 0.85f,
                Size = 0.045f + power * 0.018f,
                Gravity = true,
            });
        }

        // Short-lived foam dots stay near the surface.
        for (int i = 0; i < 10 && _particles.Count < MaxParticles; i++)
        {
            var offset = new Vector3((float)_rng.NextDouble() * 2f - 1f, 0f, (float)_rng.NextDouble() * 2f - 1f);
            if (offset.LengthSquared() > 1e-4f) offset = Vector3.Normalize(offset) * ((float)_rng.NextDouble() * 0.55f * power);
            _particles.Add(new Particle
            {
                Pos = pos + offset,
                Vel = offset * 0.2f,
                Color = new Vector3(0.80f, 0.92f, 1.0f),
                Life = 0.75f,
                MaxLife = 0.75f,
                Size = 0.07f,
                Gravity = false,
            });
        }
    }

    private void SpawnExplosionFeedback(Vector3 center, float radius)
    {
        // Fast radial particles plus a flat bright disk give a cheap shockwave impression.
        for (int i = 0; i < 48 && _particles.Count < MaxParticles; i++)
        {
            var dir = RandomUnit();
            dir.Y = MathF.Abs(dir.Y) * 0.45f + 0.15f;
            dir = Vector3.Normalize(dir);
            _particles.Add(new Particle
            {
                Pos = center + dir * 0.15f,
                Vel = dir * (5f + (float)_rng.NextDouble() * 8f),
                Color = new Vector3(1.0f, 0.55f + (float)_rng.NextDouble() * 0.25f, 0.12f),
                Life = 0.55f,
                MaxLife = 0.55f,
                Size = 0.08f + (float)_rng.NextDouble() * 0.08f,
                Gravity = false,
            });
        }

        for (int i = 0; i < 28 && _particles.Count < MaxParticles; i++)
        {
            float a = MathF.Tau * i / 28f;
            var dir = new Vector3(MathF.Cos(a), 0.05f, MathF.Sin(a));
            _particles.Add(new Particle
            {
                Pos = center + new Vector3(0, 0.08f, 0),
                Vel = dir * (radius * 1.7f),
                Color = new Vector3(1.0f, 0.75f, 0.25f),
                Life = 0.32f,
                MaxLife = 0.32f,
                Size = 0.09f,
                Gravity = false,
            });
        }
    }

    private void PlayImpactSound(float speed)
    {
        if (!_soundEnabled) return;
        double now = _sw.Elapsed.TotalSeconds;
        if (now < _nextImpactSound) return;
        _nextImpactSound = now + 0.09;
        PlaySystemSound(speed > 8f ? SystemSounds.Hand : SystemSounds.Beep);
    }

    private void PlaySplashSound()
    {
        if (!_soundEnabled) return;
        double now = _sw.Elapsed.TotalSeconds;
        if (now < _nextSplashSound) return;
        _nextSplashSound = now + 0.18;
        PlaySystemSound(SystemSounds.Asterisk);
    }

    private void PlayExplosionSound()
    {
        if (!_soundEnabled) return;
        double now = _sw.Elapsed.TotalSeconds;
        if (now < _nextExplosionSound) return;
        _nextExplosionSound = now + 0.25;
        PlaySystemSound(SystemSounds.Exclamation);
    }

    private static void PlaySystemSound(SystemSound sound)
    {
        try { sound.Play(); }
        catch { /* sound is non-critical feedback */ }
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
                Density = 1.65f,
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

        if (!_aimValid) return;

        _world.Fields.Clear();
        if (kind == ForceField.Kind.Wind)
        {
            var dir = new Vector3(_camTarget.X - _camPos.X, 0, _camTarget.Z - _camPos.Z);
            dir = dir.LengthSquared() > 1e-5f ? Vector3.Normalize(dir) : Vector3.UnitX;
            _world.Fields.Add(new ForceField { Type = kind, Position = _aimPoint + new Vector3(0, 1.6f, 0), Radius = 7.5f, Strength = 9f, WindDir = dir });
        }
        else
        {
            _world.Fields.Add(new ForceField { Type = kind, Position = _aimPoint + new Vector3(0, 1.5f, 0), Radius = 7f, Strength = 18f });
        }
        foreach (var b in _world.Bodies) b.Wake();
        NotifyStateChanged();
    }

    private void SelectBody(RigidBody? body)
    {
        if (body != null && !body.UserObject) body = null;
        if (body != null && _selectedTrigger != null)
        {
            _selectedTrigger = null;
            NotifyTriggerSelectionChanged();
        }
        if (_selectedBody == body) return;
        _selectedBody = body;
        NotifySelectionChanged();
    }

    private void SelectTrigger(SceneTrigger? trigger)
    {
        if (trigger != null && _selectedBody != null)
        {
            _selectedBody = null;
            NotifySelectionChanged();
        }
        if (_selectedTrigger == trigger) return;
        _selectedTrigger = trigger;
        NotifyTriggerSelectionChanged();
    }

    private SelectedBodySnapshot? CreateSelectedSnapshot()
    {
        var b = _selectedBody;
        if (b == null) return null;
        return new SelectedBodySnapshot
        {
            IsStatic = b.IsStatic,
            ChildCount = b.Children.Length,
            Mass = b.Mass,
            Density = b.Density,
            Friction = b.Friction,
            Restitution = b.Restitution,
            Position = b.Position,
            Velocity = b.Velocity,
            Color = b.Color,
            Breakable = b.Breakable,
            BreakThreshold = b.BreakThreshold,
        };
    }

    private SelectedTriggerSnapshot? CreateSelectedTriggerSnapshot()
    {
        var tr = _selectedTrigger;
        if (tr == null) return null;
        return new SelectedTriggerSnapshot
        {
            Name = tr.Name,
            Position = tr.Position,
            HalfExtents = tr.HalfExtents,
            Action = tr.Action,
            OneShot = tr.OneShot,
            Enabled = tr.Enabled,
            Radius = tr.Radius,
            Strength = tr.Strength,
            CooldownSeconds = tr.CooldownSeconds,
            TargetPosition = tr.TargetPosition,
        };
    }

    private void NotifySelectionChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        SelectionChanged?.Invoke(CreateSelectedSnapshot());
    }

    private void NotifyTriggerSelectionChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        TriggerSelectionChanged?.Invoke(CreateSelectedTriggerSnapshot());
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

    private void TryStartEditorToolDrag(int mx, int my)
    {
        var (origin, dir) = MouseRay(mx, my);
        var body = _world.RayCast(origin, dir, out _, out _);
        if (body != null && body.UserObject)
            SelectBody(body);

        if (_selectedBody == null || _selectedBody.IsStatic) return;

        _selectedBody.Wake();
        _toolDragging = true;
        _toolDragStartX = mx;
        _toolDragStartY = my;
        _toolStartPos = _selectedBody.Position;
        _toolStartRot = _selectedBody.Rotation;
        _toolLastScaleFactor = 1f;
    }

    private void UpdateEditorToolDrag(int mx, int my)
    {
        if (_selectedBody == null) { _toolDragging = false; return; }
        var b = _selectedBody;

        switch (_editorTool)
        {
            case EditorToolMode.Move:
                if (MouseToPlaneY(mx, my, _toolStartPos.Y, out var p))
                {
                    b.Position = new Vector3(p.X, _toolStartPos.Y, p.Z);
                    b.Velocity = Vector3.Zero;
                }
                break;

            case EditorToolMode.Rotate:
                float angle = (mx - _toolDragStartX) * 0.015f;
                b.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle) * _toolStartRot);
                b.AngularVelocity = Vector3.Zero;
                break;

            case EditorToolMode.Scale:
                float f = MathF.Exp((_toolDragStartY - my) * 0.01f);
                f = Math.Clamp(f, 0.25f, 4.0f);
                float delta = f / Math.Max(0.001f, _toolLastScaleFactor);
                b.ScaleUniform(delta);
                _toolLastScaleFactor = f;
                break;
        }

        b.UpdateDerived();
        b.Wake();
        NotifySelectionChanged();
    }

    private bool MouseToPlaneY(int mx, int my, float y, out Vector3 point)
    {
        var (origin, dir) = MouseRay(mx, my);
        point = default;
        if (MathF.Abs(dir.Y) < 1e-5f) return false;
        float t = (y - origin.Y) / dir.Y;
        if (t <= 0f) return false;
        point = origin + dir * t;
        float lim = ArenaHalf - 0.3f;
        point.X = Math.Clamp(point.X, -lim, lim);
        point.Z = Math.Clamp(point.Z, -lim, lim);
        return true;
    }

    private void TryGrab(int mx, int my)
    {
        var (origin, dir) = MouseRay(mx, my);
        var body = _world.RayCast(origin, dir, out float t, out var hitPoint);
        var trigger = PickTrigger(origin, dir, out float triggerT);

        if (trigger != null && (body == null || triggerT < t))
        {
            SelectTrigger(trigger);
            return;
        }

        if (body == null || !body.UserObject)
        {
            SelectBody(null);
            SelectTrigger(null);
            return;
        }

        SelectBody(body);
        if (body.IsStatic) return;

        body.Wake();
        _world.Grabbed = body;
        var invRot = Quaternion.Conjugate(body.Rotation);
        _world.GrabLocalAnchor = Vector3.Transform(hitPoint - body.Position, invRot);
        _world.DragTarget = hitPoint;

        // drag plane: perpendicular to the view direction, through the grab point
        var camFwd = Vector3.Normalize(_camTarget - _camPos);
        _dragPlaneDist = Vector3.Dot(hitPoint - _camPos, camFwd);
    }

    private SceneTrigger? PickTrigger(Vector3 origin, Vector3 dir, out float bestT)
    {
        bestT = float.PositiveInfinity;
        SceneTrigger? best = null;
        foreach (var tr in _triggers)
        {
            var min = tr.Position - tr.HalfExtents;
            var max = tr.Position + tr.HalfExtents;
            if (!RayAabb(origin, dir, min, max, out float t)) continue;
            if (t < bestT) { bestT = t; best = tr; }
        }
        return best;
    }

    private static bool RayAabb(Vector3 origin, Vector3 dir, Vector3 min, Vector3 max, out float tHit)
    {
        float tMin = 0f, tMax = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? dir.X : axis == 1 ? dir.Y : dir.Z;
            float mn = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float mx = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-6f)
            {
                if (o < mn || o > mx) { tHit = 0f; return false; }
                continue;
            }
            float inv = 1f / d;
            float t1 = (mn - o) * inv;
            float t2 = (mx - o) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) { tHit = 0f; return false; }
        }
        tHit = tMin;
        return true;
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
        GL.Uniform1(_uTime, (float)_sw.Elapsed.TotalSeconds);
        GL.Uniform1(_uWaterWaveAmp, 0f);

        // floor
        GL.BindTexture(GL.TEXTURE_2D, _texFloor);
        GL.Uniform1(_uUvScale, 20f);
        GL.Uniform3(_uColor, 0.62f, 0.64f, 0.68f);
        GL.UniformMatrix4(_uModel, ToArray(Matrix4x4.CreateScale(40f, 1f, 40f)));
        _planeMesh.Draw();
        GL.Uniform1(_uWaterWaveAmp, 0f);

        // bodies
        GL.Uniform1(_uUvScale, 1f);
        foreach (var b in _world.Bodies)
        {
            // sleeping bodies dim slightly so you can see the engine actually sleeps
            var color = b == _selectedBody ? b.Color * 1.55f + new Vector3(0.25f, 0.22f, 0.08f)
                      : b == _world.Grabbed ? b.Color * 1.35f
                      : b == _jointFirstBody ? b.Color * 1.45f + new Vector3(0.25f, 0.35f, 0.9f)
                      : b.Sleeping ? b.Color * 0.72f
                      : b.Color;

            bool fieldAffected = TryGetAffectingFieldColor(b, out var fieldColor);
            if (fieldAffected)
            {
                // Affected bodies get a visible tint and a faint glow, so the user sees what the field is touching.
                color = color * 0.65f + fieldColor * 0.65f;
                GL.Uniform1(_uEmissive, 0.35f);
            }
            else
            {
                GL.Uniform1(_uEmissive, 0f);
            }

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
        GL.Uniform1(_uEmissive, 0f);

        DrawJointRods();
        DrawFieldMarkers();
        DrawEditorGizmo();
        DrawAimMarker();
        DrawParticles();
        DrawTriggers();
        DrawChallengeMarker();
        DrawWater();
    }

    /// <summary>Visual rods and springs drawn between joint anchors.</summary>
    private void DrawJointRods()
    {
        if (_world.Joints.Count == 0 && _jointFirstBody == null) return;
        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 0.35f);

        foreach (var j in _world.Joints)
        {
            if (j.Type == Joint.Kind.Spring)
            {
                GL.Uniform3(_uColor, 0.40f, 0.85f, 1.00f);
                DrawSpringLine(j.WorldA, j.WorldB);
            }
            else
            {
                GL.Uniform3(_uColor, 0.78f, 0.78f, 0.84f);
                DrawRodSegment(j.WorldA, j.WorldB, 0.035f);
            }
        }

        // While the user is choosing the second object, draw a temporary line from the first pick to the aim point.
        if (_jointFirstBody != null && (_pendingSceneAction == PendingSceneActionKind.Connect || _pendingSceneAction == PendingSceneActionKind.Spring) && _aimValid)
        {
            if (_pendingSceneAction == PendingSceneActionKind.Spring)
            {
                GL.Uniform3(_uColor, 0.45f, 0.95f, 1.00f);
                DrawSpringLine(_jointFirstWorld, _aimPoint + new Vector3(0, 0.35f, 0));
            }
            else
            {
                GL.Uniform3(_uColor, 1.0f, 0.9f, 0.25f);
                DrawRodSegment(_jointFirstWorld, _aimPoint + new Vector3(0, 0.35f, 0), 0.045f);
            }
        }

        GL.Uniform1(_uEmissive, 0f);
    }

    private void DrawRodSegment(Vector3 a, Vector3 b, float thickness)
    {
        var mid = (a + b) * 0.5f;
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;
        var dir = d / len;
        var rot = RotationFromTo(Vector3.UnitY, dir);
        var m = Matrix4x4.CreateScale(thickness, len * 0.5f, thickness)
              * Matrix4x4.CreateFromQuaternion(rot)
              * Matrix4x4.CreateTranslation(mid);
        GL.UniformMatrix4(_uModel, ToArray(m));
        _cubeMesh.Draw();
    }

    private void DrawSpringLine(Vector3 a, Vector3 b)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;
        var dir = d / len;
        var side = Vector3.Cross(dir, Vector3.UnitY);
        if (side.LengthSquared() < 1e-5f) side = Vector3.Cross(dir, Vector3.UnitX);
        side = Vector3.Normalize(side);

        const int coils = 10;
        Vector3 prev = a;
        for (int i = 1; i <= coils; i++)
        {
            float t = i / (float)coils;
            float amp = (i == coils ? 0f : 0.16f * (i % 2 == 0 ? 1f : -1f));
            var next = Vector3.Lerp(a, b, t) + side * amp;
            DrawRodSegment(prev, next, 0.028f);
            prev = next;
        }
    }

    private void DrawFieldMarkers()
    {
        if (_world.Fields.Count == 0) return;

        float t = (float)_sw.Elapsed.TotalSeconds;
        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);

        // Draw the solid “source” markers first so each field still has a clear anchor point.
        foreach (var f in _world.Fields)
        {
            var c = FieldColor(f.Type);
            GL.Uniform1(_uEmissive, 1f);
            GL.Uniform1(_uAlpha, 1f);
            GL.Uniform3(_uColor, c.X, c.Y, c.Z);
            float coreSize = f.Type == ForceField.Kind.Wind ? 0.18f : 0.25f;
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(coreSize) * Matrix4x4.CreateTranslation(f.Position)));
            _sphereMesh.Draw();
        }
        GL.Uniform1(_uEmissive, 0f);

        // Then overlay translucent animated volumes / particles so the field “reads” in space.
        GL.Enable(GL.BLEND);
        GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
        GL.DepthMask(0);

        foreach (var f in _world.Fields)
        {
            switch (f.Type)
            {
                case ForceField.Kind.Attract:
                case ForceField.Kind.Repel:
                    DrawRadialFieldEffect(f, t);
                    break;
                case ForceField.Kind.Wind:
                    DrawWindFieldEffect(f, t);
                    break;
            }
        }

        GL.DepthMask(1);
        GL.Disable(GL.BLEND);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uEmissive, 0f);
    }

    private Vector3 FieldColor(ForceField.Kind kind) => kind switch
    {
        ForceField.Kind.Attract => new Vector3(0.30f, 0.60f, 1.00f),
        ForceField.Kind.Repel => new Vector3(1.00f, 0.40f, 0.30f),
        ForceField.Kind.Wind => new Vector3(0.55f, 0.95f, 0.95f),
        _ => new Vector3(1f),
    };


    private bool TryGetAffectingFieldColor(RigidBody body, out Vector3 color)
    {
        color = default;
        if (body.IsStatic || _world.Fields.Count == 0) return false;

        foreach (var field in _world.Fields)
        {
            float dist = Vector3.Distance(body.Position, field.Position);
            if (dist > field.Radius) continue;
            color = FieldColor(field.Type);
            return true;
        }
        return false;
    }

    private void DrawRadialFieldEffect(ForceField f, float t)
    {
        var c = FieldColor(f.Type);
        bool inward = f.Type == ForceField.Kind.Attract;

        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uEmissive, 1f);

        // A couple of faint pulsing shells show the field radius.
        for (int layer = 0; layer < 2; layer++)
        {
            float pulse = 0.5f + 0.5f * MathF.Sin(t * (1.8f + layer * 0.35f) + layer * 1.7f);
            float radius = f.Radius * (0.82f + layer * 0.12f + pulse * 0.05f);
            float alpha = 0.06f + pulse * 0.045f;
            GL.Uniform1(_uAlpha, alpha);
            GL.Uniform3(_uColor, c.X, c.Y, c.Z);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(radius) * Matrix4x4.CreateTranslation(f.Position)));
            _sphereMesh.Draw();
        }

        // Moving particles make the direction obvious: inward spiral for attract, outward for repel.
        const int count = 18;
        for (int i = 0; i < count; i++)
        {
            float seed = i / (float)count;
            float anim = (t * 0.55f + seed) % 1f;
            float flow = inward ? (1f - anim) : anim;
            float radius = MathF.Max(0.25f, f.Radius * (0.12f + 0.83f * flow));
            float angle = seed * MathF.Tau + t * (inward ? 2.2f : -2.2f) + (1f - flow) * 5.5f;
            float lift = MathF.Sin(angle * 2.3f + t * 2.4f) * 0.22f * (0.35f + flow);
            var pos = f.Position + new Vector3(MathF.Cos(angle) * radius, lift, MathF.Sin(angle) * radius);

            float size = inward ? (0.04f + 0.09f * (1f - flow))
                                : (0.04f + 0.09f * flow);
            float alpha = inward ? (0.14f + 0.28f * (1f - flow))
                                 : (0.14f + 0.28f * flow);
            GL.Uniform1(_uAlpha, alpha);
            GL.Uniform3(_uColor, c.X, c.Y, c.Z);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(pos)));
            _sphereMesh.Draw();
        }
    }

    private void DrawWindFieldEffect(ForceField f, float t)
    {
        var c = FieldColor(f.Type);
        GetWindFrame(f, out var dir, out var side, out var up, out var rot);

        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uEmissive, 1f);

        // A clearer elongated bubble with a couple of nested shells so the volume reads better.
        for (int layer = 0; layer < 2; layer++)
        {
            float shellPulse = 0.5f + 0.5f * MathF.Sin(t * (2.0f + layer * 0.45f) + layer * 0.9f);
            GL.Uniform1(_uAlpha, 0.07f + shellPulse * 0.035f);
            GL.Uniform3(_uColor, c.X, c.Y, c.Z);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(1.15f + layer * 0.2f, f.Radius * (0.88f + layer * 0.08f), 1.15f + layer * 0.2f)
                * Matrix4x4.CreateFromQuaternion(rot)
                * Matrix4x4.CreateTranslation(f.Position)));
            _capsuleMesh.Draw();
        }

        // More streaks inside the flow so it looks less subtle than before.
        const int lanes = 5;
        const int perLane = 6;
        for (int lane = 0; lane < lanes; lane++)
        {
            float laneOffset = lane - (lanes - 1) * 0.5f;
            for (int i = 0; i < perLane; i++)
            {
                float seed = i / (float)perLane + lane * 0.13f;
                float flow = (t * 1.1f + seed) % 1f;
                float along = (flow - 0.5f) * f.Radius * 1.85f;
                float sideOffset = laneOffset * 0.42f;
                float upOffset = MathF.Sin(t * 2.7f + lane * 1.1f + i * 0.6f) * 0.2f;
                var pos = f.Position + dir * along + side * sideOffset + up * upOffset;

                float len = 0.28f + 0.16f * flow;
                float thick = 0.045f + 0.01f * MathF.Abs(laneOffset);
                GL.Uniform1(_uAlpha, 0.16f + 0.22f * flow);
                GL.Uniform3(_uColor, c.X, c.Y, c.Z);
                GL.UniformMatrix4(_uModel, ToArray(
                    Matrix4x4.CreateScale(thick, len, thick)
                    * Matrix4x4.CreateFromQuaternion(rot)
                    * Matrix4x4.CreateTranslation(pos)));
                _capsuleMesh.Draw();
            }
        }

        // Arrow-like chevrons make the direction unmistakable.
        const int arrowCount = 4;
        for (int i = 0; i < arrowCount; i++)
        {
            float flow = ((t * 0.8f) + i / (float)arrowCount) % 1f;
            float along = (flow - 0.5f) * f.Radius * 1.75f;
            var basePos = f.Position + dir * along;
            float alpha = 0.18f + 0.22f * flow;
            for (int wing = -1; wing <= 1; wing += 2)
            {
                var wingDir = Vector3.Normalize(dir * 0.9f + side * 0.35f * wing);
                var wingRot = RotationFromTo(Vector3.UnitY, wingDir);
                var wingPos = basePos - dir * 0.12f + side * (0.12f * wing);
                GL.Uniform1(_uAlpha, alpha);
                GL.Uniform3(_uColor, c.X, c.Y, c.Z);
                GL.UniformMatrix4(_uModel, ToArray(
                    Matrix4x4.CreateScale(0.04f, 0.22f, 0.04f)
                    * Matrix4x4.CreateFromQuaternion(wingRot)
                    * Matrix4x4.CreateTranslation(wingPos)));
                _capsuleMesh.Draw();
            }
        }

        // Small “dust” dots riding through the stream sell the idea of moving air.
        const int dustCount = 14;
        for (int i = 0; i < dustCount; i++)
        {
            float seed = i / (float)dustCount;
            float flow = (t * 1.25f + seed) % 1f;
            float along = (flow - 0.5f) * f.Radius * 1.9f;
            float spiral = seed * MathF.Tau * 2f + t * 1.6f;
            var pos = f.Position
                + dir * along
                + side * MathF.Cos(spiral) * 0.55f
                + up * MathF.Sin(spiral) * 0.28f;
            GL.Uniform1(_uAlpha, 0.10f + 0.18f * flow);
            GL.Uniform3(_uColor, c.X, c.Y, c.Z);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(0.05f) * Matrix4x4.CreateTranslation(pos)));
            _sphereMesh.Draw();
        }
    }

    private void GetWindFrame(ForceField f, out Vector3 dir, out Vector3 side, out Vector3 up, out Quaternion rot)
    {
        dir = f.WindDir.LengthSquared() > 1e-6f ? Vector3.Normalize(f.WindDir) : Vector3.UnitX;
        side = Vector3.Cross(dir, Vector3.UnitY);
        if (side.LengthSquared() < 1e-6f) side = Vector3.Cross(dir, Vector3.UnitX);
        side = Vector3.Normalize(side);
        up = Vector3.Normalize(Vector3.Cross(side, dir));
        rot = RotationFromTo(Vector3.UnitY, dir);
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

    private void DrawEditorGizmo()
    {
        if (_selectedBody == null) return;
        var p = _selectedBody.Position;

        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 1f);
        GL.Uniform1(_uAlpha, 1f);

        // X axis handle
        DrawGizmoRod(p, p + Vector3.UnitX * 1.25f, new Vector3(1.0f, 0.25f, 0.20f), 0.035f);
        // Y axis handle
        DrawGizmoRod(p, p + Vector3.UnitY * 1.25f, new Vector3(0.25f, 1.0f, 0.25f), 0.035f);
        // Z axis handle
        DrawGizmoRod(p, p + Vector3.UnitZ * 1.25f, new Vector3(0.25f, 0.45f, 1.0f), 0.035f);

        // Tool-specific hint marker. Move = small floor disc, rotate = halo, scale = cube corner.
        var c = _editorTool switch
        {
            EditorToolMode.Move => new Vector3(1.0f, 0.85f, 0.20f),
            EditorToolMode.Rotate => new Vector3(0.90f, 0.45f, 1.0f),
            EditorToolMode.Scale => new Vector3(0.35f, 1.0f, 0.85f),
            _ => new Vector3(1.0f, 1.0f, 0.45f),
        };
        GL.Uniform3(_uColor, c.X, c.Y, c.Z);
        float s = _editorTool == EditorToolMode.Scale ? 0.18f : 0.09f;
        GL.UniformMatrix4(_uModel, ToArray(Matrix4x4.CreateScale(s) * Matrix4x4.CreateTranslation(p + new Vector3(0, 1.45f, 0))));
        (_editorTool == EditorToolMode.Scale ? _cubeMesh : _sphereMesh).Draw();

        GL.Uniform1(_uEmissive, 0f);
        GL.Uniform1(_uAlpha, 1f);
    }

    private void DrawGizmoRod(Vector3 a, Vector3 b, Vector3 color, float thickness)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-5f) return;
        var dir = d / len;
        var rot = RotationFromTo(Vector3.UnitY, dir);
        var mid = (a + b) * 0.5f;
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);
        GL.UniformMatrix4(_uModel, ToArray(
            Matrix4x4.CreateScale(thickness, len * 0.5f, thickness)
            * Matrix4x4.CreateFromQuaternion(rot)
            * Matrix4x4.CreateTranslation(mid)));
        _capsuleMesh.Draw();
    }

    private void DrawTriggers()
    {
        if (_triggers.Count == 0) return;

        GL.Enable(GL.BLEND);
        GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
        GL.DepthMask(0);
        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 0.75f);

        float t = (float)_sw.Elapsed.TotalSeconds;
        foreach (var tr in _triggers)
        {
            var color = TriggerColor(tr.Action);
            float press = tr.WasPressed ? 1f : 0f;
            float pulse = tr.Pulse;
            bool selected = tr == _selectedTrigger;
            float alpha = tr.Enabled ? 0.38f + press * 0.28f + pulse * 0.22f : 0.16f;
            if (selected) alpha += 0.28f;
            var drawColor = selected ? color * 1.35f + new Vector3(0.25f, 0.22f, 0.05f) : color;
            GL.Uniform1(_uAlpha, Math.Clamp(alpha, 0.10f, 0.95f));
            GL.Uniform3(_uColor, drawColor.X, drawColor.Y, drawColor.Z);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(tr.HalfExtents.X, tr.HalfExtents.Y, tr.HalfExtents.Z)
                * Matrix4x4.CreateTranslation(tr.Position)));
            _cubeMesh.Draw();

            // Thin pulsing halo around the plate.
            float halo = 1.05f + 0.08f * MathF.Sin(t * 4f + tr.Position.X);
            GL.Uniform1(_uAlpha, selected ? 0.55f : (tr.Enabled ? 0.18f + pulse * 0.25f : 0.07f));
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(tr.HalfExtents.X * halo, 0.025f, tr.HalfExtents.Z * halo)
                * Matrix4x4.CreateTranslation(tr.Position + new Vector3(0, 0.075f, 0))));
            _cubeMesh.Draw();
        }

        GL.DepthMask(1);
        GL.Disable(GL.BLEND);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uEmissive, 0f);
    }

    private static Vector3 TriggerColor(TriggerActionKind action) => action switch
    {
        TriggerActionKind.Explosion => new Vector3(1.0f, 0.45f, 0.10f),
        TriggerActionKind.Wind => new Vector3(0.55f, 0.95f, 0.95f),
        TriggerActionKind.ToggleGravity => new Vector3(0.70f, 0.55f, 1.00f),
        TriggerActionKind.ToggleAttractor => new Vector3(0.30f, 0.60f, 1.00f),
        TriggerActionKind.ToggleRepeller => new Vector3(1.00f, 0.40f, 0.30f),
        _ => new Vector3(0.8f),
    };

    private void DrawChallengeMarker()
    {
        if (_challengeKind == ChallengeKind.None || _challengeTargetRadius <= 0f) return;
        float t = (float)_sw.Elapsed.TotalSeconds;
        var color = _challengeSuccess ? new Vector3(0.25f, 1.0f, 0.35f)
                  : _challengeFailed ? new Vector3(1.0f, 0.25f, 0.2f)
                  : new Vector3(1.0f, 0.85f, 0.12f);
        float pulse = 0.5f + 0.5f * MathF.Sin(t * 3.0f);

        GL.Enable(GL.BLEND);
        GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
        GL.DepthMask(0);
        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 1f);
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);

        GL.Uniform1(_uAlpha, 0.16f + pulse * 0.08f);
        GL.UniformMatrix4(_uModel, ToArray(
            Matrix4x4.CreateScale(_challengeTargetRadius * (0.95f + pulse * 0.05f)) * Matrix4x4.CreateTranslation(_challengeTarget)));
        _sphereMesh.Draw();

        GL.Uniform1(_uAlpha, 0.45f);
        GL.UniformMatrix4(_uModel, ToArray(
            Matrix4x4.CreateScale(_challengeTargetRadius, 0.035f, _challengeTargetRadius)
            * Matrix4x4.CreateTranslation(new Vector3(_challengeTarget.X, 0.055f, _challengeTarget.Z))));
        _sphereMesh.Draw();

        GL.DepthMask(1);
        GL.Disable(GL.BLEND);
        GL.Uniform1(_uAlpha, 1f);
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
        GL.Uniform1(_uTime, w.Time);
        GL.Uniform1(_uWaterWaveAmp, w.WaveAmplitude);
        int rc = w.FillRipples(_rippleBuffer, WaterVolume.MaxRipples);
        GL.Uniform1(_uRippleCount, rc);
        if (rc > 0) GL.Uniform4(_uRipples, rc, _rippleBuffer);
        GL.UniformMatrix4(_uModel, ToArray(m));
        _waterMesh.Draw();
        GL.Uniform1(_uWaterWaveAmp, 0f);

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
        GL.Uniform1(_uEmissive, 0f);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform3(_uColor, 1.0f, 0.85f, 0.2f); // bright amber, hard to miss

        // a thin flattened sphere = a little disc sitting just above the floor
        var m = Matrix4x4.CreateScale(0.35f, 0.04f, 0.35f)
              * Matrix4x4.CreateTranslation(_aimPoint + new Vector3(0, 0.02f, 0));
        GL.UniformMatrix4(_uModel, ToArray(m));
        _sphereMesh.Draw();

        if (_pendingSceneAction == PendingSceneActionKind.Attractor || _pendingSceneAction == PendingSceneActionKind.Repeller)
            DrawPendingFieldPreview();
        else if (_pendingSceneAction == PendingSceneActionKind.Wind)
            DrawPendingWindPreview();
        else if (_pendingSceneAction == PendingSceneActionKind.Explosion)
            DrawPendingExplosionPreview();
    }

    private void DrawPendingFieldPreview()
    {
        float t = (float)_sw.Elapsed.TotalSeconds;
        var kind = _pendingSceneAction == PendingSceneActionKind.Attractor ? ForceField.Kind.Attract : ForceField.Kind.Repel;
        var color = FieldColor(kind);
        var center = _aimPoint + new Vector3(0, 1.5f, 0);
        const float radius = 7f;
        bool inward = kind == ForceField.Kind.Attract;

        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Enable(GL.BLEND);
        GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
        GL.DepthMask(0);
        GL.Uniform1(_uEmissive, 1f);

        // Main preview shell: users can immediately read both placement and final radius.
        for (int i = 0; i < 2; i++)
        {
            float pulse = 0.5f + 0.5f * MathF.Sin(t * (2.1f + i * 0.4f) + i * 1.3f);
            float shellRadius = radius * (0.94f + i * 0.05f + pulse * 0.025f);
            GL.Uniform1(_uAlpha, 0.07f + pulse * 0.05f);
            GL.Uniform3(_uColor, color.X, color.Y, color.Z);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(shellRadius) * Matrix4x4.CreateTranslation(center)));
            _sphereMesh.Draw();
        }

        // A slightly brighter core to show the exact origin point of the field.
        GL.Uniform1(_uAlpha, 0.85f);
        GL.UniformMatrix4(_uModel, ToArray(
            Matrix4x4.CreateScale(0.22f) * Matrix4x4.CreateTranslation(center)));
        _sphereMesh.Draw();

        // Direction cue particles, so attract/repel differ before placement too.
        const int orbitDots = 12;
        for (int i = 0; i < orbitDots; i++)
        {
            float seed = i / (float)orbitDots;
            float flow = (t * 0.6f + seed) % 1f;
            if (inward) flow = 1f - flow;
            float r = radius * (0.18f + 0.72f * flow);
            float a = seed * MathF.Tau + t * (inward ? 2.0f : -2.0f);
            var pos = center + new Vector3(MathF.Cos(a) * r, MathF.Sin(t * 3f + i) * 0.12f, MathF.Sin(a) * r);
            float size = 0.05f + 0.04f * (inward ? (1f - flow) : flow);
            GL.Uniform1(_uAlpha, 0.14f + 0.22f * (inward ? (1f - flow) : flow));
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(pos)));
            _sphereMesh.Draw();
        }

        GL.DepthMask(1);
        GL.Disable(GL.BLEND);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uEmissive, 0f);
    }

    private void DrawPendingWindPreview()
    {
        float t = (float)_sw.Elapsed.TotalSeconds;
        var center = _aimPoint + new Vector3(0, 1.6f, 0);
        const float radius = 7.5f;
        var previewDir = new Vector3(_camTarget.X - _camPos.X, 0, _camTarget.Z - _camPos.Z);
        previewDir = previewDir.LengthSquared() > 1e-6f ? Vector3.Normalize(previewDir) : Vector3.UnitX;
        var fakeField = new ForceField { Type = ForceField.Kind.Wind, Position = center, Radius = radius, WindDir = previewDir };
        var color = FieldColor(ForceField.Kind.Wind);
        GetWindFrame(fakeField, out var dir, out var side, out var up, out var rot);

        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Enable(GL.BLEND);
        GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
        GL.DepthMask(0);
        GL.Uniform1(_uEmissive, 1f);
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);

        // Volume preview.
        for (int layer = 0; layer < 2; layer++)
        {
            float pulse = 0.5f + 0.5f * MathF.Sin(t * (2.2f + layer * 0.4f) + layer * 0.8f);
            GL.Uniform1(_uAlpha, 0.08f + pulse * 0.04f);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(1.2f + layer * 0.18f, radius * (0.90f + layer * 0.08f), 1.2f + layer * 0.18f)
                * Matrix4x4.CreateFromQuaternion(rot)
                * Matrix4x4.CreateTranslation(center)));
            _capsuleMesh.Draw();
        }

        // Strong direction cue arrow line.
        const int arrows = 5;
        for (int i = 0; i < arrows; i++)
        {
            float flow = ((t * 0.85f) + i / (float)arrows) % 1f;
            float along = (flow - 0.5f) * radius * 1.7f;
            var basePos = center + dir * along;
            GL.Uniform1(_uAlpha, 0.18f + 0.18f * flow);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(0.06f, 0.30f, 0.06f)
                * Matrix4x4.CreateFromQuaternion(rot)
                * Matrix4x4.CreateTranslation(basePos)));
            _capsuleMesh.Draw();

            for (int wing = -1; wing <= 1; wing += 2)
            {
                var wingDir = Vector3.Normalize(dir * 0.85f + side * 0.45f * wing);
                var wingRot = RotationFromTo(Vector3.UnitY, wingDir);
                var wingPos = basePos - dir * 0.14f + side * (0.14f * wing);
                GL.Uniform1(_uAlpha, 0.22f + 0.15f * flow);
                GL.UniformMatrix4(_uModel, ToArray(
                    Matrix4x4.CreateScale(0.05f, 0.20f, 0.05f)
                    * Matrix4x4.CreateFromQuaternion(wingRot)
                    * Matrix4x4.CreateTranslation(wingPos)));
                _capsuleMesh.Draw();
            }
        }

        // Dust / stream particles.
        const int dots = 18;
        for (int i = 0; i < dots; i++)
        {
            float seed = i / (float)dots;
            float flow = (t * 1.15f + seed) % 1f;
            float along = (flow - 0.5f) * radius * 1.8f;
            float spiral = seed * MathF.Tau * 2f + t * 1.8f;
            var pos = center + dir * along + side * MathF.Cos(spiral) * 0.5f + up * MathF.Sin(spiral) * 0.22f;
            GL.Uniform1(_uAlpha, 0.12f + 0.18f * flow);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(0.05f) * Matrix4x4.CreateTranslation(pos)));
            _sphereMesh.Draw();
        }

        GL.DepthMask(1);
        GL.Disable(GL.BLEND);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uEmissive, 0f);
    }

    private void DrawPendingExplosionPreview()
    {
        float t = (float)_sw.Elapsed.TotalSeconds;
        var center = _aimPoint + new Vector3(0, 0.35f, 0);
        const float radius = 5f;
        var color = new Vector3(1.0f, 0.55f, 0.12f);

        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Enable(GL.BLEND);
        GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
        GL.DepthMask(0);
        GL.Uniform1(_uEmissive, 1f);
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);

        // A translucent sphere shows the blast volume before the user commits the click.
        float pulse = 0.5f + 0.5f * MathF.Sin(t * 3.2f);
        GL.Uniform1(_uAlpha, 0.10f + pulse * 0.05f);
        GL.UniformMatrix4(_uModel, ToArray(
            Matrix4x4.CreateScale(radius * (0.97f + pulse * 0.035f)) * Matrix4x4.CreateTranslation(center)));
        _sphereMesh.Draw();

        // A flat, brighter ground ring makes the affected footprint obvious from the camera view.
        GL.Uniform1(_uAlpha, 0.38f);
        GL.UniformMatrix4(_uModel, ToArray(
            Matrix4x4.CreateScale(radius, 0.035f, radius) * Matrix4x4.CreateTranslation(_aimPoint + new Vector3(0, 0.05f, 0))));
        _sphereMesh.Draw();

        // Outward preview dots communicate that this is an impulse, not a persistent field.
        const int dots = 16;
        for (int i = 0; i < dots; i++)
        {
            float seed = i / (float)dots;
            float flow = (t * 0.95f + seed) % 1f;
            float a = seed * MathF.Tau;
            float r = radius * (0.14f + flow * 0.78f);
            var pos = center + new Vector3(MathF.Cos(a) * r, 0.1f + flow * 0.35f, MathF.Sin(a) * r);
            float size = 0.06f + flow * 0.05f;
            GL.Uniform1(_uAlpha, 0.20f + flow * 0.35f);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(pos)));
            _sphereMesh.Draw();
        }

        GL.DepthMask(1);
        GL.Disable(GL.BLEND);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uEmissive, 0f);
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
