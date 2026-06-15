using MakarovPhysicsSandbox.Physics;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using MakarovPhysicsSandbox.Campaign;
using MakarovPhysicsSandbox.Core;
using MakarovPhysicsSandbox.Material;

namespace MakarovPhysicsSandbox;

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
    public bool ShowTriggerWiring => _showTriggerWiring;
    public bool HasActiveChallenge => _challengeKind != ChallengeKind.None;
    public string CurrentChallengeTitle => _challengeTitle;
    public string CurrentChallengeGoal => _challengeGoal;
    public string CurrentChallengeMessage => _challengeMessage;
    public string CurrentScenarioTitle => _challengeKind != ChallengeKind.None
        ? _challengeTitle
        : _levelIndex >= 0 ? LevelTitle(_levelIndex) : string.Empty;
    public string CurrentScenarioGoal => _challengeKind != ChallengeKind.None
        ? _challengeGoal
        : _levelIndex >= 0 ? LevelGoal(_levelIndex) : string.Empty;
    public bool IsVerticalSliceLoaded => _verticalSliceLoaded;
    public bool IsVerticalSliceRunning => _verticalSliceRunning;
    public bool IsVerticalSliceFinished => _verticalSliceFinished;
    public string VerticalSliceResultTitle => _verticalSliceResultTitle;
    public string VerticalSliceResultDetail => _verticalSliceResultDetail;
    public int VerticalSliceStars => _verticalSliceStars;
    public string VerticalSliceScoreText => _challengeKind == ChallengeKind.AndroidCrashTest
        ? $"Damage {_challengeScore}% · Time {_challengeTimer:0.0}s"
        : string.Empty;
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
    private Mesh _cubeMesh = null!, _sphereMesh = null!, _capsuleMesh = null!, _cylinderMesh = null!, _planeMesh = null!, _waterMesh = null!, _quadMesh = null!;
    private uint _texFloor, _texCrate, _texStripes, _texMetal, _texConcrete, _texBarrel, _texAndroid, _texVehicle, _texTire, _texGlass, _texSky, _texBall, _texBowlingPin, _texBrick, _texCartWood, _texRustyMetal, _texBeachBall, _texMetalCube, _texGasCylinder, _texSoftParticle;
    private uint _bumpCrate, _bumpBrick, _bumpCartWood, _bumpRustyMetal, _bumpBall, _bumpBowlingPin, _bumpGlass, _bumpVehicle, _bumpTire, _bumpBarrel, _bumpBeachBall, _bumpMetalCube, _bumpGasCylinder;
    private uint _shadowFbo, _shadowTex;
    private const int ShadowSize = 2048;

    private int _uModel, _uView, _uProj, _uLightVP, _uColor, _uLightDir, _uCamPos, _uShadowMap, _uAlbedo, _uBumpMap, _uUseBumpMap, _uUvScale, _uWorldUv, _uAlpha, _uEmissive, _uBumpStrength, _uTime, _uWaterWaveAmp, _uRippleCount, _uRipples;
    private readonly float[] _rippleBuffer = new float[WaterVolume.MAX_RIPPLES * 4];
    private int _dModel, _dLightVP;

    // ---- camera (orbit) ----
    private float _camYaw = 0.6f, _camPitch = 0.42f, _camDist = 16f;
    private readonly Vector3 _camTarget = new(0, 2, 0);
    private Vector3 _camPos;
    private Matrix4x4 _view, _proj;
    private uint _skyProgram;
    private int _uSkyModel, _uSkyView, _uSkyProj, _uSkyCamPos, _uSkyTime;
    private uint _particleProgram;
    private int _uPModel, _uPView, _uPProj, _uPColor, _uPAlpha;

    // ---- input state ----
    private bool _rmbDown;
    private int _lastMouseX, _lastMouseY;
    private float _dragPlaneDist;

    // ---- sandbox state ----
    private bool _paused;
    private bool _slowMo;
    // Cinematic payoff on big blasts: a brief auto slow-mo that eases back to normal speed,
    // plus a camera zoom-punch and decaying shake. Purely visual; never blocks input.
    private float _cinematicTime;      // remaining real-time seconds
    private float _cinematicDuration;  // total, for easing
    private float _cinematicShake;     // current shake intensity
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
    // Rotating a jointed assembly (bridge, vehicle, cart) must move the whole group rigidly,
    // including world-anchored joint points, or the joints tear it apart. Captured at drag start.
    private readonly List<RigidBody> _rotGroup = new();
    private Vector3[] _rotStartPos = Array.Empty<Vector3>();
    private Quaternion[] _rotStartRot = Array.Empty<Quaternion>();
    private readonly List<Joint> _rotAnchorJoints = new();
    private Vector3[] _rotAnchorStart = Array.Empty<Vector3>();
    private float _toolLastScaleFactor = 1f;
    private Vector3 _aimPoint;
    private bool _aimValid;
    private const float MuzzleSpeed = 26f;
    private static readonly Vector3 DefaultGravity = new(0, -9.81f, 0);
    private readonly List<Particle> _particles = new(1024);
    private readonly List<Beam> _beams = new(128);
    private const int MaxParticles = 1200;
    private const int MaxBeams = 96;
    private float _impactFlash;

    // ---- ragdolls (the 3D People Playground "toy"; see Ragdoll.cs) ----
    private readonly RagdollSystem _ragdolls = new();

    // ---- fire / heat (first M1 interacting system; see Heat.cs) ----
    private readonly HeatSystem _heat = new();

    // ---- electricity (M1 interacting system) ----
    private readonly ElectricitySystem _electricity = new();

    // ---- lightweight feedback sounds / water-entry tracking ----
    private bool _soundEnabled = true;
    private double _nextImpactSound, _nextSplashSound, _nextExplosionSound;
    private double _nextBreakSound, _nextZapSound, _nextFireSound;
    private readonly Dictionary<RigidBody, bool> _waterTouchState = new();

    // ---- interactive triggers / pressure plates ----
    private readonly List<SceneTrigger> _triggers = new();
    private readonly List<ScheduledTriggerOutput> _scheduledTriggerOutputs = new();
    private bool _showTriggerWiring = true;

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

    // ---- M3 vertical slice state ----
    private bool _verticalSliceLoaded;
    private bool _verticalSliceRunning;
    private bool _verticalSliceFinished;
    private string _verticalSliceResultTitle = "";
    private string _verticalSliceResultDetail = "";
    private int _verticalSliceStars;

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
    // World-space texture tiling for static box surfaces: tiles per world unit (1/tile-size).
    // Brick courses read naturally at ~2 units per tile; tweak if bricks look too big/small.
    private const float WallBrickDensity = 0.45f;
    private const float StaticSurfaceDensity = 0.5f;

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
        if (disposing) Audio.Shutdown();
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
    public void SpawnRagdoll()  { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Ragdoll); Focus(); } }
    public void SpawnAndroid()  { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Android); Focus(); } }
    public void SpawnVehicle()  { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Vehicle); Focus(); } }
    public void SpawnPoliceVehicle() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.PoliceVehicle); Focus(); } }
    public void SpawnAmbulance() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Ambulance); Focus(); } }
    public void SpawnDroneTarget() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.DroneTarget); Focus(); } }
    public void SpawnBridgeSpan() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.BridgeSpan); Focus(); } }
    public void SpawnCatapultLauncher() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.CatapultLauncher); Focus(); } }
    public void SpawnWoodenCart() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.WoodenCart); Focus(); } }
    public void SpawnGlassBlock() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.GlassBlock); Focus(); } }
    public void SpawnWreckingBallTarget() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.WreckingBallTarget); Focus(); } }
    public void SpawnExplosiveBarrel() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.ExplosiveBarrel); Focus(); } }
    public void SpawnCylinder() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Cylinder); Focus(); } }
    public void SpawnBeachBall() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.BeachBall); Focus(); } }
    public void SpawnMetalCube() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.MetalCube); Focus(); } }
    public void SpawnGasCylinder() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.GasCylinder); Focus(); } }
    public void SpawnSentinelBot() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.SentinelBot); Focus(); } }
    public void Ignite()        { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Ignite); Focus(); } }
    public void Electrify()     { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Electrify); Focus(); } }
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

    public void ToggleTriggerWiring()
    {
        _showTriggerWiring = !_showTriggerWiring;
        StatusUpdated?.Invoke(_showTriggerWiring ? "Trigger wiring shown." : "Trigger wiring hidden. Selected trigger wiring remains visible.");
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
        b.MaterialId = props.MaterialId;
        b.Density = Math.Clamp(props.Density, 0.001f, 100f);
        b.Breakable = props.Breakable;
        b.BreakThreshold = Math.Clamp(props.BreakThreshold, 1f, 50f);
        b.Flammability = Math.Clamp(props.Flammability, 0f, 1.5f);
        b.Conductivity = Math.Clamp(props.Conductivity, 0f, 1.5f);
        b.ExplosivePower = Math.Clamp(props.ExplosivePower, 0f, 5f);
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
        foreach (var output in _selectedTrigger.Outputs)
        {
            copy.Outputs.Add(new TriggerOutput
            {
                TargetId = output.TargetId,
                TargetName = output.TargetName,
                Action = output.Action,
                Delay = output.Delay,
                Radius = output.Radius,
                Strength = output.Strength,
                Enabled = output.Enabled,
            });
        }
        _triggers.Add(copy);
        SelectTrigger(copy);
        NotifyStateChanged();
        Focus();
    }

    public void RemoveSelectedTriggerOutput(int index)
    {
        if (_selectedTrigger == null) return;
        if (index < 0 || index >= _selectedTrigger.Outputs.Count)
        {
            StatusUpdated?.Invoke("Select an output in the trigger panel first.");
            return;
        }
        var removed = _selectedTrigger.Outputs[index];
        _selectedTrigger.Outputs.RemoveAt(index);
        _selectedTrigger.Pulse = 0.45f;
        NotifyTriggerSelectionChanged();
        NotifyStateChanged();
        StatusUpdated?.Invoke($"Removed trigger output: {removed.Action} -> {(string.IsNullOrWhiteSpace(removed.TargetName) ? removed.TargetId : removed.TargetName)}.");
        Focus();
    }

    public void ClearSelectedTriggerOutputs()
    {
        if (_selectedTrigger == null) return;
        int count = _selectedTrigger.Outputs.Count;
        _selectedTrigger.Outputs.Clear();
        _selectedTrigger.Pulse = 0.45f;
        NotifyTriggerSelectionChanged();
        NotifyStateChanged();
        StatusUpdated?.Invoke(count > 0 ? $"Cleared {count} trigger output(s)." : "Trigger already has no graph outputs.");
        Focus();
    }

    public void TestSelectedTriggerOutput(int index)
    {
        if (_selectedTrigger == null) return;
        if (index < 0 || index >= _selectedTrigger.Outputs.Count)
        {
            StatusUpdated?.Invoke("Select an output in the trigger panel first.");
            return;
        }
        var tr = _selectedTrigger;
        var output = tr.Outputs[index];
        if (!output.Enabled)
        {
            StatusUpdated?.Invoke("Selected trigger output is disabled.");
            return;
        }
        var target = ResolveOutputTargetPosition(output, tr.TargetPosition);
        ExecuteTriggerOutput(tr.Name, output, target);
        tr.Pulse = 1.0f;
        NotifyStateChanged();
        Focus();
    }

    public void UpdateSelectedTriggerOutput(int index, TriggerActionKind action, float delay, float radius, float strength, bool enabled)
    {
        if (_selectedTrigger == null) return;
        if (index < 0 || index >= _selectedTrigger.Outputs.Count)
        {
            StatusUpdated?.Invoke("Select an output in the trigger panel first.");
            return;
        }
        var output = _selectedTrigger.Outputs[index];
        output.Action = action;
        output.Delay = Math.Clamp(delay, 0f, 30f);
        output.Radius = Math.Clamp(radius, 0.5f, 40f);
        output.Strength = Math.Clamp(strength, 0.1f, 80f);
        output.Enabled = enabled;
        if (!string.IsNullOrWhiteSpace(output.TargetId))
        {
            var wanted = MechanismKindForTriggerAction(action);
            if (wanted.HasValue && TryGetMechanismKindById(output.TargetId, out var actual) && actual != wanted.Value)
                StatusUpdated?.Invoke($"Output updated, but {action} expects {wanted.Value}; current target is {actual}.");
            else
                StatusUpdated?.Invoke($"Output updated: {action} -> {(string.IsNullOrWhiteSpace(output.TargetName) ? output.TargetId : output.TargetName)}.");
        }
        else
        {
            StatusUpdated?.Invoke($"Legacy output updated: {action}.");
        }
        _selectedTrigger.Pulse = 0.45f;
        NotifyTriggerSelectionChanged();
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

        // Combined time scale: manual slow-mo and the cinematic ease-back.
        float simScale = _slowMo ? 0.2f : 1f;
        if (_cinematicTime > 0f && !_paused)
        {
            float t = 1f - _cinematicTime / MathF.Max(_cinematicDuration, 1e-3f); // 0 at start -> 1 at end
            simScale *= 0.25f + 0.75f * t * t;                                     // 0.25x slow easing back to 1x
            _cinematicTime -= dt;
            if (_cinematicTime < 0f) _cinematicTime = 0f;
        }

        if (_stepOnce) { _world.Step(PhysicsWorld.FixedStep); _stepOnce = false; }
        else if (!_paused) _world.Step(dt * simScale);
        LockWheelAxes();

        float simDt = _paused ? 0f : dt * simScale;
        UpdateMechanisms(simDt);
        UpdateTriggers(simDt);
        UpdateTriggerOutputs(simDt);
        SpawnEffectsFromWorld();
        SpawnMaterialBreakEffects();
        _ragdolls.Update(simDt, _world);
        _heat.Update(simDt, _world, _ragdolls);
        _electricity.Update(simDt, _world, _ragdolls);
        UpdateMaterialReactions(simDt);
        UpdateDroneHover(simDt);
        SpawnFireEffects(simDt);
        SpawnElectricityEffects(simDt);
        SpawnAndroidDamageEffects(simDt);
        SpawnSteamAndWetEffects(simDt);
        SpawnAmbientSceneEffects(simDt);
        UpdateParticles(simDt);
        UpdateBeams(simDt);
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
                    bool bodyTool = _pendingSceneAction is PendingSceneActionKind.Connect or PendingSceneActionKind.Spring or PendingSceneActionKind.Disconnect or PendingSceneActionKind.Ignite or PendingSceneActionKind.Electrify;
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
            case 0x30: CancelPendingSceneAction(); SpawnAndroidAtAim(); break;        // 0 android dummy
            case 0x49: ArmSceneAction(PendingSceneActionKind.Ignite); break;          // I ignite (click a body)
            case 0x44: ArmSceneAction(PendingSceneActionKind.Electrify); break;       // D electrify (click a body)
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
        // Optional direction-based sky program. If it fails to build, _skyProgram stays 0 and
        // DrawSkybox falls back to the textured cube, so a GLSL issue can never break the renderer.
        try
        {
            _skyProgram = Shaders.Build(Shaders.SkyVertex, Shaders.SkyFragment);
            _uSkyModel = GL.GetUniformLocation(_skyProgram, "uModel");
            _uSkyView = GL.GetUniformLocation(_skyProgram, "uView");
            _uSkyProj = GL.GetUniformLocation(_skyProgram, "uProj");
            _uSkyCamPos = GL.GetUniformLocation(_skyProgram, "uCamPos");
            _uSkyTime = GL.GetUniformLocation(_skyProgram, "uTime");
        }
        catch { _skyProgram = 0; }

        // Billboard particle program (fire/smoke). Falls back to sphere particles if it fails to build.
        try
        {
            _particleProgram = Shaders.Build(Shaders.ParticleVertex, Shaders.ParticleFragment);
            _uPModel = GL.GetUniformLocation(_particleProgram, "uModel");
            _uPView = GL.GetUniformLocation(_particleProgram, "uView");
            _uPProj = GL.GetUniformLocation(_particleProgram, "uProj");
            _uPColor = GL.GetUniformLocation(_particleProgram, "uColor");
            _uPAlpha = GL.GetUniformLocation(_particleProgram, "uAlpha");
        }
        catch { _particleProgram = 0; }
        _depthProgram = Shaders.Build(Shaders.DepthVertex, Shaders.DepthFragment);

        _uModel = GL.GetUniformLocation(_mainProgram, "uModel");
        _uView = GL.GetUniformLocation(_mainProgram, "uView");
        _uProj = GL.GetUniformLocation(_mainProgram, "uProj");
        _uLightVP = GL.GetUniformLocation(_mainProgram, "uLightVP");
        _uColor = GL.GetUniformLocation(_mainProgram, "uColor");
        _uLightDir = GL.GetUniformLocation(_mainProgram, "uLightDir");
        _uCamPos = GL.GetUniformLocation(_mainProgram, "uCamPos");
        _uAlbedo = GL.GetUniformLocation(_mainProgram, "uAlbedo");
        _uBumpMap = GL.GetUniformLocation(_mainProgram, "uBumpMap");
        _uUseBumpMap = GL.GetUniformLocation(_mainProgram, "uUseBumpMap");
        _uUvScale = GL.GetUniformLocation(_mainProgram, "uUvScale");
        _uWorldUv = GL.GetUniformLocation(_mainProgram, "uWorldUv");
        _uAlpha = GL.GetUniformLocation(_mainProgram, "uAlpha");
        _uEmissive = GL.GetUniformLocation(_mainProgram, "uEmissive");
        _uBumpStrength = GL.GetUniformLocation(_mainProgram, "uBumpStrength");
        _uTime = GL.GetUniformLocation(_mainProgram, "uTime");
        _uWaterWaveAmp = GL.GetUniformLocation(_mainProgram, "uWaterWaveAmp");
        _uRippleCount = GL.GetUniformLocation(_mainProgram, "uRippleCount");
        _uRipples = GL.GetUniformLocation(_mainProgram, "uRipples");
        _uShadowMap = GL.GetUniformLocation(_mainProgram, "uShadowMap");

        _dModel = GL.GetUniformLocation(_depthProgram, "uModel");
        _dLightVP = GL.GetUniformLocation(_depthProgram, "uLightVP");

        _texFloor = Textures.CreateCheckerFloor();
        _texCrate = Textures.WoodCrateAlbedo();
        _texStripes = Textures.CreateStripes();
        _texMetal = Textures.CreateMetal();
        _texSoftParticle = Textures.SoftParticle();
        _texConcrete = Textures.CreateConcrete();
        _texBarrel = Textures.LoadOrCreate("barrel_albedo.png", () => Textures.CreateBarrel());
        _texAndroid = Textures.CreateAndroidPanel();
        _texVehicle = Textures.VehiclePaintAlbedo();
        _texTire = Textures.TireAlbedo();
        _texGlass = Textures.GlassAlbedo();
        _texSky = Textures.LoadOrCreate("skybox_clouds.png", () => Textures.CreateSkybox());
        _texBall = Textures.BallAlbedo();
        _texBowlingPin = Textures.BowlingPinAlbedo();
        _texBrick = Textures.BrickWallAlbedo();
        _texCartWood = Textures.CartWoodAlbedo();
        _texRustyMetal = Textures.RustyMetalAlbedo();
        _texBeachBall = Textures.LoadOrCreate("beach_ball_albedo.png", () => Textures.CreateBall());
        _texMetalCube = Textures.LoadOrCreate("metal_cube_albedo.png", () => Textures.CreateMetal());
        _texGasCylinder = Textures.LoadOrCreate("gas_cylinder_albedo.png", () => Textures.CreateMetal());
        _bumpCrate = Textures.WoodCrateBump();
        _bumpBrick = Textures.BrickWallBump();
        _bumpCartWood = Textures.CartWoodBump();
        _bumpRustyMetal = Textures.RustyMetalBump();
        _bumpBall = Textures.BallBump();
        _bumpBowlingPin = Textures.BowlingPinBump();
        _bumpGlass = Textures.GlassBump();
        _bumpVehicle = Textures.VehiclePaintBump();
        _bumpTire = Textures.TireBump();
        _bumpBarrel = Textures.LoadOrCreate("barrel_bump.png", () => Textures.CreateBarrel());
        _bumpBeachBall = Textures.LoadOrCreate("beach_ball_bump.png", () => Textures.CreateBall());
        _bumpMetalCube = Textures.LoadOrCreate("metal_cube_bump.png", () => Textures.CreateMetal());
        _bumpGasCylinder = Textures.LoadOrCreate("gas_cylinder_bump.png", () => Textures.CreateMetal());

        _cubeMesh = Mesh.CreateCube();
        _sphereMesh = Mesh.CreateSphere();
        _capsuleMesh = Mesh.CreateCapsule();
        _cylinderMesh = Mesh.CreateCylinder();
        _planeMesh = Mesh.CreatePlane();
        _waterMesh = Mesh.CreateGridPlane(64);
        _quadMesh = Mesh.CreateBillboardQuad();

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
        _ragdolls.Clear();
        _heat.Clear();
        _electricity.Clear();
        _waterTouchState.Clear();
        _triggers.Clear();
        _scheduledTriggerOutputs.Clear();
        ClearMechanisms();
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
        _ragdolls.Clear();
        _heat.Clear();
        _electricity.Clear();
        _triggers.Clear();
        _scheduledTriggerOutputs.Clear();
        ClearMechanisms();
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
            case "Bridge Jump":
                BuildBridgeJump();
                break;
            case "Catapult Bridge Siege":
                BuildCatapultBridgeSiege();
                break;
            case "Drone Target Range":
                BuildDroneTargetRange();
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
            case "Android Fire Lab":
                BuildAndroidFireLab();
                break;
            case "Electrical Chain Lab":
                BuildElectricalChainLab();
                break;
            case "Vehicle Crash Test":
                BuildVehicleCrashTest();
                break;
            case "Mechanism Chain Reaction":
                BuildMechanismChainReaction();
                break;
            case "Android Stress Chamber":
                BuildAndroidStressChamber();
                break;
            case "Android Crash Test Chamber":
                BuildAndroidCrashTestChamber();
                break;
            case "Motor Gate Timer Lab":
                BuildMotorGateTimerLab();
                break;
            case "Conveyor Chain Lab":
                BuildConveyorChainLab();
                break;
            case "Piston Crusher Lab":
                BuildPistonCrusherLab();
                break;
            case "Explosive Domino":
                BuildExplosiveDomino();
                break;
            case "Barrel Pyramid":
                BuildBarrelPyramid();
                break;
            case "Electric Floor Trap":
                BuildElectricFloorTrap();
                break;
            case "Burning Barricade":
                BuildBurningBarricade();
                break;
            case "Wrecking Ball":
                BuildWreckingBall();
                break;
            case "Ragdoll Bowling":
                BuildRagdollBowling();
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

    public void LoadVerticalSlice()
    {
        if (!_initialized) return;
        BuildAndroidCrashTestChamber();
        _verticalSliceLoaded = true;
        _verticalSliceRunning = false;
        _verticalSliceFinished = false;
        _verticalSliceResultTitle = "";
        _verticalSliceResultDetail = "";
        _verticalSliceStars = 0;
        _paused = true;
        StatusUpdated?.Invoke("Vertical slice loaded: Android Crash Test Chamber. Press F8 / Start Test.");
        NotifyStateChanged();
        Focus();
    }

    public void StartVerticalSliceTest()
    {
        if (!_initialized) return;
        if (!_verticalSliceLoaded || _challengeKind != ChallengeKind.AndroidCrashTest)
            LoadVerticalSlice();

        _verticalSliceRunning = true;
        _verticalSliceFinished = false;
        _verticalSliceResultTitle = "";
        _verticalSliceResultDetail = "";
        _verticalSliceStars = 0;
        _challengeTimer = 0f;
        _challengeSuccess = false;
        _challengeFailed = false;
        _challengeMessage = "Start the chain reaction and damage the android target.";
        _paused = false;
        _slowMo = false;

        var trigger = _triggers.FirstOrDefault(t => t.Name.Contains("start", StringComparison.OrdinalIgnoreCase));
        if (trigger != null)
        {
            trigger.Enabled = true;
            trigger.OneShot = true;
            trigger.Cooldown = 0f;
            trigger.WasPressed = false;
            FireTrigger(trigger);
        }

        StatusUpdated?.Invoke("Test started: follow the chain reaction to the crash-test payoff.");
        NotifyStateChanged();
        Focus();
    }

    public void RetryVerticalSlice()
    {
        LoadVerticalSlice();
        StartVerticalSliceTest();
    }

    public void DismissVerticalSliceResult()
    {
        _verticalSliceFinished = false;
        NotifyStateChanged();
        Focus();
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
        _verticalSliceLoaded = false;
        _verticalSliceRunning = false;
        _verticalSliceFinished = false;
        _verticalSliceResultTitle = "";
        _verticalSliceResultDetail = "";
        _verticalSliceStars = 0;
    }

    private void CompleteChallenge(string message)
    {
        if (_challengeSuccess || _challengeFailed) return;
        _challengeSuccess = true;
        _challengeMessage = "SUCCESS: " + message;
        StatusUpdated?.Invoke($"Challenge complete: {_challengeTitle} — {message}");
        NotifyStateChanged();

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
        NotifyStateChanged();
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

            case ChallengeKind.AndroidCrashTest:
                UpdateAndroidCrashTestChallenge();
                break;
        }
    }

    private void UpdateAndroidCrashTestChallenge()
    {
        int damage = AndroidDamagePercent();
        _challengeScore = damage;
        int severed = _ragdolls.All.SelectMany(r => r.Bones).Count(b => b.Severed || b.Health <= 0f);
        _challengeMessage = $"Start the chain reaction. Damage target android: {damage}% / 65%. Severed/broken parts: {severed}.";

        if (!_verticalSliceRunning) return;
        if (damage >= 65 || severed >= 2)
        {
            int stars = damage >= 85 && _challengeTimer <= 18f ? 3 : damage >= 75 ? 2 : 1;
            FinishVerticalSlice(true, stars, $"Target damaged: {damage}%. Time: {_challengeTimer:0.0}s. Broken parts: {severed}.");
            CompleteChallenge("crash-test target destroyed");
        }
        else if (_challengeTimer >= 45f)
        {
            FinishVerticalSlice(false, 0, $"Only {damage}% damage after 45 seconds. Rework the chain or retry.");
            FailChallenge("target survived the test window");
        }
    }

    private int AndroidDamagePercent()
    {
        var bones = _ragdolls.All.SelectMany(r => r.Bones).Where(b => b.Android).ToList();
        if (bones.Count == 0) return 0;
        float rawDamage = bones.Average(b => 1f - b.HealthFrac);
        float severBonus = bones.Count(b => b.Severed || b.Health <= 0f) / (float)bones.Count * 0.35f;
        return (int)Math.Clamp((rawDamage + severBonus) * 100f, 0f, 100f);
    }

    private void FinishVerticalSlice(bool success, int stars, string detail)
    {
        if (_verticalSliceFinished) return;
        _verticalSliceFinished = true;
        _verticalSliceRunning = false;
        _verticalSliceStars = Math.Clamp(stars, 0, 3);
        _verticalSliceResultTitle = success ? "TEST COMPLETE" : "TEST FAILED";
        _verticalSliceResultDetail = detail;
        if (success)
        {
            _slowMo = true;
            foreach (var rag in _ragdolls.All)
                foreach (var bone in rag.Bones)
                    bone.Body.Wake();
        }
        NotifyStateChanged();
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
                        var b = MakeBreakable(RigidBody.CreateBox(new Vector3(x * 0.55f, 0.28f + y * 0.56f, z * 0.55f), new Vector3(0.25f), density: 1.0f), threshold: 4.8f);
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
        // Wood is now mostly texture-driven; random candy colours made crates look like toy blocks.
        body.Color = body.MaterialId == MaterialId.Wood ? Vector3.One : color;
        _world.Bodies.Add(body);
    }

    private static RigidBody MakeBreakable(RigidBody body, float threshold = 6.5f, int pieces = 8)
    {
        body.Breakable = true;
        body.BreakThreshold = threshold;
        body.BreakPieces = pieces;
        return body;
    }

    private static RigidBody WithMaterial(RigidBody body, MaterialId material, bool overwriteColor = true)
    {
        Materials.Get(material).ApplyTo(body, overwriteColor);
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
                        var b = MakeBreakable(RigidBody.CreateBox(new Vector3(x * 0.55f, 0.28f + y * 0.56f, z * 0.55f), new Vector3(0.25f, 0.25f, 0.25f), density: 1.1f), threshold: 4.8f);
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
        AddBridgeSpan(Vector3.Zero, length: 7.2f, width: 2.1f, plankCount: 9, withCargo: true);
        StatusUpdated?.Invoke("Preset: Bridge Test — jointed wooden span with cargo loads.");
    }

    private List<RigidBody> AddBridgeSpan(Vector3 center, float length = 7.2f, float width = 2.1f, int plankCount = 9, bool withCargo = false)
    {
        var planks = new List<RigidBody>();
        float halfLength = length * 0.5f;
        float supportX = halfLength + 0.55f;
        float plankStep = length / Math.Max(1, plankCount - 1);
        float plankHalfX = plankStep * 0.45f;
        float deckY = center.Y + 1.15f;
        float supportY = center.Y + 0.45f;
        float zEdge = width * 0.42f;

        var left = RigidBody.CreateStaticBox(center + new Vector3(-supportX, supportY, 0f), new Vector3(0.55f, 0.45f, width * 0.72f));
        left.Color = new Vector3(0.25f, 0.27f, 0.30f);
        _world.Bodies.Add(left);
        var right = RigidBody.CreateStaticBox(center + new Vector3(supportX, supportY, 0f), new Vector3(0.55f, 0.45f, width * 0.72f));
        right.Color = new Vector3(0.25f, 0.27f, 0.30f);
        _world.Bodies.Add(right);

        for (int i = 0; i < plankCount; i++)
        {
            float x = -halfLength + i * plankStep;
            var p = WithMaterial(RigidBody.CreateBox(center + new Vector3(x, deckY, 0f), new Vector3(plankHalfX, 0.09f, width * 0.50f), density: 0.72f), MaterialId.Wood);
            p.Friction = 0.85f;
            p.Restitution = 0.05f;
            p.Breakable = true;
            p.BreakThreshold = 8.0f;
            AddBody(p, new Vector3(0.55f, 0.34f, 0.17f));
            planks.Add(p);
        }

        foreach (float z in new[] { -zEdge, zEdge })
        {
            _world.Joints.Add(MakePointJoint(planks[0], null, new Vector3(-plankHalfX, 0f, z), center + new Vector3(-halfLength - plankHalfX * 0.35f, deckY, z)));
            _world.Joints.Add(MakePointJoint(planks[^1], null, new Vector3(plankHalfX, 0f, z), center + new Vector3(halfLength + plankHalfX * 0.35f, deckY, z)));
        }

        for (int i = 0; i < planks.Count - 1; i++)
            foreach (float z in new[] { -zEdge, zEdge })
                _world.Joints.Add(MakePointJoint(planks[i], planks[i + 1], new Vector3(plankHalfX, 0f, z), new Vector3(-plankHalfX, 0f, z)));

        if (withCargo)
        {
            for (int i = 0; i < 5; i++)
            {
                var load = RigidBody.CreateSphere(center + new Vector3(-2.0f + i * 1.0f, deckY + 1.65f + i * 0.22f, 0f), 0.35f, density: 2.3f);
                AddBody(load, new Vector3(0.25f, 0.45f, 0.85f));
            }
        }

        return planks;
    }

    private void BuildBridgeJump()
    {
        ResetToEmptyScene();
        AddBridgeSpan(Vector3.Zero, length: 8.2f, width: 2.2f, plankCount: 11, withCargo: false);

        var rig = MakeVehicle(new Vector3(-7.8f, 1.35f, 0f), 0.85f);
        rig.Bodies[0].Velocity = new Vector3(7.5f, 0.0f, 0f);
        foreach (var b in rig.Bodies)
        {
            if (b != rig.Bodies[0]) b.Velocity = new Vector3(7.5f, 0.0f, 0f);
            _world.Bodies.Add(b);
        }
        foreach (var j in rig.Joints) _world.Joints.Add(j);

        SpawnDroneTarget(new Vector3(4.4f, 2.2f, 0.0f), new Vector3(0.2f, 0.85f, 1.0f));
        StatusUpdated?.Invoke("Preset: Bridge Jump — vehicle crosses a jointed bridge toward a drone target.");
    }

    private void BuildCatapultBridgeSiege()
    {
        ResetToEmptyScene();
        AddBridgeSpan(new Vector3(1.1f, 0f, 0f), length: 7.5f, width: 2.0f, plankCount: 10, withCargo: false);
        AddCatapultLauncher(new Vector3(-5.8f, 0f, 0f), new Vector3(8.4f, 4.8f, 0.0f));

        for (int i = 0; i < 4; i++)
        {
            var block = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(2.8f + i * 0.35f, 2.05f + i * 0.10f, 0.35f), new Vector3(0.24f), density: 0.9f), threshold: 4.0f), MaterialId.Stone);
            AddBody(block, new Vector3(0.72f, 0.32f, 0.25f));
        }
        SpawnDroneTarget(new Vector3(3.3f, 2.35f, -0.55f), new Vector3(0.25f, 0.90f, 1.0f));
        StatusUpdated?.Invoke("Preset: Catapult Bridge Siege — catapult shot hits a small bridge-side target setup.");
    }

    private void BuildDroneTargetRange()
    {
        ResetToEmptyScene();
        AddCatapultLauncher(new Vector3(-5.2f, 0f, 0f), new Vector3(8.2f, 4.4f, 0.0f));
        SpawnDroneTarget(new Vector3(3.2f, 2.6f, -0.7f), new Vector3(0.25f, 0.85f, 1.0f));
        SpawnDroneTarget(new Vector3(4.0f, 2.2f, 0.3f), new Vector3(1.0f, 0.76f, 0.28f));
        SpawnDroneTarget(new Vector3(4.8f, 2.9f, 0.9f), new Vector3(0.8f, 0.35f, 1.0f));
        for (int i = 0; i < 6; i++)
        {
            var block = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(4.3f, 0.28f + i * 0.55f, -1.4f), new Vector3(0.25f), density: 0.8f), threshold: 3.8f), MaterialId.Wood);
            AddBody(block, new Vector3(0.55f, 0.32f, 0.16f));
        }
        StatusUpdated?.Invoke("Preset: Drone Target Range — catapult launcher and several synthetic drone targets.");
    }

    private void BuildCatapult()
    {
        ResetToEmptyScene();
        AddCatapultLauncher(new Vector3(-2.6f, 0f, 0f), new Vector3(7.0f, 3.8f, 0f));

        for (int y = 0; y < 4; y++)
        for (int z = -2; z <= 2; z++)
        {
            var block = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(5.2f, 0.26f + y * 0.52f, z * 0.52f), new Vector3(0.24f), density: 0.9f), threshold: 4.4f), MaterialId.Stone);
            AddBody(block, new Vector3(0.75f, 0.32f, 0.24f));
        }

        StatusUpdated?.Invoke("Preset: Catapult — stable launch demo: projectile arcs into the breakable wall.");
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

    private void BuildAndroidFireLab()
    {
        // Dry fire demo. Water is intentionally off here; the old version flooded the arena,
        // so androids sank while crates floated and the point of the preset was unreadable.
        ResetToEmptyScene(water: false);
        _ragdolls.SpawnAndroid(_world, new Vector3(1.8f, 0f, -0.45f));
        _ragdolls.SpawnAndroid(_world, new Vector3(2.3f, 0f, 0.65f));

        for (int i = 0; i < 6; i++)
        {
            var crate = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(-4.5f + i * 0.55f, 0.45f, -2.2f), new Vector3(0.25f), density: 0.65f), threshold: 4.5f), MaterialId.Wood);
            crate.Flammability = 1.25f;
            AddBody(crate, crate.Color);
        }

        var hotCore = WithMaterial(RigidBody.CreateSphere(new Vector3(-3.8f, 1.0f, -2.2f), 0.32f, density: 1.0f), MaterialId.Explosive);
        hotCore.Flammability = 1.4f;
        hotCore.ExplosivePower = 0.35f; // low-yield hot starter core, not a full bomb
        AddBody(hotCore, hotCore.Color);
        _heat.Ignite(hotCore);

        StatusUpdated?.Invoke("Preset: Android Fire Lab — dry demo: fire spreads through crates toward standing android targets.");
    }

    private void BuildElectricalChainLab()
    {
        // Dry electrical demo. The previous flooded version mostly demonstrated buoyancy, not electricity.
        ResetToEmptyScene(water: false);
        _ragdolls.SpawnAndroid(_world, new Vector3(3.0f, 0f, 0f));

        RigidBody? first = null;
        for (int i = 0; i < 9; i++)
        {
            var node = WithMaterial(RigidBody.CreateSphere(new Vector3(-5.5f + i * 0.9f, 1.15f, 0f), 0.22f, density: 3.2f), MaterialId.Metal);
            node.Friction = 0.35f;
            AddBody(node, node.Color);
            first ??= node;
        }

        for (int i = 0; i < 4; i++)
        {
            var wetCrate = WithMaterial(RigidBody.CreateBox(new Vector3(-1.2f + i * 0.55f, 2.8f, -1.3f), new Vector3(0.22f), density: 0.55f), MaterialId.Wood);
            wetCrate.Wetness = 0.85f;
            AddBody(wetCrate, new Vector3(0.45f, 0.65f, 0.92f));
        }

        if (first != null) _electricity.Electrify(first, 1.4f);
        StatusUpdated?.Invoke("Preset: Electrical Chain Lab — charged metal nodes arc toward the android; wet crates are conductive pickups, not a flooded pool.");
    }

    private void BuildVehicleCrashTest()
    {
        ResetToEmptyScene();

        var ramp = RigidBody.CreateStaticBox(new Vector3(-5.2f, 1.2f, 0f), new Vector3(2.7f, 0.12f, 0.9f));
        ramp.Rotation = Quaternion.CreateFromYawPitchRoll(0f, 0f, -0.23f);
        ramp.UpdateDerived();
        ramp.Color = new Vector3(0.38f, 0.40f, 0.45f);
        _world.Bodies.Add(ramp);

        var rig = MakeVehicle(new Vector3(-6.7f, 2.4f, 0f), 1.0f);
        foreach (var b in rig.Bodies)
        {
            b.Velocity = new Vector3(5.2f, 0f, 0f);
            _world.Bodies.Add(b);
        }
        foreach (var j in rig.Joints) _world.Joints.Add(j);

        for (int y = 0; y < 4; y++)
        for (int z = -2; z <= 2; z++)
        {
            var block = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(2.2f, 0.28f + y * 0.55f, z * 0.55f), new Vector3(0.25f), density: 1.2f), threshold: 5.2f), MaterialId.Stone);
            AddBody(block, new Vector3(0.60f, 0.54f, 0.48f));
        }

        _ragdolls.SpawnAndroid(_world, new Vector3(4.4f, 0f, 0f));
        StatusUpdated?.Invoke("Preset: Vehicle Crash Test — simple car rig, wheels, crash wall, android target.");
    }

    private void BuildMechanismChainReaction()
    {
        ResetToEmptyScene();

        var ramp = RigidBody.CreateStaticBox(new Vector3(-6.0f, 1.2f, 0f), new Vector3(2.6f, 0.12f, 0.65f));
        ramp.Rotation = Quaternion.CreateFromYawPitchRoll(0f, 0f, -0.20f);
        ramp.UpdateDerived();
        ramp.Color = new Vector3(0.38f, 0.39f, 0.42f);
        _world.Bodies.Add(ramp);

        var ball = WithMaterial(RigidBody.CreateSphere(new Vector3(-7.5f, 2.7f, 0f), 0.34f, density: 3.0f), MaterialId.Metal);
        ball.Velocity = new Vector3(2.5f, 0f, 0f);
        AddBody(ball, new Vector3(0.88f, 0.82f, 0.25f));

        for (int i = 0; i < 14; i++)
        {
            var d = WithMaterial(RigidBody.CreateBox(new Vector3(-3.8f + i * 0.36f, 0.65f, 0f), new Vector3(0.06f, 0.42f, 0.22f), density: 0.55f), MaterialId.Wood);
            d.Friction = 0.65f;
            AddBody(d, new Vector3(0.72f, 0.45f, 0.22f));
        }

        var rig = MakeVehicle(new Vector3(3.2f, 0.9f, 0f), 0.85f);
        foreach (var b in rig.Bodies) _world.Bodies.Add(b);
        foreach (var j in rig.Joints) _world.Joints.Add(j);

        _triggers.Add(new SceneTrigger
        {
            Name = "Launch explosion",
            Position = new Vector3(1.6f, 0.08f, 0f),
            HalfExtents = new Vector3(0.8f, 0.06f, 0.8f),
            Action = TriggerActionKind.Explosion,
            Radius = 4.5f,
            Strength = 9.5f,
            OneShot = true,
        });
        _triggers.Add(new SceneTrigger
        {
            Name = "Wind fan",
            Position = new Vector3(-1.0f, 0.08f, 0f),
            HalfExtents = new Vector3(0.7f, 0.06f, 0.7f),
            Action = TriggerActionKind.Wind,
            Radius = 4.0f,
            Strength = 11f,
            OneShot = false,
        });
        StatusUpdated?.Invoke("Preset: Mechanism Chain Reaction — ball, domino lane, trigger plates, vehicle and explosion.");
    }

    private void BuildAndroidStressChamber()
    {
        // Compact dry stress test. Water stays off; this should read as impact/fire/electricity/explosion, not a pool.
        ResetToEmptyScene(water: false);

        // A compact trailer/debug chamber for the current M1.5 loop:
        // impact + fire + water + electricity + explosive material + android joint tearing.
        _ragdolls.SpawnAndroid(_world, new Vector3(0.0f, 0f, 0f));

        var ram = WithMaterial(RigidBody.CreateSphere(new Vector3(-6.0f, 2.2f, 0f), 0.42f, density: 4.2f), MaterialId.Metal);
        ram.Velocity = new Vector3(8.5f, -0.2f, 0f);
        ram.Restitution = 0.25f;
        AddBody(ram, new Vector3(0.82f, 0.78f, 0.62f));

        var hotCrate = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(-2.7f, 0.45f, -1.5f), new Vector3(0.32f), density: 0.65f), threshold: 4.2f), MaterialId.Wood);
        hotCrate.Flammability = 1.25f;
        AddBody(hotCrate, hotCrate.Color);
        _heat.Ignite(hotCrate);

        var explosive = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(2.2f, 0.45f, -1.1f), new Vector3(0.33f), density: 1.1f), threshold: 2.4f), MaterialId.Explosive);
        explosive.ExplosivePower = 1.25f;
        AddBody(explosive, explosive.Color);

        for (int i = 0; i < 6; i++)
        {
            var conductor = WithMaterial(RigidBody.CreateSphere(new Vector3(-1.5f + i * 0.72f, 0.85f, 1.8f), 0.20f, density: 3.2f), MaterialId.Metal);
            conductor.Friction = 0.35f;
            AddBody(conductor, new Vector3(0.70f, 0.76f, 0.84f));
            if (i == 0) _electricity.Electrify(conductor, 1.5f);
        }

        _triggers.Add(new SceneTrigger
        {
            Name = "Stress blast plate",
            Position = new Vector3(1.4f, 0.08f, 0.0f),
            HalfExtents = new Vector3(0.75f, 0.06f, 0.75f),
            Action = TriggerActionKind.Explosion,
            Radius = 4.0f,
            Strength = 9.0f,
            OneShot = true,
        });

        StatusUpdated?.Invoke("Preset: Android Stress Chamber — impact, fire, water, electricity, explosive material and joint tearing in one room.");
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

        var wallColor = new Vector3(1.0f, 1.0f, 1.0f);
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
            w.Tag = "ArenaWall";
            _world.Bodies.Add(w);
        }
    }

    private void SpawnBody(int kind)
    {
        EvictIfFull();

        float J() => (float)_rng.NextDouble(); // jitter helper

        // Place the new object resting on whatever the user is aiming at (floor or the top of
        // another object) instead of dropping it from high up — dropping made it slam into the
        // ground above its break threshold and shatter on placement.
        float ax = _aimValid ? _aimPoint.X : (float)(_rng.NextDouble() * 4 - 2);
        float az = _aimValid ? _aimPoint.Z : (float)(_rng.NextDouble() * 4 - 2);
        float ay = _aimValid ? _aimPoint.Y : 0f;
        float lim = ArenaHalf - 1f;
        ax = Math.Clamp(ax + (J() - 0.5f), -lim, lim);
        az = Math.Clamp(az + (J() - 0.5f), -lim, lim);
        Vector3 At(float halfY) => new(ax, ay + halfY + 0.04f, az);

        RigidBody body;
        switch (kind)
        {
            case 1: { float r = 0.35f + J() * 0.4f; body = RigidBody.CreateSphere(At(r), r); break; }
            case 3: { float r = 0.28f + J() * 0.18f; body = WithMaterial(RigidBody.CreateCapsule(At(r * 2f), r), MaterialId.Metal); break; }
            case 4: { var h = new Vector3(0.75f + J() * 0.35f, 0.10f + J() * 0.05f, 0.45f + J() * 0.15f); body = WithMaterial(RigidBody.CreateBox(At(h.Y), h), MaterialId.Wood); break; } // plank
            case 5: { var h = new Vector3(0.20f + J() * 0.08f, 0.75f + J() * 0.35f, 0.20f + J() * 0.08f); body = WithMaterial(RigidBody.CreateBox(At(h.Y), h), MaterialId.Wood); break; } // pillar
            case 6: body = MakeDumbbell(At(0.45f), 0.8f + J() * 0.5f); break;
            case 7: body = MakeHammer(At(0.55f), 0.9f + J() * 0.4f); break;
            case 8: body = WithMaterial(MakeTable(At(0.55f), 0.9f + J() * 0.3f), MaterialId.Wood); break;
            default: { var h = new Vector3(0.3f + J() * 0.45f, 0.3f + J() * 0.45f, 0.3f + J() * 0.45f); body = WithMaterial(RigidBody.CreateBox(At(h.Y), h), MaterialId.Wood); break; }
        }

        // Yaw-only variety so flat objects still sit flat where placed (no tumbling onto a corner).
        if (body.Children.Length > 1 || body.Children[0].Shape != ShapeType.Sphere)
        {
            body.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, J() * MathF.Tau);
            body.UpdateDerived();
        }
        if (body.MaterialId == MaterialId.Wood && !body.Breakable)
        {
            body.Breakable = true;
            body.BreakThreshold = 5.0f;   // survives gentle placement, breaks when smashed
            body.BreakPieces = 10;
        }
        body.Color = body.MaterialId == MaterialId.Wood ? Vector3.One : Palette[_rng.Next(Palette.Length)];
        body.Velocity = Vector3.Zero;
        _world.Bodies.Add(body);
    }

    // compound bodies: a couple of multi-shape props to show off CreateCompound

    private static RigidBody MakeDumbbell(Vector3 pos, float k = 1f)
    {
        var b = WithMaterial(RigidBody.CreateCompound(pos, [
            ChildShape.Box(new Vector3(0.40f * k, 0.06f * k, 0.06f * k)),    // thin handle along X
            ChildShape.Sphere(0.27f * k, new Vector3(-0.44f * k, 0, 0)),     // weights overlap the handle ends
            ChildShape.Sphere(0.27f * k, new Vector3( 0.44f * k, 0, 0)),
        ], density: 2.2f), MaterialId.Metal);
        b.Tag = "Dumbbell";
        b.Color = new Vector3(0.82f, 0.84f, 0.86f);
        return b;
    }

    private static RigidBody MakeHammer(Vector3 pos, float k = 1f)
    {
        var b = RigidBody.CreateCompound(pos, [
            ChildShape.Box(new Vector3(0.34f * k, 0.05f * k, 0.05f * k)),                       // handle along X
            // head sits at the end of the handle and overlaps it, so they read as one tool
            // (previously the capsule handle ended short of the head, leaving a visible gap).
            ChildShape.Box(new Vector3(0.12f * k, 0.17f * k, 0.26f * k), new Vector3(0.36f * k, 0, 0)),
        ], density: 2.6f);
        b.MaterialId = MaterialId.Wood;
        b.Tag = "Hammer";
        b.Breakable = false;   // a tool, not a breakable prop: it should survive falls intact
        b.Color = Vector3.One;
        return b;
    }

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

    /// <summary>Drop a legacy humanoid ragdoll at the aim point (kept for tuning/dev use).</summary>
    private void SpawnRagdollAtAim()
    {
        EvictIfFull();
        var foot = _aimValid ? _aimPoint : new Vector3(0f, 0f, 0f);
        _ragdolls.Spawn(_world, foot);
    }

    /// <summary>Drop the product-facing synthetic android dummy at the aim point.</summary>
    private void SpawnAndroidAtAim()
    {
        EvictIfFull();
        var foot = _aimValid ? _aimPoint : new Vector3(0f, 0f, 0f);
        _ragdolls.SpawnAndroid(_world, foot);
    }

    private void SpawnVehicleAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 0.72f, 0f);
        var vehicle = MakeVehicle(p, 1.0f);
        foreach (var b in vehicle.Bodies) _world.Bodies.Add(b);
        foreach (var j in vehicle.Joints) _world.Joints.Add(j);
        StatusUpdated?.Invoke("Spawned vehicle test rig.");
    }

    private void SpawnPoliceVehicleAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 0.72f, 0f);
        var vehicle = MakeVehicle(p, 1.0f);
        if (vehicle.Bodies.Count > 0)
        {
            vehicle.Bodies[0].Tag = "PoliceVehicleChassis";
            vehicle.Bodies[0].Color = new Vector3(0.20f, 0.34f, 0.82f);
        }
        foreach (var b in vehicle.Bodies) _world.Bodies.Add(b);
        foreach (var j in vehicle.Joints) _world.Joints.Add(j);
        StatusUpdated?.Invoke("Spawned police vehicle variant.");
    }

    private void SpawnAmbulanceAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 0.78f, 0f);
        var vehicle = MakeVehicle(p, 1.15f);
        if (vehicle.Bodies.Count > 0)
        {
            vehicle.Bodies[0].Tag = "AmbulanceChassis";
            vehicle.Bodies[0].Color = Vector3.One;
        }
        foreach (var b in vehicle.Bodies) _world.Bodies.Add(b);
        foreach (var j in vehicle.Joints) _world.Joints.Add(j);
        StatusUpdated?.Invoke("Spawned ambulance vehicle variant.");
    }

    private void SpawnExplosiveBarrelAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 0.66f, 0f);
        var barrel = WithMaterial(RigidBody.CreateBox(p, new Vector3(0.36f, 0.62f, 0.36f), density: 1.05f), MaterialId.Explosive);
        barrel.Color = new Vector3(1.0f, 1.0f, 1.0f);
        barrel.Tag = "ExplosiveBarrel";
        barrel.Restitution = 0.18f;
        barrel.Friction = 0.55f;
        barrel.ExplosivePower = MathF.Max(barrel.ExplosivePower, 1.8f);
        barrel.Flammability = MathF.Max(barrel.Flammability, 1.0f);
        barrel.Conductivity = MathF.Max(barrel.Conductivity, 0.35f);
        _world.Bodies.Add(barrel);
        StatusUpdated?.Invoke("Placed explosive barrel. It can detonate from fire, shock or heavy impact.");
    }


    private void SpawnCylinderAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 0.68f, 0f);
        var cyl = WithMaterial(RigidBody.CreateCapsule(p, 0.32f, density: 1.1f), MaterialId.Metal);
        cyl.Tag = "Cylinder";
        cyl.Color = Vector3.One;
        cyl.Restitution = 0.16f;
        cyl.Friction = 0.48f;
        cyl.Conductivity = 0.70f;
        _world.Bodies.Add(cyl);
        StatusUpdated?.Invoke("Placed cylinder. Physics uses a capsule proxy; visuals read as a metal cylinder.");
    }

    private void SpawnBeachBallAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 0.55f, 0f);
        var ball = WithMaterial(RigidBody.CreateSphere(p, 0.46f, density: 0.18f), MaterialId.Rubber);
        ball.Tag = "BeachBall";
        ball.Color = Vector3.One;
        ball.Restitution = 0.92f;
        ball.Friction = 0.28f;
        ball.Flammability = 0.08f;
        _world.Bodies.Add(ball);
        StatusUpdated?.Invoke("Placed beach ball. Light, bouncy and buoyant.");
    }

    private void SpawnMetalCubeAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 0.52f, 0f);
        var cube = WithMaterial(RigidBody.CreateBox(p, new Vector3(0.48f, 0.48f, 0.48f), density: 2.8f), MaterialId.Metal);
        cube.Tag = "MetalCube";
        cube.Color = Vector3.One;
        cube.Restitution = 0.10f;
        cube.Friction = 0.55f;
        cube.Conductivity = 0.9f;
        _world.Bodies.Add(cube);
        StatusUpdated?.Invoke("Placed heavy metal cube.");
    }

    private void SpawnGasCylinderAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 0.72f, 0f);
        var cyl = WithMaterial(MakeBreakable(RigidBody.CreateCapsule(p, 0.28f, density: 1.25f), threshold: 3.2f, pieces: 8), MaterialId.Explosive);
        cyl.Tag = "GasCylinder";
        cyl.Color = Vector3.One;
        cyl.Restitution = 0.14f;
        cyl.Friction = 0.50f;
        cyl.ExplosivePower = 1.35f;
        cyl.Flammability = 0.75f;
        cyl.Conductivity = 0.45f;
        _world.Bodies.Add(cyl);
        StatusUpdated?.Invoke("Placed gas cylinder. It behaves like a smaller explosive pressure vessel.");
    }

    private void SpawnSentinelBotAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 0.90f, 0f);
        var bot = WithMaterial(RigidBody.CreateCompound(p, [
            ChildShape.Box(new Vector3(0.38f, 0.34f, 0.24f), new Vector3(0f, 0.10f, 0f)),
            ChildShape.Sphere(0.20f, new Vector3(0f, 0.58f, 0f)),
            ChildShape.Sphere(0.18f, new Vector3(-0.34f, -0.22f, 0f)),
            ChildShape.Sphere(0.18f, new Vector3( 0.34f, -0.22f, 0f)),
            ChildShape.Capsule(0.07f, new Vector3(-0.50f, 0.12f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f)),
            ChildShape.Capsule(0.07f, new Vector3( 0.50f, 0.12f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f)),
        ], density: 0.85f), MaterialId.Synthetic);
        bot.Tag = "SentinelBot";
        bot.Color = new Vector3(0.72f, 0.88f, 0.92f);
        bot.Restitution = 0.18f;
        bot.Friction = 0.65f;
        bot.Breakable = true;
        bot.BreakThreshold = 6.2f;
        bot.BreakPieces = 10;
        bot.Conductivity = 0.75f;
        bot.Flammability = 0.22f;
        _world.Bodies.Add(bot);
        StatusUpdated?.Invoke("Placed sentinel bot target.");
    }

    private void SpawnBridgeSpanAtAim()
    {
        EvictIfFull();
        var p = _aimValid ? _aimPoint : Vector3.Zero;
        AddBridgeSpan(p, length: 7.2f, width: 2.0f, plankCount: 9, withCargo: false);
        StatusUpdated?.Invoke("Placed bridge span. It is a jointed wooden deck with anchored supports.");
    }

    private void SpawnCatapultLauncherAtAim()
    {
        EvictIfFull();
        var p = _aimValid ? _aimPoint : Vector3.Zero;
        AddCatapultLauncher(p, Vector3.Zero);
        StatusUpdated?.Invoke("Placed catapult launcher. It arms a projectile but fires only from a trigger or test action.");
    }

    private void SpawnWoodenCartAtAim()
    {
        EvictIfFull();
        var p = _aimValid ? _aimPoint : Vector3.Zero;
        AddWoodenCart(p + new Vector3(0f, 0.35f, 0f));
        StatusUpdated?.Invoke("Placed wooden cart. It is sized to carry an explosive barrel or other props.");
    }

    private void SpawnGlassBlockAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 0.65f, 0f);
        AddGlassBlock(p, new Vector3(0.42f, 0.65f, 0.42f));
        StatusUpdated?.Invoke("Placed breakable glass block. Hard impacts replace it with glass shards.");
    }

    private void SpawnWreckingBallTargetAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 2.0f, 0f);
        AddWreckingBallTarget(p);
        StatusUpdated?.Invoke("Placed wrecking ball target. Heavy suspended object for bridge/cart/catapult tests.");
    }

    private void SpawnDroneTargetAtAim()
    {
        EvictIfFull();
        var p = (_aimValid ? _aimPoint : Vector3.Zero) + new Vector3(0f, 1.85f, 0f);
        SpawnDroneTarget(p, new Vector3(0.25f, 0.85f, 1.0f));
        StatusUpdated?.Invoke("Placed synthetic drone target.");
    }

    private RigidBody SpawnDroneTarget(Vector3 p, Vector3 color)
    {
        var drone = RigidBody.CreateCompound(p, [
            ChildShape.Box(new Vector3(0.32f, 0.12f, 0.24f)),
            ChildShape.Box(new Vector3(0.55f, 0.035f, 0.055f)),
            ChildShape.Box(new Vector3(0.055f, 0.035f, 0.55f)),
            ChildShape.Sphere(0.13f, new Vector3(-0.52f, 0f, -0.52f)),
            ChildShape.Sphere(0.13f, new Vector3(-0.52f, 0f,  0.52f)),
            ChildShape.Sphere(0.13f, new Vector3( 0.52f, 0f, -0.52f)),
            ChildShape.Sphere(0.13f, new Vector3( 0.52f, 0f,  0.52f)),
        ], density: 0.55f);
        WithMaterial(drone, MaterialId.Synthetic);
        drone.Tag = "DroneTarget";
        drone.Color = color;
        drone.Friction = 0.42f;
        drone.Restitution = 0.28f;
        drone.Breakable = true;
        drone.BreakThreshold = 12.5f;
        drone.Flammability = MathF.Max(drone.Flammability, 0.35f);
        drone.Conductivity = MathF.Max(drone.Conductivity, 0.65f);
        _world.Bodies.Add(drone);
        return drone;
    }

    private RigidBody AddGlassBlock(Vector3 p, Vector3 halfExtents)
    {
        var glass = WithMaterial(RigidBody.CreateBox(p, halfExtents, density: 1.20f), MaterialId.Glass);
        glass.Tag = "GlassBlock";
        glass.Color = new Vector3(0.72f, 0.92f, 1.0f);
        glass.Restitution = 0.16f;
        glass.Friction = 0.22f;
        glass.Breakable = true;
        glass.BreakThreshold = 2.8f;
        glass.BreakPieces = 18;
        _world.Bodies.Add(glass);
        return glass;
    }

    private List<RigidBody> AddWoodenCart(Vector3 p)
    {
        // Rolling cart: body and four separate wheel spheres connected by point joints.
        // This keeps the cart stable but lets the wheels rotate physically instead of being visual-only children.
        var parts = new List<RigidBody>(5);
        var body = WithMaterial(RigidBody.CreateCompound(p + new Vector3(0f, 0.62f, 0f), [
            ChildShape.Box(new Vector3(1.12f, 0.10f, 0.66f), new Vector3(0f, 0.00f, 0f)),
            ChildShape.Box(new Vector3(1.16f, 0.26f, 0.07f), new Vector3(0f, 0.32f, -0.70f)),
            ChildShape.Box(new Vector3(1.16f, 0.26f, 0.07f), new Vector3(0f, 0.32f,  0.70f)),
            ChildShape.Box(new Vector3(0.07f, 0.26f, 0.64f), new Vector3(-1.16f, 0.32f, 0f)),
            ChildShape.Box(new Vector3(0.07f, 0.26f, 0.64f), new Vector3( 1.16f, 0.32f, 0f)),
        ], density: 0.55f), MaterialId.Wood);
        body.Tag = "WoodenCart";
        body.Color = Vector3.One;
        body.Friction = 0.52f;
        body.Restitution = 0.08f;
        body.Breakable = true;
        body.BreakThreshold = 9.5f;
        body.BreakPieces = 12;
        _world.Bodies.Add(body);
        parts.Add(body);

        // Wheels sit OUTBOARD of the side rails. Previously they overlapped the rails, and
        // since jointed bodies still collide, the solver fought that overlap and the cart
        // could jam in place instead of rolling.
        Vector3[] wheelOffsets =
        {
            new(-0.80f, -0.10f, -1.02f),
            new( 0.80f, -0.10f, -1.02f),
            new(-0.80f, -0.10f,  1.02f),
            new( 0.80f, -0.10f,  1.02f),
        };
        foreach (var off in wheelOffsets)
        {
            var wheel = WithMaterial(RigidBody.CreateSphere(body.Position + off, 0.23f, density: 0.9f), MaterialId.Metal);
            wheel.Tag = "WoodenCartWheel";
            wheel.Color = Vector3.One;
            wheel.Friction = 1.10f;
            wheel.Restitution = 0.15f;
            _world.Bodies.Add(wheel);
            _world.Joints.Add(new Joint { Type = Joint.Kind.Point, A = body, B = wheel, LocalA = off, LocalB = Vector3.Zero });
            parts.Add(wheel);
        }
        return parts;
    }

    private RigidBody AddWreckingBallTarget(Vector3 p)
    {
        var anchor = RigidBody.CreateStaticBox(p + new Vector3(0f, 1.45f, 0f), new Vector3(0.18f, 0.12f, 0.18f));
        anchor.Tag = "WreckingBallAnchor";
        anchor.Color = new Vector3(0.35f, 0.35f, 0.34f);
        _world.Bodies.Add(anchor);

        var ball = WithMaterial(RigidBody.CreateSphere(p, 0.52f, density: 2.4f), MaterialId.Metal);
        ball.Tag = "WreckingBallTarget";
        ball.Color = new Vector3(0.45f, 0.46f, 0.47f);
        ball.Friction = 0.42f;
        ball.Restitution = 0.18f;
        ball.Breakable = false;
        _world.Bodies.Add(ball);
        _world.Joints.Add(MakeSpring(anchor, ball, stiffness: 24f, damping: 2.2f));
        return ball;
    }

    private void AddCatapultLauncher(Vector3 p, Vector3 projectileVelocity)
    {
        // Single editable compound launcher. The old catapult consisted of several static bodies,
        // so selecting/moving/scaling it only affected one part. This version edits as one object.
        var launcher = WithMaterial(RigidBody.CreateCompound(p + new Vector3(0f, 0.58f, 0f), [
            ChildShape.Box(new Vector3(1.35f, 0.18f, 0.62f), new Vector3(0f, -0.38f, 0f)),
            ChildShape.Box(new Vector3(1.85f, 0.07f, 0.17f), new Vector3(0.65f, 0.24f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.18f)),
            ChildShape.Box(new Vector3(0.38f, 0.055f, 0.32f), new Vector3(2.35f, 0.50f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.18f)),
            ChildShape.Box(new Vector3(0.12f, 0.44f, 0.16f), new Vector3(-0.78f, -0.06f, -0.42f)),
            ChildShape.Box(new Vector3(0.12f, 0.44f, 0.16f), new Vector3(-0.78f, -0.06f,  0.42f)),
            ChildShape.Sphere(0.16f, new Vector3(-0.85f, -0.50f, -0.45f)),
            ChildShape.Sphere(0.16f, new Vector3(-0.85f, -0.50f,  0.45f)),
        ], density: 0.75f), MaterialId.Wood);
        launcher.Tag = "CatapultLauncher";
        launcher.Color = Vector3.One;
        launcher.SetStatic(true);
        launcher.RefreshProxies();
        _world.Bodies.Add(launcher);

        var projectile = WithMaterial(RigidBody.CreateSphere(p + new Vector3(2.35f, 1.55f, 0f), 0.33f, density: 1.6f), MaterialId.Stone);
        projectile.Color = new Vector3(0.66f, 0.62f, 0.54f);
        projectile.Restitution = 0.18f;
        projectile.Friction = 0.42f;
        projectile.Tag = "CatapultProjectile";
        if (projectileVelocity.LengthSquared() > 0.01f)
            projectile.Velocity = projectileVelocity;
        else
            projectile.Sleeping = true;
        _world.Bodies.Add(projectile);
    }

    private static VehicleRig MakeVehicle(Vector3 pos, float k)
    {
        var rig = new VehicleRig();
        var chassis = RigidBody.CreateCompound(pos, [
            ChildShape.Box(new Vector3(0.95f * k, 0.22f * k, 0.42f * k)),
            ChildShape.Box(new Vector3(0.45f * k, 0.18f * k, 0.36f * k), new Vector3(-0.10f * k, 0.32f * k, 0f)),
        ], density: 1.1f);
        WithMaterial(chassis, MaterialId.Synthetic);
        chassis.Color = new Vector3(1.0f, 1.0f, 1.0f);
        chassis.Tag = "VehicleChassis";
        chassis.Friction = 0.55f;
        chassis.Restitution = 0.12f;
        // Keep the vehicle readable by default. Later vehicle-damage work can detach/break parts deliberately;
        // the previous version could fracture immediately because wheels overlapped the chassis.
        chassis.Breakable = false;
        chassis.BreakThreshold = 12.0f;
        chassis.Flammability = 0.45f;
        chassis.Conductivity = 0.25f;
        rig.Bodies.Add(chassis);

        Vector3[] offsets =
        {
            new(-0.68f * k, -0.46f * k,  0.58f * k),
            new( 0.68f * k, -0.46f * k,  0.58f * k),
            new(-0.68f * k, -0.46f * k, -0.58f * k),
            new( 0.68f * k, -0.46f * k, -0.58f * k),
        };
        foreach (var off in offsets)
        {
            var wheel = RigidBody.CreateSphere(pos + off, 0.22f * k, density: 1.1f);
            WithMaterial(wheel, MaterialId.Rubber);
            wheel.Color = new Vector3(1.0f, 1.0f, 1.0f);
            wheel.Tag = "VehicleWheel";
            wheel.Friction = 1.25f;
            wheel.Restitution = 0.35f;
            wheel.Flammability = 0.8f;
            wheel.Conductivity = 0.12f;
            rig.Bodies.Add(wheel);
            rig.Joints.Add(new Joint
            {
                Type = Joint.Kind.Point,
                A = chassis,
                B = wheel,
                LocalA = off,
                LocalB = Vector3.Zero,
            });
        }
        return rig;
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
        _ragdolls.DamageInRadius(_aimPoint, 5f, 130f, _world);
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

        if (_pendingSceneAction == PendingSceneActionKind.Ignite)
        {
            var (o, dir2) = MouseRay(_lastMouseX, _lastMouseY);
            var target = _world.RayCast(o, dir2, out _, out _);
            if (target == null) return false;        // missed: stay armed, show the hint
            _pendingSceneAction = PendingSceneActionKind.None;
            _heat.Ignite(target);
            NotifyStateChanged();
            return true;
        }

        if (_pendingSceneAction == PendingSceneActionKind.Electrify)
        {
            var (o, dir2) = MouseRay(_lastMouseX, _lastMouseY);
            var target = _world.RayCast(o, dir2, out _, out _);
            if (target == null) return false;
            _pendingSceneAction = PendingSceneActionKind.None;
            _electricity.Electrify(target, 1.2f);
            NotifyStateChanged();
            return true;
        }

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
            case PendingSceneActionKind.Ragdoll:
                SpawnRagdollAtAim();
                break;
            case PendingSceneActionKind.Android:
                SpawnAndroidAtAim();
                break;
            case PendingSceneActionKind.Vehicle:
                SpawnVehicleAtAim();
                break;
            case PendingSceneActionKind.PoliceVehicle:
                SpawnPoliceVehicleAtAim();
                break;
            case PendingSceneActionKind.Ambulance:
                SpawnAmbulanceAtAim();
                break;
            case PendingSceneActionKind.DroneTarget:
                SpawnDroneTargetAtAim();
                break;
            case PendingSceneActionKind.BridgeSpan:
                SpawnBridgeSpanAtAim();
                break;
            case PendingSceneActionKind.CatapultLauncher:
                SpawnCatapultLauncherAtAim();
                break;
            case PendingSceneActionKind.WoodenCart:
                SpawnWoodenCartAtAim();
                break;
            case PendingSceneActionKind.GlassBlock:
                SpawnGlassBlockAtAim();
                break;
            case PendingSceneActionKind.WreckingBallTarget:
                SpawnWreckingBallTargetAtAim();
                break;
            case PendingSceneActionKind.ExplosiveBarrel:
                SpawnExplosiveBarrelAtAim();
                break;
            case PendingSceneActionKind.Cylinder:
                SpawnCylinderAtAim();
                break;
            case PendingSceneActionKind.BeachBall:
                SpawnBeachBallAtAim();
                break;
            case PendingSceneActionKind.MetalCube:
                SpawnMetalCubeAtAim();
                break;
            case PendingSceneActionKind.GasCylinder:
                SpawnGasCylinderAtAim();
                break;
            case PendingSceneActionKind.SentinelBot:
                SpawnSentinelBotAtAim();
                break;
            case PendingSceneActionKind.Motor:
                AddMotorAtAim();
                break;
            case PendingSceneActionKind.Gate:
                AddGateAtAim();
                break;
            case PendingSceneActionKind.Timer:
                AddTimerAtAim();
                break;
            case PendingSceneActionKind.Conveyor:
                AddConveyorAtAim();
                break;
            case PendingSceneActionKind.Piston:
                AddPistonAtAim();
                break;
            case PendingSceneActionKind.SlidingDoor:
                AddSlidingDoorAtAim();
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
        PendingSceneActionKind.Ragdoll => "ragdoll",
        PendingSceneActionKind.Android => "android dummy",
        PendingSceneActionKind.Vehicle => "vehicle",
        PendingSceneActionKind.PoliceVehicle => "police vehicle",
        PendingSceneActionKind.Ambulance => "ambulance",
        PendingSceneActionKind.DroneTarget => "drone target",
        PendingSceneActionKind.BridgeSpan => "bridge span",
        PendingSceneActionKind.CatapultLauncher => "catapult launcher",
        PendingSceneActionKind.WoodenCart => "wooden cart",
        PendingSceneActionKind.GlassBlock => "glass block",
        PendingSceneActionKind.WreckingBallTarget => "wrecking ball target",
        PendingSceneActionKind.ExplosiveBarrel => "explosive barrel",
        PendingSceneActionKind.Cylinder => "cylinder",
        PendingSceneActionKind.BeachBall => "beach ball",
        PendingSceneActionKind.MetalCube => "metal cube",
        PendingSceneActionKind.GasCylinder => "gas cylinder",
        PendingSceneActionKind.SentinelBot => "sentinel bot",
        PendingSceneActionKind.Motor => "motor hinge",
        PendingSceneActionKind.Gate => "gate",
        PendingSceneActionKind.Timer => "timer",
        PendingSceneActionKind.Conveyor => "conveyor belt",
        PendingSceneActionKind.Piston => "piston actuator",
        PendingSceneActionKind.SlidingDoor => "sliding door",
        PendingSceneActionKind.Explosion => "explosion",
        PendingSceneActionKind.Attractor => "attractor",
        PendingSceneActionKind.Repeller => "repeller",
        PendingSceneActionKind.Wind => "wind",
        PendingSceneActionKind.Connect => "connect",
        PendingSceneActionKind.Spring => "spring",
        PendingSceneActionKind.Disconnect => "disconnect",
        PendingSceneActionKind.Ignite => "ignite",
        PendingSceneActionKind.Electrify => "electrify",
        _ => "tool",
    };

    private string PendingSceneActionInstruction() => _pendingSceneAction switch
    {
        PendingSceneActionKind.Connect => "Connect: click first object, then click second object. Esc cancels.",
        PendingSceneActionKind.Spring => "Spring: click first object, then click second object. Esc cancels.",
        PendingSceneActionKind.Disconnect => "Disconnect: click an object to remove all its links/springs. Esc cancels.",
        PendingSceneActionKind.Ignite => "Ignite: click an object to set it on fire. Esc cancels.",
        PendingSceneActionKind.Electrify => "Electrify: click a conductive/wet object. Esc cancels.",
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
        _ragdolls.Clear();
        _heat.Clear();
        _electricity.Clear();
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

        if (tr.Outputs.Count == 0)
        {
            ExecuteTriggerOutput(tr.Name, new TriggerOutput
            {
                Action = tr.Action,
                TargetId = "",
                TargetName = "legacy target",
                Delay = 0f,
                Radius = tr.Radius,
                Strength = tr.Strength,
                Enabled = true,
            }, tr.TargetPosition);
        }
        else
        {
            foreach (var output in tr.Outputs)
            {
                if (!output.Enabled) continue;
                var fallback = ResolveOutputTargetPosition(output, tr.TargetPosition);
                if (output.Delay > 0.01f)
                {
                    _scheduledTriggerOutputs.Add(new ScheduledTriggerOutput
                    {
                        SourceName = tr.Name,
                        Output = new TriggerOutput
                        {
                            TargetId = output.TargetId,
                            TargetName = output.TargetName,
                            Action = output.Action,
                            Delay = output.Delay,
                            Radius = output.Radius,
                            Strength = output.Strength,
                            Enabled = output.Enabled,
                        },
                        FallbackTargetPosition = fallback,
                        Remaining = output.Delay,
                    });
                }
                else
                {
                    ExecuteTriggerOutput(tr.Name, output, fallback);
                }
            }
            StatusUpdated?.Invoke($"Triggered: {tr.Name} -> {tr.Outputs.Count} output(s)");
        }
        NotifyStateChanged();
    }

    private void UpdateTriggerOutputs(float dt)
    {
        if (dt <= 0f || _scheduledTriggerOutputs.Count == 0) return;
        for (int i = _scheduledTriggerOutputs.Count - 1; i >= 0; i--)
        {
            var scheduled = _scheduledTriggerOutputs[i];
            scheduled.Remaining -= dt;
            if (scheduled.Remaining > 0f)
                continue;
            ExecuteTriggerOutput(scheduled.SourceName, scheduled.Output, scheduled.FallbackTargetPosition);
            _scheduledTriggerOutputs.RemoveAt(i);
        }
    }

    private Vector3 ResolveOutputTargetPosition(TriggerOutput output, Vector3 fallback)
        => !string.IsNullOrWhiteSpace(output.TargetId) && TryGetMechanismPositionById(output.TargetId, out var p) ? p : fallback;

    private void ExecuteTriggerOutput(string sourceName, TriggerOutput output, Vector3 targetPosition)
    {
        float radius = output.Radius > 0 ? output.Radius : 5.0f;
        float strength = output.Strength > 0 ? output.Strength : 10.0f;
        switch (output.Action)
        {
            case TriggerActionKind.Explosion:
                ApplyExplosionAt(targetPosition + new Vector3(0, 0.25f, 0), radius, strength);
                StatusUpdated?.Invoke($"Triggered: {sourceName} -> explosion");
                break;
            case TriggerActionKind.Wind:
                _world.Fields.Clear();
                _world.Fields.Add(new ForceField { Type = ForceField.Kind.Wind, Position = targetPosition + new Vector3(0, 1.6f, 0), Radius = radius, Strength = strength, WindDir = Vector3.UnitX });
                foreach (var b in _world.Bodies) b.Wake();
                StatusUpdated?.Invoke($"Triggered: {sourceName} -> wind field");
                break;
            case TriggerActionKind.ToggleGravity:
                ToggleGravity();
                StatusUpdated?.Invoke($"Triggered: {sourceName} -> gravity toggled");
                break;
            case TriggerActionKind.ToggleAttractor:
                _world.Fields.Clear();
                _world.Fields.Add(new ForceField { Type = ForceField.Kind.Attract, Position = targetPosition + new Vector3(0, 1.5f, 0), Radius = radius, Strength = strength });
                foreach (var b in _world.Bodies) b.Wake();
                StatusUpdated?.Invoke($"Triggered: {sourceName} -> attractor");
                break;
            case TriggerActionKind.ToggleRepeller:
                _world.Fields.Clear();
                _world.Fields.Add(new ForceField { Type = ForceField.Kind.Repel, Position = targetPosition + new Vector3(0, 1.5f, 0), Radius = radius, Strength = strength });
                foreach (var b in _world.Bodies) b.Wake();
                StatusUpdated?.Invoke($"Triggered: {sourceName} -> repeller");
                break;
            case TriggerActionKind.LaunchCatapult:
                LaunchNearestCatapult(targetPosition, radius, strength);
                StatusUpdated?.Invoke($"Triggered: {sourceName} -> catapult launch");
                break;
            case TriggerActionKind.StartMotor:
            case TriggerActionKind.OpenGate:
            case TriggerActionKind.StartTimer:
            case TriggerActionKind.StartConveyor:
            case TriggerActionKind.StartPiston:
            case TriggerActionKind.ToggleDoor:
                if (!string.IsNullOrWhiteSpace(output.TargetId) && ActivateMechanismById(output.TargetId, output.Action))
                    StatusUpdated?.Invoke($"Triggered: {sourceName} -> {output.TargetName}");
                else
                    ActivateNearestMechanism(MechanismKindForTriggerAction(output.Action) ?? MechanismKind.Timer, targetPosition, radius);
                break;
        }
    }

    private void LaunchNearestCatapult(Vector3 targetPosition, float radius, float strength)
    {
        RigidBody? best = null;
        float bestD2 = radius * radius;
        foreach (var b in _world.Bodies)
        {
            if (!string.Equals(b.Tag as string, "CatapultLauncher", StringComparison.Ordinal)) continue;
            float d2 = Vector3.DistanceSquared(b.Position, targetPosition);
            if (d2 < bestD2) { bestD2 = d2; best = b; }
        }
        if (best == null) return;
        var muzzle = best.Position + Vector3.Transform(new Vector3(2.35f, 0.95f, 0f), best.Rotation);
        var projectile = WithMaterial(RigidBody.CreateSphere(muzzle, 0.33f, density: 1.6f), MaterialId.Stone);
        projectile.Tag = "CatapultProjectile";
        projectile.Color = new Vector3(0.66f, 0.62f, 0.54f);
        projectile.Restitution = 0.18f;
        projectile.Friction = 0.42f;
        var dir = Vector3.Transform(Vector3.UnitX, best.Rotation);
        projectile.Velocity = dir * (5.2f + strength * 0.18f) + Vector3.UnitY * 4.0f;
        _world.Bodies.Add(projectile);
        best.Wake();
    }

    private void ApplyExplosionAt(Vector3 center, float radius, float strength)
    {
        _world.ApplyExplosion(center, radius, strength);
        _ragdolls.DamageInRadius(center, radius, strength * 10.0f, _world);
        SpawnExplosionFeedback(center, radius);
        PlayExplosionSound();
        foreach (var b in _world.Bodies) b.Wake();
    }

    private void UpdateMaterialReactions(float dt)
    {
        if (dt <= 0f) return;

        // Explicit material gameplay: explosive objects can detonate from heat, fire,
        // electricity or extreme impacts. This no longer guesses from UI values.
        List<RigidBody>? detonate = null;
        foreach (var b in _world.Bodies)
        {
            if (b.IsStatic || !b.UserObject) continue;
            if (b.ExplosivePower <= 0f) continue;

            bool hot = b.Burning || b.Temperature > 230f;
            bool shocked = b.Charge > 0.72f;
            bool hardHit = b.Velocity.LengthSquared() > 90f;
            if (hot || shocked || hardHit) (detonate ??= new List<RigidBody>()).Add(b);
        }

        if (detonate == null) return;
        foreach (var b in detonate)
        {
            if (!_world.Bodies.Contains(b)) continue;
            var pos = b.Position;
            float radius = (3.0f + b.BoundingRadius * 1.5f) * MathF.Sqrt(MathF.Max(0.25f, b.ExplosivePower));
            float strength = (8.5f + b.Mass * 0.25f) * MathF.Max(0.5f, b.ExplosivePower);
            _world.RemoveBody(b);
            _world.Joints.RemoveAll(j => j.Involves(b));
            if (_selectedBody == b) SelectBody(null);
            ApplyExplosionAt(pos, radius, strength);
        }
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

            // A soft impact flash helps hard hits read even before debris spreads.
            if (speed >= 4.5f)
            {
                _impactFlash = MathF.Max(_impactFlash, Math.Clamp((speed - 4.5f) * 0.05f, 0.04f, 0.26f));
                AddParticle(
                    point + normal * 0.03f,
                    Vector3.Zero,
                    new Vector3(1.00f, 0.88f, 0.55f),
                    0.12f + MathF.Min(speed * 0.01f, 0.10f),
                    0.16f + MathF.Min(speed * 0.012f, 0.18f),
                    false);
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


    private void SpawnMaterialBreakEffects()
    {
        if (_world.BreakEvents.Count == 0) return;

        foreach (var e in _world.BreakEvents)
        {
            float power = Math.Clamp(e.Speed / 10f, 0.45f, 2.2f);
            PlayBreakSound(e.MaterialId, power);
            switch (e.MaterialId)
            {
                case MaterialId.Wood:
                    SpawnBreakDust(e.Position, e.Normal, new Vector3(0.50f, 0.32f, 0.16f), 10, power);
                    SpawnBreakSplinters(e.Position, e.Normal, new Vector3(0.78f, 0.48f, 0.20f), 14, power);
                    break;
                case MaterialId.Glass:
                case MaterialId.Ice:
                    SpawnGlassBreak(e.Position, e.Normal, e.MaterialId == MaterialId.Ice, power);
                    break;
                case MaterialId.Stone:
                    SpawnBreakDust(e.Position, e.Normal, new Vector3(0.45f, 0.43f, 0.39f), 20, power);
                    SpawnBreakChunks(e.Position, e.Normal, new Vector3(0.55f, 0.54f, 0.50f), 10, power);
                    break;
                case MaterialId.Plastic:
                    SpawnBreakDust(e.Position, e.Normal, new Vector3(0.90f, 0.55f, 0.25f), 8, power);
                    SpawnBreakChunks(e.Position, e.Normal, e.Color, 10, power);
                    break;
                case MaterialId.Synthetic:
                    SpawnBreakDust(e.Position, e.Normal, new Vector3(0.18f, 0.22f, 0.25f), 8, power);
                    SpawnBreakChunks(e.Position, e.Normal, e.Color, 8, power);
                    SpawnSyntheticSparks(e.Position, e.Normal, 18, power);
                    break;
                case MaterialId.Metal:
                    SpawnSyntheticSparks(e.Position, e.Normal, 22, power);
                    SpawnBreakChunks(e.Position, e.Normal, new Vector3(0.58f, 0.60f, 0.64f), 6, power);
                    break;
                case MaterialId.Explosive:
                    SpawnExplosionFeedback(e.Position, 1.8f + power * 0.8f);
                    SpawnBreakDust(e.Position, e.Normal, new Vector3(0.30f, 0.22f, 0.18f), 18, power);
                    break;
                default:
                    SpawnBreakDust(e.Position, e.Normal, e.Color, 8, power);
                    SpawnBreakChunks(e.Position, e.Normal, e.Color, 8, power);
                    break;
            }
        }
    }

    private void SpawnBreakDust(Vector3 pos, Vector3 normal, Vector3 color, int count, float power)
    {
        for (int i = 0; i < count && _particles.Count < MaxParticles; i++)
        {
            var dir = Vector3.Normalize(normal * 0.55f + RandomUnit() * 0.95f);
            AddParticle(
                pos + dir * 0.04f,
                dir * (0.35f + (float)_rng.NextDouble() * 1.35f * power),
                Vector3.Clamp(color * (0.75f + (float)_rng.NextDouble() * 0.35f), Vector3.Zero, Vector3.One),
                0.65f + (float)_rng.NextDouble() * 0.55f,
                0.09f + 0.08f * power,
                false);
        }
    }

    private void SpawnBreakChunks(Vector3 pos, Vector3 normal, Vector3 color, int count, float power)
    {
        for (int i = 0; i < count && _particles.Count < MaxParticles; i++)
        {
            var dir = Vector3.Normalize(normal * 0.35f + RandomUnit());
            AddParticle(
                pos + RandomUnit() * 0.06f,
                dir * (0.85f + (float)_rng.NextDouble() * 2.2f * power),
                Vector3.Clamp(color * (0.85f + (float)_rng.NextDouble() * 0.30f), Vector3.Zero, Vector3.One),
                0.45f + (float)_rng.NextDouble() * 0.35f,
                0.045f + 0.035f * power,
                true);
        }
    }

    private void SpawnBreakSplinters(Vector3 pos, Vector3 normal, Vector3 color, int count, float power)
    {
        for (int i = 0; i < count && _beams.Count < MaxBeams; i++)
        {
            var dir = Vector3.Normalize(normal * 0.35f + RandomUnit());
            float len = 0.14f + (float)_rng.NextDouble() * (0.24f + 0.10f * power);
            var a = pos + RandomUnit() * 0.08f;
            var b = a + dir * len;
            AddBeam(a, b, color, 0.22f + (float)_rng.NextDouble() * 0.14f, 0.015f + 0.008f * power);
        }
    }

    private void SpawnGlassBreak(Vector3 pos, Vector3 normal, bool ice, float power)
    {
        var shardColor = ice ? new Vector3(0.72f, 0.92f, 1.0f) : new Vector3(0.82f, 0.96f, 1.0f);
        SpawnBreakDust(pos, normal, shardColor, ice ? 12 : 8, power);
        for (int i = 0; i < 24 && _particles.Count < MaxParticles; i++)
        {
            var dir = Vector3.Normalize(normal * 0.25f + RandomUnit());
            AddParticle(
                pos + RandomUnit() * 0.04f,
                dir * (1.2f + (float)_rng.NextDouble() * 3.4f * power),
                shardColor,
                0.32f + (float)_rng.NextDouble() * 0.28f,
                0.030f + (float)_rng.NextDouble() * 0.025f,
                true);
        }
        for (int i = 0; i < 10 && _beams.Count < MaxBeams; i++)
        {
            var dir = Vector3.Normalize(normal * 0.15f + RandomUnit());
            var a = pos + RandomUnit() * 0.05f;
            AddBeam(a, a + dir * (0.10f + (float)_rng.NextDouble() * 0.20f), shardColor, 0.18f, 0.010f);
        }
    }

    private void SpawnSyntheticSparks(Vector3 pos, Vector3 normal, int count, float power)
    {
        for (int i = 0; i < count && _particles.Count < MaxParticles; i++)
        {
            var dir = Vector3.Normalize(normal * 0.30f + RandomUnit() + Vector3.UnitY * 0.15f);
            AddParticle(
                pos + RandomUnit() * 0.05f,
                dir * (1.8f + (float)_rng.NextDouble() * 4.0f * power),
                new Vector3(0.45f, 0.90f, 1.0f),
                0.15f + (float)_rng.NextDouble() * 0.14f,
                0.026f + (float)_rng.NextDouble() * 0.026f,
                true);
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

    private void SpawnFireEffects(float dt)
    {
        if (dt <= 0f) return;

        int burningCount = 0;
        // VFX polish pass: fire is now three layers, not just a tint:
        //  - orange/yellow flame beads close to the body;
        //  - darker smoke that rises slower and lives longer;
        //  - small ember sparks, especially on android/synthetic bones.
        foreach (var b in _world.Bodies)
        {
            if (!b.Burning) continue;
            burningCount++;

            bool android = b.Tag is RagdollBone rb && rb.Android;
            float scale = Math.Clamp(b.BoundingRadius, 0.18f, 1.2f);
            int bursts = android ? 3 : (b.Tag is RagdollBone ? 2 : 1);

            for (int i = 0; i < bursts && _particles.Count < MaxParticles; i++)
            {
                if (_rng.NextDouble() > 18.0 * dt) continue;
                var jitter = RandomUnit() * (0.10f + scale * 0.20f);
                jitter.Y = MathF.Abs(jitter.Y) * 0.6f;
                AddParticle(
                    b.Position + jitter,
                    new Vector3(
                        ((float)_rng.NextDouble() - 0.5f) * 0.45f,
                        1.25f + (float)_rng.NextDouble() * 1.85f,
                        ((float)_rng.NextDouble() - 0.5f) * 0.45f),
                    new Vector3(1.0f, 0.30f + (float)_rng.NextDouble() * 0.50f, 0.04f),
                    0.22f + (float)_rng.NextDouble() * 0.20f,
                    0.07f + scale * 0.035f,
                    false);
            }

            // Smoke: slower, larger, less bright. Androids produce cooler gray/blue smoke.
            if (_rng.NextDouble() < 2.0 * dt && _particles.Count < MaxParticles)
            {
                var jitter = RandomUnit() * (0.12f + scale * 0.15f);
                jitter.Y = MathF.Abs(jitter.Y) + scale * 0.15f;
                var smokeColor = android
                    ? new Vector3(0.30f, 0.34f, 0.38f)
                    : new Vector3(0.20f, 0.19f, 0.17f);
                AddSmokeParticle(
                    b.Position + jitter,
                    new Vector3(
                        ((float)_rng.NextDouble() - 0.5f) * 0.18f,
                        0.65f + (float)_rng.NextDouble() * 0.65f,
                        ((float)_rng.NextDouble() - 0.5f) * 0.18f),
                    smokeColor,
                    1.1f + (float)_rng.NextDouble() * 0.8f,
                    0.16f + scale * 0.10f);
            }

            // Embers/sparks sell synthetic android burning better than generic orange fire.
            if ((android || b.Conductivity > 0.45f) && _rng.NextDouble() < 9.0 * dt && _particles.Count < MaxParticles)
            {
                var dir = Vector3.Normalize(RandomUnit() + Vector3.UnitY * 0.55f);
                AddParticle(
                    b.Position + RandomUnit() * (0.08f + scale * 0.10f),
                    dir * (1.5f + (float)_rng.NextDouble() * 2.5f),
                    android ? new Vector3(0.45f, 0.90f, 1.0f) : new Vector3(1.0f, 0.85f, 0.20f),
                    0.22f + (float)_rng.NextDouble() * 0.18f,
                    0.035f + (float)_rng.NextDouble() * 0.025f,
                    true);
            }
        }

        PlayFireSound(burningCount);
    }

    private void SpawnElectricityEffects(float dt)
    {
        if (dt <= 0f) return;

        // Sparks attached to charged bodies, especially android bones. This makes shock damage
        // readable even when the object itself is only mildly blue-tinted.
        foreach (var b in _world.Bodies)
        {
            if (b.Charge <= 0.05f) continue;
            bool android = b.Tag is RagdollBone rb && rb.Android;
            float chance = (android ? 22f : 9f) * dt * Math.Clamp(b.Charge, 0.25f, 1.5f);
            if (_rng.NextDouble() < chance)
            {
                var dir = Vector3.Normalize(RandomUnit() + Vector3.UnitY * 0.25f);
                AddParticle(
                    b.Position + RandomUnit() * (0.06f + b.BoundingRadius * 0.20f),
                    dir * (2.2f + (float)_rng.NextDouble() * 3.2f),
                    new Vector3(0.35f, 0.85f, 1.0f),
                    0.16f + (float)_rng.NextDouble() * 0.14f,
                    0.035f + (float)_rng.NextDouble() * 0.025f,
                    true);
            }
        }

        // Short lightning rods between charged conductive/wet neighbours. It is deliberately
        // probabilistic so the scene flickers instead of showing permanent blue sticks.
        for (int i = 0; i < _world.Bodies.Count; i++)
        {
            var a = _world.Bodies[i];
            if (a.IsStatic || a.Charge <= 0.18f) continue;
            float ca = Math.Clamp(a.Conductivity + a.Wetness * 0.75f, 0f, 1.5f);
            if (ca <= 0.05f) continue;

            for (int j = i + 1; j < _world.Bodies.Count; j++)
            {
                var b = _world.Bodies[j];
                if (b.IsStatic) continue;
                float cb = Math.Clamp(b.Conductivity + b.Wetness * 0.75f, 0f, 1.5f);
                if (cb <= 0.05f) continue;

                float reach = 1.15f + a.BoundingRadius + b.BoundingRadius;
                float d2 = Vector3.DistanceSquared(a.Position, b.Position);
                if (d2 > reach * reach) continue;
                float d = MathF.Sqrt(MathF.Max(d2, 1e-5f));
                float falloff = 1f - d / reach;
                if (_rng.NextDouble() > falloff * a.Charge * 3.2f * dt) continue;

                SpawnElectricArc(a.Position, b.Position, falloff);
            }
        }
    }

    private void SpawnAndroidDamageEffects(float dt)
    {
        if (dt <= 0f) return;

        // Synthetic android damage feedback: coolant droplets, smoke and occasional sparks.
        // This keeps the project away from human gore while making damage readable.
        foreach (var rag in _ragdolls.All)
        {
            foreach (var bone in rag.Bones)
            {
                if (!bone.Android) continue;
                var b = bone.Body;

                float damage = 1f - bone.HealthFrac;
                float leak = MathF.Max(bone.Leak, damage > 0.55f ? (damage - 0.55f) * 1.4f : 0f);

                if (leak > 0.05f && _rng.NextDouble() < leak * 10.0 * dt && _particles.Count < MaxParticles)
                {
                    AddParticle(
                        b.Position + RandomUnit() * (0.04f + b.BoundingRadius * 0.12f),
                        b.Velocity * 0.08f + new Vector3(
                            ((float)_rng.NextDouble() - 0.5f) * 0.25f,
                            -0.15f - (float)_rng.NextDouble() * 0.55f,
                            ((float)_rng.NextDouble() - 0.5f) * 0.25f),
                        new Vector3(0.05f, 0.72f, 0.82f),
                        0.55f + (float)_rng.NextDouble() * 0.35f,
                        0.035f + leak * 0.025f,
                        true);
                }

                if ((bone.Severed || damage > 0.70f) && _rng.NextDouble() < 5.0 * dt && _particles.Count < MaxParticles)
                {
                    AddParticle(
                        b.Position + RandomUnit() * (0.05f + b.BoundingRadius * 0.14f),
                        new Vector3(
                            ((float)_rng.NextDouble() - 0.5f) * 0.20f,
                            0.35f + (float)_rng.NextDouble() * 0.65f,
                            ((float)_rng.NextDouble() - 0.5f) * 0.20f),
                        new Vector3(0.20f, 0.24f, 0.28f),
                        0.75f + (float)_rng.NextDouble() * 0.50f,
                        0.10f + b.BoundingRadius * 0.04f,
                        false);
                }

                if ((bone.Severed || bone.ShockStun > 0.1f || b.Charge > 0.25f) &&
                    _rng.NextDouble() < (bone.Severed ? 8.0 : 4.0) * dt && _particles.Count < MaxParticles)
                {
                    var dir = Vector3.Normalize(RandomUnit() + Vector3.UnitY * 0.25f);
                    AddParticle(
                        b.Position + RandomUnit() * (0.04f + b.BoundingRadius * 0.12f),
                        dir * (1.8f + (float)_rng.NextDouble() * 3.2f),
                        new Vector3(0.45f, 0.90f, 1.0f),
                        0.16f + (float)_rng.NextDouble() * 0.16f,
                        0.028f + (float)_rng.NextDouble() * 0.022f,
                        true);
                }
            }
        }
    }

    private void SpawnSteamAndWetEffects(float dt)
    {
        if (dt <= 0f) return;

        foreach (var b in _world.Bodies)
        {
            if (b.Wetness <= 0.15f) continue;

            // Hot/wet objects emit pale steam. This covers the satisfying case of throwing
            // a burning or glowing android part into water.
            if (b.Temperature > 80f && _rng.NextDouble() < (4.0 + b.Wetness * 8.0) * dt)
            {
                AddParticle(
                    b.Position + Vector3.UnitY * (b.BoundingRadius * 0.35f) + RandomUnit() * (0.06f + b.BoundingRadius * 0.10f),
                    new Vector3(
                        ((float)_rng.NextDouble() - 0.5f) * 0.18f,
                        0.55f + (float)_rng.NextDouble() * 0.75f,
                        ((float)_rng.NextDouble() - 0.5f) * 0.18f),
                    new Vector3(0.78f, 0.86f, 0.92f),
                    0.75f + (float)_rng.NextDouble() * 0.55f,
                    0.11f + b.BoundingRadius * 0.05f,
                    false);
            }

            // Very subtle water droplets on moving wet objects, useful after a splash.
            if (b.Velocity.LengthSquared() > 4f && _rng.NextDouble() < b.Wetness * 3.0 * dt)
            {
                AddParticle(
                    b.Position + RandomUnit() * (0.08f + b.BoundingRadius * 0.16f),
                    -b.Velocity * 0.10f + RandomUnit() * 0.35f,
                    new Vector3(0.55f, 0.80f, 1.0f),
                    0.35f + (float)_rng.NextDouble() * 0.25f,
                    0.035f,
                    true);
            }
        }
    }

    private void SpawnElectricArc(Vector3 a, Vector3 b, float power)
    {
        if (_beams.Count >= MaxBeams) return;
        PlayZapSound(power);

        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;
        var dir = d / len;
        var side = Vector3.Cross(dir, Vector3.UnitY);
        if (side.LengthSquared() < 1e-5f) side = Vector3.Cross(dir, Vector3.UnitX);
        side = Vector3.Normalize(side);
        var up = Vector3.Normalize(Vector3.Cross(side, dir));

        // Split the arc into a few jittered rods. They are not true screen-space lightning,
        // but in 3D motion they read much better than a single straight line.
        Vector3 prev = a;
        int segments = 3 + _rng.Next(3);
        for (int k = 1; k <= segments && _beams.Count < MaxBeams; k++)
        {
            float t = k / (float)segments;
            Vector3 p = Vector3.Lerp(a, b, t);
            if (k < segments)
                p += (side * ((float)_rng.NextDouble() - 0.5f) + up * ((float)_rng.NextDouble() - 0.5f)) * (0.10f + 0.14f * power);

            AddBeam(prev, p, new Vector3(0.45f, 0.90f, 1.0f), 0.08f + 0.05f * power, 0.025f + 0.020f * power);
            prev = p;
        }

        // Contact sparks at both ends.
        for (int i = 0; i < 4 && _particles.Count < MaxParticles; i++)
        {
            var end = (i & 1) == 0 ? a : b;
            AddParticle(end + RandomUnit() * 0.05f, RandomUnit() * (1.4f + (float)_rng.NextDouble() * 2.4f),
                new Vector3(0.65f, 0.95f, 1.0f), 0.16f, 0.035f, true);
        }
    }

    private void AddSmokeParticle(Vector3 pos, Vector3 vel, Vector3 color, float life, float size)
    {
        if (_particles.Count >= MaxParticles) return;
        _particles.Add(new Particle
        {
            Pos = pos,
            Vel = vel,
            Color = color,
            Life = life,
            MaxLife = MathF.Max(life, 1e-4f),
            Size = size,
            Gravity = false,
            Smoke = true,
        });
    }

    private void AddParticle(Vector3 pos, Vector3 vel, Vector3 color, float life, float size, bool gravity)
    {
        if (_particles.Count >= MaxParticles) return;
        _particles.Add(new Particle
        {
            Pos = pos,
            Vel = vel,
            Color = color,
            Life = life,
            MaxLife = MathF.Max(life, 1e-4f),
            Size = size,
            Gravity = gravity,
        });
    }

    private void AddBeam(Vector3 a, Vector3 b, Vector3 color, float life, float thickness)
    {
        if (_beams.Count >= MaxBeams) return;
        _beams.Add(new Beam
        {
            A = a,
            B = b,
            Color = color,
            Life = life,
            MaxLife = MathF.Max(life, 1e-4f),
            Thickness = thickness,
        });
    }

    private void TriggerCinematic(Vector3 center, float strength)
    {
        if (_paused) return;
        float dur = Math.Clamp(0.55f + strength * 0.05f, 0.55f, 1.2f);
        if (_cinematicTime < dur) { _cinematicTime = dur; _cinematicDuration = dur; }
        _cinematicShake = Math.Clamp(strength * 0.03f, 0.06f, 0.30f);
        _ = center;
    }

    private void SpawnExplosionFeedback(Vector3 center, float radius)
    {
        if (radius >= 3.5f) TriggerCinematic(center, radius);   // only sizeable blasts earn a cinematic
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

        // rising smoke cloud left behind by the blast
        for (int i = 0; i < 8 && _particles.Count < MaxParticles; i++)
        {
            var dir = RandomUnit();
            dir.Y = MathF.Abs(dir.Y) * 0.5f + 0.2f;
            AddSmokeParticle(
                center + RandomUnit() * radius * 0.3f,
                Vector3.Normalize(dir) * (1.2f + (float)_rng.NextDouble() * 1.8f),
                new Vector3(0.16f, 0.15f, 0.14f),
                1.3f + (float)_rng.NextDouble() * 0.9f,
                0.18f + (float)_rng.NextDouble() * 0.16f + radius * 0.05f);
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
        _nextImpactSound = now + 0.13;
        bool hard = speed > 8f;
        // louder + lower-pitched the harder the hit; small random pitch spread avoids machine-gun sameness
        float gain = Math.Clamp(0.12f + speed * 0.035f, 0.10f, 0.65f);
        float pitch = (hard ? 0.85f : 1.0f) + (float)(_rng.NextDouble() - 0.5) * 0.12f;
        Audio.Play(hard ? Sound.ImpactHard : Sound.ImpactSoft, gain, pitch);
    }

    private void PlaySplashSound()
    {
        if (!_soundEnabled) return;
        double now = _sw.Elapsed.TotalSeconds;
        if (now < _nextSplashSound) return;
        _nextSplashSound = now + 0.16;
        Audio.Play(Sound.Splash, 0.7f, 0.95f + (float)(_rng.NextDouble() - 0.5) * 0.1f);
    }

    private void PlayExplosionSound()
    {
        if (!_soundEnabled) return;
        double now = _sw.Elapsed.TotalSeconds;
        if (now < _nextExplosionSound) return;
        _nextExplosionSound = now + 0.18;
        Audio.Play(Sound.Explosion, 1.0f, 0.9f + (float)(_rng.NextDouble() - 0.5) * 0.15f);
    }

    private void PlayBreakSound(MaterialId material, float power)
    {
        if (!_soundEnabled) return;
        double now = _sw.Elapsed.TotalSeconds;
        if (now < _nextBreakSound) return;
        _nextBreakSound = now + 0.14;
        bool shatter = material is MaterialId.Glass or MaterialId.Ice;
        Audio.Play(shatter ? Sound.BreakGlass : Sound.BreakWood,
                   Math.Clamp(0.25f + power * 0.16f, 0.18f, 0.65f),
                   0.95f + (float)(_rng.NextDouble() - 0.5) * 0.2f);
    }

    private void PlayZapSound(float power)
    {
        if (!_soundEnabled) return;
        double now = _sw.Elapsed.TotalSeconds;
        if (now < _nextZapSound) return;
        _nextZapSound = now + 0.07;
        Audio.Play(Sound.Zap, Math.Clamp(0.35f + power * 0.3f, 0.3f, 1.0f),
                   0.9f + (float)(_rng.NextDouble() - 0.5) * 0.3f);
    }

    private void PlayFireSound(int burningCount)
    {
        if (!_soundEnabled || burningCount <= 0) return;
        double now = _sw.Elapsed.TotalSeconds;
        if (now < _nextFireSound) return;
        _nextFireSound = now + 0.11;   // periodic crackle while anything burns
        Audio.Play(Sound.FireCrackle, Math.Clamp(0.25f + burningCount * 0.08f, 0.25f, 0.8f),
                   0.85f + (float)(_rng.NextDouble() - 0.5) * 0.35f);
    }

    private void UpdateDroneHover(float dt)
    {
        if (dt <= 0f || _zeroG) return;
        foreach (var b in _world.Bodies)
        {
            if (!string.Equals(b.Tag as string, "DroneTarget", StringComparison.Ordinal)) continue;
            if (b.IsStatic || b.Children.Length < 6) continue;

            // Gameplay hover: cancel gravity while the intact synthetic drone exists.
            // Broken debris pieces are single fragments and do not keep this tag/shape set.
            b.Velocity.Y += -DefaultGravity.Y * dt;
            float desiredY = 2.45f;
            float error = desiredY - b.Position.Y;
            b.Velocity.Y += Math.Clamp(error * 5.5f - b.Velocity.Y * 0.45f, -4.0f, 5.0f) * dt;
            if (b.Position.Y < 1.35f && b.Velocity.Y < 0f) b.Velocity.Y *= 0.15f;
            b.AngularVelocity *= 0.88f;
            b.Wake();
        }
    }

    private void SpawnAmbientSceneEffects(float dt)
    {
        if (dt <= 0f) return;
        // Low-frequency motes give the sandbox a little life without becoming noisy.
        if (_rng.NextDouble() < 4.0 * dt && _particles.Count < MaxParticles)
        {
            var pos = new Vector3(
                ((float)_rng.NextDouble() - 0.5f) * 18f,
                0.8f + (float)_rng.NextDouble() * 5.2f,
                ((float)_rng.NextDouble() - 0.5f) * 18f);
            var vel = new Vector3(
                ((float)_rng.NextDouble() - 0.5f) * 0.12f,
                0.08f + (float)_rng.NextDouble() * 0.16f,
                ((float)_rng.NextDouble() - 0.5f) * 0.12f);
            var color = _waterOn
                ? new Vector3(0.52f, 0.68f, 0.82f)
                : new Vector3(0.48f, 0.50f, 0.54f);
            AddParticle(pos, vel, color, 1.4f + (float)_rng.NextDouble() * 1.5f, 0.025f + (float)_rng.NextDouble() * 0.03f, false);
        }
    }

    private void UpdateParticles(float dt)
    {
        if (dt <= 0f) return;
        _impactFlash = MathF.Max(0f, _impactFlash - dt * 1.8f);
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

    private void UpdateBeams(float dt)
    {
        if (dt <= 0f) return;
        for (int i = _beams.Count - 1; i >= 0; i--)
        {
            var b = _beams[i];
            b.Life -= dt;
            if (b.Life <= 0f) { _beams.RemoveAt(i); continue; }
            _beams[i] = b;
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
                var pin = WithMaterial(RigidBody.CreateCapsule(new Vector3(p.X, p.Y + 0.52f, p.Z), 0.17f, density: 0.75f), MaterialId.Plastic);
                pin.Tag = "BowlingPin";
                pin.Color = Vector3.One;
                pin.Friction = 0.44f;
                pin.Restitution = 0.22f;
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
            MaterialId = b.MaterialId,
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
            Flammability = b.Flammability,
            Conductivity = b.Conductivity,
            ExplosivePower = b.ExplosivePower,
        };
    }

    private SelectedTriggerSnapshot? CreateSelectedTriggerSnapshot()
    {
        var tr = _selectedTrigger;
        if (tr == null) return null;
        var snapshot = new SelectedTriggerSnapshot
        {
            Id = tr.Id,
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
            OutputCount = tr.Outputs.Count,
        };
        for (int i = 0; i < tr.Outputs.Count; i++)
        {
            var output = tr.Outputs[i];
            snapshot.Outputs.Add(new SelectedTriggerOutputSnapshot
            {
                Index = i,
                TargetId = output.TargetId,
                TargetName = output.TargetName,
                Action = output.Action,
                Delay = output.Delay,
                Radius = output.Radius,
                Strength = output.Strength,
                Enabled = output.Enabled,
            });
        }
        return snapshot;
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
        // cinematic weight: 1 at the moment of the blast, easing to 0
        float cine = (_cinematicTime > 0f && _cinematicDuration > 0f) ? _cinematicTime / _cinematicDuration : 0f;
        float zoom = 1f - 0.16f * cine;   // brief punch-in
        var offset = new Vector3(
            cp * MathF.Sin(_camYaw),
            sp,
            cp * MathF.Cos(_camYaw)) * (_camDist * zoom);
        _camPos = _camTarget + offset;

        if (cine > 0f)
        {
            float sh = _cinematicShake * cine * cine;   // strong then fades
            _camPos += new Vector3(
                (float)_rng.NextDouble() * 2f - 1f,
                (float)_rng.NextDouble() * 2f - 1f,
                (float)_rng.NextDouble() * 2f - 1f) * sh;
        }

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

        if (_selectedBody == null) return;

        _selectedBody.Wake();
        _toolDragging = true;
        _toolDragStartX = mx;
        _toolDragStartY = my;
        _toolStartPos = _selectedBody.Position;
        _toolStartRot = _selectedBody.Rotation;
        _toolLastScaleFactor = 1f;
        BuildRotationGroup();
    }

    // Collect every body reachable from the selection through joints, plus the world anchor
    // points of any world-anchored joints, so the whole assembly can be rotated as one rigid unit.
    private void BuildRotationGroup()
    {
        _rotGroup.Clear();
        _rotAnchorJoints.Clear();
        if (_selectedBody == null) return;

        var seen = new HashSet<RigidBody> { _selectedBody };
        var stack = new Stack<RigidBody>();
        stack.Push(_selectedBody);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            _rotGroup.Add(cur);
            foreach (var j in _world.Joints)
            {
                RigidBody? other = j.A == cur ? j.B : (j.B == cur ? j.A : null);
                if (other != null && seen.Add(other)) stack.Push(other);
            }
        }

        foreach (var j in _world.Joints)
            if (j.B == null && _rotGroup.Contains(j.A)) _rotAnchorJoints.Add(j);

        _rotStartPos = new Vector3[_rotGroup.Count];
        _rotStartRot = new Quaternion[_rotGroup.Count];
        for (int i = 0; i < _rotGroup.Count; i++) { _rotStartPos[i] = _rotGroup[i].Position; _rotStartRot[i] = _rotGroup[i].Rotation; }
        _rotAnchorStart = new Vector3[_rotAnchorJoints.Count];
        for (int i = 0; i < _rotAnchorJoints.Count; i++) _rotAnchorStart[i] = _rotAnchorJoints[i].LocalB;
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
                var dq = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
                if (_rotGroup.Count <= 1)
                {
                    b.Rotation = Quaternion.Normalize(dq * _toolStartRot);
                    b.AngularVelocity = Vector3.Zero;
                }
                else
                {
                    var pivot = _toolStartPos;
                    for (int i = 0; i < _rotGroup.Count; i++)
                    {
                        var body = _rotGroup[i];
                        body.Position = pivot + Vector3.Transform(_rotStartPos[i] - pivot, dq);
                        body.Rotation = Quaternion.Normalize(dq * _rotStartRot[i]);
                        body.Velocity = Vector3.Zero;
                        body.AngularVelocity = Vector3.Zero;
                        body.UpdateDerived();
                    }
                    for (int i = 0; i < _rotAnchorJoints.Count; i++)
                        _rotAnchorJoints[i].LocalB = pivot + Vector3.Transform(_rotAnchorStart[i] - pivot, dq);
                }
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
        float tScene = (float)_sw.Elapsed.TotalSeconds;
        // Keep the clear colour stable. The previous ambient/impact modulation looked like background flicker.
        // Impact feedback is now handled by particles/beams, not by flashing the whole frame.
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
        GL.ActiveTexture(GL.TEXTURE2);
        GL.Uniform1(_uBumpMap, 2);
        GL.Uniform1(_uUseBumpMap, 0f);
        GL.ActiveTexture(GL.TEXTURE1);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uEmissive, 0f);
        GL.Uniform1(_uTime, tScene);
        GL.Uniform1(_uWaterWaveAmp, 0f);

        DrawSkybox();

        // floor
        GL.BindTexture(GL.TEXTURE_2D, _texFloor);
        GL.Uniform1(_uWorldUv, 0f);
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
            else if (RagdollSystem.TryTint(b, out var boneColor, out float boneGlow))
            {
                // Ragdoll bones: skin tone reddening with damage, brief flash when hit, dimmer when dead.
                if (b != _selectedBody && b != _world.Grabbed && b != _jointFirstBody)
                    color = b.Sleeping ? boneColor * 0.72f : boneColor;
                GL.Uniform1(_uEmissive, boneGlow);
            }
            else
            {
                GL.Uniform1(_uEmissive, 0f);
            }

            // Authored wood textures should read as wood, not as candy-coloured blocks.
            // Keep selection/field/ragdoll/heat/electric feedback, but neutralize normal wood tint.
            if (b.MaterialId == MaterialId.Wood && b != _selectedBody && b != _world.Grabbed && b != _jointFirstBody && !fieldAffected)
                color = Vector3.One;

            // Fire takes visual priority: a burning/glowing body overrides any other tint.
            if (HeatSystem.TryTint(b, out var hotColor, out float hotGlow))
            {
                color = hotColor;
                GL.Uniform1(_uEmissive, hotGlow);
            }
            if (ElectricitySystem.TryTint(b, out var electricColor, out float electricGlow))
            {
                color = electricColor;
                GL.Uniform1(_uEmissive, MathF.Max(electricGlow, 0.25f));
            }

            GL.Uniform3(_uColor, color.X, color.Y, color.Z);

            foreach (ref var child in b.Children.AsSpan())
            {
                if (IsBowlingPin(b))
                {
                    DrawBowlingPinVisual(b);
                    continue;
                }
                GL.BindTexture(GL.TEXTURE_2D, TextureFor(b, in child));
                uint bumpTex = BumpTextureFor(b, in child);
                GL.ActiveTexture(GL.TEXTURE2);
                GL.BindTexture(GL.TEXTURE_2D, bumpTex);
                GL.Uniform1(_uUseBumpMap, bumpTex != 0 ? 1f : 0f);
                GL.ActiveTexture(GL.TEXTURE1);
                GL.Uniform1(_uBumpStrength, BumpStrengthFor(b, in child));
                bool worldTile = b.IsStatic && child.Shape == ShapeType.Box && !IsMechanismBody(b);
                GL.Uniform1(_uWorldUv, worldTile ? 1f : 0f);
                if (b.IsStatic)
                    GL.Uniform1(_uUvScale, worldTile ? (IsArenaWall(b) ? WallBrickDensity : StaticSurfaceDensity) : 6f);

                bool asBarrel = IsExplosiveBarrel(b) && child.Shape == ShapeType.Box;
                bool asWheel = child.Shape == ShapeType.Sphere && (IsVehicleWheel(b) || IsCartWheel(b));
                bool asDumbbell = string.Equals(b.Tag as string, "Dumbbell", StringComparison.Ordinal);
                if (asBarrel)
                {
                    DrawBarrelCylinder(b, in child);
                }
                else if (asWheel)
                {
                    DrawWheelCylinder(b, in child);
                }
                else if (asDumbbell)
                {
                    DrawDumbbellPart(b, in child);
                }
                else
                {
                    GL.UniformMatrix4(_uModel, ToArray(ModelMatrix(b, in child)));
                    MeshFor(child.Shape).Draw();
                    if (IsAndroidBody(b))
                        DrawAndroidOverlay(b, in child);
                    else if (IsVehicleChassis(b))
                        DrawVehicleChassisOverlay(b, in child);
                }
                if (b.IsStatic) { GL.Uniform1(_uUvScale, 1f); GL.Uniform1(_uWorldUv, 0f); }
            }
        }
        GL.Uniform1(_uEmissive, 0f);
        GL.Uniform1(_uBumpStrength, 0f);
        GL.Uniform1(_uUseBumpMap, 0f);

        DrawJointRods();
        DrawFieldMarkers();
        DrawEditorGizmo();
        DrawAimMarker();
        DrawBeams();
        DrawParticles();
        DrawMechanisms();
        DrawTriggerWiring();
        DrawTriggers();
        DrawChallengeMarker();
        DrawWater();
    }

    private void DrawSkybox()
    {
        GL.DepthFunc(GL.LEQUAL);
        GL.CullFace(GL.FRONT);
        var model = Matrix4x4.CreateScale(78f) * Matrix4x4.CreateTranslation(_camPos);
        if (_skyProgram != 0)
        {
            GL.UseProgram(_skyProgram);
            GL.UniformMatrix4(_uSkyModel, ToArray(model));
            GL.UniformMatrix4(_uSkyView, ToArray(_view));
            GL.UniformMatrix4(_uSkyProj, ToArray(_proj));
            GL.Uniform3(_uSkyCamPos, _camPos.X, _camPos.Y, _camPos.Z);
            GL.Uniform1(_uSkyTime, (float)_sw.Elapsed.TotalSeconds);
            _cubeMesh.Draw();
            GL.UseProgram(_mainProgram);   // restore the main program for the rest of the frame
        }
        else
        {
            GL.BindTexture(GL.TEXTURE_2D, _texSky);
            GL.Uniform1(_uUvScale, 1f);
            GL.Uniform1(_uAlpha, 1f);
            GL.Uniform1(_uEmissive, 1f);
            GL.Uniform3(_uColor, 1f, 1f, 1f);
            GL.UniformMatrix4(_uModel, ToArray(model));
            _cubeMesh.Draw();
            GL.Uniform1(_uEmissive, 0f);
        }
        GL.CullFace(GL.BACK);
        GL.DepthFunc(GL.LESS);
    }

    private void DrawCartWheelOverlay(RigidBody b, in ChildShape child)
    {
        // The cart is still one stable compound body, but the wheel spokes rotate visually
        // from travelled distance so the player does not read it as a static crate-on-balls.
        var bodyRot = Matrix4x4.CreateFromQuaternion(b.Rotation);
        var center = Vector3.Transform(child.LocalPos, bodyRot) + b.Position;
        var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, bodyRot));
        var side = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, bodyRot));
        float spin = (b.Position.X + b.Position.Z) * 4.5f + (float)_sw.Elapsed.TotalSeconds * b.Velocity.Length() * 3.2f;
        float r = child.Radius * 0.92f;

        GL.BindTexture(GL.TEXTURE_2D, _texRustyMetal);
        GL.ActiveTexture(GL.TEXTURE2);
        GL.BindTexture(GL.TEXTURE_2D, _bumpRustyMetal);
        GL.Uniform1(_uUseBumpMap, 1f);
        GL.ActiveTexture(GL.TEXTURE1);
        GL.Uniform1(_uBumpStrength, 0.28f);
        GL.Uniform1(_uEmissive, 0f);

        for (int i = 0; i < 6; i++)
        {
            float a = spin + i * MathF.PI / 6f;
            var dir = Vector3.Normalize(side * MathF.Cos(a) + up * MathF.Sin(a));
            DrawGizmoRod(center - dir * r, center + dir * r, new Vector3(0.06f, 0.055f, 0.045f), 0.020f);
        }
        // Small bright hub so wheel spin reads even from a distance.
        GL.Uniform3(_uColor, 0.55f, 0.50f, 0.42f);
        GL.UniformMatrix4(_uModel, ToArray(Matrix4x4.CreateScale(child.Radius * 0.28f) * Matrix4x4.CreateTranslation(center)));
        _sphereMesh.Draw();

        GL.Uniform1(_uUseBumpMap, 0f);
        GL.Uniform1(_uBumpStrength, 0f);
    }

    private void DrawBowlingPinVisual(RigidBody b)
    {
        // Physics remains a simple capsule, but the rendered object is closer to a real bowling pin.
        // This sets a useful rule for future props: collision can stay simple while visuals are richer.
        GL.BindTexture(GL.TEXTURE_2D, _texBowlingPin);
        GL.ActiveTexture(GL.TEXTURE2);
        GL.BindTexture(GL.TEXTURE_2D, _bumpBowlingPin);
        GL.Uniform1(_uUseBumpMap, 1f);
        GL.ActiveTexture(GL.TEXTURE1);
        GL.Uniform1(_uBumpStrength, 0.20f);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 0f);

        float visualScale = 1f;
        if (b.Children.Length > 0)
            visualScale = Math.Clamp(b.Children[0].Radius / 0.17f, 0.25f, 6.0f);

        Matrix4x4 BodyMatrix(Vector3 scale, Vector3 local)
            => Matrix4x4.CreateScale(scale * visualScale)
             * Matrix4x4.CreateTranslation(local * visualScale)
             * Matrix4x4.CreateFromQuaternion(b.Rotation)
             * Matrix4x4.CreateTranslation(b.Position);

        void DrawSphere(Vector3 scale, Vector3 local, Vector3 color)
        {
            GL.Uniform3(_uColor, color.X, color.Y, color.Z);
            GL.UniformMatrix4(_uModel, ToArray(BodyMatrix(scale, local)));
            _sphereMesh.Draw();
        }

        void DrawBand(float y, float halfHeight)
        {
            // Draw the red neck rings as flattened ellipsoids instead of box strips.
            // The previous cube bands looked like red square blocks attached to the pin.
            GL.BindTexture(GL.TEXTURE_2D, _texStripes);
            GL.Uniform3(_uColor, 0.92f, 0.04f, 0.035f);
            GL.UniformMatrix4(_uModel, ToArray(BodyMatrix(new Vector3(0.155f, halfHeight, 0.155f), new Vector3(0f, y, 0f))));
            _sphereMesh.Draw();
            GL.BindTexture(GL.TEXTURE_2D, _texBowlingPin);
        }

        var white = new Vector3(1.0f, 0.97f, 0.88f);
        DrawSphere(new Vector3(0.24f, 0.17f, 0.24f), new Vector3(0f, -0.21f, 0f), white); // belly
        DrawSphere(new Vector3(0.13f, 0.25f, 0.13f), new Vector3(0f,  0.10f, 0f), white); // neck/body
        DrawSphere(new Vector3(0.115f, 0.09f, 0.115f), new Vector3(0f, 0.39f, 0f), white); // head
        DrawBand(0.18f, 0.022f);
        DrawBand(0.25f, 0.018f);

        GL.Uniform1(_uUseBumpMap, 0f);
        GL.Uniform1(_uBumpStrength, 0f);
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

    private void DrawBeams()
    {
        if (_beams.Count == 0) return;
        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 1f);

        foreach (var beam in _beams)
        {
            float fade = Math.Clamp(beam.Life / beam.MaxLife, 0f, 1f);
            GL.Uniform1(_uAlpha, 0.30f + fade * 0.55f);
            GL.Uniform3(_uColor, beam.Color.X * fade, beam.Color.Y * fade, beam.Color.Z * fade);
            DrawRodSegment(beam.A, beam.B, beam.Thickness * (0.5f + fade));
        }

        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uEmissive, 0f);
    }

    private static Matrix4x4 Billboard(Vector3 pos, float s, Vector3 r, Vector3 u, Vector3 n) => new(
        r.X * s, r.Y * s, r.Z * s, 0f,
        u.X * s, u.Y * s, u.Z * s, 0f,
        n.X,     n.Y,     n.Z,     0f,
        pos.X,   pos.Y,   pos.Z,   1f);

    private void DrawParticles()
    {
        if (_particles.Count == 0) return;
        if (_particleProgram == 0) { DrawParticlesFallback(); return; }

        // Camera basis (world space) from the view matrix, so quads always face the camera.
        var camRight = new Vector3(_view.M11, _view.M21, _view.M31);
        var camUp = new Vector3(_view.M12, _view.M22, _view.M32);
        var camFwd = new Vector3(-_view.M13, -_view.M23, -_view.M33);

        GL.UseProgram(_particleProgram);
        GL.UniformMatrix4(_uPView, ToArray(_view));
        GL.UniformMatrix4(_uPProj, ToArray(_proj));
        GL.ActiveTexture(GL.TEXTURE0);
        GL.BindTexture(GL.TEXTURE_2D, _texSoftParticle);   // sampler defaults to unit 0
        GL.Enable(GL.BLEND);
        GL.DepthMask(0);
        GL.Disable(GL.CULL_FACE);

        // Fire / sparks: additive blending so overlapping puffs glow.
        GL.BlendFunc(GL.SRC_ALPHA, GL.ONE);
        bool anySmoke = false;
        foreach (var p in _particles)
        {
            if (p.Smoke) { anySmoke = true; continue; }
            float fade = p.Life / p.MaxLife;
            GL.Uniform3(_uPColor, p.Color.X, p.Color.Y, p.Color.Z);
            GL.Uniform1(_uPAlpha, fade);
            GL.UniformMatrix4(_uPModel, ToArray(Billboard(p.Pos, p.Size * (2.6f + (1f - fade) * 1.4f), camRight, camUp, camFwd)));
            _quadMesh.Draw();
        }

        // Smoke: standard alpha blending, soft and expanding.
        if (anySmoke)
        {
            GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
            foreach (var p in _particles)
            {
                if (!p.Smoke) continue;
                float fade = p.Life / p.MaxLife;
                float shade = 0.20f + (1f - fade) * 0.18f;
                GL.Uniform3(_uPColor, shade, shade, shade);
                GL.Uniform1(_uPAlpha, fade * 0.55f);
                GL.UniformMatrix4(_uPModel, ToArray(Billboard(p.Pos, p.Size * (3.0f + (1f - fade) * 4.0f), camRight, camUp, camFwd)));
                _quadMesh.Draw();
            }
        }

        GL.Enable(GL.CULL_FACE);
        GL.DepthMask(1);
        GL.Disable(GL.BLEND);
        GL.UseProgram(_mainProgram);   // restore main program for the rest of the frame
    }

    private void DrawParticlesFallback()
    {
        if (_particles.Count == 0) return;
        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);

        // Opaque pass: fire / sparks (glowing).
        GL.Uniform1(_uEmissive, 1f);
        bool anySmoke = false;
        foreach (var p in _particles)
        {
            if (p.Smoke) { anySmoke = true; continue; }
            float fade = p.Life / p.MaxLife;
            GL.Uniform3(_uColor, p.Color.X * fade, p.Color.Y * fade, p.Color.Z * fade);
            GL.UniformMatrix4(_uModel, ToArray(
                Matrix4x4.CreateScale(p.Size * (0.4f + fade)) * Matrix4x4.CreateTranslation(p.Pos)));
            _sphereMesh.Draw();
        }
        GL.Uniform1(_uEmissive, 0f);

        // Translucent pass: smoke. Alpha-blended with depth-write off so overlapping puffs read as
        // a soft volume instead of a pile of solid grey balls.
        if (anySmoke)
        {
            GL.Enable(GL.BLEND);
            GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
            GL.DepthMask(0);
            foreach (var p in _particles)
            {
                if (!p.Smoke) continue;
                float fade = p.Life / p.MaxLife;            // 1 fresh -> 0 gone
                float grow = 1.0f + (1.0f - fade) * 1.5f;   // expands modestly as it ages
                float shade = 0.26f + (1.0f - fade) * 0.20f;
                GL.Uniform1(_uAlpha, fade * 0.40f);          // translucent, fades out completely
                GL.Uniform3(_uColor, shade, shade, shade);
                GL.UniformMatrix4(_uModel, ToArray(
                    Matrix4x4.CreateScale(p.Size * grow) * Matrix4x4.CreateTranslation(p.Pos)));
                _sphereMesh.Draw();
            }
            GL.Uniform1(_uAlpha, 1f);
            GL.DepthMask(1);
            GL.Disable(GL.BLEND);
        }
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

    private void DrawTriggerWiring()
    {
        if (_triggers.Count == 0) return;
        if (!_showTriggerWiring && _selectedTrigger == null) return;

        GL.Enable(GL.BLEND);
        GL.BlendFunc(GL.SRC_ALPHA, GL.ONE_MINUS_SRC_ALPHA);
        GL.DepthMask(0);
        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 0.65f);

        float t = (float)_sw.Elapsed.TotalSeconds;
        foreach (var tr in _triggers)
        {
            if (!_showTriggerWiring && tr != _selectedTrigger) continue;

            bool selected = tr == _selectedTrigger;
            float pulse = tr.Pulse;
            float alpha = selected ? 0.72f : (tr.Enabled ? 0.30f : 0.12f);
            alpha += pulse * 0.30f;
            float thickness = selected ? 0.040f : 0.023f;
            var a = tr.Position + new Vector3(0, 0.18f, 0);

            if (tr.Outputs.Count == 0)
            {
                if (!TriggerActionUsesTarget(tr.Action)) continue;
                var color = TriggerColor(tr.Action);
                var b = tr.TargetPosition + new Vector3(0, 0.48f + 0.08f * MathF.Sin(t * 4f + tr.Position.X), 0);
                GL.Uniform1(_uAlpha, Math.Clamp(alpha, 0.08f, 0.95f));
                GL.Uniform3(_uColor, color.X, color.Y, color.Z);
                DrawRodSegment(a, b, thickness);
                DrawTriggerTargetMarker(tr.TargetPosition, tr.Radius, color, selected, t);
                continue;
            }

            foreach (var output in tr.Outputs)
            {
                if (!output.Enabled || !TriggerActionUsesTarget(output.Action)) continue;
                var color = TriggerColor(output.Action);
                var target = ResolveOutputTargetPosition(output, tr.TargetPosition);
                var b = target + new Vector3(0, 0.48f + 0.08f * MathF.Sin(t * 4f + tr.Position.X), 0);
                GL.Uniform1(_uAlpha, Math.Clamp(alpha, 0.08f, 0.95f));
                GL.Uniform3(_uColor, color.X, color.Y, color.Z);
                DrawRodSegment(a, b, thickness);
                DrawTriggerTargetMarker(target, output.Radius > 0 ? output.Radius : tr.Radius, color, selected, t);
            }
        }

        GL.DepthMask(1);
        GL.Disable(GL.BLEND);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uEmissive, 0f);
    }

    private void DrawTriggerTargetMarker(Vector3 p, float sourceRadius, Vector3 color, bool selected, float t)
    {
        float pulse = selected ? 1.0f + 0.08f * MathF.Sin(t * 5.0f) : 1.0f;
        float y = 0.16f;
        float radius = Math.Clamp(sourceRadius, 0.75f, 8.0f);
        float alpha = selected ? 0.45f : 0.20f;

        GL.Uniform1(_uAlpha, alpha);
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);

        // Target crosshair.
        var crossA = Matrix4x4.CreateScale(0.06f * pulse, 0.035f, 0.42f * pulse)
                   * Matrix4x4.CreateTranslation(p + new Vector3(0, y, 0));
        GL.UniformMatrix4(_uModel, ToArray(crossA));
        _cubeMesh.Draw();
        var crossB = Matrix4x4.CreateScale(0.42f * pulse, 0.035f, 0.06f * pulse)
                   * Matrix4x4.CreateTranslation(p + new Vector3(0, y, 0));
        GL.UniformMatrix4(_uModel, ToArray(crossB));
        _cubeMesh.Draw();

        // Approximate action radius as a square frame. It is deliberately cheap and readable.
        if (selected || _showTriggerWiring)
        {
            float r = radius * (selected ? 1.0f : 0.65f);
            GL.Uniform1(_uAlpha, selected ? 0.22f : 0.10f);
            DrawGroundSegment(p + new Vector3(-r, y, -r), p + new Vector3( r, y, -r), color, 0.018f);
            DrawGroundSegment(p + new Vector3( r, y, -r), p + new Vector3( r, y,  r), color, 0.018f);
            DrawGroundSegment(p + new Vector3( r, y,  r), p + new Vector3(-r, y,  r), color, 0.018f);
            DrawGroundSegment(p + new Vector3(-r, y,  r), p + new Vector3(-r, y, -r), color, 0.018f);
        }
    }

    private void DrawGroundSegment(Vector3 a, Vector3 b, Vector3 color, float thickness)
    {
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);
        DrawRodSegment(a, b, thickness);
    }

    private static bool TriggerActionUsesTarget(TriggerActionKind action) => action switch
    {
        TriggerActionKind.ToggleGravity => false,
        _ => true,
    };

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
        TriggerActionKind.StartMotor => new Vector3(0.95f, 0.70f, 0.18f),
        TriggerActionKind.OpenGate => new Vector3(0.40f, 1.00f, 0.45f),
        TriggerActionKind.StartTimer => new Vector3(1.00f, 0.88f, 0.25f),
        TriggerActionKind.StartConveyor => new Vector3(0.25f, 0.90f, 1.0f),
        TriggerActionKind.StartPiston => new Vector3(1.00f, 0.35f, 0.28f),
        TriggerActionKind.ToggleDoor => new Vector3(0.35f, 0.85f, 1.00f),
        TriggerActionKind.LaunchCatapult => new Vector3(1.0f, 0.70f, 0.20f),
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
        int rc = w.FillRipples(_rippleBuffer, WaterVolume.MAX_RIPPLES);
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

    private uint TextureFor(RigidBody b, in ChildShape child)
    {
        if (IsArenaWall(b)) return _texBrick;
        if (string.Equals(b.Tag as string, "WoodenCartWheel", StringComparison.Ordinal)) return _texRustyMetal;
        if (string.Equals(b.Tag as string, "BeachBall", StringComparison.Ordinal)) return _texBeachBall;
        if (string.Equals(b.Tag as string, "MetalCube", StringComparison.Ordinal)) return _texMetalCube;
        if (string.Equals(b.Tag as string, "GasCylinder", StringComparison.Ordinal)) return _texGasCylinder;
        if (string.Equals(b.Tag as string, "SentinelBot", StringComparison.Ordinal)) return _texAndroid;
        if (IsWoodenCart(b)) return child.Shape == ShapeType.Sphere ? _texRustyMetal : _texCartWood;
        if (string.Equals(b.Tag as string, "CatapultLauncher", StringComparison.Ordinal)) return child.Shape == ShapeType.Sphere ? _texRustyMetal : _texCartWood;
        if (string.Equals(b.Tag as string, "BowlingPin", StringComparison.Ordinal)) return _texBowlingPin;
        if (string.Equals(b.Tag as string, "Hammer", StringComparison.Ordinal))
            return child.HalfExtents.Z > 0.15f ? _texMetal : _texCartWood;   // metal head, wood handle
        if (string.Equals(b.Tag as string, "Dumbbell", StringComparison.Ordinal)) return _texRustyMetal;
        if (string.Equals(b.Tag as string, "WreckingBallTarget", StringComparison.Ordinal)) return _texRustyMetal;
        if (string.Equals(b.Tag as string, "WreckingBallAnchor", StringComparison.Ordinal)) return _texRustyMetal;

        ShapeType shape = child.Shape;
        return b.IsStatic ? _texConcrete
         : IsExplosiveBarrel(b) ? _texBarrel
         : IsAndroidBody(b) ? _texAndroid
         : IsVehicleChassis(b) ? _texVehicle
         : IsVehicleWheel(b) ? _texTire
         : b.MaterialId switch
         {
             MaterialId.Wood => _texCrate,
             MaterialId.Metal => _texMetal,
             MaterialId.Stone => _texConcrete,
             MaterialId.Glass => _texGlass,
             MaterialId.Ice => _texStripes,
             MaterialId.Synthetic => shape == ShapeType.Capsule ? _texMetal : _texAndroid,
             _ => shape switch
             {
                 ShapeType.Sphere => _texBall,
                 ShapeType.Capsule => _texMetal,
                 _ => _texCrate,
             },
         };
    }

    private uint BumpTextureFor(RigidBody b, in ChildShape child)
    {
        if (IsArenaWall(b)) return _bumpBrick;
        if (string.Equals(b.Tag as string, "WoodenCartWheel", StringComparison.Ordinal)) return _bumpRustyMetal;
        if (string.Equals(b.Tag as string, "BeachBall", StringComparison.Ordinal)) return _bumpBeachBall;
        if (string.Equals(b.Tag as string, "MetalCube", StringComparison.Ordinal)) return _bumpMetalCube;
        if (string.Equals(b.Tag as string, "GasCylinder", StringComparison.Ordinal)) return _bumpGasCylinder;
        if (IsExplosiveBarrel(b)) return _bumpBarrel;
        if (IsWoodenCart(b)) return child.Shape == ShapeType.Sphere ? _bumpRustyMetal : _bumpCartWood;
        if (string.Equals(b.Tag as string, "CatapultLauncher", StringComparison.Ordinal)) return child.Shape == ShapeType.Sphere ? _bumpRustyMetal : _bumpCartWood;
        if (string.Equals(b.Tag as string, "BowlingPin", StringComparison.Ordinal)) return _bumpBowlingPin;
        if (string.Equals(b.Tag as string, "Hammer", StringComparison.Ordinal))
            return child.Shape == ShapeType.Box ? _bumpRustyMetal : _bumpCartWood;
        if (string.Equals(b.Tag as string, "Dumbbell", StringComparison.Ordinal) || string.Equals(b.Tag as string, "WreckingBallTarget", StringComparison.Ordinal)) return _bumpRustyMetal;
        if (IsVehicleChassis(b)) return _bumpVehicle;
        if (IsVehicleWheel(b)) return _bumpTire;
        if (b.MaterialId == MaterialId.Wood) return _bumpCrate;
        if (b.MaterialId == MaterialId.Glass) return _bumpGlass;
        if (child.Shape == ShapeType.Sphere && !IsVehicleWheel(b)) return _bumpBall;
        return 0;
    }

    private float BumpStrengthFor(RigidBody b, in ChildShape child)
    {
        if (IsArenaWall(b)) return 0.72f;
        if (string.Equals(b.Tag as string, "WoodenCartWheel", StringComparison.Ordinal)) return 0.28f;
        if (string.Equals(b.Tag as string, "BeachBall", StringComparison.Ordinal)) return 0.14f;
        if (string.Equals(b.Tag as string, "MetalCube", StringComparison.Ordinal) || string.Equals(b.Tag as string, "GasCylinder", StringComparison.Ordinal)) return 0.38f;
        if (IsExplosiveBarrel(b)) return 0.42f;
        if (IsWoodenCart(b)) return child.Shape == ShapeType.Sphere ? 0.24f : 0.55f;
        if (b.MaterialId == MaterialId.Wood || string.Equals(b.Tag as string, "Hammer", StringComparison.Ordinal)) return 0.52f;
        if (string.Equals(b.Tag as string, "Dumbbell", StringComparison.Ordinal) || string.Equals(b.Tag as string, "WreckingBallTarget", StringComparison.Ordinal)) return 0.42f;
        if (b.MaterialId == MaterialId.Glass) return 0.22f;
        if (child.Shape == ShapeType.Sphere && !IsVehicleWheel(b)) return 0.24f;
        if (IsVehicleChassis(b) || IsVehicleWheel(b)) return 0.18f;
        return 0f;
    }

    private bool IsMechanismBody(RigidBody b)
    {
        foreach (var m in _mechanisms)
            if (ReferenceEquals(m.Body, b)) return true;
        return false;
    }

    private static bool IsArenaWall(RigidBody b)
        => string.Equals(b.Tag as string, "ArenaWall", StringComparison.Ordinal);

    private static bool IsBowlingPin(RigidBody b)
        => string.Equals(b.Tag as string, "BowlingPin", StringComparison.Ordinal);

    private static bool IsWoodenCart(RigidBody b)
        => string.Equals(b.Tag as string, "WoodenCart", StringComparison.Ordinal);

    private static bool IsExplosiveBarrel(RigidBody b)
        => string.Equals(b.Tag as string, "ExplosiveBarrel", StringComparison.Ordinal)
           || (b.MaterialId == MaterialId.Explosive && b.Children.Length == 1 && b.Children[0].Shape == ShapeType.Capsule && b.ExplosivePower > 0.5f);

    private static bool IsAndroidBody(RigidBody b)
        => b.Tag is RagdollBone bone && bone.Android;

    private static bool IsVehicleChassis(RigidBody b)
        => string.Equals(b.Tag as string, "VehicleChassis", StringComparison.Ordinal)
        || string.Equals(b.Tag as string, "PoliceVehicleChassis", StringComparison.Ordinal)
        || string.Equals(b.Tag as string, "AmbulanceChassis", StringComparison.Ordinal);

    private static bool IsVehicleWheel(RigidBody b)
        => string.Equals(b.Tag as string, "VehicleWheel", StringComparison.Ordinal);

    private void DrawAndroidOverlay(RigidBody b, in ChildShape child)
    {
        var localToWorld = Matrix4x4.CreateFromQuaternion(child.LocalRot)
                         * Matrix4x4.CreateTranslation(child.LocalPos)
                         * Matrix4x4.CreateFromQuaternion(b.Rotation)
                         * Matrix4x4.CreateTranslation(b.Position);

        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 0.28f);
        GL.Uniform3(_uColor, 0.26f, 0.92f, 1.00f);

        if (child.Shape == ShapeType.Sphere)
        {
            var visor = Matrix4x4.CreateScale(child.Radius * 0.95f, child.Radius * 0.22f, child.Radius * 0.30f)
                      * Matrix4x4.CreateTranslation(0f, 0f, child.Radius * 0.82f)
                      * localToWorld;
            GL.UniformMatrix4(_uModel, ToArray(visor));
            _cubeMesh.Draw();

            var crown = Matrix4x4.CreateScale(child.Radius * 0.18f, child.Radius * 0.18f, child.Radius * 0.18f)
                      * Matrix4x4.CreateTranslation(0f, child.Radius * 0.92f, 0f)
                      * localToWorld;
            GL.UniformMatrix4(_uModel, ToArray(crown));
            _sphereMesh.Draw();
        }
        else if (child.Shape == ShapeType.Box)
        {
            var he = child.HalfExtents;
            // chest core / limb strips.
            var panel = Matrix4x4.CreateScale(he.X * 0.38f, he.Y * 0.34f, he.Z * 0.10f)
                      * Matrix4x4.CreateTranslation(0f, 0f, he.Z * 1.08f)
                      * localToWorld;
            GL.UniformMatrix4(_uModel, ToArray(panel));
            _cubeMesh.Draw();

            if (he.Y > he.X * 1.8f)
            {
                var band1 = Matrix4x4.CreateScale(he.X * 0.55f, he.Y * 0.08f, he.Z * 0.08f)
                          * Matrix4x4.CreateTranslation(0f, he.Y * 0.46f, he.Z * 1.06f)
                          * localToWorld;
                var band2 = Matrix4x4.CreateScale(he.X * 0.55f, he.Y * 0.08f, he.Z * 0.08f)
                          * Matrix4x4.CreateTranslation(0f, -he.Y * 0.46f, he.Z * 1.06f)
                          * localToWorld;
                GL.UniformMatrix4(_uModel, ToArray(band1)); _cubeMesh.Draw();
                GL.UniformMatrix4(_uModel, ToArray(band2)); _cubeMesh.Draw();
            }
        }

        GL.Uniform1(_uEmissive, 0f);
    }

    private void DrawVehicleChassisOverlay(RigidBody b, in ChildShape child)
    {
        if (child.Shape != ShapeType.Box) return;
        var localToWorld = Matrix4x4.CreateFromQuaternion(child.LocalRot)
                         * Matrix4x4.CreateTranslation(child.LocalPos)
                         * Matrix4x4.CreateFromQuaternion(b.Rotation)
                         * Matrix4x4.CreateTranslation(b.Position);
        var he = child.HalfExtents;

        // windshield
        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 0.05f);
        GL.Uniform3(_uColor, 0.12f, 0.16f, 0.20f);
        if (he.Y > 0.16f)
        {
            // Glass greenhouse, oriented to the real front (+X): slanted windshield, rear window, side glass.
            void Glass(Matrix4x4 m) { GL.UniformMatrix4(_uModel, ToArray(m * localToWorld)); _cubeMesh.Draw(); }
            // front windshield: thin slab at the front, top leaning back
            Glass(Matrix4x4.CreateScale(he.X * 0.10f, he.Y * 0.55f, he.Z * 0.84f)
                * Matrix4x4.CreateFromAxisAngle(Vector3.UnitZ, 0.55f)
                * Matrix4x4.CreateTranslation(he.X * 0.70f, he.Y * 0.28f, 0f));
            // rear window: opposite slant
            Glass(Matrix4x4.CreateScale(he.X * 0.10f, he.Y * 0.50f, he.Z * 0.82f)
                * Matrix4x4.CreateFromAxisAngle(Vector3.UnitZ, -0.50f)
                * Matrix4x4.CreateTranslation(-he.X * 0.74f, he.Y * 0.28f, 0f));
            // side windows
            foreach (float sz in new[] { -he.Z * 1.01f, he.Z * 1.01f })
                Glass(Matrix4x4.CreateScale(he.X * 0.62f, he.Y * 0.40f, he.Z * 0.05f)
                    * Matrix4x4.CreateTranslation(0f, he.Y * 0.26f, sz));

            // variant roof details
            string? vtag = b.Tag as string;
            if (string.Equals(vtag, "PoliceVehicleChassis", StringComparison.Ordinal))
            {
                foreach (var (sx, cr, cg, cb) in new[] { (-he.X * 0.16f, 0.95f, 0.10f, 0.10f), (he.X * 0.16f, 0.10f, 0.30f, 0.98f) })
                {
                    GL.Uniform1(_uEmissive, 0.95f);
                    GL.Uniform3(_uColor, cr, cg, cb);
                    var bar = Matrix4x4.CreateScale(he.X * 0.14f, he.Y * 0.16f, he.Z * 0.55f)
                            * Matrix4x4.CreateTranslation(sx, he.Y * 1.08f, 0f)
                            * localToWorld;
                    GL.UniformMatrix4(_uModel, ToArray(bar));
                    _cubeMesh.Draw();
                }
                GL.Uniform1(_uEmissive, 0f);
            }
            else if (string.Equals(vtag, "AmbulanceChassis", StringComparison.Ordinal))
            {
                GL.Uniform1(_uEmissive, 0.30f);
                GL.Uniform3(_uColor, 0.90f, 0.12f, 0.12f);
                var v1 = Matrix4x4.CreateScale(he.X * 0.10f, he.Y * 0.06f, he.Z * 0.46f)
                       * Matrix4x4.CreateTranslation(0f, he.Y * 1.04f, 0f) * localToWorld;
                GL.UniformMatrix4(_uModel, ToArray(v1)); _cubeMesh.Draw();
                var v2 = Matrix4x4.CreateScale(he.X * 0.30f, he.Y * 0.06f, he.Z * 0.15f)
                       * Matrix4x4.CreateTranslation(0f, he.Y * 1.04f, 0f) * localToWorld;
                GL.UniformMatrix4(_uModel, ToArray(v2)); _cubeMesh.Draw();
                GL.Uniform1(_uEmissive, 0f);
            }
        }
        else
        {
            // headlights at the front (+X), spread across the width (Z)
            GL.Uniform1(_uEmissive, 0.70f);
            GL.Uniform3(_uColor, 1.00f, 0.95f, 0.72f);
            foreach (float sz in new[] { -he.Z * 0.62f, he.Z * 0.62f })
            {
                var light = Matrix4x4.CreateScale(he.Y * 0.5f)
                          * Matrix4x4.CreateTranslation(he.X * 1.02f, -he.Y * 0.10f, sz)
                          * localToWorld;
                GL.UniformMatrix4(_uModel, ToArray(light));
                _sphereMesh.Draw();
            }
            // taillights at the back (-X), red
            GL.Uniform1(_uEmissive, 0.55f);
            GL.Uniform3(_uColor, 0.95f, 0.12f, 0.10f);
            foreach (float sz in new[] { -he.Z * 0.62f, he.Z * 0.62f })
            {
                var light = Matrix4x4.CreateScale(he.Y * 0.4f)
                          * Matrix4x4.CreateTranslation(-he.X * 1.02f, -he.Y * 0.10f, sz)
                          * localToWorld;
                GL.UniformMatrix4(_uModel, ToArray(light));
                _sphereMesh.Draw();
            }
            GL.Uniform1(_uEmissive, 0f);
        }
        GL.Uniform1(_uEmissive, 0f);
    }

    private void DrawVehicleWheelOverlay(RigidBody b, in ChildShape child)
    {
        var localToWorld = Matrix4x4.CreateFromQuaternion(child.LocalRot)
                         * Matrix4x4.CreateTranslation(child.LocalPos)
                         * Matrix4x4.CreateFromQuaternion(b.Rotation)
                         * Matrix4x4.CreateTranslation(b.Position);
        float r = child.Radius;
        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 0.03f);
        GL.Uniform3(_uColor, 0.76f, 0.78f, 0.82f);
        foreach (float z in new[] { -r * 0.62f, r * 0.62f })
        {
            var hub = Matrix4x4.CreateScale(r * 0.42f, r * 0.42f, r * 0.10f)
                    * Matrix4x4.CreateTranslation(0f, 0f, z)
                    * localToWorld;
            GL.UniformMatrix4(_uModel, ToArray(hub));
            _sphereMesh.Draw();
        }
        GL.Uniform1(_uEmissive, 0f);
    }

    private void DrawExplosiveBarrelOverlay(RigidBody b, in ChildShape child)
    {
        // The authored barrel texture already contains the drum ribs and explosive label.
        // Keep only a tiny dark bung cap; avoid big horizontal mesh plates that made it
        // look like a stack of disks rather than a real cylinder/drum.
        var localToWorld = Matrix4x4.CreateFromQuaternion(child.LocalRot)
                         * Matrix4x4.CreateTranslation(child.LocalPos)
                         * Matrix4x4.CreateFromQuaternion(b.Rotation)
                         * Matrix4x4.CreateTranslation(b.Position);

        float r = child.Radius;
        float half = child.HalfHeight;
        GL.BindTexture(GL.TEXTURE_2D, _texRustyMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 0f);
        GL.Uniform3(_uColor, 0.16f, 0.16f, 0.17f);
        var cap = Matrix4x4.CreateScale(r * 0.20f, r * 0.08f, r * 0.20f)
                * Matrix4x4.CreateTranslation(r * 0.16f, half + r * 0.55f, 0f)
                * localToWorld;
        GL.UniformMatrix4(_uModel, ToArray(cap));
        _sphereMesh.Draw();
    }

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

    private static bool IsCartWheel(RigidBody b)
        => string.Equals(b.Tag as string, "WoodenCartWheel", StringComparison.Ordinal);

    // Wheels are spheres on point joints, so friction/impacts can spin them about any axis.
    // Lock each wheel's spin to its axle (world Z for an axis-aligned vehicle/cart) so they roll
    // like real wheels instead of tumbling. (Assumes the rig is spawned axis-aligned, which it is.)
    private void LockWheelAxes()
    {
        foreach (var b in _world.Bodies)
        {
            if (b.IsStatic) continue;
            if (!IsVehicleWheel(b) && !IsCartWheel(b)) continue;
            float spin = b.AngularVelocity.Z;   // project onto the Z axle
            b.AngularVelocity = new Vector3(0f, 0f, spin);
        }
    }

    /// <summary>Draw an explosive barrel as a proper upright cylinder (its physics body is a capsule).</summary>
    private void DrawBarrelCylinder(RigidBody b, in ChildShape child)
    {
        float r = MathF.Max(child.HalfExtents.X, child.HalfExtents.Z);
        float halfH = child.HalfExtents.Y;
        var m = Matrix4x4.CreateScale(r, halfH, r)
              * Matrix4x4.CreateFromQuaternion(child.LocalRot)
              * Matrix4x4.CreateTranslation(child.LocalPos)
              * Matrix4x4.CreateFromQuaternion(b.Rotation)
              * Matrix4x4.CreateTranslation(b.Position);
        GL.UniformMatrix4(_uModel, ToArray(m));
        _cylinderMesh.Draw();
    }

    /// <summary>Draw a wheel as a cylinder/disc (physics body is a sphere). The axle is world Z
    /// for an axis-aligned vehicle/cart; the body's own rotation carries the rolling spin.</summary>
    private void DrawWheelCylinder(RigidBody b, in ChildShape child)
    {
        float r = child.Radius;
        float halfWidth = r * 0.55f;
        var m = Matrix4x4.CreateScale(r, halfWidth, r)
              * Matrix4x4.CreateRotationX(MathF.PI * 0.5f)              // axis Y -> Z (axle)
              * Matrix4x4.CreateFromQuaternion(b.Rotation)
              * Matrix4x4.CreateTranslation(b.Position + Vector3.Transform(child.LocalPos, b.Rotation));
        GL.UniformMatrix4(_uModel, ToArray(m));
        _cylinderMesh.Draw();
    }

    /// <summary>Render a dumbbell as a thin cylinder handle plus two thick weight discs (its
    /// physics is a box handle + two spheres). Cylinder axis is laid along the body X axis.</summary>
    private void DrawDumbbellPart(RigidBody b, in ChildShape child)
    {
        bool handle = child.Shape == ShapeType.Box;
        float radius = handle ? child.HalfExtents.Y : child.Radius;
        float halfLen = handle ? child.HalfExtents.X : child.Radius * 0.5f;
        var m = Matrix4x4.CreateScale(radius, halfLen, radius)
              * Matrix4x4.CreateRotationZ(MathF.PI * 0.5f)              // axis Y -> X (handle direction)
              * Matrix4x4.CreateTranslation(child.LocalPos)
              * Matrix4x4.CreateFromQuaternion(b.Rotation)
              * Matrix4x4.CreateTranslation(b.Position);
        GL.UniformMatrix4(_uModel, ToArray(m));
        _cylinderMesh.Draw();
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
