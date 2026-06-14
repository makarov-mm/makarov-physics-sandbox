using System.Runtime.InteropServices;

namespace MakarovPhysicsSandbox;

/// <summary>
/// Sound identifiers. The actual waveforms are synthesised procedurally at init time
/// (see <see cref="Audio"/>), so there are no audio asset files to ship or license.
/// </summary>
internal enum Sound
{
    ImpactSoft,
    ImpactHard,
    Explosion,
    Zap,
    FireCrackle,
    BreakWood,
    BreakGlass,
    Splash,
    Thud,
}

/// <summary>
/// A tiny game-audio engine, hand-written in the same spirit as the rest of the project
/// (the renderer is hand-rolled OpenGL via P/Invoke; this is hand-rolled audio via winmm).
///
///  * No NuGet, no asset files. Every sound is SYNTHESISED into a PCM clip at startup.
///  * Output is a streaming <c>waveOut</c> device (winmm) fed by a small software mixer
///    on a background thread, so many impacts/explosions can overlap (real polyphony,
///    unlike System.Media.SoundPlayer / SystemSounds which the game used as a placeholder).
///  * Completely non-critical: if the device cannot be opened, everything becomes a no-op.
///    Audio must never crash or stall the simulation.
///
/// Format: 44100 Hz, mono, 16-bit signed PCM.
/// </summary>
internal static class Audio
{
    private const int SampleRate = 44100;
    private const int BufferFrames = 1024;            // ~23 ms per buffer
    private const int BufferCount = 4;                // ~93 ms ring
    private const int MaxVoices = 32;
    private const float Master = 0.85f;

    private static bool _initTried;
    private static bool _ok;
    private static readonly object _gate = new();

    private static IntPtr _hwo;
    private static IntPtr[] _hdr = Array.Empty<IntPtr>();
    private static IntPtr[] _data = Array.Empty<IntPtr>();
    private static int _flagsOffset;
    private static Thread? _thread;
    private static volatile bool _running;

    private static float[]?[] _clips = Array.Empty<float[]?>();
    private static readonly Voice[] _voices = new Voice[MaxVoices];
    private static readonly short[] _scratch = new short[BufferFrames];
    private static readonly float[] _acc = new float[BufferFrames];

    private struct Voice
    {
        public float[] Clip;
        public double Pos;
        public double Step;   // playback rate (pitch)
        public float Gain;
        public bool Active;
        public long StartTick; // for oldest-voice stealing
    }

    // ----------------------------------------------------------------- public API

    /// <summary>Play a synthesised sound. Safe to call from the game thread at any time.</summary>
    public static void Play(Sound id, float gain = 1f, float pitch = 1f)
    {
        EnsureInit();
        if (!_ok) return;

        int i = (int)id;
        if (i < 0 || i >= _clips.Length) return;
        var clip = _clips[i];
        if (clip == null || clip.Length == 0) return;

        gain = Math.Clamp(gain, 0f, 1.6f);
        pitch = Math.Clamp(pitch, 0.25f, 4f);

        lock (_gate)
        {
            int slot = -1;
            long oldest = long.MaxValue;
            int oldestSlot = 0;
            for (int v = 0; v < MaxVoices; v++)
            {
                if (!_voices[v].Active) { slot = v; break; }
                if (_voices[v].StartTick < oldest) { oldest = _voices[v].StartTick; oldestSlot = v; }
            }
            if (slot < 0) slot = oldestSlot; // steal the oldest voice

            _voices[slot] = new Voice
            {
                Clip = clip,
                Pos = 0,
                Step = pitch,
                Gain = gain,
                Active = true,
                StartTick = Environment.TickCount64,
            };
        }
    }

    public static void Shutdown()
    {
        lock (_gate)
        {
            if (!_ok && !_running) return;
            _running = false;
        }
        try { _thread?.Join(250); } catch { /* ignore */ }

        lock (_gate)
        {
            if (_hwo != IntPtr.Zero)
            {
                try
                {
                    waveOutReset(_hwo);
                    for (int b = 0; b < _hdr.Length; b++)
                        if (_hdr[b] != IntPtr.Zero) waveOutUnprepareHeader(_hwo, _hdr[b], (uint)Marshal.SizeOf<WAVEHDR>());
                    waveOutClose(_hwo);
                }
                catch { /* ignore */ }
                _hwo = IntPtr.Zero;
            }
            foreach (var p in _hdr) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            foreach (var p in _data) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            _hdr = Array.Empty<IntPtr>();
            _data = Array.Empty<IntPtr>();
            _ok = false;
        }
    }

