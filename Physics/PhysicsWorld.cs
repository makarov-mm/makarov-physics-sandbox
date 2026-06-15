using System.Numerics;

namespace MakarovPhysicsSandbox.Physics;

internal sealed class PhysicsWorld
{
    public const float FixedStep = 1f / 120f;

    private const int SolverIterations = 10;
    private const float Beta = 0.2f;            // Baumgarte factor
    private const float Slop = 0.008f;
    private const float RestitutionThreshold = 1.0f;

    // rolling resistance for balls in contact (per second); tuned so a ball coasts to rest
    // in a couple of seconds instead of rolling indefinitely
    private const float RollAngularDamp = 1.6f;
    private const float RollLinearDamp = 0.9f;

    // sleeping: a body below both velocity thresholds for SleepDelay seconds nods off
    private const float SleepLinVelSq = 0.08f * 0.08f;
    private const float SleepAngVelSq = 0.30f * 0.30f;
    private const float SleepDelay = 0.6f;
    private const float WakeImpactSpeed = 0.30f;

    private const float WarmStartMatchDistSq = 0.04f * 0.04f;

    public readonly List<RigidBody> Bodies = [];
    public readonly List<Joint> Joints = [];
    public readonly List<ForceField> Fields = [];
    public readonly List<WaterVolume> Waters = [];

    // strong contacts this frame, drained by the renderer to spawn sparks
    public readonly List<(Vector3 point, Vector3 normal, float speed)> Impacts = [];

    // one-frame fracture events, drained by the renderer to spawn material-specific debris VFX
    public readonly List<BreakEvent> BreakEvents = [];
    public readonly RigidBody Ground = new()
    {
        InvMass = 0f,
        Restitution = 0.2f,
        Friction = 0.7f,
    };
    public Vector3 Gravity = new(0, -9.81f, 0);

    private readonly List<Contact> _contacts = [];
    private float _accumulator;
    private readonly Random _breakRng = new(24680);

    public RigidBody? Grabbed;
    public Vector3 GrabLocalAnchor;
    public Vector3 DragTarget;

    public int AwakeCount
    {
        get
        {
            int n = 0;
            foreach (var b in Bodies) if (!b.Inactive) n++;
            return n;
        }
    }

    // ---- warm starting cache ----
    // Impulses solved on the previous substep, keyed by body pair and re-anchored
    // in A's local frame. Feeding them back as the starting guess is what lets a
    // stack of boxes actually settle instead of buzzing forever: the solver only
    // has to correct the small per-frame change, not rediscover the whole load path
    // from zero in 10 iterations.
    private struct CachedImpulse
    {
        public Vector3 LocalA;
        public float Pn, Pt1, Pt2;
    }
    private Dictionary<(RigidBody, RigidBody), List<CachedImpulse>> _warmCache = new();
    private Dictionary<(RigidBody, RigidBody), List<CachedImpulse>> _warmCacheBack = new();

    public void Step(float dt)
    {
        Impacts.Clear();
        BreakEvents.Clear();
        _accumulator += MathF.Min(dt, 0.05f);
        int steps = 0;
        while (_accumulator >= FixedStep && steps++ < 8)
        {
            SubStep(FixedStep);
            _accumulator -= FixedStep;
        }
        if (steps >= 8) _accumulator = 0f; // can't keep up - drop time instead of spiraling
    }

    private void SubStep(float h)
    {
        foreach (var w in Waters) w.Step(h);
        foreach (var b in Bodies)
        {
            if (b.Inactive) continue;
            b.Velocity += Gravity * h;
        }

        ApplyEnvironment(h);
        ApplyDragSpring(h);

        _contacts.Clear();
        foreach (var b in Bodies) b.Touching = false;
        GenerateContacts();
        foreach (var c in _contacts) { c.A.Touching = true; c.B.Touching = true; }

        // joints share the solver with contacts: wake the pairs first so a tug on one
        // end of a chain travels down it, then interleave the iterations
        foreach (var j in Joints) j.WakePair();
        foreach (var j in Joints) j.Presolve(h);

        foreach (var c in _contacts) Presolve(c, h);
        for (int i = 0; i < SolverIterations; i++)
        {
            foreach (var j in Joints) j.Solve(h);
            foreach (var c in _contacts) SolveContact(c);
        }
        for (int i = 0; i < SolverIterations; i++)
            foreach (var c in _contacts) SolvePosition(c);

        StoreWarmCache();
        CollectImpacts();
        BreakBodiesFromImpacts();

        foreach (var b in Bodies)
        {
            if (b.Inactive) continue;

            b.Position += (b.Velocity + b.BiasVelocity) * h;

            var w = b.AngularVelocity + b.BiasAngularVelocity;
            if (w.LengthSquared() > 1e-10f)
            {
                var dq = new Quaternion(w.X, w.Y, w.Z, 0f).Multiply(b.Rotation);

                b.Rotation = Quaternion.Normalize(new Quaternion(
                    b.Rotation.X + dq.X * 0.5f * h,
                    b.Rotation.Y + dq.Y * 0.5f * h,
                    b.Rotation.Z + dq.Z * 0.5f * h,
                    b.Rotation.W + dq.W * 0.5f * h));
            }

            b.BiasVelocity = Vector3.Zero;
            b.BiasAngularVelocity = Vector3.Zero;
            if (b.RippleCooldown > 0f) b.RippleCooldown -= h;
            if (b.Wetness > 0f) b.Wetness = MathF.Max(0f, b.Wetness - h * 0.18f);
            if (b.Charge > 0f) b.Charge = MathF.Max(0f, b.Charge - h * 0.85f);

            // mild damping keeps stacks from buzzing
            b.Velocity *= 1f / (1f + 0.02f * h);
            b.AngularVelocity *= 1f / (1f + 0.08f * h);

            // rolling resistance: a ball resting on a surface should coast to a stop, not
            // roll forever (wind-pushed balls, the domino striker, thrown bowling balls).
            // Only applied to balls that are actually touching something, so airborne or
            // floating spheres are unaffected.
            if (b.IsBall && b.Touching)
            {
                b.AngularVelocity *= 1f / (1f + RollAngularDamp * h);
                // shave the horizontal glide too; gravity-aligned (fall) component is left alone
                var v = b.Velocity;
                var up = Vector3.UnitY;
                var vUp = Vector3.Dot(v, up) * up;
                var vFlat = v - vUp;
                vFlat *= 1f / (1f + RollLinearDamp * h);
                b.Velocity = vFlat + vUp;
            }

            // sleep bookkeeping
            if (b.Velocity.LengthSquared() < SleepLinVelSq
                && b.AngularVelocity.LengthSquared() < SleepAngVelSq)
            {
                b.SleepTimer += h;
                if (b.SleepTimer >= SleepDelay && b != Grabbed)
                {
                    b.Sleeping = true;
                    b.Velocity = Vector3.Zero;
                    b.AngularVelocity = Vector3.Zero;
                }
            }
            else
            {
                b.SleepTimer = 0f;
            }

            b.UpdateDerived();
        }
    }

