using MakarovPhysicsSandbox.Physics;
using System.Numerics;
using MakarovPhysicsSandbox.Campaign;
using MakarovPhysicsSandbox.Core;
using MakarovPhysicsSandbox.Material;

namespace MakarovPhysicsSandbox;

internal sealed partial class GlPanel
{
    private readonly List<SceneMechanism> _mechanisms = new();
    public void SpawnMotor() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Motor); Focus(); } }
    public void SpawnGate()  { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Gate); Focus(); } }
    public void SpawnTimer() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Timer); Focus(); } }
    public void SpawnConveyor() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Conveyor); Focus(); } }
    public void SpawnPiston() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.Piston); Focus(); } }
    public void SpawnSlidingDoor() { if (_initialized) { ArmSceneAction(PendingSceneActionKind.SlidingDoor); Focus(); } }


    public void SnapSelectedTriggerTargetToNearestMechanism()
    {
        if (_selectedTrigger == null)
        {
            StatusUpdated?.Invoke("Select a trigger plate first, then press F7 to target a mechanism.");
            return;
        }
        if (_mechanisms.Count == 0)
        {
            StatusUpdated?.Invoke("No mechanisms exist in the scene yet.");
            return;
        }

        SceneTrigger? tr = _selectedTrigger;
        MechanismKind? wanted = MechanismKindForTriggerAction(tr.Action);
        SceneMechanism? best = null;
        float bestSq = float.MaxValue;

        foreach (var m in _mechanisms)
        {
            if (!m.Enabled) continue;
            if (wanted.HasValue && m.Kind != wanted.Value) continue;
            float d = Vector3.DistanceSquared(tr.Position, m.Position);

            if (d < bestSq)
            {
                bestSq = d;
                best = m;
            }
        }

        // If the trigger action is not mechanism-specific, fall back to the nearest mechanism.
        if (best is null && wanted.HasValue)
        {
            foreach (SceneMechanism m in _mechanisms)
            {
                if (!m.Enabled) continue;
                float d = Vector3.DistanceSquared(tr.Position, m.Position);
                if (d < bestSq)
                {
                    bestSq = d;
                    best = m;
                }
            }
        }

        if (best == null)
        {
            StatusUpdated?.Invoke("No enabled mechanism found for trigger target snapping.");
            return;
        }

        tr.TargetPosition = best.Position;
        tr.Radius = MathF.Max(tr.Radius, MathF.Sqrt(bestSq) + 0.75f);
        tr.Outputs.Clear();
        tr.Outputs.Add(new TriggerOutput
        {
            TargetId = best.Id,
            TargetName = best.DisplayName,
            Action = tr.Action,
            Delay = 0f,
            Radius = tr.Radius,
            Strength = tr.Strength,
            Enabled = true,
        });
        tr.Pulse = 0.55f;
        NotifyTriggerSelectionChanged();
        NotifyStateChanged();
        StatusUpdated?.Invoke($"Trigger target snapped to {best.Name} ({best.Kind}).");
        Focus();
    }

    private static MechanismKind? MechanismKindForTriggerAction(TriggerActionKind action) => action switch
    {
        TriggerActionKind.StartMotor => MechanismKind.Motor,
        TriggerActionKind.OpenGate => MechanismKind.Gate,
        TriggerActionKind.StartTimer => MechanismKind.Timer,
        TriggerActionKind.StartConveyor => MechanismKind.Conveyor,
        TriggerActionKind.StartPiston => MechanismKind.Piston,
        TriggerActionKind.ToggleDoor => MechanismKind.SlidingDoor,
        _ => null,
    };

    private void ClearMechanisms()
    {
        _mechanisms.Clear();
    }

    private void AddMotorAtAim()
    {
        var p = _aimPoint + new Vector3(0, 0.65f, 0);
        var body = RigidBody.CreateBox(p, new Vector3(1.0f, 0.12f, 0.16f), density: 1.15f);
        body = WithMaterial(body, MaterialId.Metal);
        body.Color = new Vector3(0.95f, 0.72f, 0.22f);
        body.Restitution = 0.15f;
        body.Friction = 0.45f;
        body.Wake();
        _world.Bodies.Add(body);
        _world.Joints.Add(new Joint
        {
            Type = Joint.Kind.Point,
            A = body,
            B = null,
            LocalA = Vector3.Zero,
            LocalB = p,
        });

        _mechanisms.Add(new SceneMechanism
        {
            Name = "Motor hinge",
            Kind = MechanismKind.Motor,
            Position = p,
            Body = body,
            Active = false,
            Radius = 5f,
            MotorSpeed = 7.0f,
            MotorTorque = 1.0f,
            Strength = 1.0f,
        });
        StatusUpdated?.Invoke("Placed motor hinge. Trigger it with a StartMotor plate or timer.");
    }

    private void AddGateAtAim()
    {
        Vector3 closed = _aimPoint + new Vector3(0, 1.0f, 0);
        RigidBody body = RigidBody.CreateStaticBox(closed, new Vector3(0.18f, 1.0f, 1.35f));
        body.UserObject = true;
        body.MaterialId = MaterialId.Metal;
        body.Color = new Vector3(0.36f, 0.48f, 0.58f);
        body.Conductivity = 0.9f;
        _world.Bodies.Add(body);

        _mechanisms.Add(new SceneMechanism
        {
            Name = "Gate",
            Kind = MechanismKind.Gate,
            Position = closed,
            Body = body,
            ClosedPosition = closed,
            OpenPosition = closed + new Vector3(0, 2.4f, 0),
            Radius = 6f,
            OpenSpeed = 1.8f,
        });
        StatusUpdated?.Invoke("Placed gate. Trigger it with an OpenGate plate or timer.");
    }

    private void AddTimerAtAim()
    {
        Vector3 p = _aimPoint + new Vector3(0, 0.12f, 0);

        _mechanisms.Add(new SceneMechanism
        {
            Name = "Timer",
            Kind = MechanismKind.Timer,
            Position = p,
            Radius = 7f,
            Delay = 1.5f,
            Remaining = 1.5f,
            TimerAction = TimerMechanismAction.Chain,
        });

        StatusUpdated?.Invoke("Placed timer. A StartTimer trigger will count down, then open gates and start motors.");
    }

    private void AddConveyorAtAim()
    {
        Vector3 p = _aimPoint + new Vector3(0, 0.14f, 0);
        RigidBody body = RigidBody.CreateStaticBox(p, new Vector3(2.1f, 0.08f, 0.78f));

        body.UserObject = true;
        body.MaterialId = MaterialId.Rubber;
        body.Color = new Vector3(0.08f, 0.10f, 0.12f);
        body.Friction = 1.35f;
        _world.Bodies.Add(body);

        _mechanisms.Add(new SceneMechanism
        {
            Name = "Conveyor belt",
            Kind = MechanismKind.Conveyor,
            Position = p,
            Body = body,
            Active = true,
            Radius = 6f,
            Strength = 2.4f,
            MotorAxis = Vector3.UnitX,
        });

        StatusUpdated?.Invoke("Placed conveyor belt. It pushes dynamic objects along its arrow direction.");
    }


    private void AddPistonAtAim()
    {
        Vector3 closed = _aimPoint + new Vector3(0, 0.55f, 0);
        RigidBody body = RigidBody.CreateStaticBox(closed, new Vector3(0.70f, 0.26f, 0.58f));

        body.UserObject = true;
        body.MaterialId = MaterialId.Metal;
        body.Color = new Vector3(0.74f, 0.24f, 0.18f);
        body.Friction = 0.85f;
        body.Conductivity = 0.75f;
        _world.Bodies.Add(body);

        _mechanisms.Add(new SceneMechanism
        {
            Name = "Piston actuator",
            Kind = MechanismKind.Piston,
            Position = closed,
            Body = body,
            ClosedPosition = closed,
            OpenPosition = closed + new Vector3(2.15f, 0f, 0f),
            Radius = 6.0f,
            OpenSpeed = 2.6f,
            Strength = 2.0f,
            Active = false,
            MotorAxis = Vector3.UnitX,
        });

        StatusUpdated?.Invoke("Placed piston actuator. Trigger it with StartPiston to push objects along its stroke.");
    }

    private void AddSlidingDoorAtAim()
    {
        var closed = _aimPoint + new Vector3(0, 1.0f, 0);
        var body = RigidBody.CreateStaticBox(closed, new Vector3(0.20f, 1.05f, 1.15f));
        body.UserObject = true;
        body.MaterialId = MaterialId.Metal;
        body.Color = new Vector3(0.22f, 0.44f, 0.62f);
        body.Conductivity = 0.9f;
        _world.Bodies.Add(body);

        _mechanisms.Add(new SceneMechanism
        {
            Name = "Sliding door",
            Kind = MechanismKind.SlidingDoor,
            Position = closed,
            Body = body,
            ClosedPosition = closed,
            OpenPosition = closed + new Vector3(0f, 0f, 2.4f),
            Radius = 6.0f,
            OpenSpeed = 1.8f,
            Active = false,
            MotorAxis = Vector3.UnitZ,
        });

        StatusUpdated?.Invoke("Placed sliding door. Trigger it with ToggleDoor to open/close the passage.");
    }

    private void UpdateMechanisms(float dt)
    {
        if (dt <= 0f || _mechanisms.Count == 0) return;

        foreach (var m in _mechanisms)
        {
            if (!m.Enabled) continue;

            if (m is { Kind: MechanismKind.Gate, Body: not null })
            {
                float target = m.Active ? 1f : 0f;
                float step = MathF.Max(0.05f, m.OpenSpeed) * dt;
                if (m.OpenAmount < target) m.OpenAmount = MathF.Min(target, m.OpenAmount + step);
                else if (m.OpenAmount > target) m.OpenAmount = MathF.Max(target, m.OpenAmount - step);

                m.Body.Position = Vector3.Lerp(m.ClosedPosition, m.OpenPosition, Smooth01(m.OpenAmount));
                m.Body.UpdateDerived();
                m.Position = m.Body.Position;
            }
            else if (m.Kind == MechanismKind.Motor && m.Body != null)
            {
                m.MotorSpin += (m.Active ? m.MotorSpeed : 0.35f) * dt;
                if (m.Active)
                {
                    m.Body.AngularVelocity += m.MotorAxis * (m.MotorSpeed * m.MotorTorque * dt * 6.0f);
                    m.Body.Velocity *= 0.995f;
                    m.Body.Wake();
                }
                m.Position = m.Body.Position;
            }
            else if (m.Kind == MechanismKind.Timer && m.TimerRunning)
            {
                m.Remaining -= dt;
                if (m.Remaining <= 0f)
                {
                    m.TimerRunning = false;
                    m.Active = false;
                    FireTimerMechanism(m);
                }
            }
            else if (m.Kind == MechanismKind.Conveyor)
            {
                UpdateConveyor(m, dt);
            }
            else if ((m.Kind == MechanismKind.Piston || m.Kind == MechanismKind.SlidingDoor) && m.Body != null)
            {
                float target = m.Active ? 1f : 0f;
                float step = MathF.Max(0.05f, m.OpenSpeed) * dt;
                if (m.OpenAmount < target) m.OpenAmount = MathF.Min(target, m.OpenAmount + step);
                else if (m.OpenAmount > target) m.OpenAmount = MathF.Max(target, m.OpenAmount - step);

                m.Body.Position = Vector3.Lerp(m.ClosedPosition, m.OpenPosition, Smooth01(m.OpenAmount));
                m.Body.UpdateDerived();
                m.Position = m.Body.Position;

                if (m.Kind == MechanismKind.Piston && m.Active)
                    PushBodiesNearPiston(m, dt);
            }
        }
    }

    private void UpdateConveyor(SceneMechanism m, float dt)
    {
        if (!m.Active || dt <= 0f) return;
        var dir = m.MotorAxis.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(m.MotorAxis);
        float halfX = 2.25f, halfZ = 0.90f;
        foreach (var b in _world.Bodies)
        {
            if (b.IsStatic) continue;
            var rel = b.Position - m.Position;
            if (MathF.Abs(rel.X) > halfX || MathF.Abs(rel.Z) > halfZ) continue;
            if (rel.Y < 0.0f || rel.Y > 1.25f) continue;

            b.Velocity += dir * (m.Strength * dt * 5.0f);
            b.Velocity = new Vector3(b.Velocity.X, b.Velocity.Y * 0.995f, b.Velocity.Z);
            b.Wake();
        }
    }


    private void PushBodiesNearPiston(SceneMechanism m, float dt)
    {
        var dir = m.MotorAxis.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(m.MotorAxis);
        foreach (var b in _world.Bodies)
        {
            if (b.IsStatic || b == m.Body) continue;
            var rel = b.Position - m.Position;
            if (Vector3.Dot(rel, dir) < -0.35f || Vector3.Dot(rel, dir) > 1.25f) continue;
            if (MathF.Abs(rel.Y) > 1.05f || MathF.Abs(rel.Z) > 1.15f) continue;
            b.Velocity += dir * (m.Strength * dt * 7.5f);
            b.Wake();
        }
    }

    private static float Smooth01(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    private void ActivateNearestMechanism(MechanismKind kind, Vector3 origin, float radius)
    {
        SceneMechanism? best = null;
        float bestSq = MathF.Max(radius, 1f) * MathF.Max(radius, 1f);
        foreach (var m in _mechanisms)
        {
            if (!m.Enabled || m.Kind != kind) continue;
            float d = Vector3.DistanceSquared(m.Position, origin);
            if (d < bestSq)
            {
                bestSq = d;
                best = m;
            }
        }

        if (best == null)
        {
            StatusUpdated?.Invoke($"No {kind} mechanism in trigger radius.");
            return;
        }

        ActivateMechanism(best);
    }

    private bool TryGetMechanismPositionById(string id, out Vector3 position)
    {
        foreach (var m in _mechanisms)
        {
            if (string.Equals(m.Id, id, StringComparison.Ordinal))
            {
                position = m.Position;
                return true;
            }
        }
        position = default;
        return false;
    }

    private bool TryGetMechanismKindById(string id, out MechanismKind kind)
    {
        foreach (var m in _mechanisms)
        {
            if (string.Equals(m.Id, id, StringComparison.Ordinal))
            {
                kind = m.Kind;
                return true;
            }
        }
        kind = default;
        return false;
    }

    private bool ActivateMechanismById(string id, TriggerActionKind action)
    {
        var expected = MechanismKindForTriggerAction(action);
        foreach (var m in _mechanisms)
        {
            if (!string.Equals(m.Id, id, StringComparison.Ordinal)) continue;
            if (expected.HasValue && m.Kind != expected.Value)
            {
                StatusUpdated?.Invoke($"Trigger target '{m.DisplayName}' exists, but action {action} expects {expected.Value}.");
                return false;
            }
            ActivateMechanism(m);
            return true;
        }
        return false;
    }

    private void ActivateMechanism(SceneMechanism m)
    {
        if (!m.Enabled) return;
        switch (m.Kind)
        {
            case MechanismKind.Motor:
                m.Active = true;
                m.Body?.Wake();
                SpawnMotorBurst(m.Position);
                break;
            case MechanismKind.Gate:
                m.Active = true;
                SpawnGatePulse(m.Position);
                break;
            case MechanismKind.Timer:
                m.Active = true;
                m.TimerRunning = true;
                m.Remaining = MathF.Max(0.15f, m.Delay);
                SpawnTimerPulse(m.Position);
                break;
            case MechanismKind.Conveyor:
                m.Active = true;
                SpawnConveyorPulse(m.Position);
                break;
            case MechanismKind.Piston:
                m.Active = true;
                SpawnPistonPulse(m.Position);
                break;
            case MechanismKind.SlidingDoor:
                m.Active = !m.Active;
                SpawnDoorPulse(m.Position);
                break;
        }
    }

    private void FireTimerMechanism(SceneMechanism timer)
    {
        SpawnTimerPulse(timer.Position + new Vector3(0, 0.25f, 0));
        switch (timer.TimerAction)
        {
            case TimerMechanismAction.OpenGate:
                ActivateNearestMechanism(MechanismKind.Gate, timer.Position, timer.Radius);
                break;
            case TimerMechanismAction.StartMotor:
                ActivateNearestMechanism(MechanismKind.Motor, timer.Position, timer.Radius);
                break;
            case TimerMechanismAction.StartConveyor:
                ActivateNearestMechanism(MechanismKind.Conveyor, timer.Position, timer.Radius);
                break;
            case TimerMechanismAction.StartPiston:
                ActivateNearestMechanism(MechanismKind.Piston, timer.Position, timer.Radius);
                break;
            case TimerMechanismAction.ToggleDoor:
                ActivateNearestMechanism(MechanismKind.SlidingDoor, timer.Position, timer.Radius);
                break;
            case TimerMechanismAction.Explosion:
                ApplyExplosionAt(timer.Position + new Vector3(0, 0.35f, 0), 4.0f, 8.0f);
                break;
            default:
                ActivateNearestMechanism(MechanismKind.Gate, timer.Position, timer.Radius);
                ActivateNearestMechanism(MechanismKind.SlidingDoor, timer.Position, timer.Radius);
                ActivateNearestMechanism(MechanismKind.Motor, timer.Position, timer.Radius);
                ActivateNearestMechanism(MechanismKind.Conveyor, timer.Position, timer.Radius);
                ActivateNearestMechanism(MechanismKind.Piston, timer.Position, timer.Radius);
                break;
        }
        StatusUpdated?.Invoke("Timer fired: chain reaction advanced.");
    }

    private void SpawnMotorBurst(Vector3 p)
    {
        AddBeam(p + new Vector3(-0.6f, 0.1f, 0), p + new Vector3(0.6f, 0.1f, 0), new Vector3(1.0f, 0.75f, 0.20f), 0.35f, 0.035f);
        for (int i = 0; i < 14; i++) AddParticle(p, RandomUnitVector() * (1.2f + _rng.NextSingle() * 2.4f), new Vector3(1.0f, 0.72f, 0.22f), 0.45f, 0.07f, false);
    }

    private void SpawnGatePulse(Vector3 p)
    {
        for (int i = 0; i < 18; i++) AddParticle(p + new Vector3(0, -0.8f, 0), RandomUnitVector() * (0.8f + _rng.NextSingle() * 1.4f), new Vector3(0.35f, 1.0f, 0.45f), 0.55f, 0.08f, false);
    }

    private void SpawnTimerPulse(Vector3 p)
    {
        for (int i = 0; i < 18; i++) AddParticle(p, RandomUnitVector() * (0.7f + _rng.NextSingle() * 1.8f), new Vector3(1.0f, 0.88f, 0.25f), 0.45f, 0.07f, false);
    }

    private void SpawnConveyorPulse(Vector3 p)
    {
        for (int i = 0; i < 18; i++) AddParticle(p + new Vector3(0, 0.18f, 0), new Vector3(1.0f, 0.15f + _rng.NextSingle() * 0.15f, (_rng.NextSingle() - 0.5f) * 0.7f), new Vector3(0.25f, 0.90f, 1.0f), 0.35f, 0.055f, false);
    }

    private void SpawnPistonPulse(Vector3 p)
    {
        AddBeam(p + new Vector3(-0.9f, 0.1f, 0), p + new Vector3(0.9f, 0.1f, 0), new Vector3(1.0f, 0.35f, 0.25f), 0.30f, 0.04f);
        for (int i = 0; i < 18; i++) AddParticle(p, RandomUnitVector() * (0.9f + _rng.NextSingle() * 1.8f), new Vector3(1.0f, 0.35f, 0.22f), 0.40f, 0.065f, false);
    }

    private void SpawnDoorPulse(Vector3 p)
    {
        for (int i = 0; i < 16; i++) AddParticle(p, RandomUnitVector() * (0.7f + _rng.NextSingle() * 1.5f), new Vector3(0.35f, 0.85f, 1.0f), 0.45f, 0.065f, false);
    }

    private Vector3 RandomUnitVector()
    {
        float z = _rng.NextSingle() * 2f - 1f;
        float a = _rng.NextSingle() * MathF.Tau;
        float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(MathF.Cos(a) * r, z, MathF.Sin(a) * r);
    }

    private void DrawMechanisms()
    {
        if (_mechanisms.Count == 0) return;

        GL.BindTexture(GL.TEXTURE_2D, _texMetal);
        GL.Uniform1(_uUvScale, 1f);
        GL.Uniform1(_uEmissive, 0.55f);

        float t = (float)_sw.Elapsed.TotalSeconds;
        foreach (var m in _mechanisms)
        {
            switch (m.Kind)
            {
                case MechanismKind.Motor:
                    DrawMotorMarker(m, t);
                    break;
                case MechanismKind.Gate:
                    DrawGateMarker(m, t);
                    break;
                case MechanismKind.Timer:
                    DrawTimerMarker(m, t);
                    break;
                case MechanismKind.Conveyor:
                    DrawConveyorMarker(m, t);
                    break;
                case MechanismKind.Piston:
                    DrawPistonMarker(m, t);
                    break;
                case MechanismKind.SlidingDoor:
                    DrawSlidingDoorMarker(m, t);
                    break;
            }
        }

        GL.Uniform1(_uEmissive, 0f);
        GL.Uniform1(_uAlpha, 1f);
    }

    private void DrawMotorMarker(SceneMechanism m, float t)
    {
        var p = m.Position + new Vector3(0, 0.38f, 0);
        var color = m.Active ? new Vector3(1.0f, 0.78f, 0.20f) : new Vector3(0.76f, 0.55f, 0.18f);
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);
        float angle = m.MotorSpin + t * (m.Active ? 2.0f : 0.25f);
        var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
        DrawRodSegment(p + Vector3.Transform(new Vector3(-0.72f, 0, 0), rot), p + Vector3.Transform(new Vector3(0.72f, 0, 0), rot), 0.045f);
        DrawRodSegment(p + Vector3.Transform(new Vector3(0, 0, -0.72f), rot), p + Vector3.Transform(new Vector3(0, 0, 0.72f), rot), 0.045f);
    }

    private void DrawGateMarker(SceneMechanism m, float t)
    {
        var p = Vector3.Lerp(m.ClosedPosition, m.OpenPosition, Smooth01(m.OpenAmount));
        var color = m.Active ? new Vector3(0.35f, 1.0f, 0.45f) : new Vector3(0.28f, 0.65f, 0.42f);
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);
        DrawRodSegment(m.ClosedPosition + new Vector3(0, -1.1f, -1.45f), m.ClosedPosition + new Vector3(0, 1.45f, -1.45f), 0.04f);
        DrawRodSegment(m.ClosedPosition + new Vector3(0, -1.1f, 1.45f), m.ClosedPosition + new Vector3(0, 1.45f, 1.45f), 0.04f);
        if (m.Active)
            AddBeam(p + new Vector3(0, -0.95f, -1.35f), p + new Vector3(0, -0.95f, 1.35f), color, 0.08f, 0.03f);
    }

    private void DrawConveyorMarker(SceneMechanism m, float t)
    {
        var p = m.Position + new Vector3(0, 0.085f, 0);   // sit on the belt top (box half-height 0.08), not above it
        var dir = m.MotorAxis.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(m.MotorAxis);
        float flow = (t * MathF.Max(0.2f, m.Strength)) % 1.0f;
        var color = m.Active ? new Vector3(0.25f, 0.90f, 1.0f) : new Vector3(0.18f, 0.36f, 0.42f);
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);
        for (int i = -2; i <= 2; i++)
        {
            float x = -1.6f + i * 0.75f + flow * 0.75f;
            var center = p + dir * x;
            DrawRodSegment(center - dir * 0.18f + new Vector3(0, 0.02f, -0.42f), center + dir * 0.18f + new Vector3(0, 0.02f, 0.42f), 0.025f);
        }
    }

    private void DrawTimerMarker(SceneMechanism m, float t)
    {
        var p = m.Position + new Vector3(0, 0.08f, 0);
        float pulse = m.TimerRunning ? 0.55f + 0.45f * MathF.Sin(t * 14f) : 0.25f + 0.08f * MathF.Sin(t * 3f);
        var color = new Vector3(1.0f, 0.82f + pulse * 0.18f, 0.20f);
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);
        GL.UniformMatrix4(_uModel, ToArray(Matrix4x4.CreateScale(0.35f, 0.10f, 0.35f) * Matrix4x4.CreateTranslation(p)));
        _cubeMesh.Draw();

        if (m.TimerRunning)
        {
            float ring = 0.55f + (m.Delay <= 0 ? 0 : (1f - Math.Clamp(m.Remaining / m.Delay, 0f, 1f))) * 0.55f;
            DrawRodSegment(p + new Vector3(-ring, 0.08f, -ring), p + new Vector3(ring, 0.08f, -ring), 0.025f);
            DrawRodSegment(p + new Vector3(ring, 0.08f, -ring), p + new Vector3(ring, 0.08f, ring), 0.025f);
            DrawRodSegment(p + new Vector3(ring, 0.08f, ring), p + new Vector3(-ring, 0.08f, ring), 0.025f);
            DrawRodSegment(p + new Vector3(-ring, 0.08f, ring), p + new Vector3(-ring, 0.08f, -ring), 0.025f);
        }
    }

    private void DrawPistonMarker(SceneMechanism m, float t)
    {
        var dir = m.MotorAxis.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(m.MotorAxis);
        var p = Vector3.Lerp(m.ClosedPosition, m.OpenPosition, Smooth01(m.OpenAmount)) + new Vector3(0, 0.12f, 0);
        var color = m.Active ? new Vector3(1.0f, 0.35f, 0.24f) : new Vector3(0.62f, 0.25f, 0.18f);
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);
        DrawRodSegment(m.ClosedPosition + new Vector3(0, 0.02f, 0), m.OpenPosition + new Vector3(0, 0.02f, 0), 0.035f);
        DrawRodSegment(p - dir * 0.35f, p + dir * 0.35f, 0.065f);
        if (m.Active)
            AddBeam(p - dir * 0.55f, p + dir * 0.55f, color, 0.08f, 0.035f);
    }

    private void DrawSlidingDoorMarker(SceneMechanism m, float t)
    {
        var p = Vector3.Lerp(m.ClosedPosition, m.OpenPosition, Smooth01(m.OpenAmount));
        var color = m.Active ? new Vector3(0.35f, 0.85f, 1.0f) : new Vector3(0.25f, 0.48f, 0.65f);
        GL.Uniform3(_uColor, color.X, color.Y, color.Z);
        DrawRodSegment(m.ClosedPosition + new Vector3(0, -1.1f, -1.25f), m.ClosedPosition + new Vector3(0, 1.35f, -1.25f), 0.04f);
        DrawRodSegment(m.ClosedPosition + new Vector3(0, -1.1f, 1.25f), m.ClosedPosition + new Vector3(0, 1.35f, 1.25f), 0.04f);
        DrawRodSegment(m.ClosedPosition + new Vector3(0, 1.35f, -1.25f), m.ClosedPosition + new Vector3(0, 1.35f, 1.25f), 0.04f);
        if (m.Active)
            AddBeam(p + new Vector3(0, -1.0f, -1.1f), p + new Vector3(0, -1.0f, 1.1f), color, 0.08f, 0.03f);
    }


    private void BuildAndroidCrashTestChamber()
    {
        ResetToEmptyScene();
        _challengeBodies.Clear();

        // Designed vertical slice lane: visible trigger -> graph outputs -> door/conveyor/piston -> barrel/android payoff.
        _aimPoint = new Vector3(-3.9f, 0f, 0f);
        AddPistonAtAim();
        var piston = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.Piston);
        if (piston != null)
        {
            piston.Name = "Launch piston";
            piston.OpenSpeed = 3.3f;
            piston.Strength = 4.4f;
            piston.Radius = 8.0f;
        }

        _aimPoint = new Vector3(-0.4f, 0f, 0f);
        AddSlidingDoorAtAim();
        var door = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.SlidingDoor);
        if (door != null)
        {
            door.Name = "Safety door";
            door.Radius = 7.0f;
        }

        _aimPoint = new Vector3(-1.6f, 0f, 0f);
        AddConveyorAtAim();
        var conveyor = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.Conveyor);
        if (conveyor != null)
        {
            conveyor.Name = "Feed conveyor";
            conveyor.Active = false;
            conveyor.Strength = 3.0f;
            conveyor.Radius = 7.0f;
            conveyor.MotorAxis = Vector3.UnitX;
        }

        _aimPoint = new Vector3(-5.6f, 0f, 0f);
        AddTimerAtAim();
        var timer = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.Timer);
        if (timer != null)
        {
            timer.Name = "Crash-test sequencer";
            timer.Delay = 0.65f;
            timer.Remaining = timer.Delay;
            timer.Radius = 8f;
            timer.TimerAction = TimerMechanismAction.Chain;
        }

        // The android target is placed at the far end of the lane so the payoff has a clear focal point.
        _ragdolls.SpawnAndroid(_world, new Vector3(3.7f, 0f, 0.15f));
        foreach (var rag in _ragdolls.All)
            foreach (var bone in rag.Bones)
                if (bone.Android) _challengeBodies.Add(bone.Body);

        // Payload objects in the lane. The barrel is both a physical projectile and a guaranteed VFX payoff target.
        _aimPoint = new Vector3(-2.75f, 0f, 0.05f);
        SpawnExplosiveBarrelAtAim();
        var barrelPosition = new Vector3(-2.75f, 0.85f, 0.05f);

        for (int i = 0; i < 4; i++)
        {
            var crate = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(-2.15f + i * 0.42f, 0.36f, -0.35f + (i % 2) * 0.42f), new Vector3(0.22f), density: 0.70f), threshold: 3.8f), MaterialId.Wood);
            AddBody(crate, crate.Color);
        }

        // A simple crash wall behind the android makes impacts read better and keeps debris in view.
        var backWall = RigidBody.CreateStaticBox(new Vector3(5.0f, 1.25f, 0f), new Vector3(0.18f, 1.25f, 2.2f));
        backWall.MaterialId = MaterialId.Stone;
        backWall.Color = new Vector3(0.42f, 0.43f, 0.45f);
        _world.Bodies.Add(backWall);

        var plate = new SceneTrigger
        {
            Name = "Start crash-test sequence",
            Position = new Vector3(-6.45f, 0.08f, 0f),
            HalfExtents = new Vector3(0.85f, 0.06f, 0.80f),
            Action = TriggerActionKind.StartTimer,
            TargetPosition = timer?.Position ?? new Vector3(-5.6f, 0.10f, 0f),
            Radius = 6.0f,
            Strength = 12.0f,
            OneShot = true,
        };
        if (timer != null)
        {
            plate.Outputs.Add(new TriggerOutput { TargetId = timer.Id, TargetName = timer.DisplayName, Action = TriggerActionKind.StartTimer, Delay = 0.00f, Radius = 7.0f, Strength = 1.0f, Enabled = true });
        }
        if (door != null)
            plate.Outputs.Add(new TriggerOutput { TargetId = door.Id, TargetName = door.DisplayName, Action = TriggerActionKind.ToggleDoor, Delay = 0.35f, Radius = 7.0f, Strength = 1.0f, Enabled = true });
        if (conveyor != null)
            plate.Outputs.Add(new TriggerOutput { TargetId = conveyor.Id, TargetName = conveyor.DisplayName, Action = TriggerActionKind.StartConveyor, Delay = 0.70f, Radius = 7.0f, Strength = 1.0f, Enabled = true });
        if (piston != null)
            plate.Outputs.Add(new TriggerOutput { TargetId = piston.Id, TargetName = piston.DisplayName, Action = TriggerActionKind.StartPiston, Delay = 1.35f, Radius = 8.0f, Strength = 1.0f, Enabled = true });
        plate.Outputs.Add(new TriggerOutput { TargetId = "", TargetName = "barrel payoff", Action = TriggerActionKind.Explosion, Delay = 3.15f, Radius = 4.4f, Strength = 12.5f, Enabled = true });
        plate.TargetPosition = barrelPosition;
        _triggers.Add(plate);

        // A starter ball presses the plate if the player simply unpauses the scene.
        var starter = WithMaterial(RigidBody.CreateSphere(new Vector3(-7.55f, 0.72f, 0f), 0.30f, density: 3.0f), MaterialId.Metal);
        starter.Velocity = new Vector3(1.6f, 0f, 0f);
        AddBody(starter, new Vector3(0.95f, 0.84f, 0.25f));

        StartChallenge(ChallengeKind.AndroidCrashTest,
            "Android Crash Test Chamber",
            "Start the sequence and deal at least 65% damage to the synthetic android target.");
        _challengeStartCount = Math.Max(1, _challengeBodies.Count);
        _verticalSliceLoaded = true;
        _verticalSliceRunning = false;
        _verticalSliceFinished = false;
        StatusUpdated?.Invoke("Vertical slice: Android Crash Test Chamber — press F8 / Start Test to run the designed chain reaction.");
    }

    private void BuildPistonCrusherLab()
    {
        ResetToEmptyScene();

        _aimPoint = new Vector3(-1.8f, 0f, 0f);
        AddPistonAtAim();
        var piston = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.Piston);
        if (piston != null)
        {
            piston.Name = "Crusher piston";
            piston.OpenSpeed = 3.0f;
            piston.Strength = 3.2f;
            piston.Radius = 7f;
        }

        _aimPoint = new Vector3(1.7f, 0f, 0f);
        AddSlidingDoorAtAim();
        var door = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.SlidingDoor);
        if (door != null)
        {
            door.Name = "Exit sliding door";
            door.Radius = 7f;
        }

        _aimPoint = new Vector3(-4.2f, 0f, 0f);
        AddTimerAtAim();
        var timer = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.Timer);
        if (timer != null)
        {
            timer.Name = "Crusher timer";
            timer.Delay = 0.8f;
            timer.Remaining = timer.Delay;
            timer.Radius = 8f;
            timer.TimerAction = TimerMechanismAction.Chain;
        }

        _triggers.Add(new SceneTrigger
        {
            Name = "Start crusher sequence",
            Position = new Vector3(-5.0f, 0.08f, 0f),
            HalfExtents = new Vector3(0.75f, 0.06f, 0.80f),
            Action = TriggerActionKind.StartTimer,
            TargetPosition = new Vector3(-4.2f, 0.10f, 0f),
            Radius = 5.0f,
            OneShot = true,
        });

        var ball = WithMaterial(RigidBody.CreateSphere(new Vector3(-6.2f, 1.15f, 0f), 0.32f, density: 3.2f), MaterialId.Metal);
        ball.Velocity = new Vector3(2.8f, 0f, 0f);
        AddBody(ball, new Vector3(0.95f, 0.82f, 0.30f));

        for (int i = 0; i < 5; i++)
        {
            var crate = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(-0.15f + i * 0.42f, 0.35f, -0.15f + (i % 2) * 0.35f), new Vector3(0.22f), density: 0.65f), threshold: 3.8f), MaterialId.Wood);
            AddBody(crate, crate.Color);
        }

        _ragdolls.SpawnAndroid(_world, new Vector3(0.15f, 0f, 1.25f));
        StatusUpdated?.Invoke("Preset: Piston Crusher Lab — plate starts timer, then piston pushes crates while the sliding door opens.");
    }

    private void BuildMotorGateTimerLab()
    {
        ResetToEmptyScene();

        // A compact cause/effect lane: ball -> pressure plate -> timer -> gate + motor -> crash/explosion.
        var ramp = RigidBody.CreateStaticBox(new Vector3(-7.0f, 1.3f, 0f), new Vector3(2.4f, 0.12f, 0.7f));
        ramp.Rotation = Quaternion.CreateFromYawPitchRoll(0f, 0f, -0.24f);
        ramp.UpdateDerived();
        ramp.Color = new Vector3(0.40f, 0.42f, 0.48f);
        _world.Bodies.Add(ramp);

        var ball = WithMaterial(RigidBody.CreateSphere(new Vector3(-6.6f, 1.55f, 0f), 0.36f, density: 3.4f), MaterialId.Metal);
        ball.Velocity = new Vector3(5.8f, 0f, 0f);
        AddBody(ball, new Vector3(0.92f, 0.78f, 0.25f));

        // Gate blocks the lane until timer opens it.
        _aimPoint = new Vector3(-0.2f, 0f, 0f);
        AddGateAtAim();
        var gate = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.Gate);
        if (gate != null) gate.Name = "Timed gate";

        // Motor after the gate: a spinning striker that kicks objects into the target wall.
        _aimPoint = new Vector3(2.8f, 0f, 0f);
        AddMotorAtAim();
        var motor = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.Motor);
        if (motor != null)
        {
            motor.Name = "Striker motor";
            motor.MotorSpeed = 10f;
        }

        // Timer near the pressure plate. It chains into open gate + start motor.
        _aimPoint = new Vector3(-3.4f, 0f, 0f);
        AddTimerAtAim();
        var timer = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.Timer);
        if (timer != null)
        {
            timer.Name = "Gate timer";
            timer.Delay = 1.25f;
            timer.Remaining = timer.Delay;
            timer.Radius = 8.0f;
            timer.TimerAction = TimerMechanismAction.Chain;
        }

        var startPlate = new SceneTrigger
        {
            Name = "Start timer plate",
            Position = new Vector3(-4.7f, 0.08f, 0f),
            HalfExtents = new Vector3(0.95f, 0.06f, 0.82f),
            Action = TriggerActionKind.StartTimer,
            TargetPosition = timer?.Position ?? new Vector3(-3.4f, 0.10f, 0f),
            Radius = 6.0f,
            OneShot = true,
        };
        if (timer != null)
            startPlate.Outputs.Add(new TriggerOutput { TargetId = timer.Id, TargetName = timer.DisplayName, Action = TriggerActionKind.StartTimer, Radius = 7.0f, Strength = 1.0f, Enabled = true });
        if (gate != null)
            startPlate.Outputs.Add(new TriggerOutput { TargetId = gate.Id, TargetName = gate.DisplayName, Action = TriggerActionKind.OpenGate, Delay = 1.30f, Radius = 7.0f, Strength = 1.0f, Enabled = true });
        if (motor != null)
            startPlate.Outputs.Add(new TriggerOutput { TargetId = motor.Id, TargetName = motor.DisplayName, Action = TriggerActionKind.StartMotor, Delay = 1.45f, Radius = 7.0f, Strength = 1.0f, Enabled = true });
        _triggers.Add(startPlate);

        _triggers.Add(new SceneTrigger
        {
            Name = "Final blast plate",
            Position = new Vector3(5.1f, 0.08f, 0f),
            HalfExtents = new Vector3(0.74f, 0.06f, 0.85f),
            Action = TriggerActionKind.Explosion,
            TargetPosition = new Vector3(6.4f, 0.85f, 0f),
            Radius = 4.2f,
            Strength = 10.0f,
            OneShot = true,
        });

        for (int y = 0; y < 4; y++)
        for (int z = -2; z <= 2; z++)
        {
            var block = MakeBreakable(RigidBody.CreateBox(new Vector3(6.4f, 0.25f + y * 0.5f, z * 0.5f), new Vector3(0.22f, 0.24f, 0.22f), density: 1.1f), threshold: 4.5f);
            block = WithMaterial(block, MaterialId.Stone);
            AddBody(block, block.Color);
        }

        _ragdolls.SpawnAndroid(_world, new Vector3(4.2f, 0f, 1.6f));
        StatusUpdated?.Invoke("Preset: Motor Gate Timer Lab — ball starts timer, timer opens gate and starts motor, then final plate detonates.");
    }

    private void BuildConveyorChainLab()
    {
        ResetToEmptyScene();

        _aimPoint = new Vector3(-1.6f, 0f, 0f);
        AddConveyorAtAim();
        var conveyor = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.Conveyor);
        if (conveyor != null)
        {
            conveyor.Name = "Starter conveyor";
            conveyor.Active = false;
            conveyor.Strength = 3.0f;
            conveyor.Radius = 7.0f;
        }

        _aimPoint = new Vector3(2.1f, 0f, 0f);
        AddGateAtAim();
        var gate = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.Gate);
        if (gate != null) gate.Name = "Safety gate";

        _aimPoint = new Vector3(-4.2f, 0f, 0f);
        AddTimerAtAim();
        var timer = _mechanisms.LastOrDefault(m => m.Kind == MechanismKind.Timer);
        if (timer != null)
        {
            timer.Name = "Conveyor timer";
            timer.Delay = 1.0f;
            timer.Remaining = 1.0f;
            timer.TimerAction = TimerMechanismAction.Chain;
            timer.Radius = 8.0f;
        }

        var startConveyorPlate = new SceneTrigger
        {
            Name = "Start conveyor sequence",
            Position = new Vector3(-5.2f, 0.08f, 0f),
            HalfExtents = new Vector3(0.85f, 0.06f, 0.75f),
            Action = TriggerActionKind.StartTimer,
            TargetPosition = timer?.Position ?? new Vector3(-4.2f, 0.10f, 0f),
            Radius = 6.0f,
            OneShot = true,
        };
        if (timer != null)
            startConveyorPlate.Outputs.Add(new TriggerOutput { TargetId = timer.Id, TargetName = timer.DisplayName, Action = TriggerActionKind.StartTimer, Radius = 7.0f, Strength = 1.0f, Enabled = true });
        if (gate != null)
            startConveyorPlate.Outputs.Add(new TriggerOutput { TargetId = gate.Id, TargetName = gate.DisplayName, Action = TriggerActionKind.OpenGate, Delay = 1.05f, Radius = 7.0f, Strength = 1.0f, Enabled = true });
        if (conveyor != null)
            startConveyorPlate.Outputs.Add(new TriggerOutput { TargetId = conveyor.Id, TargetName = conveyor.DisplayName, Action = TriggerActionKind.StartConveyor, Delay = 1.15f, Radius = 7.0f, Strength = 1.0f, Enabled = true });
        _triggers.Add(startConveyorPlate);

        RigidBody ball = WithMaterial(RigidBody.CreateSphere(new Vector3(-6.2f, 0.72f, 0f), 0.32f, density: 3.0f), MaterialId.Metal);
        ball.Velocity = new Vector3(4.2f, 0f, 0f);
        AddBody(ball, new Vector3(0.95f, 0.86f, 0.36f));

        for (int i = 0; i < 4; i++)
        {
            var crate = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(-1.7f + i * 0.72f, 0.55f, -0.05f), new Vector3(0.28f), density: 0.65f), threshold: 4.2f), MaterialId.Wood);
            AddBody(crate, crate.Color);
        }

        _aimPoint = new Vector3(4.6f, 0f, 0f);
        SpawnExplosiveBarrelAtAim();

        for (int y = 0; y < 3; y++)
        for (int z = -1; z <= 1; z++)
        {
            var block = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(6.1f, 0.30f + y * 0.56f, z * 0.56f), new Vector3(0.25f), density: 1.15f), threshold: 4.5f), MaterialId.Stone);
            AddBody(block, block.Color);
        }

        _ragdolls.SpawnAndroid(_world, new Vector3(5.4f, 0f, 1.3f));
        StatusUpdated?.Invoke("Preset: Conveyor Chain Lab — plate starts timer, timer opens gate and starts conveyor, crates push barrel into wall.");
    }
}
