using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MovieMakerReborn.Model;

namespace MovieMakerReborn.Engine;

/// <summary>Builds an ffmpeg filter graph that mixes video sound and music for a time range.</summary>
public static class AudioGraph
{
    public sealed class Result
    {
        public List<string> InputArgs = new();
        public string Filter = "";
        public int Inputs;
    }

    static string Tempo(double speed)
    {
        var parts = new List<string>();
        double s = speed;
        while (s > 2.0 + 1e-9) { parts.Add("atempo=2.0"); s /= 2; }
        while (s < 0.5 - 1e-9) { parts.Add("atempo=0.5"); s /= 0.5; }
        if (Math.Abs(s - 1) > 1e-6) parts.Add($"atempo={FF.F(s)}");
        return parts.Count == 0 ? "" : "," + string.Join(",", parts);
    }

    static string Pitch(double k) => $"asetrate={(int)Math.Round(48000 * k)},aresample=48000,atempo={FF.F(1 / k)}";

    /// <summary>ffmpeg filters (and optional background noise) for an audio effect.</summary>
    public static (string filters, string noise, double noiseAmp) Fx(string fx) => fx switch
    {
        "deep" => (Pitch(0.78), null, 0),
        "chipmunk" => (Pitch(1.45), null, 0),
        "robot" => ("afftfilt=real='hypot(re,im)*sin(0)':imag='hypot(re,im)*cos(0)':win_size=512:overlap=0.75", null, 0),
        "echo" => ("aecho=0.8:0.85:140|280:0.35|0.18", null, 0),
        "hall" => ("aecho=0.8:0.88:45|90|180|360|540:0.35|0.28|0.2|0.12|0.08", null, 0),
        "telephone" => ("highpass=f=320,lowpass=f=3300,acompressor=threshold=0.1:ratio=6,volume=1.6", null, 0),
        "radio" => ("highpass=f=200,lowpass=f=5000,acompressor=threshold=0.15:ratio=4,volume=1.3", "pink", 0.004),
        "megaphone" => ("highpass=f=700,lowpass=f=3200,acrusher=bits=6:mix=0.4:mode=log:aa=1,volume=1.8", null, 0),
        "underwater" => ("lowpass=f=450,aecho=0.6:0.6:40:0.4,volume=1.5", null, 0),
        "lofi" => ("lowpass=f=3400,acrusher=bits=10:samples=3:mix=0.6", "pink", 0.003),
        "noir" => ("highpass=f=180,lowpass=f=4800,acompressor=threshold=0.12:ratio=5,aecho=0.6:0.5:35:0.22", "pink", 0.006),
        "silent" => ("highpass=f=350,lowpass=f=3000,tremolo=f=5:d=0.12,acrusher=bits=9:mix=0.3", "brown", 0.03),
        "western" => ("highpass=f=250,lowpass=f=4000,acrusher=bits=10:mix=0.3,vibrato=f=0.8:d=0.03", "white", 0.008),
        "vhs" => ("lowpass=f=8000,vibrato=f=0.7:d=0.06,acompressor=threshold=0.2:ratio=3", "pink", 0.004),
        _ => (null, null, 0),
    };

