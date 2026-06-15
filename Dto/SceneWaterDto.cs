using MakarovPhysicsSandbox.Physics;
using System.Numerics;

namespace MakarovPhysicsSandbox.Dto;

public sealed class SceneWaterDto
{
    public Vector3 Center { get; set; }
    public float HalfX { get; set; }
    public float HalfZ { get; set; }
    public float SurfaceY { get; set; }
    public float Density { get; set; }
    public float LinearDrag { get; set; }
    public float WaveAmplitude { get; set; }
    public float Time { get; set; }

    public static SceneWaterDto FromWater(WaterVolume w) => new()
    {
        Center = w.Center,
        HalfX = w.HalfX,
        HalfZ = w.HalfZ,
        SurfaceY = w.SurfaceY,
        Density = w.Density,
        LinearDrag = w.LinearDrag,
        WaveAmplitude = w.WaveAmplitude,
        Time = w.Time,
    };
}
