using System.Numerics;

namespace MakarovPhysicsSandbox;

internal sealed partial class GlPanel
{
    private List<SceneMechanismDto> CreateMechanismDtos()
    {
        var result = new List<SceneMechanismDto>();
        foreach (var m in _mechanisms)
        {
            result.Add(new SceneMechanismDto
            {
                Id = m.Id,
                Name = m.Name,
                DisplayName = m.DisplayName,
                Kind = m.Kind.ToString(),
                Position = V3.From(m.Position),
                Enabled = m.Enabled,
                Active = m.Active,
                Radius = m.Radius,
                Strength = m.Strength,
                ClosedPosition = V3.From(m.ClosedPosition),
                OpenPosition = V3.From(m.OpenPosition),
                OpenAmount = m.OpenAmount,
                OpenSpeed = m.OpenSpeed,
                MotorSpeed = m.MotorSpeed,
                MotorTorque = m.MotorTorque,
                MotorAxis = V3.From(m.MotorAxis),
                Delay = m.Delay,
                Remaining = m.Remaining,
                TimerRunning = m.TimerRunning,
                TimerAction = m.TimerAction.ToString(),
            });
        }
        return result;
    }

    private void LoadMechanismDtos(IEnumerable<SceneMechanismDto> dtos)
    {
        ClearMechanisms();
        foreach (var dto in dtos)
        {
            if (!Enum.TryParse<MechanismKind>(dto.Kind, out var kind)) continue;
            if (!Enum.TryParse<TimerMechanismAction>(dto.TimerAction, out var timerAction)) timerAction = TimerMechanismAction.Chain;
            var pos = dto.Position.ToVector3();
            var m = new SceneMechanism
            {
                Id = string.IsNullOrWhiteSpace(dto.Id) ? SceneId.New("mechanism") : dto.Id,
                Name = string.IsNullOrWhiteSpace(dto.DisplayName)
                    ? string.IsNullOrWhiteSpace(dto.Name) ? kind.ToString() : dto.Name
                    : dto.DisplayName,
                Kind = kind,
                Position = pos,
                Enabled = dto.Enabled,
                Active = dto.Active,
                Radius = dto.Radius <= 0 ? 6.0f : dto.Radius,
                Strength = dto.Strength <= 0 ? 1.0f : dto.Strength,
                ClosedPosition = dto.ClosedPosition.ToVector3(),
                OpenPosition = dto.OpenPosition.ToVector3(),
                OpenAmount = dto.OpenAmount,
                OpenSpeed = dto.OpenSpeed <= 0 ? 1.6f : dto.OpenSpeed,
                MotorSpeed = dto.MotorSpeed <= 0 ? 5.0f : dto.MotorSpeed,
                MotorTorque = dto.MotorTorque <= 0 ? 1.0f : dto.MotorTorque,
                MotorAxis = dto.MotorAxis != null && dto.MotorAxis.ToVector3().LengthSquared() > 1e-6f ? Vector3.Normalize(dto.MotorAxis.ToVector3()) : Vector3.UnitX,
                Delay = dto.Delay <= 0 ? 1.5f : dto.Delay,
                Remaining = dto.Remaining <= 0 ? dto.Delay : dto.Remaining,
                TimerRunning = dto.TimerRunning,
                TimerAction = timerAction,
            };

            if (kind == MechanismKind.Gate)
            {
                if (m.ClosedPosition.LengthSquared() < 1e-6f) m.ClosedPosition = pos;
                if (m.OpenPosition.LengthSquared() < 1e-6f) m.OpenPosition = m.ClosedPosition + new Vector3(0, 2.4f, 0);
                var body = RigidBody.CreateStaticBox(Vector3.Lerp(m.ClosedPosition, m.OpenPosition, Smooth01(m.OpenAmount)), new Vector3(0.18f, 1.0f, 1.35f));
                body.UserObject = true;
                body.MaterialId = MaterialId.Metal;
                body.Color = new Vector3(0.36f, 0.48f, 0.58f);
                body.Conductivity = 0.9f;
                _world.Bodies.Add(body);
                m.Body = body;
                m.Position = body.Position;
            }
            else if (kind == MechanismKind.Motor)
            {
                var body = WithMaterial(RigidBody.CreateBox(pos, new Vector3(1.0f, 0.12f, 0.16f), density: 1.15f), MaterialId.Metal);
                body.Color = new Vector3(0.95f, 0.72f, 0.22f);
                _world.Bodies.Add(body);
                _world.Joints.Add(new Joint { Type = Joint.Kind.Point, A = body, B = null, LocalA = Vector3.Zero, LocalB = pos });
                m.Body = body;
                m.Position = body.Position;
            }
            else if (kind == MechanismKind.Conveyor)
            {
                var body = RigidBody.CreateStaticBox(pos, new Vector3(2.1f, 0.08f, 0.78f));
                body.UserObject = true;
                body.MaterialId = MaterialId.Rubber;
                body.Color = new Vector3(0.08f, 0.10f, 0.12f);
                body.Friction = 1.35f;
                _world.Bodies.Add(body);
                m.Body = body;
                m.Position = body.Position;
                if (m.Strength <= 0) m.Strength = 2.4f;
            }
            else if (kind == MechanismKind.Piston)
            {
                if (m.ClosedPosition.LengthSquared() < 1e-6f) m.ClosedPosition = pos;
                if (m.OpenPosition.LengthSquared() < 1e-6f) m.OpenPosition = m.ClosedPosition + new Vector3(2.15f, 0f, 0f);
                var body = RigidBody.CreateStaticBox(Vector3.Lerp(m.ClosedPosition, m.OpenPosition, Smooth01(m.OpenAmount)), new Vector3(0.70f, 0.26f, 0.58f));
                body.UserObject = true;
                body.MaterialId = MaterialId.Metal;
                body.Color = new Vector3(0.74f, 0.24f, 0.18f);
                body.Friction = 0.85f;
                body.Conductivity = 0.75f;
                _world.Bodies.Add(body);
                m.Body = body;
                m.Position = body.Position;
                if (m.Strength <= 0) m.Strength = 2.0f;
                if (m.MotorAxis.LengthSquared() < 1e-6f) m.MotorAxis = Vector3.UnitX;
            }
            else if (kind == MechanismKind.SlidingDoor)
            {
                if (m.ClosedPosition.LengthSquared() < 1e-6f) m.ClosedPosition = pos;
                if (m.OpenPosition.LengthSquared() < 1e-6f) m.OpenPosition = m.ClosedPosition + new Vector3(0, 0, 2.4f);
                var body = RigidBody.CreateStaticBox(Vector3.Lerp(m.ClosedPosition, m.OpenPosition, Smooth01(m.OpenAmount)), new Vector3(0.20f, 1.05f, 1.15f));
                body.UserObject = true;
                body.MaterialId = MaterialId.Metal;
                body.Color = new Vector3(0.22f, 0.44f, 0.62f);
                body.Conductivity = 0.9f;
                _world.Bodies.Add(body);
                m.Body = body;
                m.Position = body.Position;
                if (m.MotorAxis.LengthSquared() < 1e-6f) m.MotorAxis = Vector3.UnitZ;
            }
            _mechanisms.Add(m);
        }
    }
}
