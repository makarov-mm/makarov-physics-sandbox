using System.Runtime.InteropServices;

namespace MakarovPhysicsSandbox;

/// <summary>
/// OpenGL bindings. GL 1.1 entry points come straight from opengl32.dll exports,
/// everything newer is resolved with wglGetProcAddress after a context is current.
/// </summary>
internal static class GL
{
    // ---- constants ----
    public const uint COLOR_BUFFER_BIT = 0x00004000;
    public const uint DEPTH_BUFFER_BIT = 0x00000100;

    public const uint DEPTH_TEST = 0x0B71;
    public const uint BLEND = 0x0BE2;
    public const uint SRC_ALPHA = 0x0302;
    public const uint ONE_MINUS_SRC_ALPHA = 0x0303;
    public const uint ONE = 1;
    public const uint CULL_FACE = 0x0B44;
    public const uint MULTISAMPLE = 0x809D;
    public const uint BACK = 0x0405;
    public const uint FRONT = 0x0404;
    public const uint CCW = 0x0901;
    public const uint LESS = 0x0201;
    public const uint LEQUAL = 0x0203;

    public const uint TRIANGLES = 0x0004;
    public const uint UNSIGNED_INT = 0x1405;
    public const uint FLOAT = 0x1406;

    public const uint ARRAY_BUFFER = 0x8892;
    public const uint ELEMENT_ARRAY_BUFFER = 0x8893;
    public const uint STATIC_DRAW = 0x88E4;

    public const uint VERTEX_SHADER = 0x8B31;
    public const uint FRAGMENT_SHADER = 0x8B30;
    public const uint COMPILE_STATUS = 0x8B81;
    public const uint LINK_STATUS = 0x8B82;

    public const uint TEXTURE_2D = 0x0DE1;
    public const uint TEXTURE0 = 0x84C0;
    public const uint TEXTURE1 = 0x84C1;
    public const uint TEXTURE2 = 0x84C2;
    public const uint REPEAT = 0x2901;
    public const uint LINEAR_MIPMAP_LINEAR = 0x2703;
    public const uint RGBA = 0x1908;
    public const int RGBA8 = 0x8058;
    public const uint UNSIGNED_BYTE = 0x1401;
    public const uint DEPTH_COMPONENT = 0x1902;
    public const uint DEPTH_COMPONENT24 = 0x81A6;
    public const uint NEAREST = 0x2600;
    public const uint LINEAR = 0x2601;
    public const uint TEXTURE_MAG_FILTER = 0x2800;
    public const uint TEXTURE_MIN_FILTER = 0x2801;
    public const uint TEXTURE_WRAP_S = 0x2802;
    public const uint TEXTURE_WRAP_T = 0x2803;
    public const uint CLAMP_TO_BORDER = 0x812D;
    public const uint TEXTURE_BORDER_COLOR = 0x1004;

    public const uint FRAMEBUFFER = 0x8D40;
    public const uint DEPTH_ATTACHMENT = 0x8D00;
    public const uint FRAMEBUFFER_COMPLETE = 0x8CD5;
    public const uint NONE = 0;

    public const uint VERSION = 0x1F02;

    // ---- GL 1.1: direct exports ----
    [DllImport("opengl32.dll", EntryPoint = "glClear")] public static extern void Clear(uint mask);
    [DllImport("opengl32.dll", EntryPoint = "glClearColor")] public static extern void ClearColor(float r, float g, float b, float a);
    [DllImport("opengl32.dll", EntryPoint = "glEnable")] public static extern void Enable(uint cap);
    [DllImport("opengl32.dll", EntryPoint = "glDisable")] public static extern void Disable(uint cap);
    [DllImport("opengl32.dll", EntryPoint = "glViewport")] public static extern void Viewport(int x, int y, int w, int h);
    [DllImport("opengl32.dll", EntryPoint = "glDepthFunc")] public static extern void DepthFunc(uint func);
    [DllImport("opengl32.dll", EntryPoint = "glDepthMask")] public static extern void DepthMask(byte flag);
    [DllImport("opengl32.dll", EntryPoint = "glBlendFunc")] public static extern void BlendFunc(uint sfactor, uint dfactor);
    [DllImport("opengl32.dll", EntryPoint = "glCullFace")] public static extern void CullFace(uint mode);
    [DllImport("opengl32.dll", EntryPoint = "glFrontFace")] public static extern void FrontFace(uint mode);
    [DllImport("opengl32.dll", EntryPoint = "glDrawElements")] public static extern void DrawElements(uint mode, int count, uint type, IntPtr indices);
    [DllImport("opengl32.dll", EntryPoint = "glGetError")] public static extern uint GetError();
    [DllImport("opengl32.dll", EntryPoint = "glGetString")] public static extern IntPtr GetStringPtr(uint name);
    [DllImport("opengl32.dll", EntryPoint = "glGenTextures")] public static extern void GenTextures(int n, uint[] textures);

