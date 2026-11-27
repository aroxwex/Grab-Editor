using System.Globalization;
using GrabEditor.Models;

namespace GrabEditor.Services;

public class ExportOptions
{
    public string Output = "";
    public int Height = 1080;            // 0 means "fit a size limit"
    public string Quality = "balanced";  // fast | balanced | high
    public double TargetMb = 0;          // > 0: aim for this file size (Discord)
    public int Fps = 30;
}

public class ExportPlan
{
    public List<string> Args = new List<string>();
    public double Duration;
    public string Error = "";
    public int Width;
    public int Height;
}

/// <summary>Turns the timeline into one ffmpeg command: cut, speed, volume, fades, fit, text, join.</summary>
public static class ExportBuilder
{
    static string N(double v)
    {
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    static int Even(double v)
    {
        var i = (int)Math.Round(v);
        return i % 2 == 0 ? i : i + 1;
    }

    /// <summary>One atempo filter only works between 0.5 and 2, so bigger changes are chained.</summary>
    static string Atempo(double speed)
    {
        if (Math.Abs(speed - 1.0) < 0.001) return "";
        var parts = new List<string>();
        var s = speed;
        while (s > 2.0) { parts.Add("atempo=2"); s /= 2.0; }
        while (s < 0.5) { parts.Add("atempo=0.5"); s /= 0.5; }
        parts.Add("atempo=" + N(s));
        return string.Join(",", parts);
    }

    static string Volume(double v)
    {
        return v > 1.0 ? "volume=" + N(v) + ",alimiter=limit=0.95" : "volume=" + N(v);
    }

    /// <summary>Escapes a path for use inside a quoted ffmpeg filter option.</summary>
    static string FfPath(string p)
    {
        return p.Replace('\\', '/').Replace(":", "\\:").Replace("'", "'\\''");
    }

    static string FindFont()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var name in new[] { "segoeuib.ttf", "segoeui.ttf", "arialbd.ttf", "arial.ttf", "tahoma.ttf" })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    static string Hex(string color)
    {
        var c = (color ?? "#FFFFFF").TrimStart('#');
        return c.Length == 6 ? "0x" + c : "0xFFFFFF";
    }

