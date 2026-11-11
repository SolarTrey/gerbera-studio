using System.Text.Json;
using System.Text.Json.Serialization;

namespace MovieMakerReborn.Model;

public enum MediaKind { Video, Image, Audio }
public enum ClipKind { Video, Image, Title }

public class MediaRef
{
    public string Id { get; set; } = NewId();
    public string Path { get; set; } = "";
    public MediaKind Kind { get; set; }
    public double Duration { get; set; }
    /// <summary>Display width/height after container rotation and SAR are applied.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public double Sar { get; set; } = 1;
    public double Fps { get; set; }
    public bool HasAudio { get; set; }
    public bool HasVideo { get; set; }
    public string VideoCodec { get; set; } = "";

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

public class TextStyle
{
    public string Font { get; set; } = "Segoe UI";
    /// <summary>Font size as a fraction of output height.</summary>
    public double Size { get; set; } = 0.06;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public string Color { get; set; } = "#FFFFFF";
    public string OutlineColor { get; set; } = "#000000";
    /// <summary>Outline thickness as a fraction of font size (0 = none).</summary>
    public double Outline { get; set; } = 0.06;
    public bool Shadow { get; set; } = true;
    public string Align { get; set; } = "Center";   // Left, Center, Right
    public string VAlign { get; set; } = "Bottom";  // Top, Middle, Bottom
    public double Opacity { get; set; } = 1;
    public string Effect { get; set; } = "fade";
    public bool Glow { get; set; }
    public string GlowColor { get; set; } = "#FFB000";
    public string Plate { get; set; } = "none"; // none, box, bar
    public string PlateColor { get; set; } = "#000000";
    public double PlateOpacity { get; set; } = 0.6;

    public TextStyle Clone() => (TextStyle)MemberwiseClone();
    public string Key => $"{Font}|{Size:F4}|{Bold}|{Italic}|{Color}|{OutlineColor}|{Outline:F3}|{Shadow}|{Align}|{Glow}|{GlowColor}|{Plate}|{PlateColor}|{PlateOpacity:F2}";
}

public class Clip
{
    public string Id { get; set; } = MediaRef.NewId();
    public ClipKind Kind { get; set; }
    public string MediaId { get; set; }
    public double TrimIn { get; set; }
    public double TrimOut { get; set; }
    /// <summary>Duration for photos and titles.</summary>
    public double Duration { get; set; } = 7;
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public int Rotation { get; set; }
    public string Effect { get; set; } = "none";
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Saturation { get; set; }
    public string Transition { get; set; } = "none";
    public double TransitionDur { get; set; } = 1.5;
    public string Pan { get; set; } = "none";
    public string Text { get; set; } = "";
    public TextStyle Style { get; set; }
    public string BgColor { get; set; } = "#000000";
    public bool Reverse { get; set; }
    public string AudioFx { get; set; } = "none";
    public ChromaKey Key { get; set; }
    public string TitleBorder { get; set; } = "none"; // none, classic

    public double Length => Kind == ClipKind.Video ? Math.Max(0.04, (TrimOut - TrimIn) / Math.Max(0.01, Speed)) : Math.Max(0.04, Duration);
}

public class ChromaKey
{
    public bool Enabled { get; set; }
    public string Color { get; set; } = "#00FF00";
    public double Tolerance { get; set; } = 0.16;
    public double Softness { get; set; } = 0.10;
    public bool Spill { get; set; } = true;
    public string BgMediaId { get; set; }
    public string BgColor { get; set; } = "#000000";
}

public class Caption
{
    public string Id { get; set; } = MediaRef.NewId();
    public string Text { get; set; } = "Enter text here";
    public double Start { get; set; }
    public double Duration { get; set; } = 5;
    public TextStyle Style { get; set; } = new();
}

public class MusicItem
{
    public string Id { get; set; } = MediaRef.NewId();
    public string MediaId { get; set; }
    public double Start { get; set; }
    public double TrimIn { get; set; }
    public double TrimOut { get; set; }
    public double Volume { get; set; } = 1;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public string AudioFx { get; set; } = "none";
    public double Length => Math.Max(0.04, TrimOut - TrimIn);
}

public class Timeline
{
    public double[] Start = Array.Empty<double>();
    public double[] Dur = Array.Empty<double>();
    public double[] Trans = Array.Empty<double>();
    public double Total;

