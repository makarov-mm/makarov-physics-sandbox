using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakarovPhysicsSandbox;

internal sealed partial class GlPanel
{
    private static readonly JsonSerializerOptions SceneJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void SaveScene(string path)
    {
        var bodyIndex = new Dictionary<RigidBody, int>();
        var bodies = new List<SceneBodyDto>();

        foreach (var b in _world.Bodies)
        {
            if (b.IsStatic && !b.UserObject) continue; // arena walls are rebuilt on load
            bodyIndex[b] = bodies.Count;
            bodies.Add(SceneBodyDto.FromBody(b));
        }

        var joints = new List<SceneJointDto>();
        foreach (var j in _world.Joints)
        {
            if (!bodyIndex.TryGetValue(j.A, out int a)) continue;
            int? b = null;
            if (j.B != null)
            {
                if (!bodyIndex.TryGetValue(j.B, out int bi)) continue;
                b = bi;
            }

            joints.Add(new SceneJointDto
            {
                Type = j.Type.ToString(),
                A = a,
                B = b,
                LocalA = V3.From(j.LocalA),
                LocalB = V3.From(j.LocalB),
                Length = j.Length,
                Stiffness = j.Stiffness,
                Damping = j.Damping,
            });
        }

        var dto = new SceneDto
        {
            Version = 1,
            SavedAtUtc = DateTime.UtcNow,
            ZeroGravity = _zeroG,
            WaterOn = _waterOn,
            Gravity = V3.From(_world.Gravity),
            Bodies = bodies,
            Joints = joints,
            Fields = _world.Fields.Select(SceneForceFieldDto.FromField).ToList(),
            Waters = _world.Waters.Select(SceneWaterDto.FromWater).ToList(),
            Triggers = _triggers.Select(SceneTriggerDto.FromTrigger).ToList(),
        };

        var json = JsonSerializer.Serialize(dto, SceneJsonOptions);
        File.WriteAllText(path, json);
    }

    public void LoadScene(string path)
    {
        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<SceneDto>(json, SceneJsonOptions)
                  ?? throw new InvalidDataException("Scene file is empty or invalid.");

        CancelPendingSceneAction();
        _jointFirstBody = null;
        _jointFirstLocal = Vector3.Zero;
        _jointFirstWorld = Vector3.Zero;
        _world.Grabbed = null;
        SelectBody(null);
        SelectTrigger(null);
        _particles.Clear();

        _world.Bodies.Clear();
        _world.Joints.Clear();
        _world.Fields.Clear();
        _world.Waters.Clear();
        _triggers.Clear();
        AddWalls();

        var loadedBodies = new List<RigidBody>();
        foreach (var bodyDto in dto.Bodies ?? [])
        {
            var body = bodyDto.ToBody();
            _world.Bodies.Add(body);
            loadedBodies.Add(body);
        }

        foreach (var jointDto in dto.Joints ?? [])
        {
            if (jointDto.A < 0 || jointDto.A >= loadedBodies.Count) continue;
            RigidBody? b = null;
            if (jointDto.B.HasValue)
            {
                if (jointDto.B.Value < 0 || jointDto.B.Value >= loadedBodies.Count) continue;
                b = loadedBodies[jointDto.B.Value];
            }

            if (!Enum.TryParse<Joint.Kind>(jointDto.Type, out var type)) type = Joint.Kind.Distance;
            _world.Joints.Add(new Joint
            {
                Type = type,
                A = loadedBodies[jointDto.A],
                B = b,
                LocalA = jointDto.LocalA.ToVector3(),
                LocalB = jointDto.LocalB.ToVector3(),
                Length = jointDto.Length,
                Stiffness = jointDto.Stiffness <= 0 ? 18f : jointDto.Stiffness,
                Damping = jointDto.Damping <= 0 ? 2.2f : jointDto.Damping,
            });
        }

        foreach (var fieldDto in dto.Fields ?? [])
        {
            if (!Enum.TryParse<ForceField.Kind>(fieldDto.Type, out var type)) continue;
            _world.Fields.Add(new ForceField
            {
                Type = type,
                Position = fieldDto.Position.ToVector3(),
                Radius = fieldDto.Radius,
                Strength = fieldDto.Strength,
                WindDir = fieldDto.WindDir.ToVector3(),
            });
        }

        foreach (var waterDto in dto.Waters ?? [])
        {
            _world.Waters.Add(new WaterVolume
            {
                Center = waterDto.Center.ToVector3(),
                HalfX = waterDto.HalfX,
                HalfZ = waterDto.HalfZ,
                SurfaceY = waterDto.SurfaceY,
                Density = waterDto.Density,
                LinearDrag = waterDto.LinearDrag,
                WaveAmplitude = waterDto.WaveAmplitude,
                Time = waterDto.Time,
            });
        }

        foreach (var triggerDto in dto.Triggers ?? [])
        {
            if (!Enum.TryParse<TriggerActionKind>(triggerDto.Action, out var action)) action = TriggerActionKind.Explosion;
            _triggers.Add(new SceneTrigger
            {
                Name = string.IsNullOrWhiteSpace(triggerDto.Name) ? "Trigger" : triggerDto.Name,
                Position = triggerDto.Position.ToVector3(),
                HalfExtents = triggerDto.HalfExtents.ToVector3(),
                Action = action,
                OneShot = triggerDto.OneShot,
                Enabled = triggerDto.Enabled,
                Radius = triggerDto.Radius <= 0 ? 5.0f : triggerDto.Radius,
                Strength = triggerDto.Strength <= 0 ? 10.0f : triggerDto.Strength,
                CooldownSeconds = triggerDto.CooldownSeconds <= 0 ? 1.0f : triggerDto.CooldownSeconds,
                TargetOffset = triggerDto.TargetOffset?.ToVector3() ?? Vector3.Zero,
            });
        }

        _zeroG = dto.ZeroGravity;
        _waterOn = dto.WaterOn || _world.Waters.Count > 0;
        _world.Gravity = _zeroG ? Vector3.Zero : dto.Gravity.ToVector3();
        if (_world.Gravity.LengthSquared() < 1e-6f && !_zeroG) _world.Gravity = DefaultGravity;

        foreach (var b in _world.Bodies) b.Wake();
        NotifyStateChanged();
        StatusUpdated?.Invoke($"Loaded scene: {loadedBodies.Count} object(s), {_world.Joints.Count} link(s).");
    }
}

