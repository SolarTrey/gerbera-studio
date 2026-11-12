using System.Diagnostics;
using MovieMakerReborn.Model;

namespace MovieMakerReborn.Engine;

public sealed class VideoCodecInfo
{
    public string Key, Label, Encoder, Family; // family: h264, hevc, av1, vp9, prores, ffv1
    public string[] Containers;
    public bool Hardware, Lossless, NoQuality;
    public override string ToString() => Label;
}

public sealed class ExportSettings
{
    public int Width = 1920, Height = 1080;
    public int FpsNum = 30, FpsDen = 1;
    public string Interp = "drop";
    public VideoCodecInfo Codec;
    public string Container = "mp4";
    public bool UseBitrate;
    public int Crf = 20;            // quality (lower = better)
    public int BitrateKbps = 20000;
    public string Speed = "medium"; // fast, medium, slow
    public bool TenBit;
    public string AudioCodec = "aac";
    public int AudioKbps = 256;
    public string OutputPath = "";
}

public static class Codecs
{
    public static readonly VideoCodecInfo[] All =
    {
        new() { Key="h264", Label="H.264 / AVC (most compatible)", Encoder="libx264", Family="h264", Containers=new[]{"mp4","mkv","mov"} },
        new() { Key="hevc", Label="H.265 / HEVC (smaller files)", Encoder="libx265", Family="hevc", Containers=new[]{"mp4","mkv","mov"} },
        new() { Key="av1", Label="AV1 (smallest, modern)", Encoder="libsvtav1", Family="av1", Containers=new[]{"mp4","mkv","webm"} },
        new() { Key="vp9", Label="VP9 (web)", Encoder="libvpx-vp9", Family="vp9", Containers=new[]{"webm","mkv","mp4"} },
        new() { Key="h264_nvenc", Label="H.264 — NVIDIA GPU", Encoder="h264_nvenc", Family="h264", Hardware=true, Containers=new[]{"mp4","mkv","mov"} },
        new() { Key="hevc_nvenc", Label="H.265 — NVIDIA GPU", Encoder="hevc_nvenc", Family="hevc", Hardware=true, Containers=new[]{"mp4","mkv","mov"} },
        new() { Key="av1_nvenc", Label="AV1 — NVIDIA GPU (RTX 40+)", Encoder="av1_nvenc", Family="av1", Hardware=true, Containers=new[]{"mp4","mkv","webm"} },
        new() { Key="h264_qsv", Label="H.264 — Intel Quick Sync", Encoder="h264_qsv", Family="h264", Hardware=true, Containers=new[]{"mp4","mkv","mov"} },
        new() { Key="hevc_qsv", Label="H.265 — Intel Quick Sync", Encoder="hevc_qsv", Family="hevc", Hardware=true, Containers=new[]{"mp4","mkv","mov"} },
        new() { Key="av1_qsv", Label="AV1 — Intel Arc", Encoder="av1_qsv", Family="av1", Hardware=true, Containers=new[]{"mp4","mkv","webm"} },
        new() { Key="h264_amf", Label="H.264 — AMD GPU", Encoder="h264_amf", Family="h264", Hardware=true, Containers=new[]{"mp4","mkv","mov"} },
        new() { Key="hevc_amf", Label="H.265 — AMD GPU", Encoder="hevc_amf", Family="hevc", Hardware=true, Containers=new[]{"mp4","mkv","mov"} },
        new() { Key="av1_amf", Label="AV1 — AMD GPU (RX 7000+)", Encoder="av1_amf", Family="av1", Hardware=true, Containers=new[]{"mp4","mkv","webm"} },
        new() { Key="prores", Label="Apple ProRes 422 HQ (editing master)", Encoder="prores_ks", Family="prores", NoQuality=true, Containers=new[]{"mov","mkv"} },
        new() { Key="ffv1", Label="FFV1 lossless (archive)", Encoder="ffv1", Family="ffv1", Lossless=true, NoQuality=true, Containers=new[]{"mkv"} },
    };

    public static (string Key, string Label, string[] Containers)[] Audio =
    {
        ("aac", "AAC", new[]{"mp4","mkv","mov"}),
        ("opus", "Opus", new[]{"webm","mkv","mp4"}),
        ("flac", "FLAC (lossless)", new[]{"mkv","mp4"}),
        ("pcm", "PCM 24-bit (uncompressed)", new[]{"mov","mkv"}),
        ("none", "No audio", new[]{"mp4","mkv","mov","webm"}),
    };
}

public sealed class ExportProgress
{
    public int Frame, Total;
    public double Fps;
    public TimeSpan Elapsed, Remaining;
    public Frame Preview;
}

