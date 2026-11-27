namespace GrabEditor.Services;

public static class Tools
{
    /// <summary>Finds yt-dlp / ffmpeg / ffprobe: first in the app's bin folder, then on PATH.</summary>
    public static string Find(string name)
    {
        var exe = name + ".exe";
        var local = Path.Combine(AppPaths.BinDir, exe);
        if (File.Exists(local)) return local;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // bad PATH entry
            }
        }
        return null;
    }
}
