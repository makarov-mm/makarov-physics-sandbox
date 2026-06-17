using MakarovPhysicsSandbox.Physics;
using System.Numerics;
using MakarovPhysicsSandbox.Material;

namespace MakarovPhysicsSandbox;

// =====================================================================================
//  Extra presets (added on top of the existing set in Core.cs / Mechanisms.cs).
//
//  These are deliberately built ONLY on systems that already exist - materials, fire,
//  electricity, explosives, breakables, joints, ragdoll/android, triggers+outputs - so
//  they add variety and "matrix" showcases without introducing new entity types.
//
//  Helper API reused from the rest of the project:
//    ResetToEmptyScene(water:)   - clear the scene (optionally flood it)
//    WithMaterial(body, id)      - apply a hard MaterialDefinition (density/flammability/...)
//    MakeBreakable(body, thr)    - mark a body fracturable
//    AddBody(body, color)        - register a dynamic body + colour
//    _heat.Ignite(body)          - set a body on fire
//    _electricity.Electrify(b,q) - inject charge
//    _ragdolls.SpawnAndroid(..)  - spawn a synthetic dummy (Android)
//    AddBrickTower / AddPyramidTower - stacked breakable structures
//    _triggers.Add(new SceneTrigger { Outputs = ... }) - wired pressure plates
// =====================================================================================
internal sealed partial class GlPanel
{
    // ---- Explosive Domino: ignite one end, the whole row chain-detonates. fire -> explosive -> blast. ----
    private void BuildExplosiveDomino()
    {
        ResetToEmptyScene();

        RigidBody? first = null;

        for (int i = 0; i < 8; i++)
        {
            RigidBody barrel = WithMaterial(RigidBody.CreateBox(new Vector3(-5.6f + i * 1.45f, 0.5f, 0f), new Vector3(0.3f, 0.5f, 0.3f), density: 1.0f), MaterialId.Explosive);
            barrel.Tag = "ExplosiveBarrel";
            barrel.ExplosivePower = 1.6f;
            AddBody(barrel, barrel.Color);
            first ??= barrel;
        }

        // Victims at the far end so the chain has a payoff target.
        _ragdolls.SpawnAndroid(_world, new Vector3(6.6f, 0f, 0.4f));
        _ragdolls.SpawnAndroid(_world, new Vector3(7.2f, 0f, -0.6f));

        if (first != null) _heat.Ignite(first);
        StatusUpdated?.Invoke("Preset: Explosive Domino — the lit barrel detonates and the blast chains down the row into the androids.");
    }

    // ---- Barrel Pyramid: a fast ram ball slams a stack of explosive barrels. impact -> explosive chain. ----
    private void BuildBarrelPyramid()
    {
        ResetToEmptyScene();

        // Pyramid of explosive barrels.
        int rows = 4;

        for (int row = 0; row < rows; row++)
        {
            int count = rows - row;
            float y = 0.5f + row * 0.92f;

            for (int i = 0; i < count; i++)
            {
                float x = 4.2f + (i - (count - 1) * 0.5f) * 0.95f;

                RigidBody barrel = WithMaterial(
                    RigidBody.CreateBox(new Vector3(x, y, 0f), new Vector3(0.32f, 0.45f, 0.32f), density: 1.0f),
                    MaterialId.Explosive);
                barrel.Tag = "ExplosiveBarrel";
                barrel.ExplosivePower = 1.6f;
                AddBody(barrel, barrel.Color);
            }
        }

        // Heavy metal ram fired into the stack.
        RigidBody ram = WithMaterial(RigidBody.CreateSphere(new Vector3(-7.5f, 1.2f, 0f), 0.5f, density: 5.0f), MaterialId.Metal);
        ram.Velocity = new Vector3(13.5f, 0f, 0f);
        ram.Restitution = 0.2f;
        AddBody(ram, new Vector3(0.85f, 0.80f, 0.65f));

        _ragdolls.SpawnAndroid(_world, new Vector3(6.2f, 0f, 1.3f));
        StatusUpdated?.Invoke("Preset: Barrel Pyramid — the ram ball impacts the explosive stack and the detonation chains through it.");
    }

    // ---- Electric Floor Trap: a wet floor + a metal grid; charge one corner and it arcs through everything. ----
    private void BuildElectricFloorTrap()
    {
        ResetToEmptyScene(water: true);

        // A dense grid of conductive metal nodes sitting on the wet floor.
        RigidBody? corner = null;
        for (int gx = 0; gx < 6; gx++)
        for (int gz = 0; gz < 4; gz++)
        {
            var node = WithMaterial(RigidBody.CreateSphere(new Vector3(-3.0f + gx * 1.1f, 0.55f, -1.8f + gz * 1.2f), 0.22f, density: 3.2f), MaterialId.Metal);
            node.Wetness = 0.6f;     // wet floor boosts conductivity / arc reach
            node.Friction = 0.4f;
            AddBody(node, node.Color);
            corner ??= node;
        }

        // Two androids standing in the grid take the spreading shock.
        _ragdolls.SpawnAndroid(_world, new Vector3(0.2f, 0f, 0.0f));
        _ragdolls.SpawnAndroid(_world, new Vector3(2.6f, 0f, 0.6f));

        if (corner != null) _electricity.Electrify(corner, 1.6f);
        StatusUpdated?.Invoke("Preset: Electric Floor Trap — charge arcs across the wet metal grid and shocks the androids standing in it.");
    }

