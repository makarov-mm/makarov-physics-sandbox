using System.Numerics;

namespace MakarovPhysicsSandbox;

/// <summary>GPU mesh: interleaved position + normal + uv, indexed triangles.</summary>
internal sealed class Mesh
{
    private readonly uint _vao;
    private readonly int _indexCount;

    public Mesh(float[] vertices, uint[] indices)
    {
        _indexCount = indices.Length;
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        uint vbo = GL.GenBuffer();
        GL.BindBuffer(GL.ARRAY_BUFFER, vbo);
        GL.BufferData(GL.ARRAY_BUFFER, vertices, GL.STATIC_DRAW);

        uint ebo = GL.GenBuffer();
        GL.BindBuffer(GL.ELEMENT_ARRAY_BUFFER, ebo);
        GL.BufferData(GL.ELEMENT_ARRAY_BUFFER, indices, GL.STATIC_DRAW);

        const int stride = 8 * sizeof(float);
        GL.VertexAttribPointer(0, 3, GL.FLOAT, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, GL.FLOAT, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, GL.FLOAT, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);

        GL.BindVertexArray(0);
    }

    public void Draw()
    {
        GL.BindVertexArray(_vao);
        GL.DrawElements(GL.TRIANGLES, _indexCount, GL.UNSIGNED_INT, IntPtr.Zero);
    }

    // ---- generators ----

    /// <summary>Unit cube: half-extent 1 along each axis (scaled by body half extents at draw time).</summary>
    public static Mesh CreateCube()
    {
        var verts = new List<float>();
        var idx = new List<uint>();

        Vector2[] uvs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

        void Face(Vector3 normal, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            uint start = (uint)(verts.Count / 8);
            int k = 0;
            foreach (var p in new[] { a, b, c, d })
            {
                verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z);
                verts.Add(normal.X); verts.Add(normal.Y); verts.Add(normal.Z);
                verts.Add(uvs[k].X); verts.Add(uvs[k].Y); k++;
            }
            idx.Add(start); idx.Add(start + 1); idx.Add(start + 2);
            idx.Add(start); idx.Add(start + 2); idx.Add(start + 3);
        }