    private void ApplyEnvironment(float h)
    {
        if (Fields.Count == 0 && Waters.Count == 0) return;
        foreach (var b in Bodies)
        {
            if (b.IsStatic) continue;
            bool touched = false;
            foreach (var f in Fields) { f.Apply(b, h); touched = true; }
            foreach (var w in Waters) w.Apply(b, Gravity, h);
            // a field should be able to stir a sleeping body back to life
            if (touched && b.Sleeping) b.Wake();
        }
    }

    private const float ImpactSparkSpeed = 2.5f;

    private void CollectImpacts()
    {
        foreach (var c in _contacts)
            if (c.ImpactSpeed > ImpactSparkSpeed)
                Impacts.Add((c.Point, c.Normal, c.ImpactSpeed));
    }

    private void BreakBodiesFromImpacts()
    {
        if (_contacts.Count == 0 || Bodies.Count > 520) return;

        var toBreak = new Dictionary<RigidBody, (float speed, Vector3 point, Vector3 normal)>();
        foreach (var c in _contacts)
        {
            if (c.ImpactSpeed < 1f) continue;
            TryMarkBreakable(c.A, c.ImpactSpeed, c.Point, c.Normal, toBreak);
            TryMarkBreakable(c.B, c.ImpactSpeed, c.Point, -c.Normal, toBreak);
        }

        foreach (var (b, hit) in toBreak)
            BreakBody(b, hit.point, hit.normal, hit.speed);
    }

    private static void TryMarkBreakable(RigidBody b, float impactSpeed, Vector3 point, Vector3 normal, Dictionary<RigidBody, (float speed, Vector3 point, Vector3 normal)> toBreak)
    {
        if (b.IsStatic || !b.UserObject || !b.Breakable) return;
        if (b.BoundingRadius < 0.16f) return;
        if (impactSpeed < MathF.Max(0.5f, b.BreakThreshold)) return;

        if (!toBreak.TryGetValue(b, out var existing) || impactSpeed > existing.speed)
            toBreak[b] = (impactSpeed, point, normal);
    }

    private void BreakBody(RigidBody b, Vector3 hitPoint, Vector3 hitNormal, float impactSpeed)
    {
        if (!Bodies.Contains(b)) return;

        var pieces = new List<RigidBody>(Math.Clamp(b.BreakPieces, 3, 18));
        foreach (var child in b.Children)
            CreateBreakPiecesForChild(b, child, pieces);

        if (pieces.Count == 0) return;

        BreakEvents.Add(new BreakEvent(
            hitPoint,
            hitNormal.LengthSquared() > 1e-6f ? Vector3.Normalize(hitNormal) : Vector3.UnitY,
            impactSpeed,
            b.MaterialId,
            b.Color,
            pieces.Count));

        var originalVel = b.Velocity;
        var originalAng = b.AngularVelocity;
        var color = b.Color;

        RemoveBody(b);

        foreach (var p in pieces)
        {
            p.Color = Vector3.Clamp(color * (0.75f + (float)_breakRng.NextDouble() * 0.35f), Vector3.Zero, Vector3.One);
            p.MaterialId = b.MaterialId;
            p.Friction = b.Friction;
            p.Restitution = MathF.Max(b.Restitution, 0.18f);
            p.Flammability = b.Flammability;
            p.Conductivity = b.Conductivity;
            p.ExplosivePower = 0f; // debris does not chain-detonate by default
            p.Breakable = false; // one-level fracture keeps runaway debris under control
            p.BreakThreshold = b.BreakThreshold;
            p.Velocity = originalVel + RandomUnit() * (1.0f + (float)_breakRng.NextDouble() * 2.5f);
            p.AngularVelocity = originalAng + RandomUnit() * (2.0f + (float)_breakRng.NextDouble() * 5.0f);
            p.UserObject = true;
            p.Wake();
            Bodies.Add(p);
        }
    }

    private void CreateBreakPiecesForChild(RigidBody source, ChildShape child, List<RigidBody> pieces)
    {
        var childPos = source.Position + Vector3.Transform(child.LocalPos, source.Rotation);
        var childRot = source.Rotation.Multiply(child.LocalRot);
        int maxPieces = Math.Clamp(source.BreakPieces, 3, 18);

        if (child.Shape == ShapeType.Box)
        {
            var he = child.HalfExtents;
            if (he.LengthSquared() < 0.03f) return;

            switch (source.MaterialId)
            {
                case MaterialId.Wood:
                    CreateWoodSplinters(source, childPos, childRot, he, pieces, maxPieces);
                    return;
                case MaterialId.Glass:
                case MaterialId.Ice:
                    CreateGlassShards(source, childPos, childRot, he, pieces, maxPieces);
                    return;
                case MaterialId.Stone:
                    CreateStoneChunks(source, childPos, childRot, he, pieces, maxPieces);
                    return;
                case MaterialId.Plastic:
                case MaterialId.Synthetic:
                    CreatePlasticFragments(source, childPos, childRot, he, pieces, maxPieces);
                    return;
            }

            var smallHe = Vector3.Max(he * 0.46f, new Vector3(0.06f));
            int made = 0;
            for (int sx = -1; sx <= 1 && made < maxPieces; sx += 2)
                for (int sy = -1; sy <= 1 && made < maxPieces; sy += 2)
                    for (int sz = -1; sz <= 1 && made < maxPieces; sz += 2)
                    {
                        var local = new Vector3(sx * he.X * 0.48f, sy * he.Y * 0.48f, sz * he.Z * 0.48f);
                        var p = RigidBody.CreateBox(childPos + Vector3.Transform(local, childRot), smallHe, MathF.Max(source.Density, 0.001f));
                        p.Rotation = childRot;
                        p.UpdateDerived();
                        pieces.Add(p);
                        made++;
                    }
            return;
        }

        if (child.Shape == ShapeType.Sphere)
        {
            float r = MathF.Max(child.Radius * 0.42f, 0.055f);
            int count = Math.Min(maxPieces, 7);
            for (int i = 0; i < count; i++)
            {
                var dir = RandomUnit();
                var p = RigidBody.CreateSphere(childPos + dir * child.Radius * 0.38f, r, MathF.Max(source.Density, 0.001f));
                pieces.Add(p);
            }
            return;
        }

        // Capsules are approximated by bead-like fragments.
        if (child.Shape == ShapeType.Capsule)
        {
            float r = MathF.Max(child.Radius * 0.38f, 0.05f);
            int count = Math.Min(maxPieces, 6);
            for (int i = 0; i < count; i++)
            {
                float u = count == 1 ? 0f : (i / (float)(count - 1) - 0.5f) * child.HalfHeight * 2f;
                var local = new Vector3(0, u, 0) + RandomUnit() * child.Radius * 0.2f;
                var p = RigidBody.CreateSphere(childPos + Vector3.Transform(local, childRot), r, MathF.Max(source.Density, 0.001f));
                pieces.Add(p);
            }
        }
    }


