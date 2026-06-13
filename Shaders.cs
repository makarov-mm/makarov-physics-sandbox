namespace MakarovPhysicsSandbox;

/// <summary>GLSL sources and a small compile/link helper.</summary>
internal static class Shaders
{
    public const string MainVertex = """
        #version 410 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUV;

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        uniform mat4 uLightVP;
        uniform float uUvScale;
        uniform float uTime;
        uniform float uWaterWaveAmp;
        uniform int  uRippleCount;
        uniform vec4 uRipples[24];   // xy = center, z = age (s), w = strength

        out vec3 vWorldPos;
        out vec3 vNormal;
        out vec2 vUV;
        out vec4 vLightSpacePos;

        void main()
        {
            vec4 world = uModel * vec4(aPos, 1.0);

            if (uWaterWaveAmp > 0.0)
            {
                float x = world.x;
                float z = world.z;
                float t = uTime;
                float w1 = sin(x * 1.15 + t * 1.35) * 0.55;
                float w2 = sin(z * 1.75 + t * 1.85) * 0.30;
                float w3 = sin((x + z) * 0.80 + t * 1.10) * 0.25;
                world.y += uWaterWaveAmp * (w1 + w2 + w3);

                // object-driven ripples: expanding rings, same formula as WaterVolume on the CPU
                for (int i = 0; i < uRippleCount; i++)
                {
                    vec2 c = uRipples[i].xy;
                    float age = uRipples[i].z;
                    float strength = uRipples[i].w;
                    float dist = length(vec2(x, z) - c);
                    float fade = max(0.0, 1.0 - age / 1.6);
                    float ring = age * 3.2;
                    float band = exp(-2.5 * abs(dist - ring));
                    world.y += strength * fade * band * sin(dist * 6.0 - age * 9.0);
                }
            }

            vWorldPos = world.xyz;
            vNormal = mat3(transpose(inverse(uModel))) * aNormal;
            if (uWaterWaveAmp > 0.0)
            {
                float x = world.x;
                float z = world.z;
                float t = uTime;
                float dx = uWaterWaveAmp * (1.15 * 0.55 * cos(x * 1.15 + t * 1.35)
                         + 0.80 * 0.25 * cos((x + z) * 0.80 + t * 1.10));
                float dz = uWaterWaveAmp * (1.75 * 0.30 * cos(z * 1.75 + t * 1.85)
                         + 0.80 * 0.25 * cos((x + z) * 0.80 + t * 1.10));
                vNormal = normalize(vec3(-dx, 1.0, -dz));
            }
            vUV = aUV * uUvScale;
            vLightSpacePos = uLightVP * world;
            gl_Position = uProj * uView * world;
        }
        """;

    public const string MainFragment = """
        #version 410 core
        in vec3 vWorldPos;
        in vec3 vNormal;
        in vec2 vUV;
        in vec4 vLightSpacePos;

        out vec4 FragColor;

        uniform vec3 uColor;
        uniform vec3 uLightDir;   // direction the light travels
        uniform vec3 uCamPos;
        uniform sampler2D uShadowMap; // unit 0
        uniform sampler2D uAlbedo;    // unit 1
        uniform float uAlpha;         // <1 for translucent water
        uniform float uEmissive;      // 1 = ignore lighting (glowing sparks)

        float ShadowFactor(vec3 n, vec3 l)
        {
            vec3 p = vLightSpacePos.xyz / vLightSpacePos.w;
            p = p * 0.5 + 0.5;
            if (p.z > 1.0) return 0.0;

            float bias = max(0.0025 * (1.0 - dot(n, l)), 0.0006);
            vec2 texel = 1.0 / vec2(textureSize(uShadowMap, 0));
            float shadow = 0.0;
            for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y)
            {
                float depth = texture(uShadowMap, p.xy + vec2(x, y) * texel).r;
                shadow += (p.z - bias > depth) ? 1.0 : 0.0;
            }
            return shadow / 9.0;
        }

        void main()
        {
            vec3 n = normalize(vNormal);
            vec3 l = normalize(-uLightDir);
            vec3 v = normalize(uCamPos - vWorldPos);
            vec3 h = normalize(l + v);

            // textures are authored bright and low-saturation; the tint does the coloring
            vec3 base = texture(uAlbedo, vUV).rgb * uColor;

            float diff = max(dot(n, l), 0.0);
            float spec = pow(max(dot(n, h), 0.0), 48.0) * 0.35;
            float shadow = ShadowFactor(n, l);
            float lit = 1.0 - shadow;

            vec3 color = base * (0.28 + 0.85 * diff * lit) + vec3(1.0) * spec * lit;
            color = mix(color, base, uEmissive); // sparks ignore the lighting and just glow

            // mild distance fog so the floor edge fades out
            float fog = clamp((length(uCamPos - vWorldPos) - 45.0) / 60.0, 0.0, 1.0);
            color = mix(color, vec3(0.09, 0.10, 0.13), fog);

            FragColor = vec4(pow(color, vec3(1.0 / 2.2)), uAlpha);
        }
        """;

    public const string DepthVertex = """
        #version 410 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uModel;
        uniform mat4 uLightVP;
        void main()
        {
            gl_Position = uLightVP * uModel * vec4(aPos, 1.0);
        }
        """;

    public const string DepthFragment = """
        #version 410 core
        void main() { }
        """;

    public static uint Build(string vertexSrc, string fragmentSrc)
    {
        uint vs = Compile(GL.VERTEX_SHADER, vertexSrc, "vertex");
        uint fs = Compile(GL.FRAGMENT_SHADER, fragmentSrc, "fragment");

        uint program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);

        if (GL.GetProgrami(program, GL.LINK_STATUS) == 0)
            throw new InvalidOperationException("Shader link failed:\n" + GL.GetProgramInfoLog(program));

        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return program;
    }

    private static uint Compile(uint type, string source, string label)
    {
        uint shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        if (GL.GetShaderi(shader, GL.COMPILE_STATUS) == 0)
            throw new InvalidOperationException($"{label} shader compile failed:\n" + GL.GetShaderInfoLog(shader));

        return shader;
    }
}
