using MakarovPhysicsSandbox.Physics;

namespace MakarovPhysicsSandbox.Core;

public sealed class VehicleRig
{
    public readonly List<RigidBody> Bodies = new(5);
    public readonly List<Joint> Joints = new(8);
}