    private void CreateWoodSplinters(RigidBody source, Vector3 childPos, Quaternion childRot, Vector3 he, List<RigidBody> pieces, int maxPieces)
    {
        int count = Math.Clamp(maxPieces, 4, 14);
        int axis = he.X >= he.Y && he.X >= he.Z ? 0 : (he.Y >= he.Z ? 1 : 2);
        for (int i = 0; i < count; i++)
        {
            var local = new Vector3(Rand(-he.X * 0.65f, he.X * 0.65f), Rand(-he.Y * 0.65f, he.Y * 0.65f), Rand(-he.Z * 0.65f, he.Z * 0.65f));
            var splinter = new Vector3(
                axis == 0 ? MathF.Max(he.X * Rand(0.28f, 0.55f), 0.05f) : MathF.Max(he.X * Rand(0.08f, 0.18f), 0.035f),
                axis == 1 ? MathF.Max(he.Y * Rand(0.28f, 0.55f), 0.05f) : MathF.Max(he.Y * Rand(0.08f, 0.18f), 0.035f),
                axis == 2 ? MathF.Max(he.Z * Rand(0.28f, 0.55f), 0.05f) : MathF.Max(he.Z * Rand(0.08f, 0.18f), 0.035f));
            var p = RigidBody.CreateBox(childPos + Vector3.Transform(local, childRot), splinter, MathF.Max(source.Density, 0.001f));
            p.Rotation = childRot;
            p.UpdateDerived();
            pieces.Add(p);
        }
    }

    private void CreateGlassShards(RigidBody source, Vector3 childPos, Quaternion childRot, Vector3 he, List<RigidBody> pieces, int maxPieces)
    {
        int count = Math.Clamp(maxPieces + 4, 8, 18);
        for (int i = 0; i < count; i++)
        {
            var local = new Vector3(Rand(-he.X * 0.75f, he.X * 0.75f), Rand(-he.Y * 0.75f, he.Y * 0.75f), Rand(-he.Z * 0.75f, he.Z * 0.75f));
            var shard = new Vector3(
                MathF.Max(he.X * Rand(0.10f, 0.26f), 0.025f),
                MathF.Max(he.Y * Rand(0.035f, 0.09f), 0.012f),
                MathF.Max(he.Z * Rand(0.10f, 0.26f), 0.025f));
            var p = RigidBody.CreateBox(childPos + Vector3.Transform(local, childRot), shard, MathF.Max(source.Density, 0.001f));
            p.Rotation = childRot;
            p.Restitution = MathF.Max(p.Restitution, 0.28f);
            p.UpdateDerived();
            pieces.Add(p);
        }
    }

    private void CreateStoneChunks(RigidBody source, Vector3 childPos, Quaternion childRot, Vector3 he, List<RigidBody> pieces, int maxPieces)
    {
        int count = Math.Clamp(maxPieces, 5, 12);
        for (int i = 0; i < count; i++)
        {
            var local = new Vector3(Rand(-he.X * 0.60f, he.X * 0.60f), Rand(-he.Y * 0.60f, he.Y * 0.60f), Rand(-he.Z * 0.60f, he.Z * 0.60f));
            var chunk = new Vector3(
                MathF.Max(he.X * Rand(0.22f, 0.42f), 0.045f),
                MathF.Max(he.Y * Rand(0.18f, 0.42f), 0.045f),
                MathF.Max(he.Z * Rand(0.22f, 0.42f), 0.045f));
            var p = RigidBody.CreateBox(childPos + Vector3.Transform(local, childRot), chunk, MathF.Max(source.Density, 0.001f));
            p.Rotation = childRot;
            p.Restitution = 0.08f;
            p.UpdateDerived();
            pieces.Add(p);
        }
    }

    private void CreatePlasticFragments(RigidBody source, Vector3 childPos, Quaternion childRot, Vector3 he, List<RigidBody> pieces, int maxPieces)
    {
        int count = Math.Clamp(maxPieces, 5, 12);
        for (int i = 0; i < count; i++)
        {
            var local = new Vector3(Rand(-he.X * 0.65f, he.X * 0.65f), Rand(-he.Y * 0.65f, he.Y * 0.65f), Rand(-he.Z * 0.65f, he.Z * 0.65f));
            var frag = new Vector3(
                MathF.Max(he.X * Rand(0.18f, 0.38f), 0.035f),
                MathF.Max(he.Y * Rand(0.12f, 0.32f), 0.030f),
                MathF.Max(he.Z * Rand(0.18f, 0.38f), 0.035f));
            var p = RigidBody.CreateBox(childPos + Vector3.Transform(local, childRot), frag, MathF.Max(source.Density, 0.001f));
            p.Rotation = childRot;
            p.Restitution = MathF.Max(source.Restitution, 0.22f);
            p.UpdateDerived();
            pieces.Add(p);
        }
    }

    private float Rand(float min, float max) => min + (float)_breakRng.NextDouble() * (max - min);
    private Vector3 RandomUnit()
    {
        var v = new Vector3(
            (float)_breakRng.NextDouble() * 2f - 1f,
            (float)_breakRng.NextDouble() * 2f - 1f,
            (float)_breakRng.NextDouble() * 2f - 1f);
        return v.LengthSquared() > 1e-6f ? Vector3.Normalize(v) : Vector3.UnitY;
    }

    /// <summary>Drops a body and any joints that referenced it. Keeps the world consistent.</summary>
    public void RemoveBody(RigidBody b)
    {
        Bodies.Remove(b);
        Joints.RemoveAll(j => j.Involves(b));
        if (Grabbed == b) Grabbed = null;
    }

    private void ApplyDragSpring(float h)
    {
        if (Grabbed == null || Grabbed.IsStatic) return;
        Grabbed.Wake();

        var anchor = Grabbed.Position + Vector3.Transform(GrabLocalAnchor, Grabbed.Rotation);
        var delta = DragTarget - anchor;
        float len = delta.Length();
        if (len > 4f) delta *= 4f / len; // limit spring stretch -> no explosions

        var velAtAnchor = Grabbed.VelocityAt(anchor);
        var force = Grabbed.Mass * (delta * 180f - velAtAnchor * 18f);
        Grabbed.ApplyImpulse(force * h, anchor);

        // extra angular damping while held, otherwise boxes spin like propellers
        Grabbed.AngularVelocity *= 1f / (1f + 4f * h);
    }

