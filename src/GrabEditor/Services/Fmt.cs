using System.Globalization;

namespace GrabEditor.Services;

public static class Fmt
{
    public static string Size(double n)
    {
        if (n <= 0) return "";
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (n >= 1024 && i < 3) { n /= 1024; i++; }
        var num = i < 2 ? Math.Round(n).ToString("0", CultureInfo.InvariantCulture)
                        : n.ToString("0.0", CultureInfo.InvariantCulture);
        return num + " " + units[i];
    }

    /// <summary>m:ss or h:mm:ss</summary>
    public static string Time(double s)
    {
        if (double.IsNaN(s) || s < 0) s = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(s));
        return t.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", t.Minutes, t.Seconds);
    }

    /// <summary>m:ss.f (one decimal) for the trim boxes.</summary>
    public static string TimeTenths(double s)
    {
        if (double.IsNaN(s) || s < 0) s = 0;
        var whole = Math.Floor(s);
        var tenth = (int)Math.Floor((s - whole) * 10);
        return Time(whole) + "." + tenth.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>hh:mm:ss.fff for yt-dlp / ffmpeg.</summary>
    public static string Hms(double s)
    {
        if (s < 0) s = 0;
        var t = TimeSpan.FromSeconds(s);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}",
            (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
    }

    /// <summary>Parses "90", "1:30", "1:30.5" or "1:02:03".</summary>
    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }
}
