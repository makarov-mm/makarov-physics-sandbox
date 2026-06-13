using System.Numerics;

namespace MakarovPhysicsSandbox;

// =====================================================================================
//  Ragdoll - the core "toy" the 3D People Playground game is built around.
//
//  It is built entirely on the primitives the engine already has: plain RigidBody parts
//  (boxes for the trunk/limbs, a sphere for the head) wired together with the existing
//  Point joints (Joint.Kind.Point - the engine's own comment already calls them "good
//  for chains and ragdolls").
//
//  On top of those primitives it adds the two things a believable, hurtable humanoid
//  needs that the bare rigid-body engine does not provide:
//
//    1. POSE MUSCLES - per-joint angular drives that pull each bone back toward its rest
//       orientation relative to its parent. This is what makes the body HOLD a standing
//       pose and spring/recoil from a hit instead of instantly collapsing into spaghetti.
//       They are deliberately soft, so a hard enough blow overpowers them. On death the
//       muscles switch off and the body becomes a fully limp ragdoll.
//
//    2. DAMAGE / DEATH / DISMEMBERMENT - per-bone health, fed from the world's EXISTING
//       impact channel (PhysicsWorld.Impacts). Because every hard contact already shows
//       up there, thrown balls, explosions and hard falls all hurt the ragdoll for free -
//       no special weapon plumbing. A bone driven to zero health severs its joints (the
//       limb comes off). Killing a vital bone (head or torso) drops the whole body limp.
//
//  This is the v1 of "active ragdoll" = "holds its pose and reacts". Full center-of-mass
//  balance (standing on its own feet, taking a step to recover) is a later milestone and
//  is intentionally NOT attempted here - see README "Roadmap".
//
//  The module is self-contained: it only touches public members of RigidBody / Joint /
//  PhysicsWorld, plus the one-line RigidBody.Tag field. It never modifies the solver, so
//  it cannot destabilise the existing sandbox.
// =====================================================================================

/// <summary>One body part of a ragdoll. The RigidBody does the physics; this carries the
/// game state (health, vitality, transient hit flash, and stubs for the fire/electricity
/// systems that land in later milestones).</summary>
internal sealed class RagdollBone
{
    public required RigidBody Body;
    public required string Name;
    public Ragdoll Owner = null!;

    public float MaxHealth = 100f;
    public float Health = 100f;
    public bool Vital;            // head / torso: zeroing this kills the whole ragdoll
    public bool Severed;          // joints already cut off from the body

    public float HitFlash;        // 0..1, decays - drives a brief emissive flash on damage

    // --- reserved for later interaction systems (declared now so render + roadmap line up) ---
    public float Temperature = 20f;   // °C, for the fire/heat milestone
    public bool Burning;              // fire milestone
    public float Charge;              // electricity milestone

    public float HealthFrac => MaxHealth > 0f ? Math.Clamp(Health / MaxHealth, 0f, 1f) : 0f;
}

/// <summary>An angular drive across one parent->child joint that pulls the child back to
/// its rest orientation relative to the parent. Velocity-level and bounded, so it is
/// unconditionally stable regardless of frame rate.</summary>
internal struct PoseMuscle
{
    public RigidBody Parent;
    public RigidBody Child;
    public RagdollBone ChildBone;     // muscle weakens as this bone takes damage
    public Quaternion RestRelative;   // target child-in-parent rotation, captured at spawn
    public float Stiffness;           // how fast it converges (1/seconds-ish)
    public float Strength;            // proportional gain on the orientation error
}

internal sealed class Ragdoll
{
    public readonly List<RagdollBone> Bones = new(16);
    public readonly List<PoseMuscle> Muscles = new(16);
    public RagdollBone Pelvis = null!;   // the part used as the "is this ragdoll still alive in the world" anchor
    public bool Alive = true;

    // ---- tunables (shared defaults; exposed so they can be tweaked live while tuning feel) ----
    public const float UprightStrength = 2.2f;   // pelvis self-righting while alive (0 = no balancing)

