using System.Diagnostics;
using MovieMakerReborn.Model;

namespace MovieMakerReborn.Engine;

/// <summary>Preview playback: renders frames on a background thread, synced to the audio clock.</summary>
public sealed class Player : IDisposable
{
    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly object _lock = new();
    volatile bool _quit;

    Project _pending;          // new project snapshot waiting to be applied
    int _pendingW, _pendingH;
    Renderer _renderer;
    Project _project;
    double _total;

    double _seekTo = -1;
    volatile bool _playing;
    double _playFrom;
    readonly Stopwatch _clock = new();
    WaveOutPlayer _audio;
    bool _restartAudio;

    public double Position { get; private set; }
    public bool IsPlaying => _playing;

    /// <summary>Raised on the render thread with a finished frame; the handler must copy it synchronously.</summary>
    public event Action<Frame, double> FrameReady;
    public event Action<bool> PlayingChanged;
    public event Action<string> Error;

    public Player()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "preview", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void SetProject(Project snapshot, int w, int h)
    {
        lock (_lock)
        {
            _pending = snapshot; _pendingW = w; _pendingH = h;
            if (_playing) _restartAudio = true;
            if (!_playing && _seekTo < 0) _seekTo = Position;
        }
        _wake.Set();
    }

    public void Seek(double t)
    {
        lock (_lock)
        {
            _seekTo = Math.Max(0, t);
            if (_playing) { _playFrom = _seekTo; _restartAudio = true; }
        }
        _wake.Set();
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_playing) return;
            if (Position >= _total - 0.05) Position = 0;
            _playFrom = Position;
            _playing = true;
            _restartAudio = true;
        }
        PlayingChanged?.Invoke(true);
        _wake.Set();
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_playing) return;
            _playing = false;
            _seekTo = Position;
        }
        PlayingChanged?.Invoke(false);
        _wake.Set();
    }

    public void Toggle() { if (_playing) Pause(); else Play(); }

    void StopAudio()
    {
        _audio?.Dispose();
        _audio = null;
        _clock.Reset();
    }

    void StartAudio(double from)
    {
        StopAudio();
        _clock.Restart();
        if (_project == null || FF.Ffmpeg == null) return;
        try
        {
            var g = AudioGraph.Build(_project, from, _total, 0);
            if (g.Inputs == 0) return;
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            args.AddRange(g.InputArgs);
            args.AddRange(new[] { "-filter_complex", g.Filter, "-map", "[aout]", "-f", "s16le", "-ar", "48000", "-ac", "2", "pipe:1" });
            var proc = Process.Start(FF.Psi(FF.Ffmpeg, args))!;
            proc.ErrorDataReceived += (_, e) => { };
            proc.BeginErrorReadLine();
            _audio = WaveOutPlayer.Start(proc);
            if (!_audio.Ok) { _audio.Dispose(); _audio = null; }
        }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
    }

    /// <summary>Timeline seconds elapsed since play started.</summary>
    double Elapsed()
    {
        if (_audio != null)
        {
            if (_audio.Started)
            {
                double a = _audio.Position;
                return a;
            }
            // Audio not started yet: hold the first frame (but don't wait forever).
            if (_clock.Elapsed.TotalSeconds < 1.0) return 0;
            return _clock.Elapsed.TotalSeconds - 1.0;
        }
        return _clock.Elapsed.TotalSeconds;
    }

    void Loop()
    {
        int lastFrame = -1;
        while (!_quit)
        {
            Project apply = null; int aw = 0, ah = 0;
            double seek; bool playing, restart; double from;
            lock (_lock)
            {
                if (_pending != null) { apply = _pending; aw = _pendingW; ah = _pendingH; _pending = null; }
                seek = _seekTo; _seekTo = -1;
                playing = _playing; restart = _restartAudio; _restartAudio = false; from = _playFrom;
            }
            try
            {
                if (apply != null)
                {
                    if (_renderer == null || _renderer.W != (aw & ~1) || _renderer.H != (ah & ~1))
                    {
                        _renderer?.Dispose();
                        _renderer = new Renderer(aw, ah, false);
                    }
                    _project = apply;
                    _renderer.SetProject(apply);
                    _total = apply.TotalDuration(_renderer.Timeline);
                    lastFrame = -1;
                }
                if (_renderer == null) { _wake.WaitOne(50); continue; }

                if (playing)
                {
                    if (restart) { StartAudio(from); lastFrame = -1; }
                    double t = from + Elapsed();
                    if (t >= _total)
                    {
                        t = _total;
                        lock (_lock) { _playing = false; }
                        StopAudio();
                        Position = t;
                        Publish(t);
                        PlayingChanged?.Invoke(false);
                        continue;
                    }
                    Position = t;
                    int f = (int)Math.Floor(t * _project.Fps);
                    if (f != lastFrame)
                    {
                        lastFrame = f;
                        Publish(f / _project.Fps);
                    }
                    else
                    {
                        double next = (f + 1) / _project.Fps - t;
                        _wake.WaitOne(TimeSpan.FromSeconds(Math.Clamp(next, 0.001, 0.02)));
                    }
                }
                else
                {
                    if (_audio != null) StopAudio();
                    if (seek >= 0)
                    {
                        Position = Math.Min(seek, Math.Max(0, _total));
                        Publish(Position);
                    }
                    else _wake.WaitOne(100);
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex.Message);
                Thread.Sleep(100);
            }
        }
        StopAudio();
        _renderer?.Dispose();
    }

    void Publish(double t)
    {
        int frameNo = (int)Math.Floor(t * (_project?.Fps ?? 30));
        var frame = _renderer.Render(t, frameNo);
        FrameReady?.Invoke(frame, t);
    }

    public void Dispose()
    {
        _quit = true;
        _wake.Set();
        _thread.Join(1500);
    }
}