    // ================= contact generation =================

    private void GenerateContacts()
    {
        foreach (var body in Bodies)
        {
            if (body.Inactive) continue;
            foreach (ref var p in body.Proxies.AsSpan())
            {
                switch (p.Shape)
                {
                    case ShapeType.Sphere: SpherePlane(in p); break;
                    case ShapeType.Box: BoxPlane(in p); break;
                    case ShapeType.Capsule: CapsulePlane(in p); break;
                }
            }
        }

        // --- broad phase ---
        // Small bodies go into a uniform spatial hash so we only test nearby pairs instead
        // of every pair (the old O(n^2) loop is what made big collapses crawl). A few "huge"
        // bodies (the arena walls) would blow up the cell size, so they're tested separately
        // against everything - there are only a handful of them.
        int count = Bodies.Count;

        float maxR = 0f;
        for (int i = 0; i < count; i++)
        {
            float r = Bodies[i].BoundingRadius;
            if (r <= HugeRadius && r > maxR) maxR = r;
        }
        _cellSize = Math.Clamp(2f * maxR, 0.6f, 2f * HugeRadius);

        if (_next.Length < count) _next = new int[Math.Max(count, 64)];
        _cellHead.Clear();

        for (int i = 0; i < count; i++)
        {
            var b = Bodies[i];
            if (b.BoundingRadius > HugeRadius) continue; // huge bodies handled below
            long key = CellKey(b.Position);
            _next[i] = _cellHead.TryGetValue(key, out int head) ? head : -1;
            _cellHead[key] = i;
        }

        // small vs small: scan each body's 3x3x3 cell neighbourhood; j>i keeps each pair once
        for (int i = 0; i < count; i++)
        {
            var a = Bodies[i];
            if (a.BoundingRadius > HugeRadius) continue;
            (int cx, int cy, int cz) = CellCoords(a.Position);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!_cellHead.TryGetValue(PackKey(cx + dx, cy + dy, cz + dz), out int j)) continue;
                        while (j != -1)
                        {
                            if (j > i) NarrowPhase(a, Bodies[j]);
                            j = _next[j];
                        }
                    }
        }

        // huge bodies (walls) vs everything; keep A = lower index so warm-start keys are stable
        for (int p = 0; p < count; p++)
        {
            if (Bodies[p].BoundingRadius <= HugeRadius) continue;
            for (int q = 0; q < count; q++)
            {
                if (q == p) continue;
                bool qHuge = Bodies[q].BoundingRadius > HugeRadius;
                if (qHuge && q < p) continue; // huge-huge pair handled once
                int lo = Math.Min(p, q), hi = Math.Max(p, q);
                NarrowPhase(Bodies[lo], Bodies[hi]);
            }
        }
    }

    private const float HugeRadius = 4f;
    private readonly Dictionary<long, int> _cellHead = new();
    private int[] _next = [];
    private float _cellSize = 1f;

    private (int, int, int) CellCoords(Vector3 p)
        => ((int)MathF.Floor(p.X / _cellSize), (int)MathF.Floor(p.Y / _cellSize), (int)MathF.Floor(p.Z / _cellSize));

    private long CellKey(Vector3 p)
    {
        var (cx, cy, cz) = CellCoords(p);
        return PackKey(cx, cy, cz);
    }

    // exact (collision-free) packing of cell coords in +/-2^20 cells into one long
    private static long PackKey(int x, int y, int z)
        => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

    private void NarrowPhase(RigidBody a, RigidBody b)
    {
        if (a.Inactive && b.Inactive) return;

        float rr = a.BoundingRadius + b.BoundingRadius;
        if ((b.Position - a.Position).LengthSquared() > rr * rr) return;

        foreach (ref var pa in a.Proxies.AsSpan())
            foreach (ref var pb in b.Proxies.AsSpan())
            {
                float prr = pa.BoundingRadius + pb.BoundingRadius;
                if ((pb.Position - pa.Position).LengthSquared() > prr * prr) continue;
                Dispatch(in pa, in pb);
            }
    }

    private void Dispatch(in ShapeProxy a, in ShapeProxy b)
    {
        var (sa, sb) = (a.Shape, b.Shape);
        if (sa == ShapeType.Sphere && sb == ShapeType.Sphere) SphereSphere(in a, in b);
        else if (sa == ShapeType.Sphere && sb == ShapeType.Box) SphereBox(in a, in b, flip: false);
        else if (sa == ShapeType.Box && sb == ShapeType.Sphere) SphereBox(in b, in a, flip: true);
        else if (sa == ShapeType.Box && sb == ShapeType.Box) BoxBox(in a, in b);
        else if (sa == ShapeType.Capsule && sb == ShapeType.Capsule) CapsuleCapsule(in a, in b);
        else if (sa == ShapeType.Capsule && sb == ShapeType.Sphere) CapsuleSphere(in a, in b, flip: false);
        else if (sa == ShapeType.Sphere && sb == ShapeType.Capsule) CapsuleSphere(in b, in a, flip: true);
        else if (sa == ShapeType.Capsule && sb == ShapeType.Box) CapsuleBox(in a, in b, flip: false);
        else CapsuleBox(in b, in a, flip: true);
    }

    private void AddContact(RigidBody a, RigidBody b, Vector3 point, Vector3 normalAtoB, float penetration)
    {
        // a sleeping body only wakes if the thing touching it actually approaches with
        // some speed; a resting contact must NOT wake it, or stacks would keep each
        // other awake forever and sleeping would be useless
        if (a.Sleeping != b.Sleeping)
        {
            var sleeper = a.Sleeping ? a : b;
            var awake = a.Sleeping ? b : a;
            float approach = MathF.Abs(Vector3.Dot(
                awake.VelocityAt(point) - sleeper.VelocityAt(point), normalAtoB));
            if (approach > WakeImpactSpeed || awake == Grabbed)
                sleeper.Wake();
        }

        _contacts.Add(new Contact { A = a, B = b, Point = point, Normal = normalAtoB, Penetration = penetration });
    }

    // ---- vs ground plane (y = 0) ----

    private void SpherePlane(in ShapeProxy s)
    {
        float dist = s.Position.Y - s.Radius;
        if (dist < 0f)
            AddContact(Ground, s.Owner, s.Position - new Vector3(0, s.Radius, 0), Vector3.UnitY, -dist);
    }

    private void BoxPlane(in ShapeProxy b)
    {
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    var local = new Vector3(sx * b.HalfExtents.X, sy * b.HalfExtents.Y, sz * b.HalfExtents.Z);
                    var corner = b.Position + Vector3.Transform(local, b.Rotation);
                    if (corner.Y < 0f)
                        AddContact(Ground, b.Owner, corner, Vector3.UnitY, -corner.Y);
                }
    }

    private void CapsulePlane(in ShapeProxy c)
    {
        c.CapsuleSegment(out var p0, out var p1);
        foreach (var p in stackalloc[] { p0, p1 })
        {
            float dist = p.Y - c.Radius;
            if (dist < 0f)
                AddContact(Ground, c.Owner, p - new Vector3(0, c.Radius, 0), Vector3.UnitY, -dist);
        }
    }

    // ---- sphere pairs ----

    private void SphereSphere(in ShapeProxy a, in ShapeProxy b)
    {
        var d = b.Position - a.Position;
        float dist = d.Length();
        float sum = a.Radius + b.Radius;
        if (dist >= sum) return;

        var n = dist > 1e-6f ? d / dist : Vector3.UnitY;
        var point = a.Position + n * (a.Radius - (sum - dist) * 0.5f);
        AddContact(a.Owner, b.Owner, point, n, sum - dist);
    }

    private void SphereBox(in ShapeProxy s, in ShapeProxy box, bool flip)
    {
        if (!SphereObbCore(s.Position, s.Radius, in box,
                out var point, out var normalBoxToSphere, out float penetration))
            return;

        if (flip) AddContact(box.Owner, s.Owner, point, normalBoxToSphere, penetration);
        else AddContact(s.Owner, box.Owner, point, -normalBoxToSphere, penetration);
    }

    /// <summary>Sphere (center, radius) vs OBB. Handles the deep case (center inside the box).</summary>
    private static bool SphereObbCore(Vector3 center, float radius, in ShapeProxy box,
        out Vector3 point, out Vector3 normalBoxToSphere, out float penetration)
    {
        var invRot = Quaternion.Conjugate(box.Rotation);
        var local = Vector3.Transform(center - box.Position, invRot);
        var h = box.HalfExtents;
        var clamped = Vector3.Clamp(local, -h, h);

        if (clamped == local)
        {
            // center is inside the box: push out through the nearest face
            float dx = h.X - MathF.Abs(local.X);
            float dy = h.Y - MathF.Abs(local.Y);
            float dz = h.Z - MathF.Abs(local.Z);

            Vector3 nLocal;
            float depth;
            if (dx <= dy && dx <= dz) { nLocal = new Vector3(MathF.Sign(local.X), 0, 0); depth = dx; }
            else if (dy <= dz) { nLocal = new Vector3(0, MathF.Sign(local.Y), 0); depth = dy; }
            else { nLocal = new Vector3(0, 0, MathF.Sign(local.Z)); depth = dz; }
            if (nLocal == Vector3.Zero) nLocal = Vector3.UnitY;

            normalBoxToSphere = Vector3.Transform(nLocal, box.Rotation);
            point = center;
            penetration = radius + depth;
            return true;
        }

        var delta = local - clamped;
        float dist = delta.Length();
        if (dist >= radius)
        {
            point = default; normalBoxToSphere = default; penetration = 0f;
            return false;
        }

        normalBoxToSphere = Vector3.Transform(delta / dist, box.Rotation);
        point = box.Position + Vector3.Transform(clamped, box.Rotation);
        penetration = radius - dist;
        return true;
    }

    // ---- capsule pairs ----

    private void CapsuleSphere(in ShapeProxy cap, in ShapeProxy s, bool flip)
    {
        cap.CapsuleSegment(out var p0, out var p1);
        var closest = ClosestPtSegmentPoint(p0, p1, s.Position);

        var d = s.Position - closest;
        float dist = d.Length();
        float sum = cap.Radius + s.Radius;
        if (dist >= sum) return;

        var n = dist > 1e-6f ? d / dist : Vector3.UnitY;
        var point = closest + n * cap.Radius;

        if (flip) AddContact(s.Owner, cap.Owner, point, -n, sum - dist);
        else AddContact(cap.Owner, s.Owner, point, n, sum - dist);
    }

    private void CapsuleCapsule(in ShapeProxy a, in ShapeProxy b)
    {
        a.CapsuleSegment(out var a0, out var a1);
        b.CapsuleSegment(out var b0, out var b1);
        ClosestPtSegmentSegment(a0, a1, b0, b1, out var pA, out var pB);

        var d = pB - pA;
        float dist = d.Length();
        float sum = a.Radius + b.Radius;
        if (dist >= sum) return;

        var n = dist > 1e-6f ? d / dist : Vector3.UnitY;
        AddContact(a.Owner, b.Owner, (pA + n * a.Radius + pB - n * b.Radius) * 0.5f, n, sum - dist);
    }

    private void CapsuleBox(in ShapeProxy cap, in ShapeProxy box, bool flip)
    {
        cap.CapsuleSegment(out var p0, out var p1);

        var invRot = Quaternion.Conjugate(box.Rotation);
        var l0 = Vector3.Transform(p0 - box.Position, invRot);
        var l1 = Vector3.Transform(p1 - box.Position, invRot);
        var h = box.HalfExtents;

        // squared distance from a point of the segment to the box is convex in t,
        // so a plain ternary search finds the deepest spot - no special cases needed
        float DistSq(float t)
        {
            var p = Vector3.Lerp(l0, l1, t);
            return (p - Vector3.Clamp(p, -h, h)).LengthSquared();
        }

        float lo = 0f, hi = 1f;
        for (int i = 0; i < 48; i++)
        {
            float m1 = lo + (hi - lo) / 3f;
            float m2 = hi - (hi - lo) / 3f;
            if (DistSq(m1) <= DistSq(m2)) hi = m2; else lo = m1;
        }
        float tBest = (lo + hi) * 0.5f;

        // also probe both ends: a capsule lying flat on a box needs more than one
        // contact point or it would wobble like a seesaw
        float lastT = float.MinValue;
        foreach (float t in stackalloc[] { tBest, 0f, 1f })
        {
            if (MathF.Abs(t - lastT) < 0.05f) continue;

            var center = Vector3.Lerp(p0, p1, t);
            if (!SphereObbCore(center, cap.Radius, in box,
                    out var point, out var normalBoxToCap, out float pen))
                continue;

            lastT = t;
            if (flip) AddContact(box.Owner, cap.Owner, point, normalBoxToCap, pen);
            else AddContact(cap.Owner, box.Owner, point, -normalBoxToCap, pen);
        }
    }

    private static Vector3 ClosestPtSegmentPoint(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = b - a;
        float t = Vector3.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-9f);
        return a + ab * Math.Clamp(t, 0f, 1f);
    }

    /// <summary>Closest points between two segments (Ericson, Real-Time Collision Detection 5.1.9).</summary>
    private static void ClosestPtSegmentSegment(
        Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        float a = d1.LengthSquared();
        float e = d2.LengthSquared();
        float f = Vector3.Dot(d2, r);
        float s, t;

        if (a <= 1e-9f && e <= 1e-9f) { c1 = p1; c2 = p2; return; }

        if (a <= 1e-9f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-9f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom > 1e-9f ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }

        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    // ---- OBB vs OBB: SAT over all 15 axes, then face clipping or edge-edge ----

    private void BoxBox(in ShapeProxy a, in ShapeProxy b)
    {
        Vector3[] axesA = [a.Axis(0), a.Axis(1), a.Axis(2)];
        Vector3[] axesB = [b.Axis(0), b.Axis(1), b.Axis(2)];
        var d = b.Position - a.Position;
        var ea = a.HalfExtents;
        var eb = b.HalfExtents;

        float bestPen = float.MaxValue;
        Vector3 bestAxis = Vector3.UnitY;
        int bestType = -1;                 // 0 = face of A, 1 = face of B, 2 = edge-edge
        int bestI = 0, bestJ = 0;

        bool Test(Vector3 axis, int type, int i, int j)
        {
            float lenSq = axis.LengthSquared();
            if (lenSq < 1e-8f) return true; // near-parallel edges, the cross product is junk
            axis /= MathF.Sqrt(lenSq);

            float ra = ea.X * MathF.Abs(Vector3.Dot(axesA[0], axis))
                     + ea.Y * MathF.Abs(Vector3.Dot(axesA[1], axis))
                     + ea.Z * MathF.Abs(Vector3.Dot(axesA[2], axis));
            float rb = eb.X * MathF.Abs(Vector3.Dot(axesB[0], axis))
                     + eb.Y * MathF.Abs(Vector3.Dot(axesB[1], axis))
                     + eb.Z * MathF.Abs(Vector3.Dot(axesB[2], axis));

            float proj = Vector3.Dot(d, axis);
            float pen = ra + rb - MathF.Abs(proj);
            if (pen < 0f) return false;    // found a separating axis, no collision

            // edge axes carry a 5% penalty: an edge-edge pair gives a single contact
            // point, a face gives a whole clipped manifold, so when penetrations are
            // close we'd much rather pick the face. (Getting the sign of this bias
            // wrong selects edges for plain stacked boxes - ask me how I know.)
            float effective = type == 2 ? pen * 1.05f + 1e-4f : pen;
            if (effective < bestPen)
            {
                bestPen = effective;
                bestAxis = proj < 0f ? -axis : axis; // orient from A toward B
                bestType = type;
                bestI = i; bestJ = j;
            }
            return true;
        }

        for (int i = 0; i < 3; i++) if (!Test(axesA[i], 0, i, 0)) return;
        for (int j = 0; j < 3; j++) if (!Test(axesB[j], 1, 0, j)) return;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (!Test(Vector3.Cross(axesA[i], axesB[j]), 2, i, j)) return;

        if (bestType == 2)
        {
            EdgeEdgeContact(in a, in b, axesA, axesB, bestAxis, bestI, bestJ);
            return;
        }

        bool refIsA = bestType == 0;
        ref readonly var refBox = ref refIsA ? ref a : ref b;
        ref readonly var incBox = ref refIsA ? ref b : ref a;
        var refNormal = refIsA ? bestAxis : -bestAxis; // points from refBox toward incBox
        int refAxisIdx = refIsA ? bestI : bestJ;

        // incident face = the face of the other box looking most directly back at us
        int incAxisIdx = 0;
        float minDot = float.MaxValue;
        float incSign = 1f;
        for (int i = 0; i < 3; i++)
        {
            float dot = Vector3.Dot(incBox.Axis(i), refNormal);
            if (dot < minDot) { minDot = dot; incAxisIdx = i; incSign = 1f; }
            if (-dot < minDot) { minDot = -dot; incAxisIdx = i; incSign = -1f; }
        }

        var incNormal = incBox.Axis(incAxisIdx) * incSign;
        int iu = (incAxisIdx + 1) % 3, iv = (incAxisIdx + 2) % 3;
        var incU = incBox.Axis(iu) * Comp(incBox.HalfExtents, iu);
        var incV = incBox.Axis(iv) * Comp(incBox.HalfExtents, iv);
        var incCenter = incBox.Position + incNormal * Comp(incBox.HalfExtents, incAxisIdx);

        var poly = new List<Vector3>(8)
        {
            incCenter + incU + incV,
            incCenter + incU - incV,
            incCenter - incU - incV,
            incCenter - incU + incV,
        };

        // Sutherland-Hodgman: shave the incident face down by the 4 side planes
        // of the reference face; whatever survives below the face is the manifold
        int ru = (refAxisIdx + 1) % 3, rv = (refAxisIdx + 2) % 3;
        var axisU = refBox.Axis(ru);
        var axisV = refBox.Axis(rv);
        ClipPolygon(poly, axisU, Vector3.Dot(refBox.Position, axisU) + Comp(refBox.HalfExtents, ru));
        ClipPolygon(poly, -axisU, -Vector3.Dot(refBox.Position, axisU) + Comp(refBox.HalfExtents, ru));
        ClipPolygon(poly, axisV, Vector3.Dot(refBox.Position, axisV) + Comp(refBox.HalfExtents, rv));
        ClipPolygon(poly, -axisV, -Vector3.Dot(refBox.Position, axisV) + Comp(refBox.HalfExtents, rv));

        float faceOffset = Vector3.Dot(refBox.Position, refNormal) + Comp(refBox.HalfExtents, refAxisIdx);
        var normalAtoB = refIsA ? refNormal : -refNormal;

        foreach (var p in poly)
        {
            float sep = Vector3.Dot(p, refNormal) - faceOffset;
            if (sep < 0f)
                AddContact(a.Owner, b.Owner, p - refNormal * (sep * 0.5f), normalAtoB, -sep);
        }
    }

    private void EdgeEdgeContact(in ShapeProxy a, in ShapeProxy b,
        Vector3[] axesA, Vector3[] axesB, Vector3 normalAtoB, int edgeAxisA, int edgeAxisB)
    {
        // pick the edge of each box closest to the other one: start at the center
        // and walk to the corner along the two non-edge axes, choosing signs that
        // move toward the contact
        var pA = a.Position;
        for (int k = 0; k < 3; k++)
        {
            if (k == edgeAxisA) continue;
            float s = Vector3.Dot(axesA[k], normalAtoB) > 0f ? 1f : -1f;
            pA += axesA[k] * (s * Comp(a.HalfExtents, k));
        }

        var pB = b.Position;
        for (int k = 0; k < 3; k++)
        {
            if (k == edgeAxisB) continue;
            float s = Vector3.Dot(axesB[k], -normalAtoB) > 0f ? 1f : -1f;
            pB += axesB[k] * (s * Comp(b.HalfExtents, k));
        }

        var d1 = axesA[edgeAxisA];
        var d2 = axesB[edgeAxisB];
        var r = pA - pB;
        float bDot = Vector3.Dot(d1, d2);
        float c = Vector3.Dot(d1, r);
        float f = Vector3.Dot(d2, r);
        float denom = 1f - bDot * bDot;
        if (MathF.Abs(denom) < 1e-6f) return; // parallel edges - the face case will pick this up

        float s1 = Math.Clamp((bDot * f - c) / denom, -Comp(a.HalfExtents, edgeAxisA), Comp(a.HalfExtents, edgeAxisA));
        float t1 = Math.Clamp((f - bDot * c) / denom, -Comp(b.HalfExtents, edgeAxisB), Comp(b.HalfExtents, edgeAxisB));

        var cA = pA + d1 * s1;
        var cB = pB + d2 * t1;

        float depth = Vector3.Dot(cA - cB, normalAtoB);
        if (depth < 0f) depth = 0.001f;

        AddContact(a.Owner, b.Owner, (cA + cB) * 0.5f, normalAtoB, depth);
    }

    private static float Comp(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

    private static void ClipPolygon(List<Vector3> poly, Vector3 n, float offset)
    {
        if (poly.Count == 0) return;
        var output = new List<Vector3>(poly.Count + 2);

        for (int i = 0; i < poly.Count; i++)
        {
            var cur = poly[i];
            var next = poly[(i + 1) % poly.Count];
            float dCur = Vector3.Dot(cur, n) - offset;
            float dNext = Vector3.Dot(next, n) - offset;

            if (dCur <= 0f) output.Add(cur);
            if (dCur * dNext < 0f)
            {
                float t = dCur / (dCur - dNext);
                output.Add(cur + (next - cur) * t);
            }
        }

        poly.Clear();
        poly.AddRange(output);
    }

    // ================= solver =================

    private void Presolve(Contact c, float h)
    {
        c.RA = c.Point - c.A.Position;
        c.RB = c.Point - c.B.Position;
        var n = c.Normal;

        c.MassNormal = 1f / EffectiveMass(c, n);

        var t1 = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.X) > 0.7f ? Vector3.UnitY : Vector3.UnitX));
        var t2 = Vector3.Cross(n, t1);
        c.Tangent1 = t1;
        c.Tangent2 = t2;
        c.MassTangent1 = 1f / EffectiveMass(c, t1);
        c.MassTangent2 = 1f / EffectiveMass(c, t2);

        float vn = Vector3.Dot(RelativeVelocity(c), n);
        float restitution = MathF.Max(c.A.Restitution, c.B.Restitution);
        c.VelocityBias = vn < -RestitutionThreshold ? -restitution * vn : 0f;
        c.ImpactSpeed = vn < 0f ? -vn : 0f;

        c.PositionBias = MathF.Min(Beta / h * MathF.Max(0f, c.Penetration - Slop), 4f);
        c.Pn = c.Pt1 = c.Pt2 = c.Pnb = 0f;

        // warm start: if we solved (almost) this contact last substep, start from
        // last frame's impulses instead of zero
        if (_warmCache.TryGetValue((c.A, c.B), out var cached))
        {
            var localA = Vector3.Transform(c.Point - c.A.Position, Quaternion.Conjugate(c.A.Rotation));
            foreach (var w in cached)
            {
                if ((w.LocalA - localA).LengthSquared() > WarmStartMatchDistSq) continue;
                c.Pn = w.Pn;
                c.Pt1 = w.Pt1;
                c.Pt2 = w.Pt2;
                ApplyPair(c, n * c.Pn + t1 * c.Pt1 + t2 * c.Pt2);
                break;
            }
        }
    }

    private void StoreWarmCache()
    {
        (_warmCache, _warmCacheBack) = (_warmCacheBack, _warmCache);
        _warmCache.Clear();
        foreach (var c in _contacts)
        {
            if (c is { Pn: 0f, Pt1: 0f, Pt2: 0f }) continue;
            var key = (c.A, c.B);
            if (!_warmCache.TryGetValue(key, out var list))
                _warmCache[key] = list = [];
            list.Add(new CachedImpulse
            {
                LocalA = Vector3.Transform(c.Point - c.A.Position, Quaternion.Conjugate(c.A.Rotation)),
                Pn = c.Pn,
                Pt1 = c.Pt1,
                Pt2 = c.Pt2,
            });
        }
    }

    private static float EffectiveMass(Contact c, Vector3 dir)
    {
        float k = 1e-6f;
        if (!c.A.Inactive)
        {
            var raCrossD = Vector3.Cross(c.RA, dir);
            k += c.A.InvMass + Vector3.Dot(Vector3.Cross(c.A.InvInertiaWorld.Transform(raCrossD), c.RA), dir);
        }
        if (!c.B.Inactive)
        {
            var rbCrossD = Vector3.Cross(c.RB, dir);
            k += c.B.InvMass + Vector3.Dot(Vector3.Cross(c.B.InvInertiaWorld.Transform(rbCrossD), c.RB), dir);
        }
        return k;
    }

    private static Vector3 RelativeVelocity(Contact c)
        => (c.B.Velocity + Vector3.Cross(c.B.AngularVelocity, c.RB))
         - (c.A.Velocity + Vector3.Cross(c.A.AngularVelocity, c.RA));

    private static void SolveContact(Contact c)
    {
        var n = c.Normal;

        // normal impulse with accumulated clamping: individual iterations may go
        // negative, only the running total has to stay >= 0
        float vn = Vector3.Dot(RelativeVelocity(c), n);
        float dPn = c.MassNormal * (c.VelocityBias - vn);
        float oldPn = c.Pn;
        c.Pn = MathF.Max(oldPn + dPn, 0f);
        ApplyPair(c, n * (c.Pn - oldPn));

        // friction: Coulomb cone approximated by a box on two tangents
        float friction = MathF.Sqrt(c.A.Friction * c.B.Friction);
        float maxPt = friction * c.Pn;

        float vt1 = Vector3.Dot(RelativeVelocity(c), c.Tangent1);
        float oldPt1 = c.Pt1;
        c.Pt1 = Math.Clamp(oldPt1 + c.MassTangent1 * -vt1, -maxPt, maxPt);
        ApplyPair(c, c.Tangent1 * (c.Pt1 - oldPt1));

        float vt2 = Vector3.Dot(RelativeVelocity(c), c.Tangent2);
        float oldPt2 = c.Pt2;
        c.Pt2 = Math.Clamp(oldPt2 + c.MassTangent2 * -vt2, -maxPt, maxPt);
        ApplyPair(c, c.Tangent2 * (c.Pt2 - oldPt2));
    }

    private static void SolvePosition(Contact c)
    {
        if (c.PositionBias <= 0f) return;
        var n = c.Normal;

        var relBias = (c.B.BiasVelocity + Vector3.Cross(c.B.BiasAngularVelocity, c.RB))
                    - (c.A.BiasVelocity + Vector3.Cross(c.A.BiasAngularVelocity, c.RA));
        float vbn = Vector3.Dot(relBias, n);
        float dP = c.MassNormal * (c.PositionBias - vbn);
        float old = c.Pnb;
        c.Pnb = MathF.Max(old + dP, 0f);
        dP = c.Pnb - old;

        var impulse = n * dP;
        if (!c.A.Inactive)
        {
            c.A.BiasVelocity -= impulse * c.A.InvMass;
            c.A.BiasAngularVelocity -= c.A.InvInertiaWorld.Transform(Vector3.Cross(c.RA, impulse));
        }
        if (!c.B.Inactive)
        {
            c.B.BiasVelocity += impulse * c.B.InvMass;
            c.B.BiasAngularVelocity += c.B.InvInertiaWorld.Transform(Vector3.Cross(c.RB, impulse));
        }
    }

    private static void ApplyPair(Contact c, Vector3 impulse)
    {
        // sleeping bodies are treated as static here; if the hit was hard enough
        // to matter, AddContact has already woken them up
        if (!c.A.Inactive)
        {
            c.A.Velocity -= impulse * c.A.InvMass;
            c.A.AngularVelocity -= c.A.InvInertiaWorld.Transform(Vector3.Cross(c.RA, impulse));
        }
        if (!c.B.Inactive)
        {
            c.B.Velocity += impulse * c.B.InvMass;
            c.B.AngularVelocity += c.B.InvInertiaWorld.Transform(Vector3.Cross(c.RB, impulse));
        }
    }

    // ================= picking =================

    /// <summary>
    /// Radial impulse from a point - the sandbox "explosion". Strength falls off with
    /// distance (linear), and a small upward bias makes the debris hop instead of just
    /// sliding along the floor. Everything in range gets woken first, otherwise sleeping
    /// bodies would just ignore the blast.
    /// </summary>
    public void ApplyExplosion(Vector3 center, float radius, float strength)
    {
        foreach (var b in Bodies)
        {
            if (b.IsStatic) continue;
            var d = b.Position - center;
            float dist = d.Length();
            if (dist > radius) continue;

            b.Wake();
            var dir = dist > 1e-4f ? d / dist : Vector3.UnitY;
            dir = Vector3.Normalize(dir + new Vector3(0, 0.6f, 0)); // lift bias
            float falloff = 1f - dist / radius;
            b.ApplyImpulse(dir * (strength * falloff * b.Mass), b.Position);
        }
    }

    public RigidBody? RayCast(Vector3 origin, Vector3 dir, out float tHit, out Vector3 hitPoint)
    {
        tHit = float.MaxValue;
        hitPoint = default;
        RigidBody? best = null;

        foreach (var body in Bodies)
        {
            if (body.IsStatic) continue;
            foreach (ref var p in body.Proxies.AsSpan())
            {
                float t = p.Shape switch
                {
                    ShapeType.Sphere => RaySphere(origin, dir, p.Position, p.Radius),
                    ShapeType.Box => RayObb(origin, dir, in p),
                    ShapeType.Capsule => RayCapsule(origin, dir, in p),
                    _ => -1f,
                };
                if (t >= 0f && t < tHit)
                {
                    tHit = t;
                    best = body;
                }
            }
        }

        if (best != null) hitPoint = origin + dir * tHit;
        return best;
    }

    private static float RaySphere(Vector3 o, Vector3 d, Vector3 center, float r)
    {
        Vector3 m = o - center;
        float b = Vector3.Dot(m, d);
        float c = Vector3.Dot(m, m) - r * r;
        float disc = b * b - c;
        if (disc < 0f) return -1f;
        float t = -b - MathF.Sqrt(disc);
        return t >= 0f ? t : -1f;
    }

    private static float RayObb(Vector3 o, Vector3 d, in ShapeProxy box)
    {
        // classic slab test, done in the box's local frame
        Quaternion invRot = Quaternion.Conjugate(box.Rotation);
        Vector3 lo = Vector3.Transform(o - box.Position, invRot);
        Vector3 ld = Vector3.Transform(d, invRot);
        Vector3 h = box.HalfExtents;

        float tMin = 0f, tMax = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float oi = Comp(lo, i), di = Comp(ld, i), hi = Comp(h, i);
            if (MathF.Abs(di) < 1e-8f)
            {
                if (oi < -hi || oi > hi) return -1f;
                continue;
            }
            float inv = 1f / di;
            float t1 = (-hi - oi) * inv;
            float t2 = (hi - oi) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) return -1f;
        }
        return tMin;
    }

    private static float RayCapsule(Vector3 o, Vector3 d, in ShapeProxy c)
    {
        var invRot = Quaternion.Conjugate(c.Rotation);
        var lo = Vector3.Transform(o - c.Position, invRot);
        var ld = Vector3.Transform(d, invRot);
        float r = c.Radius, h = c.HalfHeight;

        float best = -1f;
        void Consider(float t) { if (t >= 0f && (best < 0f || t < best)) best = t; }

        // side: infinite cylinder x^2 + z^2 = r^2, accept hits within the segment span
        float a = ld.X * ld.X + ld.Z * ld.Z;
        if (a > 1e-9f)
        {
            float b = 2f * (lo.X * ld.X + lo.Z * ld.Z);
            float cc = lo.X * lo.X + lo.Z * lo.Z - r * r;
            float disc = b * b - 4f * a * cc;
            if (disc >= 0f)
            {
                float sq = MathF.Sqrt(disc);
                foreach (float t in stackalloc[] { (-b - sq) / (2f * a), (-b + sq) / (2f * a) })
                    if (MathF.Abs(lo.Y + ld.Y * t) <= h)
                        Consider(t);
            }
        }

        // the two sphere caps
        foreach (float cy in stackalloc[] { -h, h })
        {
            var co = lo - new Vector3(0, cy, 0);
            float b = 2f * Vector3.Dot(co, ld);
            float cc = co.LengthSquared() - r * r;
            float dlen = ld.LengthSquared();
            float disc = b * b - 4f * cc * dlen;
            if (disc < 0f) continue;
            float sq = MathF.Sqrt(disc);
            Consider((-b - sq) / (2f * dlen));
            Consider((-b + sq) / (2f * dlen));
        }

        return best;
    }
}
