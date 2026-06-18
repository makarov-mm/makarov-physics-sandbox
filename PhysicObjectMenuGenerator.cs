namespace MakarovPhysicsSandbox;

public static class PhysicObjectMenuGenerator
{
    public static IEnumerable<PhysicObjectMenuItem> Generate(GlPanel glPanel)
    {
        return
        [
            new ("Sphere", "sphere.png", "1", () => glPanel.Spawn(1)),
            new("Box", "box.png", "2", () => glPanel.Spawn(2)),
            new("Metal cube", "metalcube.png", "", () => glPanel.SpawnMetalCube()),
            new("Glass cube", "glass.png", "", () => glPanel.SpawnGlassCube()),
            new("Plank", "plank.png", "4", () => glPanel.Spawn(4)),
            new("Pillar", "pillar.png", "5", () => glPanel.Spawn(5)),
            new("Dumbbell", "dumbbell.png", "6", () => glPanel.Spawn(6)),
            new("Hammer", "hammer.png", "7", () => glPanel.Spawn(7)),
            new("Table", "table.png", "8", () => glPanel.Spawn(8)),
            new("Bowling pins", "pins.png", "9", () => glPanel.SpawnPins()),
            new("Chain", "chain.png", "L", () => glPanel.SpawnChain()),
            new("Android dummy", "android.png", "0", () => glPanel.SpawnAndroid()),
            new("Drone target", "drone.png", "", () => glPanel.SpawnDroneTarget()),
            new("Target dummy", "sentinel.png", "", () => glPanel.SpawnSentinelBot()),
            new("Vehicle", "vehicle.png", "N", () => glPanel.SpawnVehicle()),
            new("Police car", "police.png", "", () => glPanel.SpawnPoliceVehicle()),
            new("Ambulance", "ambulance.png", "", () => glPanel.SpawnAmbulance()),
            new("Bridge span", "bridge.png", "", () => glPanel.SpawnBridgeSpan()),
            new("Catapult launcher", "catapult.png", "", () => glPanel.SpawnCatapultLauncher()),
            new("Wooden cart", "cart.png", "", () => glPanel.SpawnWoodenCart()),
            new("Ramp", "ramp.png", "", () => glPanel.SpawnRamp()),
            new("Glass block", "glass.png", "", () => glPanel.SpawnGlassBlock()),
            new("Glass pyramid", "glass.png", "", () => glPanel.SpawnGlassPyramid()),
            new("Spike platform", "spikepad.png", "", () => glPanel.SpawnSpikePlatform()),
            new("Fire platform", "firepad.png", "", () => glPanel.SpawnFirePlatform()),
            new("Smoke platform", "smokepad.png", "", () => glPanel.SpawnSmokePlatform()),
            new("Wrecking ball target", "wreckingball.png", "", () => glPanel.SpawnWreckingBallTarget()),
            new("Explosive barrel", "barrel.png", "", () => glPanel.SpawnExplosiveBarrel()),
            new("Cylinder", "cylinder.png", "", () => glPanel.SpawnCylinder()),
            new("Beach ball", "beachball.png", "", () => glPanel.SpawnBeachBall()),
            new("Gas cylinder", "gascylinder.png", "", () => glPanel.SpawnGasCylinder()),
            new("Glass bottle", "bottle.png", "", () => glPanel.SpawnBottle()),
            new("Firework rocket", "firework.png", "", () => glPanel.SpawnFireworkRocket()),
            new("Cannon", "cannon.png", "", () => glPanel.SpawnCannon()),
            new("Fire cannon", "firecannon.png", "", () => glPanel.SpawnFireCannon()),
            new("Motor hinge", "motor.png", "", () => glPanel.SpawnMotor()),
            new("Gate", "gate.png", "", () => glPanel.SpawnGate()),
            new("Timer", "timer.png", "", () => glPanel.SpawnTimer()),
            new("Conveyor belt", "conveyor.png", "", () => glPanel.SpawnConveyor()),
            new("Piston actuator", "piston.png", "", () => glPanel.SpawnPiston()),
            new("Sliding door", "door.png", "", () => glPanel.SpawnSlidingDoor()),
            new ("Explosion", "explosion.png", "E", () => glPanel.Detonate()),
            new("Ignite", "torch.png", "I", () => glPanel.Ignite()),
            new("Electrify", "electricity.png", "D", () => glPanel.Electrify())
        ];
    }
}