        // CCW winding when viewed from outside
        Face(new(0, 0, 1), new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)); // +Z
        Face(new(0, 0, -1), new(1, -1, -1), new(-1, -1, -1), new(-1, 1, -1), new(1, 1, -1)); // -Z
        Face(new(1, 0, 0), new(1, -1, 1), new(1, -1, -1), new(1, 1, -1), new(1, 1, 1)); // +X
        Face(new(-1, 0, 0), new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, 1), new(-1, 1, -1)); // -X
        Face(new(0, 1, 0), new(-1, 1, 1), new(1, 1, 1), new(1, 1, -1), new(-1, 1, -1)); // +Y
        Face(new(0, -1, 0), new(-1, -1, -1), new(1, -1, -1), new(1, -1, 1), new(-1, -1, 1)); // -Y

        return new Mesh(verts.ToArray(), idx.ToArray());
    }

    /// <summary>Unit UV sphere, radius 1.</summary>
    public static Mesh CreateSphere(int sectors = 36, int stacks = 24)
    {
        var verts = new List<float>();
        var idx = new List<uint>();

        for (int i = 0; i <= stacks; i++)
        {
            float phi = MathF.PI * i / stacks;
            float y = MathF.Cos(phi);
            float r = MathF.Sin(phi);

            for (int j = 0; j <= sectors; j++)
            {
                float theta = 2f * MathF.PI * j / sectors;
                float x = r * MathF.Cos(theta);
                float z = r * MathF.Sin(theta);
                verts.Add(x); verts.Add(y); verts.Add(z); // position == normal on a unit sphere
                verts.Add(x); verts.Add(y); verts.Add(z);
                verts.Add((float)j / sectors); verts.Add((float)i / stacks);
            }
        }

        for (int i = 0; i < stacks; i++)
            for (int j = 0; j < sectors; j++)
            {
                uint a = (uint)(i * (sectors + 1) + j);
                uint b = (uint)((i + 1) * (sectors + 1) + j);
                // winding chosen so triangles are CCW from outside (theta grows toward +Z->-X side)
                idx.Add(a); idx.Add(a + 1); idx.Add(b);
                idx.Add(a + 1); idx.Add(b + 1); idx.Add(b);
            }

        return new Mesh(verts.ToArray(), idx.ToArray());
    }

    /// <summary>Unit cylinder: radius 1, half-height 1, axis = Y (scale per draw). Used for
    /// barrels, wheels and prop handles so they read as real cylinders, not capsules/spheres.</summary>
    public static Mesh CreateCylinder(int sectors = 32)
    {
        var verts = new List<float>();
        var idx = new List<uint>();

        void V(float px, float py, float pz, float nx, float ny, float nz, float u, float v)
        {
            verts.Add(px); verts.Add(py); verts.Add(pz);
            verts.Add(nx); verts.Add(ny); verts.Add(nz);
            verts.Add(u); verts.Add(v);
        }

        // side wall: per sector a bottom (y=-1) and top (y=+1) vertex, radial normals
        uint side = (uint)(verts.Count / 8);
        for (int j = 0; j <= sectors; j++)
        {
            float th = 2f * MathF.PI * j / sectors;
            float x = MathF.Cos(th), z = MathF.Sin(th);
            float u = (float)j / sectors;
            V(x, -1, z, x, 0, z, u, 0f); // bottom
            V(x,  1, z, x, 0, z, u, 1f); // top
        }
        for (int j = 0; j < sectors; j++)
        {
            uint b0 = side + (uint)(j * 2), t0 = b0 + 1, b1 = b0 + 2, t1 = b0 + 3;
            idx.Add(b0); idx.Add(t0); idx.Add(t1); // CCW from outside
            idx.Add(b0); idx.Add(t1); idx.Add(b1);
        }

        // top cap (+Y)
        uint topC = (uint)(verts.Count / 8);
        V(0, 1, 0, 0, 1, 0, 0.5f, 0.5f);
        uint topR = (uint)(verts.Count / 8);
        for (int j = 0; j <= sectors; j++)
        {
            float th = 2f * MathF.PI * j / sectors;
            float x = MathF.Cos(th), z = MathF.Sin(th);
            V(x, 1, z, 0, 1, 0, 0.5f + 0.5f * x, 0.5f + 0.5f * z);
        }
        for (int j = 0; j < sectors; j++) { idx.Add(topC); idx.Add(topR + (uint)j + 1); idx.Add(topR + (uint)j); }

        // bottom cap (-Y)
        uint botC = (uint)(verts.Count / 8);
        V(0, -1, 0, 0, -1, 0, 0.5f, 0.5f);
        uint botR = (uint)(verts.Count / 8);
        for (int j = 0; j <= sectors; j++)
        {
            float th = 2f * MathF.PI * j / sectors;
            float x = MathF.Cos(th), z = MathF.Sin(th);
            V(x, -1, z, 0, -1, 0, 0.5f + 0.5f * x, 0.5f + 0.5f * z);
        }
        for (int j = 0; j < sectors; j++) { idx.Add(botC); idx.Add(botR + (uint)j); idx.Add(botR + (uint)j + 1); }

        return new Mesh(verts.ToArray(), idx.ToArray());
    }

    /// <summary>Unit quad at y = 0, normal up; scale it at draw time.</summary>
    /// <summary>
    /// Capsule: radius 0.5, cylindrical half-height 0.8 (axis = Y).
    /// Proportion matches RigidBody.CapsuleHalfHeightPerRadius; scale uniformly.
    /// </summary>
    public static Mesh CreateCapsule(int sectors = 28, int capStacks = 8)
    {
        const float R = 0.5f;
        const float H = 0.8f; // = R * 1.6

        var verts = new List<float>();
        var idx = new List<uint>();

        // rings from bottom pole to top pole:
        //   bottom cap (capStacks rings), cylinder seam handled by two identical-angle rings,
        //   top cap (capStacks rings)
        void Ring(float y, float ringR, float nyCenterY)
        {
            for (int j = 0; j <= sectors; j++)
            {
                float a = 2f * MathF.PI * j / sectors;
                float x = ringR * MathF.Cos(a);
                float z = ringR * MathF.Sin(a);
                var n = Vector3.Normalize(new Vector3(x, y - nyCenterY, z));
                verts.Add(x); verts.Add(y); verts.Add(z);
                verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z);
                verts.Add((float)j / sectors); verts.Add((y + H + R) / (2f * (H + R)));
            }
        }

        // bottom hemisphere (center at y = -H)
        for (int i = 0; i <= capStacks; i++)
        {
            float phi = MathF.PI * 0.5f * i / capStacks;     // 0 = pole, pi/2 = equator
            float y = -H - R * MathF.Cos(phi);
            float r = R * MathF.Sin(phi);
            Ring(y, r, -H);
        }
        // top hemisphere (center at y = +H)
        for (int i = 0; i <= capStacks; i++)
        {
            float phi = MathF.PI * 0.5f * (1f - (float)i / capStacks); // equator -> pole
            float y = H + R * MathF.Cos(phi);
            float r = R * MathF.Sin(phi);
            Ring(y, r, H);
        }

        int ringCount = 2 * (capStacks + 1);
        int stride = sectors + 1;
        for (int i = 0; i < ringCount - 1; i++)
            for (int j = 0; j < sectors; j++)
            {
                uint a = (uint)(i * stride + j);
                uint b = (uint)(a + stride);
                // rings go bottom -> top, so the winding is mirrored vs. CreateSphere (top -> bottom)
                idx.Add(a); idx.Add(b); idx.Add(a + 1);
                idx.Add(a + 1); idx.Add(b); idx.Add(b + 1);
            }

        return new Mesh(verts.ToArray(), idx.ToArray());
    }

    /// <summary>Unit quad in the local XY plane ([-0.5,0.5], normal +Z, UV 0..1). Used for
    /// camera-facing billboard particles (fire/smoke), oriented via a billboard model matrix.</summary>
    public static Mesh CreateBillboardQuad()
    {
        float[] verts =
        {
            -0.5f, -0.5f, 0,  0, 0, 1,  0, 0,
             0.5f, -0.5f, 0,  0, 0, 1,  1, 0,
             0.5f,  0.5f, 0,  0, 0, 1,  1, 1,
            -0.5f,  0.5f, 0,  0, 0, 1,  0, 1,
        };
        uint[] idx = { 0, 1, 2, 0, 2, 3 };
        return new Mesh(verts, idx);
    }

    public static Mesh CreatePlane()
    {
        float[] verts =
        {
            -1, 0, -1,  0, 1, 0,  0, 0,
             1, 0, -1,  0, 1, 0,  1, 0,
             1, 0,  1,  0, 1, 0,  1, 1,
            -1, 0,  1,  0, 1, 0,  0, 1,
        };
        uint[] idx = { 0, 2, 1, 0, 3, 2 }; // CCW seen from above (+Y)
        return new Mesh(verts, idx);
    }
    /// <summary>Tessellated unit quad at y = 0, normal up. Used for the animated water surface.</summary>
    public static Mesh CreateGridPlane(int cells = 64)
    {
        var verts = new List<float>();
        var idx = new List<uint>();

        for (int z = 0; z <= cells; z++)
        {
            float vz = -1f + 2f * z / cells;
            for (int x = 0; x <= cells; x++)
            {
                float vx = -1f + 2f * x / cells;
                verts.Add(vx); verts.Add(0f); verts.Add(vz);
                verts.Add(0f); verts.Add(1f); verts.Add(0f);
                verts.Add((float)x / cells); verts.Add((float)z / cells);
            }
        }

        int stride = cells + 1;
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            uint a = (uint)(z * stride + x);
            uint b = a + 1;
            uint c = (uint)((z + 1) * stride + x);
            uint d = c + 1;
            idx.Add(a); idx.Add(d); idx.Add(b);
            idx.Add(a); idx.Add(c); idx.Add(d);
        }

        return new Mesh(verts.ToArray(), idx.ToArray());
    }
}