    // ---- Burning Barricade: a wall of flammable crates; the fire eats across it toward the androids behind. ----
    private void BuildBurningBarricade()
    {
        ResetToEmptyScene();

        // A two-high barricade of dry wooden crates spanning the lane. The near edge starts burning immediately
        // so the preset reads within a few seconds instead of waiting for a tiny ignition core to maybe catch.
        for (int z = -3; z <= 3; z++)
        {
            for (int y = 0; y < 2; y++)
            {
                RigidBody crate = WithMaterial(
                    MakeBreakable(RigidBody.CreateBox(new Vector3(-0.5f, 0.4f + y * 0.72f, z * 0.74f), new Vector3(0.34f), density: 0.6f), threshold: 4.0f),
                    MaterialId.Wood);
                crate.Flammability = 1.65f;
                AddBody(crate, crate.Color);

                if (z <= -2 && y == 0)
                {
                    _heat.Ignite(crate);
                }
            }
        }

        // Small hot starter marker on the burning side.
        RigidBody core = WithMaterial(RigidBody.CreateSphere(new Vector3(-0.5f, 0.42f, -3.15f), 0.22f, density: 1.0f), MaterialId.Explosive);
        core.ExplosivePower = 0.15f; // tiny: a lighter, not a bomb
        AddBody(core, core.Color);
        _heat.Ignite(core);

        // Androids on the far side of the barricade. They should stand and wait for the fire/structure failure.
        _ragdolls.SpawnAndroid(_world, new Vector3(2.2f, 0f, -0.6f));
        _ragdolls.SpawnAndroid(_world, new Vector3(2.6f, 0f, 0.8f));

        StatusUpdated?.Invoke("Preset: Burning Barricade — burning crates spread fire across the barricade toward the android targets.");
    }

    // ---- Wrecking Ball: a heavy ball on a rigid pendulum swings into a brick tower. joints + destruction. ----
    private void BuildWreckingBall()
    {
        ResetToEmptyScene();

        // Visual anchor + the pivot point the pendulum hangs from (world-anchored joint).
        var anchorPos = new Vector3(-1.5f, 5.0f, 0f);
        var anchor = RigidBody.CreateStaticBox(anchorPos + new Vector3(0f, 0.2f, 0f), new Vector3(0.3f, 0.2f, 0.3f));
        anchor.Color = new Vector3(0.30f, 0.31f, 0.34f);
        _world.Bodies.Add(anchor);

        // Heavy ball pulled to one side; gravity makes it swing through the tower.
        var ballPos = new Vector3(-5.2f, 4.6f, 0f);
        var ball = WithMaterial(RigidBody.CreateSphere(ballPos, 0.62f, density: 6.0f), MaterialId.Metal);
        AddBody(ball, new Vector3(0.45f, 0.46f, 0.50f));

        _world.Joints.Add(new Joint
        {
            Type = Joint.Kind.Distance,
            A = ball,
            B = null,                       // world-anchored at LocalB
            LocalA = Vector3.Zero,
            LocalB = anchorPos,
            Length = Vector3.Distance(ballPos, anchorPos),
        });

        // Brick tower sitting in the swing path.
        AddBrickTower(new Vector3(1.4f, 0f, 0f), levels: 7, perRow: 3, bw: 0.30f);

        _ragdolls.SpawnAndroid(_world, new Vector3(3.4f, 0f, 0.2f));
        StatusUpdated?.Invoke("Preset: Wrecking Ball — release the scene and the pendulum ball swings into the brick tower.");
    }

    // ---- Ragdoll Bowling: a heavy ball down a lane into a triangle of androids and crates. playful. ----
    private void BuildRagdollBowling()
    {
        ResetToEmptyScene();

        // Low side rails to keep the ball in the lane.
        for (int s = -1; s <= 1; s += 2)
        {
            var rail = RigidBody.CreateStaticBox(new Vector3(0f, 0.15f, s * 1.9f), new Vector3(7.5f, 0.15f, 0.12f));
            rail.Color = new Vector3(0.36f, 0.38f, 0.42f);
            _world.Bodies.Add(rail);
        }

        // Bowling ball.
        RigidBody ball = WithMaterial(RigidBody.CreateSphere(new Vector3(-5.8f, 0.68f, 0f), 0.58f, density: 5.0f), MaterialId.Metal);
        ball.Velocity = new Vector3(15.5f, 0f, 0f);
        ball.Restitution = 0.2f;
        AddBody(ball, new Vector3(0.20f, 0.30f, 0.55f));

        // "Pins": a triangle of light breakable crates...
        int[] perRow = [1, 2, 3, 4];
        float startX = 2.6f;
        for (int row = 0; row < perRow.Length; row++)
        {
            int n = perRow[row];
            float x = startX + row * 0.7f;

            for (int i = 0; i < n; i++)
            {
                float z = (i - (n - 1) * 0.5f) * 0.6f;
                var pin = WithMaterial(MakeBreakable(RigidBody.CreateBox(new Vector3(x, 0.52f, z), new Vector3(0.20f, 0.48f, 0.20f), density: 0.55f), threshold: 4.6f), MaterialId.Plastic);
                pin.Friction = 0.82f;
                AddBody(pin, new Vector3(0.95f, 0.95f, 0.97f));
            }
        }

        // ...and a couple of androids mixed in for chaos.
        _ragdolls.SpawnAndroid(_world, new Vector3(4.8f, 0f, -0.75f));
        _ragdolls.SpawnAndroid(_world, new Vector3(5.6f, 0f, 0.75f));

        StatusUpdated?.Invoke("Preset: Ragdoll Bowling — roll the heavy ball down the lane and scatter the pins and androids.");
    }
}
