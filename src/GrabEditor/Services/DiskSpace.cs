namespace GrabEditor.Services;

public static class DiskSpace
{
    /// <summary>Free bytes on the drive that holds <paramref name="path"/>, or -1 if it can't be told.</summary>
    public static double FreeBytes(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || root.StartsWith("\\\\")) return -1;   // network paths: don't guess
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    public static string DriveName(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; }
        catch { return ""; }
    }

    /// <summary>Returns an empty string when there is enough room, otherwise a message for the user.</summary>
    public static string Check(string path, double needBytes)
    {
        var free = FreeBytes(path);
        if (free < 0) return "";
        if (free >= needBytes) return "";
        return "Not enough free space on " + DriveName(path) + ": about " + Fmt.Size(needBytes) + " needed, " + Fmt.Size(free) + " free.";
    }
}