internal sealed class SceneDto
{
    public int Version { get; set; }
    public DateTime SavedAtUtc { get; set; }
    public bool ZeroGravity { get; set; }
    public bool WaterOn { get; set; }
    public V3 Gravity { get; set; } = V3.From(new Vector3(0, -9.81f, 0));
    public List<SceneBodyDto>? Bodies { get; set; }
    public List<SceneJointDto>? Joints { get; set; }
    public List<SceneForceFieldDto>? Fields { get; set; }
    public List<SceneWaterDto>? Waters { get; set; }
    public List<SceneTriggerDto>? Triggers { get; set; }
}

internal sealed class SceneBodyDto
{
    public V3 Position { get; set; } = new();
    public Q4 Rotation { get; set; } = Q4.From(Quaternion.Identity);
    public V3 Velocity { get; set; } = new();
    public V3 AngularVelocity { get; set; } = new();
    public float Density { get; set; } = 1f;
    public float Restitution { get; set; }
    public float Friction { get; set; }
    public bool IsStatic { get; set; }
    public V3 Color { get; set; } = V3.From(new Vector3(0.8f));
    public bool Sleeping { get; set; }
    public bool Breakable { get; set; }
    public float BreakThreshold { get; set; } = 7.5f;
    public int BreakPieces { get; set; } = 8;
    public List<SceneChildShapeDto> Children { get; set; } = [];

    public static SceneBodyDto FromBody(RigidBody b) => new()
    {
        Position = V3.From(b.Position),
        Rotation = Q4.From(b.Rotation),
        Velocity = V3.From(b.Velocity),
        AngularVelocity = V3.From(b.AngularVelocity),
        Density = b.Density,
        Restitution = b.Restitution,
        Friction = b.Friction,
        IsStatic = b.IsStatic,
        Color = V3.From(b.Color),
        Sleeping = b.Sleeping,
        Breakable = b.Breakable,
        BreakThreshold = b.BreakThreshold,
        BreakPieces = b.BreakPieces,
        Children = b.Children.Select(SceneChildShapeDto.FromChild).ToList(),
    };

    public RigidBody ToBody()
    {
        var children = Children.Select(c => c.ToChild()).ToArray();
        if (children.Length == 0) children = [ChildShape.Box(new Vector3(0.5f))];

        var b = RigidBody.CreateCompound(Vector3.Zero, children, MathF.Max(Density, 0.001f));
        b.Position = Position.ToVector3();
        var q = Rotation.ToQuaternion();
        b.Rotation = Quaternion.Normalize(q == default ? Quaternion.Identity : q);
        b.Velocity = Velocity.ToVector3();
        b.AngularVelocity = AngularVelocity.ToVector3();
        b.Density = MathF.Max(Density, 0.001f);
        b.Restitution = Restitution;
        b.Friction = Friction;
        if (IsStatic) b.SetStatic(true);
        b.UserObject = true;
        b.Breakable = Breakable;
        b.BreakThreshold = BreakThreshold <= 0 ? 7.5f : BreakThreshold;
        b.BreakPieces = BreakPieces <= 0 ? 8 : BreakPieces;
        b.Color = Color.ToVector3();
        b.Sleeping = Sleeping;
        b.UpdateDerived();
        return b;
    }
}