    // ----------------------------------------------------------------- init

    private static void EnsureInit()
    {
        if (_initTried) return;
        lock (_gate)
        {
            if (_initTried) return;
            _initTried = true;
            try { Init(); }
            catch { _ok = false; }
        }
    }

    private static void Init()
    {
        BakeClips();

        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1,                 // WAVE_FORMAT_PCM
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 2,                // channels * bits/8
            nAvgBytesPerSec = SampleRate * 2,
            cbSize = 0,
        };

        int rc = waveOutOpen(out _hwo, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
        if (rc != 0 || _hwo == IntPtr.Zero) { _ok = false; return; }

        _flagsOffset = (int)Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags));
        int bytes = BufferFrames * 2;
        _hdr = new IntPtr[BufferCount];
        _data = new IntPtr[BufferCount];
        int hdrSize = Marshal.SizeOf<WAVEHDR>();

        for (int b = 0; b < BufferCount; b++)
        {
            _data[b] = Marshal.AllocHGlobal(bytes);
            _hdr[b] = Marshal.AllocHGlobal(hdrSize);
            var h = new WAVEHDR { lpData = _data[b], dwBufferLength = (uint)bytes };
            Marshal.StructureToPtr(h, _hdr[b], false);
            if (waveOutPrepareHeader(_hwo, _hdr[b], (uint)hdrSize) != 0) { _ok = false; return; }
        }

        _ok = true;
        _running = true;

        // Prime every buffer so the device starts streaming immediately.
        for (int b = 0; b < BufferCount; b++) FillAndWrite(b);

        _thread = new Thread(MixLoop) { IsBackground = true, Name = "AudioMixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    // ----------------------------------------------------------------- mixer thread

    private static void MixLoop()
    {
        int hdrSize = Marshal.SizeOf<WAVEHDR>();
        while (_running)
        {
            for (int b = 0; b < BufferCount && _running; b++)
            {
                int flags = Marshal.ReadInt32(_hdr[b], _flagsOffset);
                if ((flags & WHDR_DONE) != 0)
                    FillAndWrite(b);
            }
            Thread.Sleep(4);
        }
        _ = hdrSize;
    }

    private static void FillAndWrite(int b)
    {
        Mix(_scratch, BufferFrames);
        Marshal.Copy(_scratch, 0, _data[b], BufferFrames);
        waveOutWrite(_hwo, _hdr[b], (uint)Marshal.SizeOf<WAVEHDR>());
    }

    private static void Mix(short[] outBuf, int frames)
    {
        Array.Clear(_acc, 0, frames);

        lock (_gate)
        {
            for (int v = 0; v < MaxVoices; v++)
            {
                if (!_voices[v].Active) continue;
                var clip = _voices[v].Clip;
                double pos = _voices[v].Pos;
                double step = _voices[v].Step;
                float g = _voices[v].Gain;
                int len = clip.Length;

                for (int n = 0; n < frames; n++)
                {
                    int idx = (int)pos;
                    if (idx >= len) { _voices[v].Active = false; break; }
                    _acc[n] += clip[idx] * g;
                    pos += step;
                }
                if (_voices[v].Active)
                {
                    _voices[v].Pos = pos;
                    if ((int)pos >= len) _voices[v].Active = false;
                }
            }
        }

        for (int n = 0; n < frames; n++)
        {
            float s = _acc[n] * Master;
            if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
            outBuf[n] = (short)(s * 32767f);
        }
    }

    // ----------------------------------------------------------------- synthesis

    private static void BakeClips()
    {
        var rng = new Random(1234);
        _clips = new float[]?[Enum.GetValues<Sound>().Length];

        _clips[(int)Sound.ImpactSoft]  = Impact(rng, dur: 0.09f, thumpHz: 130f, noise: 0.55f, decay: 42f);
        _clips[(int)Sound.ImpactHard]  = Impact(rng, dur: 0.16f, thumpHz: 72f, noise: 0.80f, decay: 26f);
        _clips[(int)Sound.Thud]        = Impact(rng, dur: 0.18f, thumpHz: 80f, noise: 0.30f, decay: 30f);
        _clips[(int)Sound.Explosion]   = Explosion(rng);
        _clips[(int)Sound.Zap]         = Zap(rng);
        _clips[(int)Sound.FireCrackle] = FireCrackle(rng);
        _clips[(int)Sound.BreakWood]   = BreakWood(rng);
        _clips[(int)Sound.BreakGlass]  = BreakGlass(rng);
        _clips[(int)Sound.Splash]      = Splash(rng);
    }

    // generic "hit": a low pitched thump plus a noise transient, both exponentially damped.
    private static float[] Impact(Random rng, float dur, float thumpHz, float noise, float decay)
    {
        int n = (int)(dur * SampleRate);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float env = MathF.Exp(-decay * t);
            float thump = MathF.Sin(2f * MathF.PI * thumpHz * t) * env;
            float nz = ((float)rng.NextDouble() * 2f - 1f) * MathF.Exp(-decay * 2.2f * t) * noise;
            s[i] = (thump * 0.7f + nz) * 0.6f;
        }
        return s;
    }

    private static float[] Explosion(Random rng)
    {
        int n = (int)(0.7f * SampleRate);
        var s = new float[n];
        float brown = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            // descending sub-bass sweep
            float sweepHz = 90f - 55f * MathF.Min(t / 0.4f, 1f);
            float sub = MathF.Sin(2f * MathF.PI * sweepHz * t) * MathF.Exp(-3.2f * t);
            // low-passed (brown) noise rumble
            brown += (((float)rng.NextDouble() * 2f - 1f) - brown) * 0.06f;
            float rumble = brown * MathF.Exp(-2.6f * t) * 2.4f;
            // initial crack
            float crack = ((float)rng.NextDouble() * 2f - 1f) * MathF.Exp(-60f * t);
            s[i] = (sub * 0.8f + rumble + crack * 0.6f) * 0.55f;
        }
        return s;
    }

    private static float[] Zap(Random rng)
    {
        int n = (int)(0.18f * SampleRate);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float env = MathF.Exp(-18f * t);
            float buzz = MathF.Sign(MathF.Sin(2f * MathF.PI * 900f * t)); // square-ish
            float flutter = 0.5f + 0.5f * MathF.Sin(2f * MathF.PI * 70f * t + (float)rng.NextDouble());
            float nz = (float)rng.NextDouble() * 2f - 1f;
            s[i] = (buzz * 0.5f + nz * 0.5f) * flutter * env * 0.5f;
        }
        return s;
    }

    private static float[] FireCrackle(Random rng)
    {
        int n = (int)(0.14f * SampleRate);
        var s = new float[n];
        float bed = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            bed += (((float)rng.NextDouble() * 2f - 1f) - bed) * 0.25f; // filtered hiss
            float pop = rng.NextDouble() < 0.01 ? ((float)rng.NextDouble() * 2f - 1f) : 0f;
            float env = MathF.Exp(-10f * t);
            s[i] = (bed * 0.35f + pop) * env * 0.5f;
        }
        return s;
    }

    private static float[] BreakWood(Random rng)
    {
        int n = (int)(0.18f * SampleRate);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float nz = ((float)rng.NextDouble() * 2f - 1f) * MathF.Exp(-28f * t);
            float crack = MathF.Sin(2f * MathF.PI * 220f * t) * MathF.Exp(-35f * t);
            s[i] = (nz * 0.8f + crack * 0.4f) * 0.55f;
        }
        return s;
    }

    private static float[] BreakGlass(Random rng)
    {
        int n = (int)(0.35f * SampleRate);
        var s = new float[n];
        // a handful of high "shard" partials
        float[] sh = { 2400f, 3100f, 3700f, 4600f, 5500f };
        var ph = new float[sh.Length];
        for (int k = 0; k < ph.Length; k++) ph[k] = (float)rng.NextDouble();
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float nz = ((float)rng.NextDouble() * 2f - 1f) * MathF.Exp(-16f * t);
            float shards = 0f;
            for (int k = 0; k < sh.Length; k++)
                shards += MathF.Sin(2f * MathF.PI * sh[k] * t + ph[k]) * MathF.Exp(-(8f + k * 3f) * t);
            s[i] = (nz * 0.6f + shards * 0.12f) * 0.5f;
        }
        return s;
    }

    private static float[] Splash(Random rng)
    {
        int n = (int)(0.28f * SampleRate);
        var s = new float[n];
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            lp += (((float)rng.NextDouble() * 2f - 1f) - lp) * 0.45f; // wet, low-passed noise
            float env = MathF.Exp(-9f * t);
            float bubble = MathF.Sin(2f * MathF.PI * (400f + 600f * t) * t) * MathF.Exp(-22f * t);
            s[i] = (lp * 0.7f + bubble * 0.25f) * env * 0.5f;
        }
        return s;
    }

    // ----------------------------------------------------------------- winmm interop

    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const uint CALLBACK_NULL = 0x0;
    private const int WHDR_DONE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
}
