namespace GrabEditor.Services;

public static class AppPaths
{
    public static readonly string BaseDir = ResolveBaseDir();

    public static string DataDir => Path.Combine(BaseDir, "data");
    public static string BinDir => Path.Combine(BaseDir, "bin");
    public static string TempDir => Path.Combine(DataDir, "tmp");
    public static string CacheDir => Path.Combine(DataDir, "cache");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    static string ResolveBaseDir()
    {
        var p = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(p))
        {
            var d = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(d)) return d;
        }
        return AppContext.BaseDirectory;
    }

    public static bool EnsureDataDir(out string error)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var test = Path.Combine(DataDir, ".write-test");
            File.WriteAllText(test, "ok");
            File.Delete(test);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
