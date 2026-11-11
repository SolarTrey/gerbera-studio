using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MovieMakerReborn.Model;

namespace MovieMakerReborn.Engine;

/// <summary>Locating and invoking ffmpeg / ffprobe.</summary>
public static class FF
{
    public static string Ffmpeg { get; private set; }
    public static string Ffprobe { get; private set; }
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string AppDataDir
    {
        get
        {
            var d = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MovieMakerReborn");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static bool Locate(string preferred = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(preferred)) candidates.Add(preferred);
        var b = AppContext.BaseDirectory;
        candidates.Add(System.IO.Path.Combine(b, "ffmpeg", "ffmpeg.exe"));
        candidates.Add(System.IO.Path.Combine(b, "ffmpeg", "bin", "ffmpeg.exe"));
        candidates.Add(System.IO.Path.Combine(b, "ffmpeg.exe"));
        candidates.Add(System.IO.Path.Combine(AppDataDir, "ffmpeg", "bin", "ffmpeg.exe"));
        candidates.Add(System.IO.Path.Combine(AppDataDir, "ffmpeg", "ffmpeg.exe"));
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(System.IO.Path.Combine(p.Trim('"'), "ffmpeg.exe")); } catch { }
        }
        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c))
                {
                    var probe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(c)!, "ffprobe.exe");
                    if (File.Exists(probe)) { Ffmpeg = c; Ffprobe = probe; return true; }
                }
            }
            catch { }
        }
        return false;
    }

    public static ProcessStartInfo Psi(string exe, IEnumerable<string> args, bool stdin = false, bool stdout = true)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = stdout,
            RedirectStandardError = true,
            RedirectStandardInput = stdin,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static string F(double v) => v.ToString("0.######", Inv);

    /// <summary>Run a process to completion, returning stdout text.</summary>
    public static (int code, string stdout, string stderr) Run(string exe, IEnumerable<string> args, int timeoutMs = 60000)
    {
        using var p = Process.Start(Psi(exe, args))!;
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } }
        return (p.HasExited ? p.ExitCode : -1, outTask.Result, errTask.Result);
    }

    /// <summary>Run ffmpeg and read exactly the raw bytes it writes to stdout.</summary>
    public static byte[] RunBytes(IEnumerable<string> args, int timeoutMs = 60000)
    {
        using var p = Process.Start(Psi(Ffmpeg, args))!;
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        if (!copy.Wait(timeoutMs)) { try { p.Kill(true); } catch { } }
        p.WaitForExit(2000);
        return ms.ToArray();
    }

    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".jxl", ".jfif", ".tga", ".dng", ".exr", ".gif", ".ico", ".psd" };

    public static readonly string[] VideoExts = { "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "asf", "mts", "m2ts", "ts", "mpg", "mpeg", "vob", "flv", "3gp", "3g2", "ogv", "mxf", "dv", "hevc", "h264", "y4m", "gif" };
    public static readonly string[] PhotoExts = { "jpg", "jpeg", "jfif", "png", "bmp", "tif", "tiff", "webp", "heic", "heif", "avif", "jxl", "tga", "dng", "exr", "psd" };
    public static readonly string[] AudioExts = { "mp3", "wav", "m4a", "aac", "flac", "ogg", "oga", "opus", "wma", "aif", "aiff", "ac3", "eac3", "alac", "mka", "amr" };

    public static MediaRef Probe(string path)
    {
        var (code, json, err) = Run(Ffprobe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, 30000);
        if (code != 0 || string.IsNullOrWhiteSpace(json)) throw new Exception($"Couldn't read \"{System.IO.Path.GetFileName(path)}\".\n{err.Trim()}");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var m = new MediaRef { Path = path };
        double fmtDur = 0;
        if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var fd))
            double.TryParse(fd.GetString(), NumberStyles.Float, Inv, out fmtDur);

        JsonElement? vs = null, aus = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var ct) ? ct.GetString() : "";
                bool attached = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && !attached && vs == null) vs = s;
                if (type == "audio" && aus == null) aus = s;
            }
        }
        var ext = System.IO.Path.GetExtension(path);
        bool isImage = ImageExt.Contains(ext) && !(ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) && fmtDur > 0.2);
        m.HasAudio = aus != null;
        m.HasVideo = vs != null;
        if (vs is JsonElement v)
        {
            int w = v.TryGetProperty("width", out var we) ? we.GetInt32() : 0;
            int h = v.TryGetProperty("height", out var he) ? he.GetInt32() : 0;
            m.VideoCodec = v.TryGetProperty("codec_name", out var cn) ? cn.GetString() : "";
            double sar = 1;
            if (v.TryGetProperty("sample_aspect_ratio", out var sarE)) sar = ParseRatio(sarE.GetString(), 1);
            if (sar <= 0 || double.IsNaN(sar)) sar = 1;
            m.Sar = sar;
            double rot = 0;
            if (v.TryGetProperty("side_data_list", out var sdl))
                foreach (var sd in sdl.EnumerateArray())
                    if (sd.TryGetProperty("rotation", out var r)) rot = r.GetDouble();
            if (rot == 0 && v.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rt))
                double.TryParse(rt.GetString(), NumberStyles.Float, Inv, out rot);
            int dispW = (int)Math.Round(w * sar);
            int ri = ((int)Math.Round(rot) % 360 + 360) % 360;
            if (ri == 90 || ri == 270) { m.Width = h; m.Height = dispW; } else { m.Width = dispW; m.Height = h; }
            m.Fps = v.TryGetProperty("avg_frame_rate", out var afr) ? ParseRatio(afr.GetString(), 0) : 0;
            if (m.Fps <= 0 && v.TryGetProperty("r_frame_rate", out var rfr)) m.Fps = ParseRatio(rfr.GetString(), 30);
            double sd2 = 0;
            if (v.TryGetProperty("duration", out var vd)) double.TryParse(vd.GetString(), NumberStyles.Float, Inv, out sd2);
            m.Duration = fmtDur > 0 ? fmtDur : sd2;
            m.Kind = isImage || m.Duration <= 0.05 ? MediaKind.Image : MediaKind.Video;
        }
        else if (aus != null)
        {
            m.Kind = MediaKind.Audio;
            m.Duration = fmtDur;
        }
        else throw new Exception($"\"{System.IO.Path.GetFileName(path)}\" has no video or audio that can be used.");
        if (m.Kind == MediaKind.Image) { m.HasAudio = false; m.Duration = 0; }
        if (m.Kind == MediaKind.Video && m.Duration <= 0) m.Duration = 1;
        return m;
    }

    public static double ParseRatio(string s, double fallback)
    {
        if (string.IsNullOrEmpty(s)) return fallback;
        var parts = s.Split(new[] { '/', ':' });
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, Inv, out var a) && double.TryParse(parts[1], NumberStyles.Float, Inv, out var b) && b != 0)
            return a / b;
        return double.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : fallback;
    }

    /// <summary>ffmpeg filter fragment for user rotation (clockwise degrees).</summary>
    public static string RotateFilter(int rot) => (((rot % 360) + 360) % 360) switch
    {
        90 => "transpose=1,",
        180 => "hflip,vflip,",
        270 => "transpose=2,",
        _ => ""
    };

    public static string SarFix(MediaRef m) => Math.Abs(m.Sar - 1) > 0.01 ? "scale=trunc(iw*sar/2)*2:ih,setsar=1," : "";

    /// <summary>Displayed size after user rotation.</summary>
    public static (int w, int h) RotatedSize(MediaRef m, int rot)
    {
        int r = ((rot % 360) + 360) % 360;
        return r == 90 || r == 270 ? (m.Height, m.Width) : (m.Width, m.Height);
    }

    static HashSet<string> _encoders;
    public static HashSet<string> Encoders
    {
        get
        {
            if (_encoders != null) return _encoders;
            var set = new HashSet<string>();
            try
            {
                var (_, o, _) = Run(Ffmpeg, new[] { "-hide_banner", "-encoders" }, 15000);
                foreach (var line in o.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length > 8 && (t[0] == 'V' || t[0] == 'A'))
                    {
                        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 1) set.Add(parts[1]);
                    }
                }
            }
            catch { }
            return _encoders = set;
        }
    }

    static readonly Dictionary<string, bool> HwWorks = new();
    /// <summary>Hardware encoders are compiled in even without the GPU; try a tiny encode to be sure.</summary>
    public static bool EncoderWorks(string enc)
    {
        lock (HwWorks) if (HwWorks.TryGetValue(enc, out var ok)) return ok;
        bool works;
        if (!Encoders.Contains(enc)) works = false;
        else if (!(enc.Contains("nvenc") || enc.Contains("qsv") || enc.Contains("amf"))) works = true;
        else
        {
            try
            {
                var pix = enc.Contains("qsv") || enc.Contains("amf") ? "nv12" : "yuv420p";
                var (code, _, _) = Run(Ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30:d=0.2",
                    "-pix_fmt", pix, "-c:v", enc, "-f", "null", "-" }, 20000);
                works = code == 0;
            }
            catch { works = false; }
        }
        lock (HwWorks) HwWorks[enc] = works;
        return works;
    }

    public static string Version()
    {
        try
        {
            var (_, o, _) = Run(Ffmpeg, new[] { "-hide_banner", "-version" }, 10000);
            return o.Split('\n').FirstOrDefault()?.Trim() ?? "";
        }
        catch { return ""; }
    }

    public static string Quote(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var a in args) sb.Append(a.Contains(' ') ? $"\"{a}\" " : a + " ");
        return sb.ToString();
    }
}