    /// <param name="inputOffset">Index of the first audio input in the final ffmpeg command.</param>
    public static Result Build(Project p, double t0, double t1, int inputOffset)
    {
        var r = new Result();
        var tl = p.ComputeTimeline();
        double mix = Math.Clamp(p.AudioMix, -1, 1);
        double videoGain = mix > 0 ? 1 - mix : 1;
        double musicGain = mix < 0 ? 1 + mix : 1;
        var chains = new List<string>();
        int idx = 0;

        void Add(string path, double S, double D, double srcIn, double speed, double vol, double fi, double fo, bool reverse = false, string fx = "none")
        {
            if (vol <= 0.0001 || D <= 0) return;
            if (S + D <= t0 + 1e-6 || S >= t1 - 1e-6) return;
            double skip = Math.Max(0, t0 - S);
            double len = Math.Min(D - skip, t1 - Math.Max(S, t0));
            if (len <= 0.01) return;
            // Reversed clips play the source backwards: timeline [skip, skip+len] maps to the source's tail end.
            double srcStart = reverse ? srcIn + (D - skip - len) * speed : srcIn + skip * speed;
            double delay = Math.Max(0, S - t0);
            r.InputArgs.AddRange(new[] { "-ss", FF.F(Math.Max(0, srcStart)), "-t", FF.F(len * speed + 0.05), "-i", path });
            var sb = new StringBuilder();
            sb.Append($"[{inputOffset + r.InputArgs.Count / 6 - 1}:a]aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo");
            if (reverse) sb.Append($",atrim=duration={FF.F(len * speed)},areverse");
            sb.Append(Tempo(speed));
            var (ff, noise, amp) = Fx(fx);
            if (ff != null) sb.Append(',').Append(ff);
            if (noise != null)
            {
                chains.Add($"anoisesrc=r=48000:c={noise}:a={FF.F(amp)}:d={FF.F(len + 0.5)},aformat=sample_fmts=fltp:channel_layouts=stereo[n{idx}]");
                sb.Append($"[p{idx}]");
                chains.Add(sb.ToString());
                sb.Clear();
                sb.Append($"[p{idx}][n{idx}]amix=inputs=2:normalize=0:duration=first");
            }
            sb.Append($",atrim=duration={FF.F(len)},asetpts=PTS-STARTPTS");
            sb.Append($",volume={FF.F(vol)}");
            if (fi > 0 && skip < fi) sb.Append($",afade=t=in:st=0:d={FF.F(fi - skip)}");
            if (fo > 0)
            {
                double st = D - fo - skip, d = fo;
                if (st < 0) { d += st; st = 0; }
                if (d > 0.01) sb.Append($",afade=t=out:st={FF.F(st)}:d={FF.F(d)}");
            }
            if (delay > 0.0005) sb.Append($",adelay=delays={(long)Math.Round(delay * 1000)}:all=1");
            sb.Append($"[a{idx}]");
            chains.Add(sb.ToString());
            idx++;
        }

        for (int i = 0; i < p.Clips.Count; i++)
        {
            var c = p.Clips[i];
            if (c.Kind != ClipKind.Video) continue;
            var m = p.GetMedia(c.MediaId);
            if (m == null || !m.HasAudio || !File.Exists(m.Path)) continue;
            Add(m.Path, tl.Start[i], tl.Dur[i], c.TrimIn, c.Speed, c.Volume * videoGain, c.FadeIn, c.FadeOut, c.Reverse, c.AudioFx == "match" ? Presets.MatchingAudio(c.Effect) : c.AudioFx);
        }
        double end = p.Clips.Count > 0 ? tl.Total : double.MaxValue;
        foreach (var mu in p.Music)
        {
            var m = p.GetMedia(mu.MediaId);
            if (m == null || !File.Exists(m.Path)) continue;
            double D = Math.Min(mu.Length, end - mu.Start);
            Add(m.Path, mu.Start, D, mu.TrimIn, 1, mu.Volume * musicGain, mu.FadeIn, mu.FadeOut, false, mu.AudioFx);
        }

        double total = Math.Max(0.05, t1 - t0);
        r.Inputs = idx;
        if (idx == 0)
            r.Filter = $"anullsrc=r=48000:cl=stereo,atrim=duration={FF.F(total)}[aout]";
        else
        {
            var labels = string.Concat(Enumerable.Range(0, idx).Select(i => $"[a{i}]"));
            string mixer = idx == 1 ? "[a0]anull" : $"{labels}amix=inputs={idx}:normalize=0:dropout_transition=0";
            r.Filter = string.Join(";", chains) + $";{mixer},apad,atrim=duration={FF.F(total)}[aout]";
        }
        return r;
    }
}

