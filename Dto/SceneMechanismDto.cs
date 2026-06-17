using System.Numerics;

namespace MakarovPhysicsSandbox.Dto;

public sealed class SceneMechanismDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = "Mechanism";
    public string DisplayName { get; set; } = string.Empty;
    public string Kind { get; set; } = "Timer";
    public Vector3 Position { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public bool Active { get; set; }
    public float Radius { get; set; } = 6.0f;
    public float Strength { get; set; } = 1.0f;
    public Vector3 ClosedPosition { get; set; } = new();
    public Vector3 OpenPosition { get; set; } = new();
    public float OpenAmount { get; set; }
    public float OpenSpeed { get; set; } = 1.6f;
    public float MotorSpeed { get; set; } = 5.0f;
    public float MotorTorque { get; set; } = 1.0f;
    public Vector3 MotorAxis { get; set; } = Vector3.UnitX;
    public float Delay { get; set; } = 1.5f;
    public float Remaining { get; set; } = 1.5f;
    public bool TimerRunning { get; set; }
    public string TimerAction { get; set; } = "Chain";
}