    /// <summary>Index of the clip that "owns" time t (the latest clip that has started).</summary>
    public int IndexAt(double t)
    {
        int idx = -1;
        for (int i = 0; i < Start.Length; i++) if (Start[i] <= t + 1e-9) idx = i;
        return idx;
    }
}

public class Project
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "My Movie";
    public int AspectW { get; set; } = 16;
    public int AspectH { get; set; } = 9;
    public int FpsNum { get; set; } = 30;
    public int FpsDen { get; set; } = 1;
    public string Interp { get; set; } = "drop"; // drop, blend, mci
    public string Fit { get; set; } = "contain"; // contain, cover, stretch
    public string Background { get; set; } = "#000000";
    /// <summary>-1 = video audio only, 0 = balanced, +1 = music only.</summary>
    public double AudioMix { get; set; }
    public List<MediaRef> Media { get; set; } = new();
    public List<Clip> Clips { get; set; } = new();
    public List<MusicItem> Music { get; set; } = new();
    public List<Caption> Captions { get; set; } = new();

    [JsonIgnore] public double Fps => (double)FpsNum / FpsDen;
    [JsonIgnore] public double Aspect => (double)AspectW / AspectH;

    public MediaRef GetMedia(string id) => Media.FirstOrDefault(m => m.Id == id);

    public Timeline ComputeTimeline()
    {
        int n = Clips.Count;
        var tl = new Timeline { Start = new double[n], Dur = new double[n], Trans = new double[n] };
        double t = 0;
        for (int i = 0; i < n; i++)
        {
            var c = Clips[i];
            double d = c.Length;
            tl.Dur[i] = d;
            double tr = 0;
            if (i > 0 && c.Transition != "none")
                tr = Math.Max(0, Math.Min(c.TransitionDur, Math.Min(d / 2, tl.Dur[i - 1] / 2)));
            tl.Trans[i] = tr;
            tl.Start[i] = i == 0 ? 0 : t - tr;
            t = tl.Start[i] + d;
        }
        tl.Total = n > 0 ? t : 0;
        return tl;
    }

    /// <summary>Total including music and captions that extend past the last clip.</summary>
    public double TotalDuration(Timeline tl)
    {
        double t = tl.Total;
        if (Clips.Count == 0)
        {
            foreach (var m in Music) t = Math.Max(t, m.Start + m.Length);
            foreach (var c in Captions) t = Math.Max(t, c.Start + c.Duration);
        }
        return t;
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static Project FromJson(string json) => JsonSerializer.Deserialize<Project>(json, JsonOpts);
    public Project Clone() => FromJson(ToJson());
}

public static class Presets
{
    public static readonly (string Key, string Label)[] Transitions =
    {
        ("none", "None"), ("crossfade", "Cross-fade"), ("fadeblack", "Fade through black"), ("fadewhite", "Fade through white"),
        ("dissolve", "Dissolve"), ("wipeleft", "Wipe left"), ("wiperight", "Wipe right"), ("wipeup", "Wipe up"), ("wipedown", "Wipe down"),
        ("softwipe", "Soft wipe"), ("diagonal", "Diagonal"), ("slideleft", "Slide left"), ("slideright", "Slide right"), ("slideup", "Slide up"),
        ("slidedown", "Slide down"), ("pushleft", "Push left"), ("pushright", "Push right"), ("zoomin", "Zoom in"), ("zoomout", "Zoom out"),
        ("iris", "Iris"), ("diamond", "Diamond"), ("clock", "Clock"), ("blinds", "Blinds"), ("checker", "Checkerboard"), ("pixelate", "Pixelate"),
        ("blurx", "Blur"),
    };