/// <summary>Streams 48 kHz stereo s16 PCM to the default device via winmm, and reports the play position.</summary>
public sealed class WaveOutPlayer : IDisposable
{
    const int SampleRate = 48000, BytesPerSec = SampleRate * 4;
    const int BufferCount = 6, BufferBytes = BytesPerSec / 20; // 50 ms each

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    struct MMTIME { [FieldOffset(0)] public uint wType; [FieldOffset(4)] public uint cb; }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr h, ref MMTIME t, int size);

    const uint WAVE_MAPPER = 0xFFFFFFFF, CALLBACK_EVENT = 0x50000, WHDR_DONE = 1, TIME_BYTES = 4;

    IntPtr _h;
    readonly IntPtr[] _hdrs = new IntPtr[BufferCount];
    readonly IntPtr[] _data = new IntPtr[BufferCount];
    readonly AutoResetEvent _event = new(false);
    Thread _thread;
    volatile bool _stop;
    Stream _src;
    Process _proc;
    long _queuedBytes;
    public bool Ok { get; private set; }

    public static WaveOutPlayer Start(Process proc)
    {
        var w = new WaveOutPlayer { _proc = proc, _src = proc.StandardOutput.BaseStream };
        w.Open();
        return w;
    }

    void Open()
    {
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = SampleRate, nAvgBytesPerSec = BytesPerSec, nBlockAlign = 4, wBitsPerSample = 16, cbSize = 0 };
        int rc = waveOutOpen(out _h, WAVE_MAPPER, ref fmt, _event.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
        if (rc != 0) { Ok = false; return; }
        int hs = Marshal.SizeOf<WAVEHDR>();
        for (int i = 0; i < BufferCount; i++)
        {
            _data[i] = Marshal.AllocHGlobal(BufferBytes);
            _hdrs[i] = Marshal.AllocHGlobal(hs);
            Marshal.StructureToPtr(new WAVEHDR { lpData = _data[i], dwBufferLength = BufferBytes, dwFlags = WHDR_DONE }, _hdrs[i], false);
        }
        Ok = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "waveout", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    readonly byte[] _tmp = new byte[BufferBytes];

    bool Fill(int i)
    {
        int got = 0;
        try
        {
            while (got < BufferBytes && !_stop)
            {
                int n = _src.Read(_tmp, got, BufferBytes - got);
                if (n <= 0) break;
                got += n;
            }
        }
        catch { }
        if (got <= 0 || _stop) return false;
        got &= ~3;
        Marshal.Copy(_tmp, 0, _data[i], got);
        int hs = Marshal.SizeOf<WAVEHDR>();
        var hdr = Marshal.PtrToStructure<WAVEHDR>(_hdrs[i]);
        if ((hdr.dwFlags & 2) != 0) waveOutUnprepareHeader(_h, _hdrs[i], hs); // WHDR_PREPARED
        hdr = new WAVEHDR { lpData = _data[i], dwBufferLength = (uint)got };
        Marshal.StructureToPtr(hdr, _hdrs[i], false);
        waveOutPrepareHeader(_h, _hdrs[i], hs);
        waveOutWrite(_h, _hdrs[i], hs);
        Interlocked.Add(ref _queuedBytes, got);
        return true;
    }

    void Loop()
    {
        bool eof = false;
        for (int i = 0; i < BufferCount && !eof; i++) eof = !Fill(i);
        while (!_stop && !eof)
        {
            _event.WaitOne(100);
            for (int i = 0; i < BufferCount && !_stop && !eof; i++)
            {
                var hdr = Marshal.PtrToStructure<WAVEHDR>(_hdrs[i]);
                if ((hdr.dwFlags & WHDR_DONE) != 0) eof = !Fill(i);
            }
        }
    }

    /// <summary>Seconds of audio actually played so far.</summary>
    public double Position
    {
        get
        {
            if (!Ok) return 0;
            var t = new MMTIME { wType = TIME_BYTES };
            if (waveOutGetPosition(_h, ref t, 12) != 0) return 0;
            return t.cb / (double)BytesPerSec;
        }
    }

    /// <summary>True when the device has started consuming audio.</summary>
    public bool Started => Ok && Position > 0;

    public void Dispose()
    {
        _stop = true;
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
        _thread?.Join(500);
        if (_h != IntPtr.Zero)
        {
            waveOutReset(_h);
            int hs = Marshal.SizeOf<WAVEHDR>();
            for (int i = 0; i < BufferCount; i++) if (_hdrs[i] != IntPtr.Zero) waveOutUnprepareHeader(_h, _hdrs[i], hs);
            waveOutClose(_h);
            _h = IntPtr.Zero;
        }
        for (int i = 0; i < BufferCount; i++)
        {
            if (_hdrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(_hdrs[i]);
            if (_data[i] != IntPtr.Zero) Marshal.FreeHGlobal(_data[i]);
            _hdrs[i] = _data[i] = IntPtr.Zero;
        }
        try { _proc?.Dispose(); } catch { }
    }
}
