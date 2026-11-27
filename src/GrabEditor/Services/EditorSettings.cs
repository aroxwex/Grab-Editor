using System.Text.Json;

namespace GrabEditor.Services;

public class EditorSettingsData
{
    public string ProjectDir { get; set; } = "";
    public string ExportDir { get; set; } = "";
    public string DownloadDir { get; set; } = "";
    public int ExportHeight { get; set; } = 1080;        // 1080 | 720 | 480 | 0 (= Discord size)
    public string Quality { get; set; } = "balanced";    // fast | balanced | high
    public bool Snap { get; set; } = true;
    public List<string> Recent { get; set; } = new List<string>();
}

public static class EditorSettings
{
    public static EditorSettingsData Current { get; private set; } = new EditorSettingsData();

    public static string VideosFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Grab Editor");

    public static void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<EditorSettingsData>(File.ReadAllText(AppPaths.SettingsFile));
                if (s != null) Current = s;
            }
        }
        catch
        {
            // keep defaults
        }
        if (string.IsNullOrEmpty(Current.ExportDir)) Current.ExportDir = VideosFolder;
        if (string.IsNullOrEmpty(Current.DownloadDir)) Current.DownloadDir = Path.Combine(VideosFolder, "Downloads");
        if (string.IsNullOrEmpty(Current.ProjectDir)) Current.ProjectDir = VideosFolder;
        if (Current.Recent == null) Current.Recent = new List<string>();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.SettingsFile,
                JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // not critical
        }
    }

    public static void AddRecent(string path)
    {
        Current.Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Current.Recent.Insert(0, path);
        if (Current.Recent.Count > 6) Current.Recent.RemoveRange(6, Current.Recent.Count - 6);
        Save();
    }
}