    public static readonly (string Key, string Label)[] Effects =
    {
        ("none", "No effect"),
        // Nostalgic
        ("noir", "Film noir"), ("silent", "Silent film 1:1"), ("western", "Sepia western"), ("oldfilm", "Old film"),
        ("bw", "Black and white"), ("bwred", "B&W red filter"), ("sepia", "Sepia"), ("cyan", "Cyan tone"), ("vhs", "VHS tape"),
        // Modern
        ("cinematic", "Cinematic"), ("bloom", "Dreamy glow"), ("rgbsplit", "Chromatic"), ("glitch", "Glitch"), ("duotone", "Duotone"),
        ("cross", "Cross process"), ("vivid", "Vivid"), ("faded", "Faded matte"), ("warm", "Warm"), ("cool", "Cool"), ("sharpen", "Sharpen"),
        // Stylized
        ("negative", "Negative"), ("posterize", "Posterize"), ("threshold", "Threshold"), ("pixelate", "Pixelate"), ("blur", "Blur"),
        ("soft", "Soft focus"), ("vignette", "Edge darken"), ("night", "Night vision"), ("mirror", "Mirror"), ("grain", "Film grain"),
    };

    public static readonly (string Key, string Label)[] AudioFx =
    {
        ("none", "No audio effect"), ("match", "Match visual effect"),
        ("deep", "Deep voice"), ("chipmunk", "High voice"), ("robot", "Robot"), ("echo", "Echo"), ("hall", "Big hall"),
        ("telephone", "Telephone"), ("radio", "Radio"), ("megaphone", "Megaphone"), ("underwater", "Underwater"), ("lofi", "Lo-fi"),
        ("noir", "1940s noir microphone"), ("silent", "Silent-era projector"), ("western", "Dusty gramophone"), ("vhs", "VHS tape"),
    };

    /// <summary>Audio degradation that pairs with a visual effect (for "Match visual effect").</summary>
    public static string MatchingAudio(string visual) => visual switch
    {
        "noir" or "bw" or "bwred" => "noir",
        "silent" or "oldfilm" => "silent",
        "western" or "sepia" => "western",
        "vhs" or "glitch" or "rgbsplit" => "vhs",
        "night" => "radio",
        "faded" or "cross" or "posterize" => "lofi",
        _ => "none"
    };

    public static readonly (string Key, string Label)[] TitleStyles =
    {
        ("classic", "Classic"), ("clean", "Clean"), ("kinetic", "Kinetic"), ("lowerthird", "Lower third"), ("neon", "Neon"), ("glitch", "Glitch"),
        ("silent", "Silent film card"), ("noir", "Film noir"), ("western", "Western poster"), ("typewriter", "Typewriter"),
    };

    public static readonly (string Key, string Label, string Glyph)[] Pans =
    {
        ("none", "None", ""), ("zoomin", "Zoom in", ""), ("zoomout", "Zoom out", ""),
        ("panleft", "Pan left", ""), ("panright", "Pan right", ""), ("panup", "Pan up", ""), ("pandown", "Pan down", ""),
        ("zoomleft", "Zoom in left", ""), ("zoomright", "Zoom in right", ""), ("auto", "Automatic", ""),
    };

    public static readonly (string Key, string Label)[] TextEffects =
    {
        ("none", "None"), ("fade", "Fade"), ("flyleft", "Fly in left"), ("flyright", "Fly in right"), ("flyup", "Fly in bottom"),
        ("zoomin", "Zoom in"), ("pop", "Pop"), ("typewriter", "Typewriter"), ("scroll", "Scroll"), ("slowzoom", "Cinematic zoom"),
        ("swing", "Swing up"), ("wipe", "Wipe reveal"), ("stamp", "Stamp"), ("glitch", "Glitch"), ("flicker", "Projector flicker"),
    };

    public static readonly (int Num, int Den, string Label)[] FrameRates =
    {
        (24000, 1001, "23.976 fps (film NTSC)"), (24, 1, "24 fps (film)"), (25, 1, "25 fps (PAL)"), (30000, 1001, "29.97 fps (NTSC)"),
        (30, 1, "30 fps"), (48, 1, "48 fps (HFR film)"), (50, 1, "50 fps"), (60000, 1001, "59.94 fps"), (60, 1, "60 fps"),
        (90, 1, "90 fps"), (100, 1, "100 fps"), (120000, 1001, "119.88 fps"), (120, 1, "120 fps"), (144, 1, "144 fps"), (240, 1, "240 fps"),
    };

    public static readonly (string Key, string Label)[] Interps =
    {
        ("drop", "Drop / duplicate frames (fast)"), ("blend", "Frame blending (smooth)"), ("mci", "Motion interpolation (slowest, smoothest)"),
    };
}
