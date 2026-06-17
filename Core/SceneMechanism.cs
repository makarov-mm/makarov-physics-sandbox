using MakarovPhysicsSandbox.Physics;
using System.Numerics;

namespace MakarovPhysicsSandbox.Core;

public sealed class SceneMechanism
{
    public string Id = SceneId.New("mechanism");
    public string Name = "Mechanism";

    public string DisplayName
    {
        get => string.IsNullOrWhiteSpace(Name) ? Id : Name;
        set => Name = value;
    }

    public MechanismKind Kind;
    public Vector3 Position;
    public bool Enabled = true;
    public bool Active;
    public float Radius = 6.0f;
    public float Strength = 1.0f;

    // Optional body owned by the mechanism: motor arm or gate body.
    public RigidBody? Body;

    // Gate state.
    public Vector3 ClosedPosition;
    public Vector3 OpenPosition;
    public float OpenAmount;
    public float OpenSpeed = 1.6f;

    // Motor state.
    public Vector3 MotorAxis = Vector3.UnitY;
    public float MotorSpeed = 5.0f;
    public float MotorTorque = 1.0f;
    public float MotorSpin;

    // Timer state.
    public float Delay = 1.5f;
    public float Remaining;
    public bool TimerRunning;
    public TimerMechanismAction TimerAction = TimerMechanismAction.Chain;
}
