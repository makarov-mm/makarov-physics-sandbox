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
            if (b is { IsStatic: true, UserObject: false }) continue; // arena walls are rebuilt on load
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
                LocalA = j.LocalA,
                LocalB = j.LocalB,
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
            Gravity = _world.Gravity,
            Bodies = bodies,
            Joints = joints,
            Fields = _world.Fields.Select(SceneForceFieldDto.FromField).ToList(),
            Waters = _world.Waters.Select(SceneWaterDto.FromWater).ToList(),
            Triggers = _triggers.Select(SceneTriggerDto.FromTrigger).ToList(),
            Mechanisms = CreateMechanismDtos(),
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
                LocalA = jointDto.LocalA,
                LocalB = jointDto.LocalB,
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
                Position = fieldDto.Position,
                Radius = fieldDto.Radius,
                Strength = fieldDto.Strength,
                WindDir = fieldDto.WindDir,
            });
        }

        foreach (var waterDto in dto.Waters ?? [])
        {
            _world.Waters.Add(new WaterVolume
            {
                Center = waterDto.Center,
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
            var trigger = new SceneTrigger
            {
                Id = string.IsNullOrWhiteSpace(triggerDto.Id) ? SceneId.New("trigger") : triggerDto.Id,
                Name = string.IsNullOrWhiteSpace(triggerDto.DisplayName)
                    ? string.IsNullOrWhiteSpace(triggerDto.Name) ? "Trigger" : triggerDto.Name
                    : triggerDto.DisplayName,
                Position = triggerDto.Position,
                HalfExtents = triggerDto.HalfExtents,
                Action = action,
                OneShot = triggerDto.OneShot,
                Enabled = triggerDto.Enabled,
                Radius = triggerDto.Radius <= 0 ? 5.0f : triggerDto.Radius,
                Strength = triggerDto.Strength <= 0 ? 10.0f : triggerDto.Strength,
                CooldownSeconds = triggerDto.CooldownSeconds <= 0 ? 1.0f : triggerDto.CooldownSeconds,
                TargetOffset = triggerDto.TargetOffset ?? Vector3.Zero,
            };
            foreach (var outputDto in triggerDto.Outputs ?? [])
                trigger.Outputs.Add(outputDto.ToOutput());
            _triggers.Add(trigger);
        }

        LoadMechanismDtos(dto.Mechanisms ?? []);

        _zeroG = dto.ZeroGravity;
        _waterOn = dto.WaterOn || _world.Waters.Count > 0;
        _world.Gravity = _zeroG ? Vector3.Zero : dto.Gravity;
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
    public Vector3 Gravity { get; set; } = new(0, -9.81f, 0);
    public List<SceneBodyDto>? Bodies { get; set; }
    public List<SceneJointDto>? Joints { get; set; }
    public List<SceneForceFieldDto>? Fields { get; set; }
    public List<SceneWaterDto>? Waters { get; set; }
    public List<SceneTriggerDto>? Triggers { get; set; }
    public List<SceneMechanismDto>? Mechanisms { get; set; }
}

internal sealed class SceneBodyDto
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Velocity { get; set; }
    public Vector3 AngularVelocity { get; set; }
    public float Density { get; set; } = 1f;
    public string MaterialId { get; set; } = "Custom";
    public float Restitution { get; set; }
    public float Friction { get; set; }
    public bool IsStatic { get; set; }
    public Vector3 Color { get; set; } = new(0.8f);
    public bool Sleeping { get; set; }
    public bool Breakable { get; set; }
    public float BreakThreshold { get; set; } = 7.5f;
    public int BreakPieces { get; set; } = 8;
    public float Flammability { get; set; } = 0.7f;
    public float Conductivity { get; set; } = 0.05f;
    public float ExplosivePower { get; set; }
    public float Wetness { get; set; }
    public float Temperature { get; set; } = 20f;
    public List<SceneChildShapeDto> Children { get; set; } = [];

    public static SceneBodyDto FromBody(RigidBody b) => new()
    {
        Position = b.Position,
        Rotation = b.Rotation,
        Velocity = b.Velocity,
        AngularVelocity = b.AngularVelocity,
        Density = b.Density,
        MaterialId = b.MaterialId.ToString(),
        Restitution = b.Restitution,
        Friction = b.Friction,
        IsStatic = b.IsStatic,
        Color = b.Color,
        Sleeping = b.Sleeping,
        Breakable = b.Breakable,
        BreakThreshold = b.BreakThreshold,
        BreakPieces = b.BreakPieces,
        Flammability = b.Flammability,
        Conductivity = b.Conductivity,
        ExplosivePower = b.ExplosivePower,
        Wetness = b.Wetness,
        Temperature = b.Temperature,
        Children = b.Children.Select(SceneChildShapeDto.FromChild).ToList(),
    };

    public RigidBody ToBody()
    {
        var children = Children.Select(c => c.ToChild()).ToArray();
        if (children.Length == 0) children = [ChildShape.Box(new Vector3(0.5f))];

        var b = RigidBody.CreateCompound(Vector3.Zero, children, MathF.Max(Density, 0.001f));
        b.Position = Position;
        Quaternion q = Rotation;
        b.Rotation = Quaternion.Normalize(q == default ? Quaternion.Identity : q);
        b.Velocity = Velocity;
        b.AngularVelocity = AngularVelocity;
        b.Density = MathF.Max(Density, 0.001f);
        b.MaterialId = Materials.TryParse(MaterialId, out var materialId) ? materialId : Materials.GuessFromValues(b);
        b.Restitution = Restitution;
        b.Friction = Friction;
        if (IsStatic) b.SetStatic(true);
        b.UserObject = true;
        b.Breakable = Breakable;
        b.BreakThreshold = BreakThreshold <= 0 ? 7.5f : BreakThreshold;
        b.BreakPieces = BreakPieces <= 0 ? 8 : BreakPieces;
        b.Flammability = Flammability;
        b.Conductivity = Conductivity;
        b.ExplosivePower = ExplosivePower;
        b.Wetness = Wetness;
        b.Temperature = Temperature <= 0 ? 20f : Temperature;
        b.Color = Color;
        b.Sleeping = Sleeping;
        b.UpdateDerived();
        return b;
    }
}

