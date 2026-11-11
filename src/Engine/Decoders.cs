using System.Collections.Concurrent;
using System.Diagnostics;
using MovieMakerReborn.Model;

namespace MovieMakerReborn.Engine;

/// <summary>
/// Streams a video clip as raw BGRA frames at the project frame rate through an ffmpeg process,
/// with a small read-ahead queue. Frame k corresponds to clip-local time k / fps.
/// </summary>
public sealed class VideoDecoder : IDisposable
{
    readonly MediaRef _m;
    readonly Clip _c;
    readonly int _fpsNum, _fpsDen;
    readonly string _interp;
    readonly int _queueSize;
    public readonly int FrameW, FrameH;
    readonly int _frameBytes;

    Process _proc;
    Thread _reader;
    BlockingCollection<(int idx, byte[] buf)> _queue;
    CancellationTokenSource _cts;
    readonly ConcurrentBag<byte[]> _pool = new();
    byte[] _current;
    int _currentIdx = -1;
    int _nextIdx;
    bool _eof;
    public string LastError = "";

    readonly bool _cover;

    public VideoDecoder(MediaRef m, Clip c, int frameW, int frameH, int fpsNum, int fpsDen, string interp, bool cover, int queueSize = 6)
    {
        _m = m; _c = c; _fpsNum = fpsNum; _fpsDen = fpsDen; _interp = interp; _cover = cover;
        FrameW = Math.Max(2, frameW & ~1); FrameH = Math.Max(2, frameH & ~1);
        _frameBytes = FrameW * FrameH * 4;
        _queueSize = Math.Clamp(queueSize, 2, 16);
    }

    double Fps => (double)_fpsNum / _fpsDen;