    public Vector3 Center => Pelvis.Body.Position;

    public RagdollBone? FindBone(RigidBody body)
    {
        foreach (var b in Bones) if (b.Body == body) return b;
        return null;
    }
}

/// <summary>Owns every ragdoll in the scene, builds them, and runs their per-frame update
/// (damage intake, muscles, death, dismemberment). Drained/cleared by the GlPanel.</summary>
internal sealed class RagdollSystem
{
    private readonly List<Ragdoll> _ragdolls = new(8);

    // ---- damage tuning ----
    private const float HurtSpeed = 3.0f;     // closing speed below this does no damage
    private const float DamagePerSpeed = 9f;  // hp per (m/s) above the threshold
    private const float HitRangePad = 0.18f;  // how far past a bone's radius an impact still counts

    // ---- muscle tuning ----
    private const float ChildShare = 0.85f;   // reaction split: child moves more than the parent
    private const float ParentShare = 0.15f;
    private const float MaxDeltaW = 6.0f;     // clamp per-bone angular-velocity change per step (rad/s)

    // Bones are tiny in volume; at density 1 a whole body would weigh less than a single
    // steel shot (density 4) and get launched by one pellet. This makes a humanoid weigh
    // a handful of units, so hits knock it around believably instead of into orbit.
    private const float BoneDensity = 90f;

    public int Count => _ragdolls.Count;
    public IReadOnlyList<Ragdoll> All => _ragdolls;

    public void Clear() => _ragdolls.Clear();

