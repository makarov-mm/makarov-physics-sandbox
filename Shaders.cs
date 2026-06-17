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
        uniform float uWorldUv;     // >0.5 = derive UVs from world-space box face (constant tile size)
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
            if (uWorldUv > 0.5)
            {
                // World-space box tiling: pick the face plane from the dominant normal axis
                // and derive UVs from world coordinates, so every face tiles at a constant
                // real-world size (no stretching on long/tall walls regardless of aspect).
                vec3 an = abs(aNormal);
                vec2 wuv;
                if (an.x >= an.y && an.x >= an.z)      wuv = world.zy;
                else if (an.z >= an.x && an.z >= an.y) wuv = world.xy;
                else                                   wuv = world.xz;
                vUV = wuv * uUvScale;
            }
            else
            {
                vUV = aUV * uUvScale;
            }
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
        uniform sampler2D uBumpMap;   // unit 2, optional grayscale height map
        uniform float uUseBumpMap;    // 1 = sample uBumpMap, 0 = derive height from albedo
        uniform float uAlpha;         // <1 for translucent water
        uniform float uEmissive;      // 1 = ignore lighting (glowing sparks)
        uniform float uBumpStrength;  // lightweight texture-height bump for wood/brick/balls

        float TextureHeight(vec2 uv)
        {
            if (uUseBumpMap > 0.5) return texture(uBumpMap, uv).r;
            vec3 c = texture(uAlbedo, uv).rgb;
            return dot(c, vec3(0.299, 0.587, 0.114));
        }

        vec3 ApplyTextureBump(vec3 n)
        {
            if (uBumpStrength <= 0.001) return n;

            vec3 dp1 = dFdx(vWorldPos);
            vec3 dp2 = dFdy(vWorldPos);
            vec2 duv1 = dFdx(vUV);
            vec2 duv2 = dFdy(vUV);
            float det = duv1.x * duv2.y - duv1.y * duv2.x;
            if (abs(det) < 0.00001) return n;

            vec3 tangent = normalize((dp1 * duv2.y - dp2 * duv1.y) / det);
            vec3 bitangent = normalize((-dp1 * duv2.x + dp2 * duv1.x) / det);
            vec2 texel = 1.0 / vec2(textureSize(uAlbedo, 0));

            float hx = TextureHeight(vUV + vec2(texel.x, 0.0)) - TextureHeight(vUV - vec2(texel.x, 0.0));
            float hy = TextureHeight(vUV + vec2(0.0, texel.y)) - TextureHeight(vUV - vec2(0.0, texel.y));
            vec3 bumped = n - (tangent * hx + bitangent * hy) * uBumpStrength * 5.0;
            return normalize(bumped);
        }

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
            vec3 n = ApplyTextureBump(normalize(vNormal));
            vec3 l = normalize(-uLightDir);
            vec3 v = normalize(uCamPos - vWorldPos);
            vec3 h = normalize(l + v);

            // textures are authored bright and low-saturation; the tint does the coloring
            vec3 base = texture(uAlbedo, vUV).rgb * uColor;

            float diff = max(dot(n, l), 0.0);
            float spec = pow(max(dot(n, h), 0.0), 56.0) * 0.42;
            float rim = pow(1.0 - max(dot(n, v), 0.0), 3.0) * 0.10;
            float shadow = ShadowFactor(n, l);
            float lit = 1.0 - shadow;

            // Slightly richer lighting: warm key light, cool sky fill and a small rim term.
            vec3 skyFill = vec3(0.34, 0.40, 0.48) * max(n.y * 0.5 + 0.5, 0.0);
            vec3 color = base * (0.20 + skyFill * 0.22 + 0.92 * diff * lit) + vec3(1.0, 0.96, 0.88) * spec * lit + base * rim;
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

    // A real direction-based sky: colour is computed per pixel from the view ray, so there is
    // exactly one sun, a smooth horizon->zenith gradient and drifting clouds, with no cube seams
    // or repeated suns (unlike a 2D texture wrapped on a cube). Runs as its own program; if it
    // fails to compile/link the engine falls back to the textured skybox cube.
    public const string ParticleVertex = """
        #version 410 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUV;
        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        out vec2 vUV;
        void main()
        {
            vUV = aUV;
            gl_Position = uProj * uView * uModel * vec4(aPos, 1.0);
        }
        """;

    public const string ParticleFragment = """
        #version 410 core
        in vec2 vUV;
        out vec4 FragColor;
        uniform sampler2D uTex;
        uniform vec3 uColor;
        uniform float uAlpha;
        void main()
        {
            float m = texture(uTex, vUV).r;          // soft round mask
            FragColor = vec4(uColor, uAlpha * m);
        }
        """;

    public const string SkyVertex = """
        #version 410 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUV;
        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        out vec3 vWorldPos;
        void main()
        {
            vec4 world = uModel * vec4(aPos, 1.0);
            vWorldPos = world.xyz;
            gl_Position = uProj * uView * world;
        }
        """;

    public const string SkyFragment = """
        #version 410 core
        in vec3 vWorldPos;
        out vec4 FragColor;
        uniform vec3 uCamPos;
        uniform float uTime;

        float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
        float noise(vec2 p)
        {
            vec2 i = floor(p), f = fract(p);
            float a = hash(i), b = hash(i + vec2(1, 0)), c = hash(i + vec2(0, 1)), d = hash(i + vec2(1, 1));
            vec2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
        }
        float fbm(vec2 p)
        {
            float v = 0.0, a = 0.5;
            for (int i = 0; i < 5; i++) { v += a * noise(p); p *= 2.0; a *= 0.5; }
            return v;
        }

        void main()
        {
            vec3 d = normalize(vWorldPos - uCamPos);
            float alt = clamp(d.y, 0.0, 1.0);

            vec3 horizon = vec3(0.74, 0.85, 0.96);
            vec3 zenith  = vec3(0.18, 0.42, 0.82);
            vec3 sky = mix(horizon, zenith, pow(alt, 0.55));

            vec3 sunDir = normalize(vec3(0.35, 0.55, 0.30));
            float s = max(dot(d, sunDir), 0.0);
            sky += vec3(1.0, 0.96, 0.82) * pow(s, 1500.0) * 1.6;   // tight sun disc
            sky += vec3(1.0, 0.90, 0.72) * pow(s, 8.0) * 0.20;     // warm glow

            if (d.y > 0.05)
            {
                vec2 uv = d.xz / d.y * 0.6 + vec2(uTime * 0.006, 0.0);
                float c = fbm(uv);
                c = smoothstep(0.55, 0.95, c) * clamp((d.y - 0.05) * 3.0, 0.0, 1.0);
                sky = mix(sky, vec3(1.0), c * 0.85);
            }

            FragColor = vec4(sky, 1.0);
        }
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
        {
            throw new InvalidOperationException("Shader link failed:\n" + GL.GetProgramInfoLog(program));
        }

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
        {
            throw new InvalidOperationException($"{label} shader compile failed:\n" + GL.GetShaderInfoLog(shader));
        }

        return shader;
    }
}