    void Start(int k)
    {
        Stop();
        _eof = false;
        _nextIdx = k;
        double srcStart = _c.TrimIn + k / Fps * _c.Speed;
        double srcDur = _c.TrimOut - srcStart;
        if (srcDur <= 0.001) { _eof = true; return; }
        string vf = BuildFilter();
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", FF.F(srcStart), "-t", FF.F(srcDur + 0.05),
            "-i", _m.Path, "-an", "-sn", "-dn", "-vf", vf, "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" };
        var psi = FF.Psi(FF.Ffmpeg, args);
        _proc = new Process { StartInfo = psi };
        _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) LastError = e.Data; };
        _proc.Start();
        try { _proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _proc.BeginErrorReadLine();
        _cts = new CancellationTokenSource();
        _queue = new BlockingCollection<(int, byte[])>(_queueSize);
        var stream = _proc.StandardOutput.BaseStream;
        var q = _queue; var ct = _cts.Token; int startIdx = k;
        _reader = new Thread(() => ReadLoop(stream, q, ct, startIdx)) { IsBackground = true, Name = "decoder" };
        _reader.Start();
    }

    string BuildFilter()
    {
        string rate = $"{_fpsNum}/{_fpsDen}";
        string fpsFilter = _interp switch
        {
            "blend" => $"framerate=fps={rate}",
            "mci" => $"minterpolate=fps={rate}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1",
            _ => $"fps={rate}"
        };
        string scale = _cover ? $"scale={FrameW}:{FrameH}:force_original_aspect_ratio=increase:flags=bicubic,crop={FrameW}:{FrameH}" : $"scale={FrameW}:{FrameH}:flags=bicubic";
        string head = $"{FF.SarFix(_m)}{FF.RotateFilter(_c.Rotation)}setpts=(PTS-STARTPTS)/{FF.F(_c.Speed)}";
        return _interp == "drop" || string.IsNullOrEmpty(_interp)
            ? $"{head},{fpsFilter},{scale},format=bgra"
            : $"{head},{scale},{fpsFilter},format=bgra";
    }

    // ---------------- Reverse playback: decode short forward chunks and serve them backwards ----------------
    int TotalFrames => Math.Max(1, (int)Math.Ceiling((_c.TrimOut - _c.TrimIn) / Math.Max(0.01, _c.Speed) * Fps - 1e-6));
    int ChunkFrames => (int)Math.Clamp(400L * 1024 * 1024 / _frameBytes, 6, Math.Max(6, (long)(Fps * 2)));
    int _revChunk = -1;
    List<byte[]> _revFrames;
    Task<(int chunk, List<byte[]> frames)> _revPrefetch;
    int _revPrefetchChunk = -1;

    List<byte[]> DecodeRange(int a, int count)
    {
        var list = new List<byte[]>();
        double srcStart = _c.TrimIn + a / Fps * _c.Speed;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", FF.F(srcStart), "-t", FF.F(count / Fps * _c.Speed + 0.2),
            "-i", _m.Path, "-an", "-sn", "-dn", "-vf", BuildFilter(), "-frames:v", count.ToString(), "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" };
        using var p = Process.Start(FF.Psi(FF.Ffmpeg, args))!;
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        var st = p.StandardOutput.BaseStream;
        while (list.Count < count)
        {
            var buf = new byte[_frameBytes];
            int got = 0;
            while (got < _frameBytes) { int n = st.Read(buf, got, _frameBytes - got); if (n <= 0) break; got += n; }
            if (got < _frameBytes) break;
            list.Add(buf);
        }
        try { if (!p.HasExited) p.Kill(); } catch { }
        return list;
    }

    byte[] GetFrameReverse(int k)
    {
        int n = TotalFrames, cf = ChunkFrames;
        int f = Math.Clamp(n - 1 - k, 0, n - 1);
        int chunk = f / cf;
        if (chunk != _revChunk)
        {
            List<byte[]> frames = null;
            if (_revPrefetch != null && _revPrefetchChunk == chunk) frames = _revPrefetch.Result.frames;
            _revPrefetch = null;
            frames ??= DecodeRange(chunk * cf, Math.Min(cf, n - chunk * cf));
            _revFrames = frames; _revChunk = chunk;
            int next = chunk - 1;
            if (next >= 0)
            {
                int a = next * cf, cnt = Math.Min(cf, n - a);
                _revPrefetchChunk = next;
                _revPrefetch = Task.Run(() => (next, DecodeRange(a, cnt)));
            }
        }
        if (_revFrames == null || _revFrames.Count == 0) return null;
        return _revFrames[Math.Clamp(f - chunk * cf, 0, _revFrames.Count - 1)];
    }

    void ReadLoop(Stream s, BlockingCollection<(int, byte[])> q, CancellationToken ct, int idx)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_pool.TryTake(out var buf) || buf.Length != _frameBytes) buf = new byte[_frameBytes];
                int got = 0;
                while (got < _frameBytes)
                {
                    int n = s.Read(buf, got, _frameBytes - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < _frameBytes) break;
                q.Add((idx++, buf), ct);
            }
        }
        catch { }
        finally { try { q.CompleteAdding(); } catch { } }
    }

    void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        if (_proc != null)
        {
            try { if (!_proc.HasExited) _proc.Kill(); } catch { }
            try { _proc.Dispose(); } catch { }
            _proc = null;
        }
        if (_queue != null)
        {
            try { while (_queue.TryTake(out var f)) _pool.Add(f.buf); } catch { }
        }
        _reader = null;
        _queue = null;
    }

    /// <summary>Warm up: start the process so the first frame is ready when needed.</summary>
    public void Prefetch(int k)
    {
        if (_c.Reverse) return;
        if (_proc == null && !_eof) Start(k);
    }

    public byte[] GetFrame(int k, int timeoutMs = 8000)
    {
        if (k < 0) k = 0;
        if (_c.Reverse) return GetFrameReverse(k);
        if (_current != null && _currentIdx == k) return _current;
        if (_eof && _current != null && k > _currentIdx) return _current;
        int jump = (int)Math.Max(4, Fps * 1.5);
        bool needRestart = _queue == null && !_eof
            || k < _currentIdx
            || k > _nextIdx + jump
            || (_current == null && _queue != null && k < _nextIdx);
        if (_eof && k < _currentIdx) needRestart = true;
        if (needRestart)
        {
            if (_current != null) { _pool.Add(_current); _current = null; _currentIdx = -1; }
            Start(k);
            if (_eof) return _current;
        }
        while (_queue != null && (_current == null || _currentIdx < k))
        {
            if (_queue.TryTake(out var f, timeoutMs))
            {
                if (_current != null) _pool.Add(_current);
                _current = f.buf; _currentIdx = f.idx; _nextIdx = f.idx + 1;
            }
            else
            {
                if (_queue.IsCompleted) _eof = true;
                break;
            }
        }
        return _current;
    }

    public void Dispose()
    {
        Stop();
        _current = null;
    }
}

/// <summary>Decodes still images (any format ffmpeg reads) to BGRA.</summary>
public static class ImageLoader
{
    public sealed class Img { public byte[] Data; public int W, H; }
    static readonly ConcurrentDictionary<string, Img> Cache = new();

    public static Img Load(MediaRef m, int rotation, int maxW, int maxH)
    {
        var (rw, rh) = FF.RotatedSize(m, rotation);
        if (rw <= 0 || rh <= 0) return null;
        double s = Math.Min(1.0, Math.Min((double)maxW / rw, (double)maxH / rh));
        int w = Math.Max(2, (int)(rw * s) & ~1), h = Math.Max(2, (int)(rh * s) & ~1);
        string key = $"{m.Path}|{rotation}|{w}x{h}";
        if (Cache.TryGetValue(key, out var img)) return img;
        if (Cache.Count > 48) Cache.Clear();
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", m.Path, "-frames:v", "1",
            "-vf", $"{FF.SarFix(m)}{FF.RotateFilter(rotation)}scale={w}:{h}:flags=lanczos,format=bgra", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" };
        var bytes = FF.RunBytes(args, 30000);
        if (bytes.Length < w * h * 4) return null;
        if (bytes.Length > w * h * 4) Array.Resize(ref bytes, w * h * 4);
        img = new Img { Data = bytes, W = w, H = h };
        Cache[key] = img;
        return img;
    }
}
