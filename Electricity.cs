using MakarovPhysicsSandbox.Physics;
using System.Numerics;

namespace MakarovPhysicsSandbox;

// Lightweight gameplay electricity. This is not a circuit simulator. It is a toy-system
// that fills the interaction matrix: conductive/wet bodies carry charge, nearby conductive
// neighbours arc, and android ragdoll bones take shock damage.
internal sealed class ElectricitySystem
{
    private const float ShockDamagePerSecond = 34f;
    private const float ArcRadius = 1.15f;
    private const float ArcTransfer = 0.55f;
    private const float ChargeDecay = 1.8f;

    public void Clear() { }

    public bool Electrify(RigidBody body, float charge = 1.0f)
    {
        if (body.IsStatic) return false;
        body.Charge = MathF.Max(body.Charge, Math.Clamp(charge, 0.2f, 2.0f));
        body.Temperature = MathF.Max(body.Temperature, 75f);
        body.Wake();
        if (body.Tag is RagdollBone bone) bone.Charge = MathF.Max(bone.Charge, body.Charge);
        return true;
    }

    public void Update(float dt, PhysicsWorld world, RagdollSystem ragdolls)
    {
        if (dt <= 0f) return;

        // Arc propagation: enough to make water/metal/contact clusters feel alive, without
        // requiring a full persistent contact graph from the solver.
        foreach (var source in world.Bodies)
        {
            if (source.IsStatic || source.Charge <= 0.05f) continue;
            float sourceConductivity = EffectiveConductivity(source);
            if (sourceConductivity <= 0.05f) continue;

            foreach (var target in world.Bodies)
            {
                if (ReferenceEquals(source, target) || target.IsStatic) continue;
                float targetConductivity = EffectiveConductivity(target);
                if (targetConductivity <= 0.05f) continue;

                float reach = ArcRadius + source.BoundingRadius + target.BoundingRadius;
                float d2 = Vector3.DistanceSquared(source.Position, target.Position);
                if (d2 > reach * reach) continue;

                float d = MathF.Sqrt(MathF.Max(d2, 1e-5f));
                float falloff = 1f - d / reach;
                target.Charge = MathF.Max(target.Charge, source.Charge * ArcTransfer * falloff * targetConductivity);
                target.Temperature = MathF.Max(target.Temperature, 55f + 80f * target.Charge);
                target.Wake();
            }
        }

        foreach (var b in world.Bodies)
        {
            if (b.Charge <= 0f) continue;

            if (b.Tag is RagdollBone bone && !bone.Severed)
            {
                bone.Charge = MathF.Max(bone.Charge, b.Charge);
                ragdolls.DamageBone(bone, ShockDamagePerSecond * b.Charge * dt, world);
            }

            b.Charge = MathF.Max(0f, b.Charge - ChargeDecay * dt);
            if (b.Tag is RagdollBone rb) rb.Charge = b.Charge;
        }
    }

    private static float EffectiveConductivity(RigidBody b)
    {
        float wetBoost = Math.Clamp(b.Wetness, 0f, 1f) * 0.75f;
        return Math.Clamp(b.Conductivity + wetBoost, 0f, 1.5f);
    }

    public static bool TryTint(RigidBody b, out Vector3 color, out float emissive)
    {
        color = default;
        emissive = 0f;
        if (b.Charge <= 0.03f) return false;
        float t = Math.Clamp(b.Charge, 0f, 1f);
        color = Vector3.Lerp(b.Color, new Vector3(0.20f, 0.70f, 1.0f), t);
        emissive = 0.75f * t;
        return true;
    }
}