public static class Exporter
{
    static string SpeedPreset(VideoCodecInfo c, string speed) => c.Encoder switch
    {
        "libx264" or "libx265" => speed switch { "fast" => "veryfast", "slow" => "slow", "slowest" => "veryslow", _ => "medium" },
        "libsvtav1" => speed switch { "fast" => "10", "slow" => "5", "slowest" => "3", _ => "8" },
        _ when c.Encoder.EndsWith("nvenc") => speed switch { "fast" => "p2", "slow" => "p6", "slowest" => "p7", _ => "p4" },
        _ when c.Encoder.EndsWith("qsv") => speed switch { "fast" => "veryfast", "slow" => "slow", "slowest" => "veryslow", _ => "medium" },
        _ when c.Encoder.EndsWith("amf") => speed switch { "fast" => "speed", "slow" or "slowest" => "quality", _ => "balanced" },
        _ => ""
    };

    public static List<string> VideoArgs(ExportSettings s)
    {
        var c = s.Codec;
        var a = new List<string> { "-c:v", c.Encoder };
        bool ten = s.TenBit;
        string preset = SpeedPreset(c, s.Speed);
        string br = $"{s.BitrateKbps}k";
        switch (c.Encoder)
        {
            case "libx264":
                a.AddRange(new[] { "-preset", preset, "-pix_fmt", ten ? "yuv420p10le" : "yuv420p", "-profile:v", ten ? "high10" : "high" });
                if (s.UseBitrate) a.AddRange(new[] { "-b:v", br, "-maxrate", $"{s.BitrateKbps * 3 / 2}k", "-bufsize", $"{s.BitrateKbps * 2}k" });
                else a.AddRange(new[] { "-crf", s.Crf.ToString() });
                break;
            case "libx265":
                a.AddRange(new[] { "-preset", preset, "-pix_fmt", ten ? "yuv420p10le" : "yuv420p", "-tag:v", "hvc1", "-x265-params", "log-level=error" });
                if (s.UseBitrate) a.AddRange(new[] { "-b:v", br }); else a.AddRange(new[] { "-crf", Math.Clamp(s.Crf + 3, 0, 51).ToString() });
                break;
            case "libsvtav1":
                a.AddRange(new[] { "-preset", preset, "-pix_fmt", ten ? "yuv420p10le" : "yuv420p" });
                if (s.UseBitrate) a.AddRange(new[] { "-b:v", br }); else a.AddRange(new[] { "-crf", Math.Clamp((int)(s.Crf * 1.4 + 2), 1, 63).ToString() });
                break;
            case "libvpx-vp9":
                a.AddRange(new[] { "-pix_fmt", ten ? "yuv420p10le" : "yuv420p", "-row-mt", "1", "-deadline", "good",
                    "-cpu-used", s.Speed switch { "fast" => "5", "slow" => "1", "slowest" => "0", _ => "3" } });
                if (ten) a.AddRange(new[] { "-profile:v", "2" });
                if (s.UseBitrate) a.AddRange(new[] { "-b:v", br }); else a.AddRange(new[] { "-crf", Math.Clamp((int)(s.Crf * 1.5), 4, 63).ToString(), "-b:v", "0" });
                break;
            case "prores_ks":
                a.AddRange(new[] { "-profile:v", "3", "-pix_fmt", "yuv422p10le", "-vendor", "apl0" });
                break;
            case "ffv1":
                a.AddRange(new[] { "-level", "3", "-slices", "16", "-slicecrc", "1", "-g", "1", "-pix_fmt", ten ? "yuv444p10le" : "yuv420p" });
                break;
            default:
                if (c.Encoder.EndsWith("nvenc"))
                {
                    a.AddRange(new[] { "-preset", preset, "-pix_fmt", ten && c.Family != "h264" ? "p010le" : "yuv420p" });
                    if (s.UseBitrate) a.AddRange(new[] { "-rc", "vbr", "-b:v", br });
                    else a.AddRange(new[] { "-rc", "vbr", "-cq", Math.Clamp(s.Crf + 2, 0, 51).ToString(), "-b:v", "0" });
                    if (c.Family == "hevc") a.AddRange(new[] { "-tag:v", "hvc1" });
                }
                else if (c.Encoder.EndsWith("qsv"))
                {
                    a.AddRange(new[] { "-preset", preset, "-pix_fmt", ten && c.Family != "h264" ? "p010le" : "nv12" });
                    if (s.UseBitrate) a.AddRange(new[] { "-b:v", br }); else a.AddRange(new[] { "-global_quality", Math.Clamp(s.Crf + 2, 1, 51).ToString() });
                    if (c.Family == "hevc") a.AddRange(new[] { "-tag:v", "hvc1" });
                }
                else if (c.Encoder.EndsWith("amf"))
                {
                    a.AddRange(new[] { "-quality", preset, "-pix_fmt", ten && c.Family != "h264" ? "p010le" : "nv12" });
                    if (s.UseBitrate) a.AddRange(new[] { "-rc", "vbr_peak", "-b:v", br });
                    else { var q = Math.Clamp(s.Crf + 2, 0, 51).ToString(); a.AddRange(new[] { "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q }); }
                    if (c.Family == "hevc") a.AddRange(new[] { "-tag:v", "hvc1" });
                }
                break;
        }
        // Color metadata: we render in sRGB/BT.709.
        a.AddRange(new[] { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv" });
        return a;
    }

    public static List<string> AudioArgs(ExportSettings s)
    {
        return s.AudioCodec switch
        {
            "opus" => new() { "-c:a", "libopus", "-b:a", $"{Math.Min(s.AudioKbps, 510)}k" },
            "flac" => new() { "-c:a", "flac" },
            "pcm" => new() { "-c:a", "pcm_s24le" },
            _ => new() { "-c:a", "aac", "-b:a", $"{s.AudioKbps}k" },
        };
    }

    /// <summary>Renders and encodes the whole project. Throws on failure; returns false if cancelled.</summary>
    public static bool Run(Project project, ExportSettings s, IProgress<ExportProgress> progress, CancellationToken ct)
    {
        var p = project.Clone();
        p.FpsNum = s.FpsNum; p.FpsDen = s.FpsDen; p.Interp = s.Interp;
        var tl = p.ComputeTimeline();
        double total = p.TotalDuration(tl);
        if (total <= 0) throw new Exception("There's nothing to export yet. Add some videos or photos first.");
        int frames = Math.Max(1, (int)Math.Round(total * s.FpsNum / s.FpsDen));
        string rate = $"{s.FpsNum}/{s.FpsDen}";

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{s.Width}x{s.Height}", "-framerate", rate, "-i", "pipe:0" };
        bool audio = s.AudioCodec != "none";
        var vargs = VideoArgs(s);
        int pi = vargs.IndexOf("-pix_fmt");
        string pix = pi >= 0 ? vargs[pi + 1] : "yuv420p";
        // Convert RGB -> YUV with the BT.709 matrix explicitly (swscale defaults to BT.601).
        string vfilter = $"[0:v]scale=in_range=pc:out_range=tv:out_color_matrix=bt709,format={pix}[vout]";
        if (audio)
        {
            var g = AudioGraph.Build(p, 0, total, 1);
            args.AddRange(g.InputArgs);
            args.AddRange(new[] { "-filter_complex", vfilter + ";" + g.Filter, "-map", "[vout]", "-map", "[aout]" });
        }
        else args.AddRange(new[] { "-filter_complex", vfilter, "-map", "[vout]" });
        args.AddRange(vargs);
        args.AddRange(new[] { "-r", rate });
        if (audio) { args.AddRange(AudioArgs(s)); args.AddRange(new[] { "-ar", "48000" }); }
        if (s.Container is "mp4" or "mov") args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(s.OutputPath);

        var errors = new System.Text.StringBuilder();
        using var proc = new Process { StartInfo = FF.Psi(FF.Ffmpeg, args, stdin: true, stdout: false) };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) lock (errors) errors.AppendLine(e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();
        var stdin = proc.StandardInput.BaseStream;

        using var renderer = new Renderer(s.Width, s.Height, true, s.Interp);
        renderer.SetProject(p);
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        bool cancelled = false;
        try
        {
            for (int i = 0; i < frames; i++)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                double t = (double)i * s.FpsDen / s.FpsNum;
                var f = renderer.Render(t, i);
                try { stdin.Write(f.D, 0, f.D.Length); }
                catch (IOException)
                {
                    proc.WaitForExit(3000);
                    throw new Exception("The encoder stopped unexpectedly.\n\n" + errors.ToString().Trim());
                }
                if (sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(250) || i == frames - 1)
                {
                    lastReport = sw.Elapsed;
                    double fps = (i + 1) / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    Frame preview = null;
                    if (sw.Elapsed.TotalSeconds > 0)
                    {
                        // Small preview copy for the progress window.
                        int pw = 320, ph = Math.Max(2, (int)(320.0 * s.Height / s.Width));
                        preview = new Frame(pw, ph);
                        Compositor.DrawScaled(preview, f.D, f.W, f.H, 0, 0, pw, ph);
                    }
                    progress?.Report(new ExportProgress
                    {
                        Frame = i + 1, Total = frames, Fps = fps, Elapsed = sw.Elapsed,
                        Remaining = TimeSpan.FromSeconds((frames - i - 1) / Math.Max(0.01, fps)), Preview = preview
                    });
                }
            }
        }
        finally
        {
            try { stdin.Close(); } catch { }
        }
        if (cancelled)
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            proc.WaitForExit(5000);
            try { File.Delete(s.OutputPath); } catch { }
            return false;
        }
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new Exception("The encoder reported an error:\n\n" + errors.ToString().Trim());
        return true;
    }

    /// <summary>Render one frame at full quality (for Snapshot).</summary>
    public static Frame RenderStill(Project project, double t, int w, int h)
    {
        using var r = new Renderer(w, h, true, "drop");
        r.SetProject(project.Clone());
        var f = r.Render(t, (int)(t * project.Fps));
        var copy = new Frame(f.W, f.H);
        Compositor.Copy(copy, f);
        return copy;
    }
}