    public void Update(float dt, PhysicsWorld world)
    {
        // Self-heal: if a reset / clear / eviction removed a ragdoll's pelvis from the world,
        // drop the ragdoll so we never drive dangling bodies. (Cheap for a handful of ragdolls.)
        if (_ragdolls.Count > 0)
            _ragdolls.RemoveAll(r => !world.Bodies.Contains(r.Pelvis.Body));

        if (_ragdolls.Count == 0) return;

        // 1) Damage from the existing world impact channel - balls, blasts, hard falls.
        ApplyImpactDamage(world);

        if (dt <= 0f) return; // paused / single-step with no advance: no muscle work

        foreach (var rag in _ragdolls)
        {
            // 2) Decay hit flashes.
            foreach (var bone in rag.Bones)
                if (bone.HitFlash > 0f) bone.HitFlash = MathF.Max(0f, bone.HitFlash - dt * 3.0f);

            if (!rag.Alive) continue; // dead = limp, muscles off

            // 3) Pose muscles: hold every joint near its rest relative orientation.
            foreach (ref var m in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rag.Muscles))
                DriveMuscle(in m, dt);

            // 4) Gentle self-righting on the pelvis so a standing body does not slowly topple.
            //    This is the cheap stand-in for real balance; it is intentionally weak.
            if (Ragdoll.UprightStrength > 0f)
                DriveUpright(rag.Pelvis.Body, dt);
        }
    }

    // ------------------------------------------------------------------ damage

    private void ApplyImpactDamage(PhysicsWorld world)
    {
        if (world.Impacts.Count == 0) return;

        foreach (var (point, _, speed) in world.Impacts)
        {
            if (speed <= HurtSpeed) continue;

            // Find the closest live bone whose body is near the impact point.
            RagdollBone? hit = null;
            float bestD2 = float.MaxValue;
            foreach (var rag in _ragdolls)
            {
                foreach (var bone in rag.Bones)
                {
                    if (bone.Severed) continue;
                    float reach = bone.Body.BoundingRadius + HitRangePad;
                    float d2 = Vector3.DistanceSquared(point, bone.Body.Position);
                    if (d2 <= reach * reach && d2 < bestD2) { bestD2 = d2; hit = bone; }
                }
            }
            if (hit == null) continue;

            float dmg = (speed - HurtSpeed) * DamagePerSpeed;
            DamageBone(hit, dmg, world);
        }
    }

    /// <summary>Public so a future hitscan weapon / blade / etc. can deal direct damage too.</summary>
    public void DamageBone(RagdollBone bone, float amount, PhysicsWorld world)
    {
        if (bone.Severed || amount <= 0f) return;

        bone.Health -= amount;
        bone.HitFlash = 1f;
        bone.Body.Wake();

        if (bone.Health <= 0f)
        {
            bone.Health = 0f;
            SeverBone(bone, world);
            if (bone.Vital) Kill(bone.Owner);
        }
    }

    /// <summary>Cut every joint that still attaches this bone, so the limb detaches and
    /// flops/drops as loose debris. The body itself stays in the world.</summary>
    private static void SeverBone(RagdollBone bone, PhysicsWorld world)
    {
        if (bone.Severed) return;
        bone.Severed = true;
        world.Joints.RemoveAll(j => j.Involves(bone.Body));

        // Drop the muscles that drove this bone so we stop fighting a limb that is gone.
        bone.Owner.Muscles.RemoveAll(m => m.Child == bone.Body || m.Parent == bone.Body);
    }

    private static void Kill(Ragdoll rag)
    {
        if (!rag.Alive) return;
        rag.Alive = false;
        rag.Muscles.Clear();        // go fully limp
        foreach (var b in rag.Bones) b.Body.Wake();
    }

    // ------------------------------------------------------------------ muscles

    private static void DriveMuscle(in PoseMuscle m, float dt)
    {
        var parent = m.Parent;
        var child = m.Child;
        if (child.IsStatic && parent.IsStatic) return;

        // Target world orientation of the child = parentWorld composed with the rest offset.
        // Composition matches the engine's own convention (RefreshProxies uses Quat.Mul(Rotation, local)).
        var targetChild = Quat.Mul(parent.Rotation, m.RestRelative);

        // World-space rotation that carries the current child orientation onto the target.
        var qErr = Quat.Mul(targetChild, Quaternion.Conjugate(child.Rotation));
        var rotVec = RotationVector(qErr);                  // axis * angle, in world space

        // Desired relative angular velocity that closes the error, damped toward the parent's.
        var relW = child.AngularVelocity - parent.AngularVelocity;
        var desired = rotVec * (m.Strength * m.ChildBone.HealthFrac); // weaker as the bone is hurt
        var dW = (desired - relW) * Math.Clamp(m.Stiffness * dt, 0f, 1f);

        // Clamp so a single step can never inject a spike.
        float len = dW.Length();
        if (len > MaxDeltaW) dW *= MaxDeltaW / len;

        if (!child.IsStatic) child.AngularVelocity += dW * ChildShare;
        if (!parent.IsStatic) parent.AngularVelocity -= dW * ParentShare;
    }

    private static void DriveUpright(RigidBody pelvis, float dt)
    {
        if (pelvis.IsStatic) return;

        // Rotate the pelvis so its local up-axis lines back up with world up.
        var up = Vector3.Transform(Vector3.UnitY, pelvis.Rotation);
        var axis = Vector3.Cross(up, Vector3.UnitY);        // torque axis to right the body
        float sin = axis.Length();
        if (sin < 1e-4f) return;
        axis /= sin;
        float angle = MathF.Asin(Math.Clamp(sin, 0f, 1f));  // tilt away from vertical

        var desired = axis * (angle * Ragdoll.UprightStrength);
        var dW = (desired - pelvis.AngularVelocity) * Math.Clamp(4f * dt, 0f, 1f);
        float len = dW.Length();
        if (len > MaxDeltaW) dW *= MaxDeltaW / len;
        pelvis.AngularVelocity += dW;
    }

    /// <summary>Quaternion -> rotation vector (axis * angle), shortest arc.</summary>
    private static Vector3 RotationVector(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0f) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W); // shortest path
        float s = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        if (s < 1e-6f) return Vector3.Zero;
        float angle = 2f * MathF.Atan2(s, q.W);
        return new Vector3(q.X, q.Y, q.Z) * (angle / s);
    }

    // ------------------------------------------------------------------ render tint

    /// <summary>If this body is a ragdoll bone, returns the colour it should be drawn with
    /// (skin tone, reddening with damage, dimmed when dead) and a brief emissive hit flash.</summary>
    public static bool TryTint(RigidBody b, out Vector3 color, out float emissive)
    {
        color = default;
        emissive = 0f;
        if (b.Tag is not RagdollBone bone) return false;

        float h = bone.HealthFrac;
        // Healthy skin -> deepening red as it is hurt.
        var skin = new Vector3(0.86f, 0.66f, 0.56f);
        var hurt = new Vector3(0.55f, 0.10f, 0.09f);
        color = Vector3.Lerp(hurt, skin, h);
        if (!bone.Owner.Alive) color *= 0.7f;            // limp body reads as duller
        emissive = bone.HitFlash * 0.5f;
        return true;
    }

    // ------------------------------------------------------------------ construction

    /// <summary>Build a humanoid standing with its feet near <paramref name="footPos"/> and
    /// add it (bodies + joints) to the world.</summary>
    public Ragdoll Spawn(PhysicsWorld world, Vector3 footPos)
    {
        var rag = new Ragdoll();
        var pelvisCenter = footPos + new Vector3(0f, 1.0f, 0f); // ~1m up so the feet reach the ground

        // ---- bone layout, expressed relative to the pelvis centre (X right, Y up, Z fwd) ----
        // half-extents pick a roughly 1.5 m figure; tuned by eye, easy to change later.
        var pelvis = Box(rag, world, "pelvis", pelvisCenter, new Vector3(0.14f, 0.11f, 0.09f), vital: false, hp: 120f);
        var torso = Box(rag, world, "torso", pelvisCenter + new Vector3(0, 0.33f, 0), new Vector3(0.15f, 0.20f, 0.10f), vital: true, hp: 120f);
        var head = Sphere(rag, world, "head", pelvisCenter + new Vector3(0, 0.72f, 0), 0.13f, vital: true, hp: 60f);

        var armUL = Box(rag, world, "armUpperL", pelvisCenter + new Vector3(0.24f, 0.36f, 0), new Vector3(0.05f, 0.14f, 0.05f), false, 70f);
        var armLL = Box(rag, world, "armLowerL", pelvisCenter + new Vector3(0.24f, 0.07f, 0), new Vector3(0.045f, 0.13f, 0.045f), false, 60f);
        var armUR = Box(rag, world, "armUpperR", pelvisCenter + new Vector3(-0.24f, 0.36f, 0), new Vector3(0.05f, 0.14f, 0.05f), false, 70f);
        var armLR = Box(rag, world, "armLowerR", pelvisCenter + new Vector3(-0.24f, 0.07f, 0), new Vector3(0.045f, 0.13f, 0.045f), false, 60f);

        var legUL = Box(rag, world, "legUpperL", pelvisCenter + new Vector3(0.09f, -0.33f, 0), new Vector3(0.07f, 0.19f, 0.07f), false, 90f);
        var legLL = Box(rag, world, "legLowerL", pelvisCenter + new Vector3(0.09f, -0.78f, 0), new Vector3(0.06f, 0.20f, 0.06f), false, 80f);
        var legUR = Box(rag, world, "legUpperR", pelvisCenter + new Vector3(-0.09f, -0.33f, 0), new Vector3(0.07f, 0.19f, 0.07f), false, 90f);
        var legLR = Box(rag, world, "legLowerR", pelvisCenter + new Vector3(-0.09f, -0.78f, 0), new Vector3(0.06f, 0.20f, 0.06f), false, 80f);

        rag.Pelvis = pelvis;

        // ---- joints (Point) + matching pose muscles ----
        // parent is always the more central bone; the joint world point sits in the gap.
        Link(rag, world, pelvis, torso, pelvisCenter + new Vector3(0, 0.16f, 0), 9f, 14f);
        Link(rag, world, torso, head, pelvisCenter + new Vector3(0, 0.57f, 0), 8f, 16f);

        Link(rag, world, torso, armUL, pelvisCenter + new Vector3(0.21f, 0.50f, 0), 7f, 10f);
        Link(rag, world, armUL, armLL, pelvisCenter + new Vector3(0.24f, 0.21f, 0), 7f, 9f);
        Link(rag, world, torso, armUR, pelvisCenter + new Vector3(-0.21f, 0.50f, 0), 7f, 10f);
        Link(rag, world, armUR, armLR, pelvisCenter + new Vector3(-0.24f, 0.21f, 0), 7f, 9f);

        Link(rag, world, pelvis, legUL, pelvisCenter + new Vector3(0.09f, -0.14f, 0), 8f, 12f);
        Link(rag, world, legUL, legLL, pelvisCenter + new Vector3(0.09f, -0.56f, 0), 8f, 11f);
        Link(rag, world, pelvis, legUR, pelvisCenter + new Vector3(-0.09f, -0.14f, 0), 8f, 12f);
        Link(rag, world, legUR, legLR, pelvisCenter + new Vector3(-0.09f, -0.56f, 0), 8f, 11f);

        _ragdolls.Add(rag);
        return rag;
    }

    // ---- builders ----

    private static RagdollBone Box(Ragdoll rag, PhysicsWorld world, string name, Vector3 center, Vector3 half,
                                   bool vital, float hp)
    {
        var body = RigidBody.CreateBox(center, half, density: BoneDensity);
        return Register(rag, world, body, name, vital, hp);
    }

    private static RagdollBone Sphere(Ragdoll rag, PhysicsWorld world, string name, Vector3 center, float radius,
                                      bool vital, float hp)
    {
        var body = RigidBody.CreateSphere(center, radius, density: BoneDensity);
        return Register(rag, world, body, name, vital, hp);
    }

    private static RagdollBone Register(Ragdoll rag, PhysicsWorld world, RigidBody body, string name, bool vital, float hp)
    {
        body.Friction = 0.85f;       // skin grips, so a downed body does not slide like ice
        body.Restitution = 0.05f;
        body.Breakable = false;      // bones are severed by health, not fractured into debris
        body.UserObject = true;      // still selectable / grabbable in the editor

        var bone = new RagdollBone { Body = body, Name = name, Owner = rag, MaxHealth = hp, Health = hp, Vital = vital };
        body.Color = new Vector3(0.86f, 0.66f, 0.56f);
        body.Tag = bone;             // back-reference for render tint + damage lookup
        rag.Bones.Add(bone);
        world.Bodies.Add(body);
        return bone;
    }

    private static void Link(Ragdoll rag, PhysicsWorld world, RagdollBone parent, RagdollBone child,
                             Vector3 jointWorld, float stiffness, float strength)
    {
        // Bodies spawn at identity rotation, so a local anchor is just (jointWorld - bodyCenter).
        var joint = new Joint
        {
            Type = Joint.Kind.Point,
            A = parent.Body,
            B = child.Body,
            LocalA = jointWorld - parent.Body.Position,
            LocalB = jointWorld - child.Body.Position,
        };
        world.Joints.Add(joint);

        // Rest offset captured from the spawn pose (identity rotations -> identity, but computed
        // generically so a future posed spawn still works).
        var rest = Quat.Mul(Quaternion.Conjugate(parent.Body.Rotation), child.Body.Rotation);
        rag.Muscles.Add(new PoseMuscle
        {
            Parent = parent.Body,
            Child = child.Body,
            ChildBone = child,
            RestRelative = Quaternion.Normalize(rest),
            Stiffness = stiffness,
            Strength = strength,
        });
    }
}
