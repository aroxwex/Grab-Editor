using System.Security.Cryptography;
using System.Text.Json;
using GrabEditor.Models;

namespace GrabEditor.Services;

/// <summary>
/// Looks inside video files, makes thumbnails and small "proxy" copies for smooth playback.
/// Everything made here lives in data/cache and is only for preview: exports always use the originals.
/// </summary>
public static class MediaService
{
    public static string CacheDir(string id) => Path.Combine(AppPaths.CacheDir, id);
    public static string ThumbPath(string id) => Path.Combine(CacheDir(id), "thumb.jpg");
    public static string ProxyPath(string id) => Path.Combine(CacheDir(id), "proxy.mp4");

    public static string MakeId(string path)
    {
        var fi = new FileInfo(path);
        var raw = path.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
    }

    static string Str(JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    static double Num(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return 0;
    }

    /// <summary>Reads length, size and streams. Returns null when the file has no picture or can't be read.</summary>
    public static async Task<MediaItem> ProbeAsync(string path)
    {
        var ffprobe = Tools.Find("ffprobe");
        if (ffprobe == null || !File.Exists(path)) return null;
        try
        {
            var r = await Proc.RunAsync(ffprobe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path });
            if (r.Code != 0) return null;
            using var doc = JsonDocument.Parse(r.Out);
            var root = doc.RootElement;

            string vcodec = "", acodec = "";
            int w = 0, h = 0;
            bool hasV = false, hasA = false;
            double streamDuration = 0;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = Str(s, "codec_type");
                    if (type == "video" && !hasV)
                    {
                        if (s.TryGetProperty("disposition", out var disp) && Num(disp, "attached_pic") > 0) continue;   // cover art
                        hasV = true;
                        vcodec = Str(s, "codec_name");
                        w = (int)Num(s, "width");
                        h = (int)Num(s, "height");
                        streamDuration = Num(s, "duration");
                    }
                    else if (type == "audio" && !hasA)
                    {
                        hasA = true;
                        acodec = Str(s, "codec_name");
                    }
                }
            }
            if (!hasV) return null;

            var duration = root.TryGetProperty("format", out var fmt) ? Num(fmt, "duration") : 0;
            if (duration <= 0) duration = streamDuration;
            if (duration <= 0) return null;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            var nativeContainer = ext == ".mp4" || ext == ".m4v" || ext == ".mov";
            var nativeVideo = vcodec == "h264";
            var nativeAudio = acodec == "" || acodec == "aac" || acodec == "mp3";

            return new MediaItem
            {
                Id = MakeId(path),
                Path = path,
                Name = Path.GetFileName(path),
                Duration = duration,
                HasAudio = hasA,
                Width = w,
                Height = h,
                NeedsProxy = !(nativeContainer && nativeVideo && nativeAudio) || w > 3840,
            };
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> MakeThumbAsync(MediaItem m)
    {
        var thumb = ThumbPath(m.Id);
        if (File.Exists(thumb)) return true;
        var ff = Tools.Find("ffmpeg");
        if (ff == null || !File.Exists(m.Path)) return false;
        try
        {
            Directory.CreateDirectory(CacheDir(m.Id));
            var at = Math.Min(1.0, m.Duration / 3);
            var r = await Proc.RunAsync(ff, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error", "-ss", Fmt.Hms(at), "-i", m.Path,
                "-frames:v", "1", "-vf", "scale=-2:120", "-q:v", "4", thumb,
            });
            return r.Code == 0 && File.Exists(thumb);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Where the player should read this clip from. Empty while a proxy is still being made.</summary>
    public static string PreviewPath(MediaItem m)
    {
        if (m == null) return "";
        var proxy = ProxyPath(m.Id);
        if (File.Exists(proxy)) return proxy;
        return m.NeedsProxy || !File.Exists(m.Path) ? "" : m.Path;
    }

    /// <summary>Makes a small 540p copy for smooth playback. Returns an empty string on success, otherwise the reason.</summary>
    public static async Task<string> MakeProxyAsync(MediaItem m, CancellationToken ct, Action<double?> progress)
    {
        var proxy = ProxyPath(m.Id);
        if (File.Exists(proxy)) return "";
        var ff = Tools.Find("ffmpeg");
        if (ff == null) return "ffmpeg not found.";
        if (!File.Exists(m.Path)) return "The file is missing.";

        var needed = m.Duration * 250000.0 + 300000000.0;
        var free = DiskSpace.FreeBytes(AppPaths.CacheDir);
        if (free >= 0 && free < needed)
            return "Not enough free disk space for the preview copy (about " + Fmt.Size(needed) + " needed, " + Fmt.Size(free) + " free).";

        var part = proxy + ".part.mp4";
        try
        {
            Directory.CreateDirectory(CacheDir(m.Id));
            var args = new List<string>
            {
                "-y", "-hide_banner", "-nostats", "-progress", "pipe:1", "-i", m.Path,
                "-vf", "scale=-2:540", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-movflags", "+faststart", part,
            };
            using var p = Process.Start(Proc.Info(ff, args));
            using var reg = ct.Register(() => Proc.Kill(p));
            var errTask = p.StandardError.ReadToEndAsync();
            string line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                if (!line.StartsWith("out_time_us=")) continue;
                if (double.TryParse(line.Substring(12), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var us) && m.Duration > 0)
                    progress?.Invoke(Math.Min(us / 1000000.0 / m.Duration, 0.99));
            }
            await p.WaitForExitAsync();
            await errTask;
            if (ct.IsCancellationRequested) { try { File.Delete(part); } catch { } return "Cancelled."; }
            if (p.ExitCode != 0 || !File.Exists(part)) { try { File.Delete(part); } catch { } return "Couldn't make the preview copy."; }
            File.Move(part, proxy, true);
            return "";
        }
        catch (Exception ex)
        {
            try { File.Delete(part); } catch { }
            return ex.Message;
        }
    }

    /// <summary>Removes cache folders that haven't been touched for a while.</summary>
    public static void CleanOldCache(int days)
    {
        try
        {
            if (!Directory.Exists(AppPaths.CacheDir)) return;
            foreach (var dir in Directory.GetDirectories(AppPaths.CacheDir))
            {
                if (DateTime.Now - Directory.GetLastWriteTime(dir) > TimeSpan.FromDays(days))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    public static void TouchCache(string id)
    {
        try { var d = CacheDir(id); if (Directory.Exists(d)) Directory.SetLastWriteTime(d, DateTime.Now); } catch { }
    }

    public static double CacheBytes()
    {
        double total = 0;
        try
        {
            if (!Directory.Exists(AppPaths.CacheDir)) return 0;
            foreach (var f in new DirectoryInfo(AppPaths.CacheDir).GetFiles("*", SearchOption.AllDirectories)) total += f.Length;
        }
        catch { }
        return total;
    }

    public static void ClearCache()
    {
        try { if (Directory.Exists(AppPaths.CacheDir)) Directory.Delete(AppPaths.CacheDir, true); } catch { }
    }
}
