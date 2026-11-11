using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MovieMakerReborn.Model;

namespace MovieMakerReborn.Engine;

/// <summary>
/// Storyboard thumbnails, cached per source file at fixed time slots, so splitting/trimming reuses them instantly.
/// Missing slots are filled by fast keyframe seeks, several at once; until then the nearest cached slot is shown.
/// </summary>
public static class Thumbs
{
    public const int TW = 128, TH = 72;
    static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, BitmapSource>> Cache = new();
    static readonly ConcurrentDictionary<string, byte> Pending = new();
    static readonly SemaphoreSlim Gate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
    public static event Action Updated;
    static int _updateQueued;

    static double Step(MediaRef m) => m.Kind == MediaKind.Video ? Math.Max(1.0, m.Duration / 80.0) : 1;
    static string MediaKey(MediaRef m, int rot) => $"{m.Path}|{rot}";

    /// <summary>Thumbnails across the clip. Entries may be null while loading.</summary>
    public static BitmapSource[] Get(MediaRef m, Clip c)
    {
        if (m == null || FF.Ffmpeg == null || !File.Exists(m.Path)) return null;
        int count = c.Kind == ClipKind.Video ? Math.Clamp((int)Math.Ceiling(c.Length / 2.5), 1, 10) : 1;
        var key = MediaKey(m, c.Rotation);
        var slots = Cache.GetOrAdd(key, _ => new ConcurrentDictionary<int, BitmapSource>());
        var res = new BitmapSource[count];
        double step = Step(m);
        double len = Math.Max(0.04, c.TrimOut - c.TrimIn);
        for (int i = 0; i < count; i++)
        {
            int b = 0;
            if (c.Kind == ClipKind.Video)
            {
                double t = c.TrimIn + (i + 0.5) * len / count;
                b = (int)Math.Round(t / step);
            }
            if (slots.TryGetValue(b, out var bmp)) { res[i] = bmp; continue; }
            Request(m, c.Rotation, key, b, step);
            // Placeholder: nearest slot already cached for this file.
            BitmapSource best = null; int bestD = int.MaxValue;
            foreach (var kv in slots) { int d = Math.Abs(kv.Key - b); if (d < bestD) { bestD = d; best = kv.Value; } }
            res[i] = best;
        }
        return res;
    }

    static void Request(MediaRef m, int rot, string key, int bucket, double step)
    {
        string pk = key + "#" + bucket;
        if (!Pending.TryAdd(pk, 0)) return;
        string path = m.Path; string sar = FF.SarFix(m); bool video = m.Kind == MediaKind.Video; double dur = m.Duration;
        Task.Run(async () =>
        {
            await Gate.WaitAsync();
            try
            {
                double t = Math.Clamp(bucket * step, 0, Math.Max(0, dur - 0.1));
                string fit = $"{sar}{FF.RotateFilter(rot)}scale={TW}:{TH}:force_original_aspect_ratio=increase,crop={TW}:{TH},format=bgra";
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
                if (video) args.AddRange(new[] { "-ss", FF.F(t) });
                args.AddRange(new[] { "-i", path, "-an", "-frames:v", "1", "-vf", fit, "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" });
                var bytes = FF.RunBytes(args, 20000);
                if (bytes.Length < TW * TH * 4 && video && t > 0)
                {
                    args[args.IndexOf("-ss") + 1] = "0";
                    bytes = FF.RunBytes(args, 20000);
                }
                if (bytes.Length >= TW * TH * 4)
                {
                    var bmp = BitmapSource.Create(TW, TH, 96, 96, PixelFormats.Bgra32, null, bytes, TW * 4);
                    bmp.Freeze();
                    Cache.GetOrAdd(key, _ => new ConcurrentDictionary<int, BitmapSource>())[bucket] = bmp;
                    if (Interlocked.Exchange(ref _updateQueued, 1) == 0)
                        _ = Task.Delay(60).ContinueWith(_ => { Interlocked.Exchange(ref _updateQueued, 0); Updated?.Invoke(); });
                }
            }
            catch { }
            finally { Gate.Release(); Pending.TryRemove(pk, out _); }
        });
    }

    /// <summary>Raw BGRA of a thumbnail (for gallery previews).</summary>
    public static byte[] Pixels(BitmapSource b)
    {
        var data = new byte[b.PixelWidth * b.PixelHeight * 4];
        b.CopyPixels(data, b.PixelWidth * 4, 0);
        return data;
    }
}