internal sealed class SceneChildShapeDto
{
    public string Shape { get; set; } = nameof(ShapeType.Box);
    public Vector3 LocalPos { get; set; }
    public Quaternion LocalRot { get; set; } = Quaternion.Identity;
    public float Radius { get; set; }
    public Vector3 HalfExtents { get; set; }
    public float HalfHeight { get; set; }

    public static SceneChildShapeDto FromChild(ChildShape c) => new()
    {
        Shape = c.Shape.ToString(),
        LocalPos = c.LocalPos,
        LocalRot = c.LocalRot,
        Radius = c.Radius,
        HalfExtents = c.HalfExtents,
        HalfHeight = c.HalfHeight,
    };

    public ChildShape ToChild()
    {
        if (!Enum.TryParse<ShapeType>(Shape, out var shape)) shape = ShapeType.Box;
        Quaternion q = LocalRot;
        if (q == default) q = Quaternion.Identity;
        return new ChildShape
        {
            Shape = shape,
            LocalPos = LocalPos,
            LocalRot = Quaternion.Normalize(q),
            Radius = Radius,
            HalfExtents = HalfExtents,
            HalfHeight = HalfHeight,
        };
    }
}

internal sealed class SceneJointDto
{
    public string Type { get; set; } = nameof(Joint.Kind.Distance);
    public int A { get; set; }
    public int? B { get; set; }
    public Vector3 LocalA { get; set; } = new();
    public Vector3 LocalB { get; set; } = new();
    public float Length { get; set; }
    public float Stiffness { get; set; }
    public float Damping { get; set; }
}

internal sealed class SceneForceFieldDto
{
    public string Type { get; set; } = nameof(ForceField.Kind.Attract);
    public Vector3 Position { get; set; } = new();
    public float Radius { get; set; }
    public float Strength { get; set; }
    public Vector3 WindDir { get; set; } = Vector3.UnitX;

    public static SceneForceFieldDto FromField(ForceField f) => new()
    {
        Type = f.Type.ToString(),
        Position = f.Position,
        Radius = f.Radius,
        Strength = f.Strength,
        WindDir = f.WindDir,
    };
}

internal sealed class SceneWaterDto
{
    public Vector3 Center { get; set; }
    public float HalfX { get; set; }
    public float HalfZ { get; set; }
    public float SurfaceY { get; set; }
    public float Density { get; set; }
    public float LinearDrag { get; set; }
    public float WaveAmplitude { get; set; }
    public float Time { get; set; }

    public static SceneWaterDto FromWater(WaterVolume w) => new()
    {
        Center = w.Center,
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
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Trigger";
    public string DisplayName { get; set; } = "";
    public Vector3 Position { get; set; }
    public Vector3 HalfExtents { get; set; } = new(0.9f, 0.08f, 0.9f);
    public string Action { get; set; } = nameof(TriggerActionKind.Explosion);
    public bool OneShot { get; set; }
    public bool Enabled { get; set; } = true;
    public float Radius { get; set; } = 5.0f;
    public float Strength { get; set; } = 10.0f;
    public float CooldownSeconds { get; set; } = 1.0f;
    public Vector3? TargetOffset { get; set; }
    public List<TriggerOutputDto>? Outputs { get; set; }

    public static SceneTriggerDto FromTrigger(SceneTrigger trigger) => new()
    {
        Id = trigger.Id,
        Name = trigger.Name,
        DisplayName = trigger.DisplayName,
        Position = trigger.Position,
        HalfExtents = trigger.HalfExtents,
        Action = trigger.Action.ToString(),
        OneShot = trigger.OneShot,
        Enabled = trigger.Enabled,
        Radius = trigger.Radius,
        Strength = trigger.Strength,
        CooldownSeconds = trigger.CooldownSeconds,
        TargetOffset = trigger.TargetOffset,
        Outputs = trigger.Outputs.Select(TriggerOutputDto.FromOutput).ToList(),
    };
}

internal sealed class TriggerOutputDto
{
    public string TargetId { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string Action { get; set; } = nameof(TriggerActionKind.Explosion);
    public float Delay { get; set; }
    public float Radius { get; set; } = 5.0f;
    public float Strength { get; set; } = 10.0f;
    public bool Enabled { get; set; } = true;

    public static TriggerOutputDto FromOutput(TriggerOutput output) => new()
    {
        TargetId = output.TargetId,
        TargetName = output.TargetName,
        Action = output.Action.ToString(),
        Delay = output.Delay,
        Radius = output.Radius,
        Strength = output.Strength,
        Enabled = output.Enabled,
    };

    public TriggerOutput ToOutput()
    {
        if (!Enum.TryParse<TriggerActionKind>(Action, out var action)) action = TriggerActionKind.Explosion;
        return new TriggerOutput
        {
            TargetId = TargetId ?? "",
            TargetName = TargetName ?? "",
            Action = action,
            Delay = MathF.Max(0f, Delay),
            Radius = Radius <= 0 ? 5.0f : Radius,
            Strength = Strength <= 0 ? 10.0f : Strength,
            Enabled = Enabled,
        };
    }
}
