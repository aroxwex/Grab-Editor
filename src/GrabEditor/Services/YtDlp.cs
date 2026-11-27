using System.Globalization;
using System.Text.Json;

namespace GrabEditor.Services;

public class VideoInfo
{
    public string Title = "";
    public string Channel = "";
    public double Duration;
    public List<int> Heights = new List<int>();     // available picture heights, best first
}

/// <summary>The small part of yt-dlp that Grab Editor needs: look up a link and download it.</summary>
public static class YtDlp
{
    static bool _useJsFlag = true;
    static string _client = "";

    static readonly string[] ClientFallbacks =
    {
        "youtube:player_client=tv",
        "youtube:player_client=web_safari",
        "youtube:player_client=mweb",
    };

    static string Str(JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    static double Num(JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return 0;
    }

    static void AddCommon(List<string> args)
    {
        var qjs = Tools.Find("qjs");
        if (qjs != null && _useJsFlag) { args.Add("--js-runtimes"); args.Add("quickjs:" + qjs); }
        if (!string.IsNullOrEmpty(_client)) { args.Add("--extractor-args"); args.Add(_client); }
    }

    static bool IsBotCheck(string err)
    {
        var low = (err ?? "").ToLowerInvariant();
        return low.Contains("not a bot") || low.Contains("sign in to confirm");
    }

    static bool FlagNotSupported(string err)
    {
        var low = (err ?? "").ToLowerInvariant();
        return low.Contains("js-runtimes") && (low.Contains("no such option") || low.Contains("unrecognized"));
    }

    public static string Friendly(string err)
    {
        var e = (err ?? "").Trim();
        var low = e.ToLowerInvariant();
        var table = new (string Key, string Msg)[]
        {
            ("no space left", "Your disk is full. Free up some space and try again."),
            ("not enough space", "Your disk is full. Free up some space and try again."),
            ("unsupported url", "This link isn't supported."),
            ("video unavailable", "This video is unavailable (it may be deleted or blocked in your region)."),
            ("private video", "This video is private."),
            ("not a bot", "YouTube is asking for verification. Update yt-dlp (put a newer yt-dlp.exe in the bin folder), or download the video with Grab and import the file."),
            ("sign in", "This video needs a login. Download it with Grab (it supports cookies), then import the file."),
            ("confirm your age", "Age verification is required. Download it with Grab (it supports cookies), then import the file."),
            ("unable to download webpage", "Couldn't reach the site. Check your internet connection."),
            ("getaddrinfo", "You seem to be offline."),
            ("http error 429", "The site is temporarily rate-limiting you. Wait a bit and try again."),
        };
        foreach (var (key, msg) in table)
            if (low.Contains(key)) return msg;

        var idx = e.IndexOf("ERROR:", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) e = e.Substring(idx + 6).Trim();
        if (e.StartsWith("[")) { var close = e.IndexOf(']'); if (close > 0) e = e.Substring(close + 1).Trim(); }
        if (e.Length == 0) return "Unknown error.";
        return e.Length > 200 ? e.Substring(0, 200) + "…" : e;
    }

    // ------------------------------------------------------------------ info

    static async Task<(VideoInfo Info, string Error, string Raw)> InfoOnceAsync(string url)
    {
        var exe = Tools.Find("yt-dlp");
        if (exe == null) return (null, "yt-dlp.exe is missing from the bin folder.", "");

        var args = new List<string> { "-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8" };
        AddCommon(args);
        args.Add(url);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        (int Code, string Out, string Err) r;
        try { r = await Proc.RunAsync(exe, args, cts.Token); }
        catch (Exception ex) { return (null, ex.Message, ex.Message); }
        if (cts.IsCancellationRequested) return (null, "Timed out. Try again.", "");

        if (r.Code != 0)
        {
            var lines = r.Err.Split('\n').Where(l => l.Contains("ERROR")).ToList();
            return (null, Friendly(lines.Count > 0 ? lines[lines.Count - 1] : r.Err), r.Err);
        }

        try
        {
            using var doc = JsonDocument.Parse(r.Out);
            var root = doc.RootElement;
            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0)
                root = entries[0];

            var info = new VideoInfo
            {
                Title = Str(root, "title"),
                Channel = Str(root, "uploader") != "" ? Str(root, "uploader") : Str(root, "channel"),
                Duration = Num(root, "duration"),
            };
            var heights = new HashSet<int>();
            if (root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fmts.EnumerateArray())
                {
                    var h = (int)Num(f, "height");
                    if (h > 0 && Str(f, "vcodec") != "none") heights.Add(h);
                }
            }
            info.Heights = heights.OrderByDescending(x => x).ToList();
            return (info, "", "");
        }
        catch
        {
            return (null, "Couldn't read the video info.", "");
        }
    }

    public static async Task<(VideoInfo Info, string Error)> GetInfoAsync(string url)
    {
        _client = "";
        var r = await InfoOnceAsync(url);
        if (r.Info == null && FlagNotSupported(r.Raw)) { _useJsFlag = false; r = await InfoOnceAsync(url); }

        if (r.Info == null && IsBotCheck(r.Raw))
        {
            foreach (var client in ClientFallbacks)
            {
                _client = client;
                var t = await InfoOnceAsync(url);
                if (t.Info != null) { r = t; break; }
                if (!IsBotCheck(t.Raw)) r = t;
            }
            if (r.Info == null) _client = "";
        }
        return (r.Info, r.Error);
    }

    // ------------------------------------------------------------------ download

    static double Parse(string s)
    {
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
    }

    /// <summary>Downloads the video (picture and sound merged into an MP4) and returns the file path.</summary>
    public static async Task<(string File, string Error)> DownloadAsync(
        string url, int height, string outDir, Action<double?, string> progress, CancellationToken ct)
    {
        var yt = Tools.Find("yt-dlp");
        var ff = Tools.Find("ffmpeg");
        if (yt == null) return ("", "yt-dlp.exe is missing from the bin folder.");
        if (ff == null) return ("", "ffmpeg.exe is missing from the bin folder.");

        var tmp = Path.Combine(AppPaths.TempDir, "dl-" + DateTime.Now.Ticks);
        try
        {
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(tmp);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var args = new List<string>
                {
                    "--no-playlist", "--no-warnings", "--newline", "--encoding", "utf-8", "--windows-filenames",
                    "--progress-template",
                    "download:@@%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s",
                    "--print-to-file", "after_move:filepath", Path.Combine(tmp, "_path.txt"),
                    "-o", Path.Combine(tmp, "%(title).120B.%(ext)s"),
                    "-f", "bv*[height<=" + height + "][vcodec^=avc1]+ba[ext=m4a]/bv*[height<=" + height + "]+ba/b[height<=" + height + "]",
                    "--merge-output-format", "mp4",
                    "--ffmpeg-location", Path.GetDirectoryName(ff),
                };
                AddCommon(args);
                args.Add(url);

                using var p = Process.Start(Proc.Info(yt, args));
                using var reg = ct.Register(() => Proc.Kill(p));
                var errTask = p.StandardError.ReadToEndAsync();
                progress?.Invoke(null, "Starting…");

                var stage = 0;
                string lastFormat = null;
                string line;
                while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                {
                    line = line.Trim();
                    if (line.StartsWith("@@"))
                    {
                        var f = line.Substring(2).Split('|');
                        if (f.Length < 6) continue;
                        if (f[5] != lastFormat) { stage++; lastFormat = f[5]; }
                        var done = Parse(f[0]);
                        var total = Parse(f[1]);
                        if (total <= 0) total = Parse(f[2]);
                        var speed = Parse(f[3]);
                        var eta = Parse(f[4]);
                        double? fraction = null;
                        if (total > 0 && done >= 0)
                            fraction = Math.Min(((Math.Min(stage, 2) - 1) + Math.Min(done / total, 1.0)) / 2.0, 0.99);
                        var text = "Downloading";
                        if (speed > 0) text += "   ·   " + Fmt.Size(speed) + "/s";
                        if (eta >= 0) text += "   ·   " + Fmt.Time(eta) + " left";
                        progress?.Invoke(fraction, text);
                    }
                    else if (line.StartsWith("[Merger]")) progress?.Invoke(null, "Merging…");
                }
                await p.WaitForExitAsync();
                var err = await errTask;

                if (ct.IsCancellationRequested) return ("", "Cancelled.");
                if (p.ExitCode != 0)
                {
                    if (attempt == 0 && FlagNotSupported(err)) { _useJsFlag = false; continue; }
                    var lines = err.Split('\n').Where(l => l.Contains("ERROR")).ToList();
                    return ("", Friendly(lines.Count > 0 ? lines[lines.Count - 1] : err));
                }
                break;
            }

            string src = null;
            var pathFile = Path.Combine(tmp, "_path.txt");
            if (File.Exists(pathFile))
            {
                var last = File.ReadAllLines(pathFile).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (last != null && File.Exists(last.Trim())) src = last.Trim();
            }
            if (src == null)
            {
                src = new DirectoryInfo(tmp).GetFiles()
                    .Where(x => x.Name != "_path.txt")
                    .OrderByDescending(x => x.Length)
                    .Select(x => x.FullName)
                    .FirstOrDefault();
            }
            if (src == null) return ("", "The downloaded file wasn't found.");

            var final = Path.Combine(outDir, Path.GetFileName(src));
            for (var i = 1; File.Exists(final); i++)
                final = Path.Combine(outDir, Path.GetFileNameWithoutExtension(src) + " (" + i + ")" + Path.GetExtension(src));
            try
            {
                File.Move(src, final);
            }
            catch (IOException ex)
            {
                return ("", "Couldn't save the file to " + outDir + ". " + Friendly(ex.Message));
            }
            return (final, "");
        }
        catch (Exception ex)
        {
            return ("", Friendly(ex.Message));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}
