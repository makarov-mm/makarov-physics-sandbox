using MakarovPhysicsSandbox.Physics;
using System.Numerics;

namespace MakarovPhysicsSandbox;

// =====================================================================================
//  HeatSystem - the first M1 "interacting system": fire & heat.
//
//  Deliberately NOT a 3D heat field (that is research-grade at interactive rates). Instead
//  each body carries a single scalar Temperature, and the rules are simple and local:
//
//    * A burning body stays hot, slowly consumes "fuel", and radiates heat to nearby bodies.
//    * Any flammable body whose temperature passes the ignition point catches fire.
//    * Burning bodies cool / things not near fire drift back to ambient.
//    * When a body's fuel runs out it burns out, chars (darkens) and won't reignite.
//    * A burning body that is a RAGDOLL BONE takes fire damage over time - so fire spreads
//      bone-to-bone and can ultimately kill, wiring fire <-> ragdoll together.
//
//  This is the payoff of the "interaction matrix" thesis: ignite a torso, watch it spread
//  down the limbs, bones cook, a vital one dies, the body collapses - still burning. None
//  of that is scripted; it falls out of the rules combining.
//
//  Self-contained like Ragdoll.cs: touches only public engine members plus the thermal
//  fields on RigidBody. Per-body fuel lives here in a side table so RigidBody stays lean.
// =====================================================================================
internal sealed class HeatSystem
{
    private const float Ambient = 20f;
    private const float IgnitionPoint = 160f;   // temperature at which a flammable body lights
    private const float BurnTemperature = 650f; // a burning body sits around here
    private const float CoolRate = 1.4f;        // 1/seconds, exponential drift back to ambient
    private const float SpreadRadius = 1.7f;    // how far a flame reaches to heat neighbours
    private const float SpreadPower = 900f;     // °C/second delivered at point-blank, linear falloff
    private const float BaseFuel = 8.5f;        // seconds of burn for a fully flammable body
    private const float FireDamagePerSecond = 42f; // hp/s to a burning ragdoll bone
    private const float DensityFireproof = 2.0f;   // bodies denser than this barely burn (metal/stone)

    // bodyFuel: remaining burn time. Absent => never ignited (or already cooled to ambient).
    private readonly Dictionary<RigidBody, float> _fuel = new();

    public void Clear() => _fuel.Clear();

    /// <summary>Effective flammability after the density gate (metal/stone resist).</summary>
    private static float EffectiveFlammability(RigidBody b)
    {
        if (b.Flammability <= 0f) return 0f;

        // Ragdoll bones are intentionally dense for stable rigid-body motion. Do not use
        // that physics density to make them almost fireproof; otherwise the ignite tool
        // flashes for less than a second and never does meaningful damage.
        if (b.Tag is RagdollBone)
            return Math.Clamp(b.Flammability, 0f, 1.5f);

        float densityGate = b.Density >= DensityFireproof ? 0.12f : 1f;
        return b.Flammability * densityGate * (1f - 0.85f * Math.Clamp(b.Wetness, 0f, 1f));
    }

    /// <summary>Light a body on fire directly (the igniter tool, or future incendiary effects).</summary>
    public bool Ignite(RigidBody b)
    {
        if (b.IsStatic) return false;
        float flam = EffectiveFlammability(b);
        if (flam <= 0.02f) { b.Temperature = MathF.Max(b.Temperature, IgnitionPoint * 0.8f); return false; }
        b.Burning = true;
        b.Temperature = BurnTemperature;
        _fuel[b] = BaseFuel * Math.Clamp(flam, 0.35f, 1.35f);
        if (b.Tag is RagdollBone bone) bone.Burning = true;
        b.Wake();
        return true;
    }