    public static ExportPlan Build(Project p, ExportOptions o, string workDir)
    {
        var plan = new ExportPlan();
        if (p.Clips.Count == 0)
        {
            plan.Error = "The timeline is empty.";
            return plan;
        }

        p.Layout();
        plan.Duration = p.Duration;

        var height = o.Height > 0 ? o.Height : 720;
        var h = Even(height);
        var w = Even(height * p.AspectRatio);
        plan.Width = w;
        plan.Height = h;

        var graph = new StringBuilder();
        var inputs = new List<string>();

        for (var i = 0; i < p.Clips.Count; i++)
        {
            var c = p.Clips[i];
            var m = p.MediaOf(c);
            if (m == null || !File.Exists(m.Path))
            {
                plan.Error = "A file is missing: " + (m != null ? m.Name : "unknown") + ". Put it back where it was, or remove the clip.";
                return plan;
            }
            inputs.Add(m.Path);
            var len = c.Length;
            var speed = c.Speed > 0 ? c.Speed : 1.0;

            // picture: cut, change speed, fit into the frame, same frame rate and format for every clip
            graph.Append("[" + i + ":v:0]trim=start=" + N(c.In) + ":end=" + N(c.Out));
            graph.Append(",setpts=(PTS-STARTPTS)/" + N(speed));
            graph.Append(",fps=" + o.Fps);
            if (c.Fill)
                graph.Append(",scale=" + w + ":" + h + ":force_original_aspect_ratio=increase,crop=" + w + ":" + h);
            else
                graph.Append(",scale=" + w + ":" + h + ":force_original_aspect_ratio=decrease,pad=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2:black");
            graph.Append(",setsar=1,format=yuv420p");
            if (c.FadeIn > 0.01) graph.Append(",fade=t=in:st=0:d=" + N(c.FadeIn));
            if (c.FadeOut > 0.01) graph.Append(",fade=t=out:st=" + N(Math.Max(0, len - c.FadeOut)) + ":d=" + N(c.FadeOut));
            graph.Append("[v" + i + "];");

            // sound: same cut and speed, volume, fades. Clips without sound get silence so everything stays in step.
            var audio = new List<string>();
            if (m.HasAudio)
            {
                audio.Add("[" + i + ":a:0]atrim=start=" + N(c.In) + ":end=" + N(c.Out));
                audio.Add("asetpts=PTS-STARTPTS");
                var tempo = Atempo(speed);
                if (tempo != "") audio.Add(tempo);
                if (Math.Abs(c.Volume - 1.0) > 0.001) audio.Add(Volume(c.Volume));
            }
            else
            {
                audio.Add("anullsrc=r=48000:cl=stereo:d=" + N(len));
            }
            if (c.FadeIn > 0.01) audio.Add("afade=t=in:st=0:d=" + N(c.FadeIn));
            if (c.FadeOut > 0.01) audio.Add("afade=t=out:st=" + N(Math.Max(0, len - c.FadeOut)) + ":d=" + N(c.FadeOut));
            audio.Add("aresample=48000");
            audio.Add("aformat=sample_fmts=fltp:channel_layouts=stereo");
            graph.Append(string.Join(",", audio));
            graph.Append("[a" + i + "];");
        }

        for (var i = 0; i < p.Clips.Count; i++) graph.Append("[v" + i + "][a" + i + "]");
        graph.Append("concat=n=" + p.Clips.Count + ":v=1:a=1[vc][aout];");

        // text on top of the joined picture
        var drawtexts = new List<string>();
        var font = FindFont();
        var textIndex = 0;
        foreach (var t in p.Texts)
        {
            if (string.IsNullOrWhiteSpace(t.Text) || t.Start >= p.Duration) continue;
            if (font == null)
            {
                plan.Error = "No font was found on this PC, so text can't be added to the video.";
                return plan;
            }
            var file = Path.Combine(workDir, "text" + (textIndex++) + ".txt");
            File.WriteAllText(file, t.Text, new UTF8Encoding(false));
            var size = Math.Max(8, (int)Math.Round(h * t.Size / 100.0));
            string y;
            switch (t.Position)
            {
                case "top": y = "h*0.08"; break;
                case "center": y = "(h-text_h)/2"; break;
                default: y = "h-text_h-h*0.08"; break;
            }
            var look = t.Box
                ? "box=1:boxcolor=black@0.55:boxborderw=" + Math.Max(4, size / 4)
                : "borderw=" + Math.Max(2, size / 16) + ":bordercolor=black@0.85";
            drawtexts.Add("drawtext=fontfile='" + FfPath(font) + "':textfile='" + FfPath(file) + "':expansion=none" +
                          ":fontsize=" + size + ":fontcolor=" + Hex(t.Color) + ":" + look +
                          ":x=(w-text_w)/2:y=" + y +
                          ":enable='between(t," + N(t.Start) + "," + N(Math.Min(t.End, p.Duration)) + ")'");
        }
        graph.Append("[vc]" + (drawtexts.Count > 0 ? string.Join(",", drawtexts) : "null") + "[vout]");

        var graphFile = Path.Combine(workDir, "graph.txt");
        File.WriteAllText(graphFile, graph.ToString(), new UTF8Encoding(false));

        var a = new List<string> { "-y", "-hide_banner", "-nostats", "-progress", "pipe:1" };
        foreach (var path in inputs) { a.Add("-i"); a.Add(path); }
        a.AddRange(new[] { "-filter_complex_script", graphFile, "-map", "[vout]", "-map", "[aout]" });

        if (o.TargetMb > 0 && plan.Duration > 0)
        {
            // aim for a file size: work out the video bitrate that fits, leaving room for the sound
            var totalKbps = o.TargetMb * 1024 * 1024 * 8 * 0.92 / plan.Duration / 1000.0;
            var videoKbps = Math.Max(150, (int)(totalKbps - 128));
            a.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "medium", "-b:v", videoKbps + "k",
                "-maxrate", (int)(videoKbps * 1.15) + "k", "-bufsize", (videoKbps * 2) + "k",
            });
            a.AddRange(new[] { "-c:a", "aac", "-b:a", "128k" });
        }
        else
        {
            string preset, crf;
            switch (o.Quality)
            {
                case "fast": preset = "ultrafast"; crf = "23"; break;
                case "high": preset = "medium"; crf = "18"; break;
                default: preset = "veryfast"; crf = "20"; break;
            }
            a.AddRange(new[] { "-c:v", "libx264", "-preset", preset, "-crf", crf });
            a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        }
        a.AddRange(new[] { "-pix_fmt", "yuv420p", "-r", o.Fps.ToString(CultureInfo.InvariantCulture), "-movflags", "+faststart", o.Output });
        plan.Args = a;
        return plan;
    }
}
