using System.Numerics;

namespace MakarovPhysicsSandbox;

internal static class QuaternionExt
{
    // System.Numerics' q1*q2 operator multiplies in the opposite order to what
    // the textbook Hamilton product gives you, and it has bitten us before.
    // This is the version consistent with Vector3.Transform's  q v q*  sandwich:
    // Transform(v, Mul(p, c)) == Transform(Transform(v, c), p).
    public static Quaternion Multiply(this Quaternion a, Quaternion b)
    {
        var av = new Vector3(a.X, a.Y, a.Z);
        var bv = new Vector3(b.X, b.Y, b.Z);
        var v = a.W * bv + b.W * av + Vector3.Cross(av, bv);
        return new Quaternion(v.X, v.Y, v.Z, a.W * b.W - Vector3.Dot(av, bv));
    }
}