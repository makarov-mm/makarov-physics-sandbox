using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakarovPhysicsSandbox.Core;
using MakarovPhysicsSandbox.Dto;
using MakarovPhysicsSandbox.Physics;

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

        if (_world.Gravity.LengthSquared() < 1e-6f && !_zeroG)
        {
            _world.Gravity = DefaultGravity;
        }

        foreach (RigidBody body in _world.Bodies)
        {
            body.Wake();
        }

        NotifyStateChanged();
        StatusUpdated?.Invoke($"Loaded scene: {loadedBodies.Count} object(s), {_world.Joints.Count} link(s).");
    }
}
