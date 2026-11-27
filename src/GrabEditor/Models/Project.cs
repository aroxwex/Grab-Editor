using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrabEditor.Models;

/// <summary>A video file in the project's library.</summary>
public class MediaItem
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public double Duration { get; set; }
    public bool HasAudio { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool NeedsProxy { get; set; }
}

/// <summary>A piece of a media file placed on the timeline.</summary>
public class ClipItem
{
    public string MediaId { get; set; } = "";
    public double In { get; set; }                 // seconds into the source file
    public double Out { get; set; }
    public double Speed { get; set; } = 1.0;
    public double Volume { get; set; } = 1.0;     // 0 - 2
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public bool Fill { get; set; }                 // false = fit (bars), true = fill (crop)

    [JsonIgnore] public double Start { get; set; }                                   // set by Project.Layout
    [JsonIgnore] public double Length => (Out - In) / (Speed > 0 ? Speed : 1.0);     // length on the timeline
    [JsonIgnore] public double End => Start + Length;

    public ClipItem Copy()
    {
        return new ClipItem
        {
            MediaId = MediaId, In = In, Out = Out, Speed = Speed, Volume = Volume,
            FadeIn = FadeIn, FadeOut = FadeOut, Fill = Fill, Start = Start,
        };
    }
}

/// <summary>A caption or title shown over the video.</summary>
public class TextItem
{
    public string Text { get; set; } = "Your text";
    public double Start { get; set; }
    public double Duration { get; set; } = 3;
    public double Size { get; set; } = 7;                  // percent of the video height
    public string Color { get; set; } = "#FFFFFF";
    public string Position { get; set; } = "bottom";      // top | center | bottom
    public bool Box { get; set; }                          // dark box behind the text

    [JsonIgnore] public double End => Start + Duration;
}

public class Project
{
    public string Name { get; set; } = "Untitled";
    public string Aspect { get; set; } = "16:9";          // 16:9 | 9:16 | 1:1 | 4:5
    public List<MediaItem> Media { get; set; } = new List<MediaItem>();
    public List<ClipItem> Clips { get; set; } = new List<ClipItem>();
    public List<TextItem> Texts { get; set; } = new List<TextItem>();

    [JsonIgnore] public double Duration { get; private set; }

    [JsonIgnore]
    public double AspectRatio
    {
        get
        {
            switch (Aspect)
            {
                case "9:16": return 9.0 / 16.0;
                case "1:1": return 1.0;
                case "4:5": return 4.0 / 5.0;
                default: return 16.0 / 9.0;
            }
        }
    }

    /// <summary>Clips sit one after another with no gaps; this works out where each one starts.</summary>
    public void Layout()
    {
        double t = 0;
        foreach (var c in Clips)
        {
            c.Start = t;
            t += c.Length;
        }
        Duration = t;
    }

    public MediaItem MediaOf(ClipItem clip)
    {
        return clip == null ? null : Media.FirstOrDefault(m => m.Id == clip.MediaId);
    }

    /// <summary>The clip playing at time t. At the very end it returns the last clip.</summary>
    public ClipItem ClipAt(double t)
    {
        if (Clips.Count == 0) return null;
        foreach (var c in Clips)
            if (t >= c.Start && t < c.End) return c;
        return t >= Duration ? Clips[Clips.Count - 1] : Clips[0];
    }

    public ClipItem ClipAfter(ClipItem clip)
    {
        var i = Clips.IndexOf(clip);
        return i >= 0 && i + 1 < Clips.Count ? Clips[i + 1] : null;
    }

    static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, Options);
    }

    public static Project FromJson(string json)
    {
        var p = JsonSerializer.Deserialize<Project>(json, Options) ?? new Project();
        p.Layout();
        return p;
    }
}