    public void Update(float dt, PhysicsWorld world, RagdollSystem ragdolls)
    {
        if (dt <= 0f) return;

        // Drop fuel entries for bodies that left the world (reset / clear / eviction).
        if (_fuel.Count > 0)
            _fuel.RemoveAll(world);

        // 1) Burning bodies: hold temperature, burn fuel, hurt ragdoll bones, heat neighbours.
        //    Collect newly-ignited bodies separately so we don't mutate while iterating.
        List<RigidBody>? toIgnite = null;

        foreach (var b in world.Bodies)
        {
            if (b.Burning)
            {
                if (b.Wetness > 0.55f)
                {
                    Extinguish(b);
                    continue;
                }

                b.Temperature = MathF.Max(b.Temperature, BurnTemperature * 0.85f);

                float fuel = _fuel.GetValueOrDefault(b, BaseFuel);
                fuel -= dt;
                if (fuel <= 0f)
                {
                    BurnOut(b);
                    continue;
                }
                _fuel[b] = fuel;
                b.Wake();

                // fire damages a burning ragdoll bone (sever-capable: fire can kill).
                if (b.Tag is RagdollBone { Severed: false } bone)
                {
                    bone.Burning = true;
                    ragdolls.DamageBone(bone, FireDamagePerSecond * dt, world);
                }

                // radiate to nearby flammable bodies
                foreach (var other in world.Bodies)
                {
                    if (ReferenceEquals(other, b) || other.IsStatic || other.Burning) continue;
                    float d2 = Vector3.DistanceSquared(b.Position, other.Position);
                    float reach = SpreadRadius + other.BoundingRadius;
                    if (d2 > reach * reach) continue;
                    float falloff = 1f - MathF.Sqrt(d2) / reach;
                    other.Temperature += SpreadPower * falloff * dt;
                }
            }
            else
            {
                // 2) Cool toward ambient.
                if (b.Temperature > Ambient)
                    b.Temperature = Ambient + (b.Temperature - Ambient) / (1f + CoolRate * dt);

                // 3) Auto-ignite if hot enough and flammable.
                if (b is { Temperature: >= IgnitionPoint, IsStatic: false } && EffectiveFlammability(b) > 0.02f)
                {
                    (toIgnite ??= []).Add(b);
                }
            }
        }

        if (toIgnite != null)
            foreach (var b in toIgnite) Ignite(b);
    }

    private void Extinguish(RigidBody b)
    {
        b.Burning = false;

        if (b.Tag is RagdollBone bone)
        {
            bone.Burning = false;
        }

        b.Temperature = MathF.Min(b.Temperature, 90f);
        _fuel.Remove(b);
        b.Wake();
    }

    private void BurnOut(RigidBody b)
    {
        b.Burning = false;

        if (b.Tag is RagdollBone bone)
        {
            bone.Burning = false;
        }

        b.Flammability = 0f;          // charred: won't reignite
        b.Temperature = 220f;         // stays warm a moment, then cools via the normal path
        _fuel.Remove(b);
        b.Color *= 0.35f;             // scorch mark
    }

    // ------------------------------------------------------------------ render tint

    /// <summary>If this body is on fire (or still glowing hot), return the colour + emissive
    /// it should render with. Takes priority over the normal/ragdoll tint.</summary>
    public static bool TryTint(RigidBody b, out Vector3 color, out float emissive)
    {
        color = default;
        emissive = 0f;

        if (b.Burning)
        {
            // cheap flicker from position + temperature, no extra time plumbing needed
            float flick = 0.75f + 0.25f * MathF.Sin(b.Temperature * 0.05f + b.Position.X * 7f + b.Position.Z * 5f);
            color = new Vector3(1.0f, 0.45f, 0.12f) * flick;
            emissive = 0.85f * flick;
            return true;
        }
        if (b.Temperature > 90f)
        {
            // glowing-hot but not (yet) aflame: ember tint that fades as it cools
            float t = Math.Clamp((b.Temperature - 90f) / (IgnitionPoint - 90f), 0f, 1f);
            color = Vector3.Lerp(b.Color, new Vector3(0.9f, 0.25f, 0.08f), t);
            emissive = 0.35f * t;
            return true;
        }
        return false;
    }
}

internal static class HeatDictExtensions
{
    /// <summary>Remove fuel entries whose body is no longer in the world.</summary>
    public static void RemoveAll(this Dictionary<RigidBody, float> fuel, PhysicsWorld world)
    {
        List<RigidBody>? dead = null;

        foreach (RigidBody key in fuel.Keys)
        {
            if (!world.Bodies.Contains(key))
            {
                (dead ??= []).Add(key);
            }
        }

        if (dead is not null)
        {
            foreach (RigidBody k in dead)
            {
                fuel.Remove(k);
            }
        }
    }
}
