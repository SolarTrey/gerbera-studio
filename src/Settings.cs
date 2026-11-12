using System.Text.Json;

namespace MovieMakerReborn;

public class ExportProfile
{
    public int ShortSide { get; set; } = 2160; // 0 = custom size
    public int Width { get; set; } = 3840;
    public int Height { get; set; } = 2160;
    public bool UseProjectFps { get; set; } = true;
    public int FpsNum { get; set; } = 30;
    public int FpsDen { get; set; } = 1;
    public string Interp { get; set; } = "drop";
    public string Codec { get; set; } = "h264";
    public string Container { get; set; } = "mp4";
    public bool UseBitrate { get; set; }
    public int Crf { get; set; } = 18;
    public string BitrateMbps { get; set; } = "45";
    public string Speed { get; set; } = "medium";
    public bool TenBit { get; set; }
    public string Audio { get; set; } = "aac";
    public int AudioKbps { get; set; } = 256;
}

public class Settings
{
    public ExportProfile DefaultExport { get; set; }
    public string Theme { get; set; } = "dark";
    public bool LabSplash { get; set; }
    public bool LabSounds { get; set; } = true;
    /// <summary>Version whose "What's new" was last shown; null for settings saved by 1.0.</summary>
    public string LastSeenVersion { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsFirstRun { get; private set; }
    public List<string> Recent { get; set; } = new();
    public string LastMediaFolder { get; set; }
    public string LastProjectFolder { get; set; }
    public string LastExportFolder { get; set; }
    public string FfmpegPath { get; set; }
    public int PreviewHeight { get; set; } = 540;
    public double PreviewMaxHeight { get; set; } = 10000;
    public double ThumbSize { get; set; } = 64;
    public double Zoom { get; set; } = 0.36;
    public string Timecode { get; set; } = "seconds";
    // Last export choices
    public int ExportShortSide { get; set; } = 2160;
    public string ExportCodec { get; set; } = "h264";
    public string ExportContainer { get; set; } = "mp4";
    public int ExportCrf { get; set; } = 18;
    public string ExportSpeed { get; set; } = "medium";
    public bool ExportTenBit { get; set; }
    public string ExportAudio { get; set; } = "aac";
    public int ExportAudioKbps { get; set; } = 256;

    static string FilePath
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MovieMakerReborn");
            Directory.CreateDirectory(d);
            return Path.Combine(d, "settings.json");
        }
    }

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { }
        return new Settings { IsFirstRun = true };
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    public void AddRecent(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > 10) Recent.RemoveRange(10, Recent.Count - 10);
    }
}
