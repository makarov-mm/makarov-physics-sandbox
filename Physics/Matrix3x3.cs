using System.Numerics;

namespace MakarovPhysicsSandbox.Physics;

/// <summary>Tiny 3x3 matrix. System.Numerics has no Matrix3x3 and we need one for inertia tensors.</summary>
public struct Matrix3x3
{
    public float M00, M01, M02, M10, M11, M12, M20, M21, M22;

    public static readonly Matrix3x3 Zero = new();

    public static Matrix3x3 Diagonal(Vector3 d) => new() { M00 = d.X, M11 = d.Y, M22 = d.Z };

    public static Matrix3x3 FromQuaternion(Quaternion q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;

        return new Matrix3x3
        {
            M00 = 1 - 2 * (y * y + z * z),
            M01 = 2 * (x * y - w * z),
            M02 = 2 * (x * z + w * y),
            M10 = 2 * (x * y + w * z),
            M11 = 1 - 2 * (x * x + z * z),
            M12 = 2 * (y * z - w * x),
            M20 = 2 * (x * z - w * y),
            M21 = 2 * (y * z + w * x),
            M22 = 1 - 2 * (x * x + y * y),
        };
    }

    public readonly Vector3 Transform(Vector3 v) => new(
        M00 * v.X + M01 * v.Y + M02 * v.Z,
        M10 * v.X + M11 * v.Y + M12 * v.Z,
        M20 * v.X + M21 * v.Y + M22 * v.Z);

    public readonly Matrix3x3 Transposed() => new()
    {
        M00 = M00,
        M01 = M10,
        M02 = M20,
        M10 = M01,
        M11 = M11,
        M12 = M21,
        M20 = M02,
        M21 = M12,
        M22 = M22,
    };

    public static Matrix3x3 Multiply(in Matrix3x3 a, in Matrix3x3 b) => new()
    {
        M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
        M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
        M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
        M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
        M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
        M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
        M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22,
    };

    public static Matrix3x3 Add(in Matrix3x3 a, in Matrix3x3 b) => new()
    {
        M00 = a.M00 + b.M00,
        M01 = a.M01 + b.M01,
        M02 = a.M02 + b.M02,
        M10 = a.M10 + b.M10,
        M11 = a.M11 + b.M11,
        M12 = a.M12 + b.M12,
        M20 = a.M20 + b.M20,
        M21 = a.M21 + b.M21,
        M22 = a.M22 + b.M22,
    };

    public readonly Matrix3x3 Inverse()
    {
        // adjugate / determinant; inertia tensors are symmetric positive definite,
        // so det is never anywhere near zero for a sane body
        float c00 = M11 * M22 - M12 * M21;
        float c01 = M12 * M20 - M10 * M22;
        float c02 = M10 * M21 - M11 * M20;
        float det = M00 * c00 + M01 * c01 + M02 * c02;
        float inv = 1f / det;
        return new Matrix3x3
        {
            M00 = c00 * inv,
            M01 = (M02 * M21 - M01 * M22) * inv,
            M02 = (M01 * M12 - M02 * M11) * inv,
            M10 = c01 * inv,
            M11 = (M00 * M22 - M02 * M20) * inv,
            M12 = (M02 * M10 - M00 * M12) * inv,
            M20 = c02 * inv,
            M21 = (M01 * M20 - M00 * M21) * inv,
            M22 = (M00 * M11 - M01 * M10) * inv,
        };
    }
}
