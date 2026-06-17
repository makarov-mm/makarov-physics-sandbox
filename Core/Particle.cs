using System.Numerics;

namespace MakarovPhysicsSandbox.Core;

public struct Particle
{
    public Vector3 Pos, Vel, Color;
    public float Life, MaxLife, Size;
    public bool Gravity;
    public bool Smoke; // smoke renders non-emissive and expands as it ages
}