internal sealed class SceneChildShapeDto
{
    public string Shape { get; set; } = nameof(ShapeType.Box);
    public V3 LocalPos { get; set; } = new();
    public Q4 LocalRot { get; set; } = Q4.From(Quaternion.Identity);
    public float Radius { get; set; }
    public V3 HalfExtents { get; set; } = new();
    public float HalfHeight { get; set; }

    public static SceneChildShapeDto FromChild(ChildShape c) => new()
    {
        Shape = c.Shape.ToString(),
        LocalPos = V3.From(c.LocalPos),
        LocalRot = Q4.From(c.LocalRot),
        Radius = c.Radius,
        HalfExtents = V3.From(c.HalfExtents),
        HalfHeight = c.HalfHeight,
    };

    public ChildShape ToChild()
    {
        if (!Enum.TryParse<ShapeType>(Shape, out var shape)) shape = ShapeType.Box;
        var q = LocalRot.ToQuaternion();
        if (q == default) q = Quaternion.Identity;
        return new ChildShape
        {
            Shape = shape,
            LocalPos = LocalPos.ToVector3(),
            LocalRot = Quaternion.Normalize(q),
            Radius = Radius,
            HalfExtents = HalfExtents.ToVector3(),
            HalfHeight = HalfHeight,
        };
    }
}

internal sealed class SceneJointDto
{
    public string Type { get; set; } = nameof(Joint.Kind.Distance);
    public int A { get; set; }
    public int? B { get; set; }
    public V3 LocalA { get; set; } = new();
    public V3 LocalB { get; set; } = new();
    public float Length { get; set; }
    public float Stiffness { get; set; }
    public float Damping { get; set; }
}

internal sealed class SceneForceFieldDto
{
    public string Type { get; set; } = nameof(ForceField.Kind.Attract);
    public V3 Position { get; set; } = new();
    public float Radius { get; set; }
    public float Strength { get; set; }
    public V3 WindDir { get; set; } = V3.From(Vector3.UnitX);

    public static SceneForceFieldDto FromField(ForceField f) => new()
    {
        Type = f.Type.ToString(),
        Position = V3.From(f.Position),
        Radius = f.Radius,
        Strength = f.Strength,
        WindDir = V3.From(f.WindDir),
    };
}

internal sealed class SceneWaterDto
{
    public V3 Center { get; set; } = new();
    public float HalfX { get; set; }
    public float HalfZ { get; set; }
    public float SurfaceY { get; set; }
    public float Density { get; set; }
    public float LinearDrag { get; set; }
    public float WaveAmplitude { get; set; }
    public float Time { get; set; }

    public static SceneWaterDto FromWater(WaterVolume w) => new()
    {
        Center = V3.From(w.Center),
        HalfX = w.HalfX,
        HalfZ = w.HalfZ,
        SurfaceY = w.SurfaceY,
        Density = w.Density,
        LinearDrag = w.LinearDrag,
        WaveAmplitude = w.WaveAmplitude,
        Time = w.Time,
    };
}


internal sealed class SceneTriggerDto
{
    public string Name { get; set; } = "Trigger";
    public V3 Position { get; set; } = new();
    public V3 HalfExtents { get; set; } = V3.From(new Vector3(0.9f, 0.08f, 0.9f));
    public string Action { get; set; } = nameof(TriggerActionKind.Explosion);
    public bool OneShot { get; set; }
    public bool Enabled { get; set; } = true;
    public float Radius { get; set; } = 5.0f;
    public float Strength { get; set; } = 10.0f;
    public float CooldownSeconds { get; set; } = 1.0f;
    public V3? TargetOffset { get; set; }

    public static SceneTriggerDto FromTrigger(SceneTrigger trigger) => new()
    {
        Name = trigger.Name,
        Position = V3.From(trigger.Position),
        HalfExtents = V3.From(trigger.HalfExtents),
        Action = trigger.Action.ToString(),
        OneShot = trigger.OneShot,
        Enabled = trigger.Enabled,
        Radius = trigger.Radius,
        Strength = trigger.Strength,
        CooldownSeconds = trigger.CooldownSeconds,
        TargetOffset = V3.From(trigger.TargetOffset),
    };
}

internal sealed class V3
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public static V3 From(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
    public Vector3 ToVector3() => new(X, Y, Z);
}

internal sealed class Q4
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float W { get; set; } = 1f;

    public static Q4 From(Quaternion q) => new() { X = q.X, Y = q.Y, Z = q.Z, W = q.W };
    public Quaternion ToQuaternion() => new(X, Y, Z, W);
}
