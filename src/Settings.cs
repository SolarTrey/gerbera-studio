using System.Text.Json;

namespace MovieMakerReborn;

public class Settings
{
    public string Theme { get; set; } = "dark";
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
        return new Settings();
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