    public static uint GenTexture()
    {
        var t = new uint[1];
        GenTextures(1, t);
        return t[0];
    }
    [DllImport("opengl32.dll", EntryPoint = "glBindTexture")] public static extern void BindTexture(uint target, uint texture);
    [DllImport("opengl32.dll", EntryPoint = "glTexImage2D")] public static extern void TexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, IntPtr pixels);

    public static void TexImage2D(uint target, int level, int internalFormat, int w, int h, int border, uint format, uint type, byte[] pixels)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { TexImage2D(target, level, internalFormat, w, h, border, format, type, handle.AddrOfPinnedObject()); }
        finally { handle.Free(); }
    }
    [DllImport("opengl32.dll", EntryPoint = "glTexParameteri")] public static extern void TexParameteri(uint target, uint pname, int param);
    [DllImport("opengl32.dll", EntryPoint = "glTexParameterfv")] public static extern void TexParameterfv(uint target, uint pname, float[] params_);
    [DllImport("opengl32.dll", EntryPoint = "glDrawBuffer")] public static extern void DrawBuffer(uint buf);
    [DllImport("opengl32.dll", EntryPoint = "glReadBuffer")] public static extern void ReadBuffer(uint src);

    public static string GetString(uint name) => Marshal.PtrToStringAnsi(GetStringPtr(name)) ?? "";

    // ---- modern GL: delegates loaded at runtime ----
    private delegate uint CreateShaderDel(uint type);
    private delegate void ShaderSourceDel(uint shader, int count, IntPtr[] sources, int[] lengths);
    private delegate void CompileShaderDel(uint shader);
    private delegate void GetShaderivDel(uint shader, uint pname, out int value);
    private delegate void GetShaderInfoLogDel(uint shader, int bufSize, out int length, byte[] log);
    private delegate uint CreateProgramDel();
    private delegate void AttachShaderDel(uint program, uint shader);
    private delegate void LinkProgramDel(uint program);
    private delegate void GetProgramivDel(uint program, uint pname, out int value);
    private delegate void GetProgramInfoLogDel(uint program, int bufSize, out int length, byte[] log);
    private delegate void UseProgramDel(uint program);
    private delegate void DeleteShaderDel(uint shader);
    private delegate int GetUniformLocationDel(uint program, string name);
    private delegate void UniformMatrix4fvDel(int location, int count, byte transpose, float[] value);
    private delegate void Uniform3fDel(int location, float x, float y, float z);
    private delegate void Uniform1iDel(int location, int v);
    private delegate void Uniform1fDel(int location, float v);
    private delegate void Uniform4fvDel(int location, int count, float[] value);
    private delegate void GenVertexArraysDel(int n, uint[] arrays);
    private delegate void BindVertexArrayDel(uint array);
    private delegate void GenBuffersDel(int n, uint[] buffers);
    private delegate void BindBufferDel(uint target, uint buffer);
    private delegate void BufferDataDel(uint target, IntPtr size, IntPtr data, uint usage);
    private delegate void VertexAttribPointerDel(uint index, int size, uint type, byte normalized, int stride, IntPtr pointer);
    private delegate void EnableVertexAttribArrayDel(uint index);
    private delegate void GenFramebuffersDel(int n, uint[] fbos);
    private delegate void BindFramebufferDel(uint target, uint fbo);
    private delegate void FramebufferTexture2DDel(uint target, uint attachment, uint texTarget, uint texture, int level);
    private delegate uint CheckFramebufferStatusDel(uint target);
    private delegate void ActiveTextureDel(uint unit);
    private delegate void GenerateMipmapDel(uint target);

    private static CreateShaderDel _createShader = null!;
    private static ShaderSourceDel _shaderSource = null!;
    private static CompileShaderDel _compileShader = null!;
    private static GetShaderivDel _getShaderiv = null!;
    private static GetShaderInfoLogDel _getShaderInfoLog = null!;
    private static CreateProgramDel _createProgram = null!;
    private static AttachShaderDel _attachShader = null!;
    private static LinkProgramDel _linkProgram = null!;
    private static GetProgramivDel _getProgramiv = null!;
    private static GetProgramInfoLogDel _getProgramInfoLog = null!;
    private static UseProgramDel _useProgram = null!;
    private static DeleteShaderDel _deleteShader = null!;
    private static GetUniformLocationDel _getUniformLocation = null!;
    private static UniformMatrix4fvDel _uniformMatrix4fv = null!;
    private static Uniform3fDel _uniform3f = null!;
    private static Uniform1iDel _uniform1i = null!;
    private static Uniform1fDel _uniform1f = null!;
    private static Uniform4fvDel _uniform4fv = null!;
    private static GenVertexArraysDel _genVertexArrays = null!;
    private static BindVertexArrayDel _bindVertexArray = null!;
    private static GenBuffersDel _genBuffers = null!;
    private static BindBufferDel _bindBuffer = null!;
    private static BufferDataDel _bufferData = null!;
    private static VertexAttribPointerDel _vertexAttribPointer = null!;
    private static EnableVertexAttribArrayDel _enableVertexAttribArray = null!;
    private static GenFramebuffersDel _genFramebuffers = null!;
    private static BindFramebufferDel _bindFramebuffer = null!;
    private static FramebufferTexture2DDel _framebufferTexture2D = null!;
    private static CheckFramebufferStatusDel _checkFramebufferStatus = null!;
    private static ActiveTextureDel _activeTexture = null!;
    private static GenerateMipmapDel _generateMipmap = null!;

    private static IntPtr _opengl32;

    /// <summary>Must be called once after a GL context is made current.</summary>
    public static void LoadFunctions()
    {
        _opengl32 = Win32.GetModuleHandleW("opengl32.dll");
        if (_opengl32 == IntPtr.Zero)
            _opengl32 = Win32.LoadLibraryW("opengl32.dll");

        _createShader = Load<CreateShaderDel>("glCreateShader");
        _shaderSource = Load<ShaderSourceDel>("glShaderSource");
        _compileShader = Load<CompileShaderDel>("glCompileShader");
        _getShaderiv = Load<GetShaderivDel>("glGetShaderiv");
        _getShaderInfoLog = Load<GetShaderInfoLogDel>("glGetShaderInfoLog");
        _createProgram = Load<CreateProgramDel>("glCreateProgram");
        _attachShader = Load<AttachShaderDel>("glAttachShader");
        _linkProgram = Load<LinkProgramDel>("glLinkProgram");
        _getProgramiv = Load<GetProgramivDel>("glGetProgramiv");
        _getProgramInfoLog = Load<GetProgramInfoLogDel>("glGetProgramInfoLog");
        _useProgram = Load<UseProgramDel>("glUseProgram");
        _deleteShader = Load<DeleteShaderDel>("glDeleteShader");
        _getUniformLocation = Load<GetUniformLocationDel>("glGetUniformLocation");
        _uniformMatrix4fv = Load<UniformMatrix4fvDel>("glUniformMatrix4fv");
        _uniform3f = Load<Uniform3fDel>("glUniform3f");
        _uniform1i = Load<Uniform1iDel>("glUniform1i");
        _uniform1f = Load<Uniform1fDel>("glUniform1f");
        _uniform4fv = Load<Uniform4fvDel>("glUniform4fv");
        _genVertexArrays = Load<GenVertexArraysDel>("glGenVertexArrays");
        _bindVertexArray = Load<BindVertexArrayDel>("glBindVertexArray");
        _genBuffers = Load<GenBuffersDel>("glGenBuffers");
        _bindBuffer = Load<BindBufferDel>("glBindBuffer");
        _bufferData = Load<BufferDataDel>("glBufferData");
        _vertexAttribPointer = Load<VertexAttribPointerDel>("glVertexAttribPointer");
        _enableVertexAttribArray = Load<EnableVertexAttribArrayDel>("glEnableVertexAttribArray");
        _genFramebuffers = Load<GenFramebuffersDel>("glGenFramebuffers");
        _bindFramebuffer = Load<BindFramebufferDel>("glBindFramebuffer");
        _framebufferTexture2D = Load<FramebufferTexture2DDel>("glFramebufferTexture2D");
        _checkFramebufferStatus = Load<CheckFramebufferStatusDel>("glCheckFramebufferStatus");
        _activeTexture = Load<ActiveTextureDel>("glActiveTexture");
        _generateMipmap = Load<GenerateMipmapDel>("glGenerateMipmap");
    }

    private static T Load<T>(string name) where T : Delegate
    {
        IntPtr p = Win32.wglGetProcAddress(name);

        // wglGetProcAddress may return 0, 1, 2, 3 or -1 on failure
        long v = p.ToInt64();

        if (v is 0 or 1 or 2 or 3 or -1)
        {
            p = Win32.GetProcAddress(_opengl32, name);
        }

        if (p == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenGL function not found: {name}");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    // ---- thin public wrappers ----
    public static uint CreateShader(uint type) => _createShader(type);

    public static void ShaderSource(uint shader, string source)
    {
        IntPtr str = Marshal.StringToHGlobalAnsi(source);
        try { _shaderSource(shader, 1, new[] { str }, new[] { source.Length }); }
        finally { Marshal.FreeHGlobal(str); }
    }

    public static void CompileShader(uint shader) => _compileShader(shader);
    public static int GetShaderi(uint shader, uint pname) { _getShaderiv(shader, pname, out int v); return v; }

    public static string GetShaderInfoLog(uint shader)
    {
        var buf = new byte[4096];
        _getShaderInfoLog(shader, buf.Length, out int len, buf);
        return System.Text.Encoding.ASCII.GetString(buf, 0, Math.Max(0, len));
    }

    public static uint CreateProgram() => _createProgram();
    public static void AttachShader(uint program, uint shader) => _attachShader(program, shader);
    public static void LinkProgram(uint program) => _linkProgram(program);
    public static int GetProgrami(uint program, uint pname) { _getProgramiv(program, pname, out int v); return v; }

    public static string GetProgramInfoLog(uint program)
    {
        var buf = new byte[4096];
        _getProgramInfoLog(program, buf.Length, out int len, buf);
        return System.Text.Encoding.ASCII.GetString(buf, 0, Math.Max(0, len));
    }

    public static void UseProgram(uint program) => _useProgram(program);
    public static void DeleteShader(uint shader) => _deleteShader(shader);
    public static int GetUniformLocation(uint program, string name) => _getUniformLocation(program, name);

    public static void UniformMatrix4(int location, float[] m16) => _uniformMatrix4fv(location, 1, 0, m16);
    public static void Uniform3(int location, float x, float y, float z) => _uniform3f(location, x, y, z);
    public static void Uniform1(int location, int v) => _uniform1i(location, v);
    public static void Uniform1(int location, float v) => _uniform1f(location, v);
    /// <summary>Uploads `count` vec4s from a flat float array (x,y,z,w per element).</summary>
    public static void Uniform4(int location, int count, float[] values) => _uniform4fv(location, count, values);

    public static uint GenVertexArray() { var a = new uint[1]; _genVertexArrays(1, a); return a[0]; }
    public static void BindVertexArray(uint vao) => _bindVertexArray(vao);
    public static uint GenBuffer() { var a = new uint[1]; _genBuffers(1, a); return a[0]; }
    public static void BindBuffer(uint target, uint buffer) => _bindBuffer(target, buffer);

    public static void BufferData(uint target, float[] data, uint usage)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);

        try
        {
            _bufferData(target, data.Length * sizeof(float), h.AddrOfPinnedObject(), usage);
        }
        finally
        {
            h.Free();
        }
    }

    public static void BufferData(uint target, uint[] data, uint usage)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);

        try
        {
            _bufferData(target, (IntPtr)(data.Length * sizeof(uint)), h.AddrOfPinnedObject(), usage);
        }
        finally
        {
            h.Free();
        }
    }

    public static void VertexAttribPointer(uint index, int size, uint type, bool normalized, int stride, int offset) => _vertexAttribPointer(index, size, type, (byte)(normalized ? 1 : 0), stride, offset);
    public static void EnableVertexAttribArray(uint index) => _enableVertexAttribArray(index);

    public static uint GenFramebuffer()
    {
        var a = new uint[1]; 
        _genFramebuffers(1, a);
        return a[0];
    }

    public static void BindFramebuffer(uint target, uint fbo) => _bindFramebuffer(target, fbo);
    public static void FramebufferTexture2D(uint target, uint attachment, uint texTarget, uint texture, int level) => _framebufferTexture2D(target, attachment, texTarget, texture, level);
    public static uint CheckFramebufferStatus(uint target) => _checkFramebufferStatus(target);
    public static void ActiveTexture(uint unit) => _activeTexture(unit);
    public static void GenerateMipmap(uint target) => _generateMipmap(target);
}
