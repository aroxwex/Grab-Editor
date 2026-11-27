using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GrabEditor.Models;
using GrabEditor.Services;

namespace GrabEditor;

// Playback: the preview plays the clip under the playhead and hops to the next one when it ends.
public partial class MainWindow
{
    readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
    bool _playing;
    ClipItem _curClip;
    string _curSource = "";
    bool _mediaReady;
    double _pendingSeek;

    readonly List<(TextItem Item, Border View)> _textViews = new List<(TextItem, Border)>();

    // ------------------------------------------------------------------ the playhead

    void SetTime(double t, bool seekPlayer)
    {
        t = Math.Max(0, Math.Min(_project.Duration, t));
        _time = t;
        Timeline.Time = t;
        UpdateTimeText();
        UpdateTextOverlayVisibility();
        if (seekPlayer) SeekPlayer(t);
    }

    void UpdateTimeText()
    {
        TimeText.Text = Fmt.TimeTenths(_time) + " / " + Fmt.TimeTenths(_project.Duration);
    }

    void ScrollToTime()
    {
        var x = Timeline.XOf(_time);
        var left = TimelineScroll.HorizontalOffset;
        var right = left + TimelineScroll.ViewportWidth;
        if (x > right - 60 || x < left + 10)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - TimelineScroll.ViewportWidth * 0.3));
    }

    void Step(double seconds)
    {
        if (_project.Clips.Count == 0) return;
        Pause();
        SetTime(_time + seconds, true);
        ScrollToTime();
    }

    void GoStart_Click(object sender, RoutedEventArgs e)
    {
        Pause();
        SetTime(0, true);
        ScrollToTime();
    }

    void GoEnd_Click(object sender, RoutedEventArgs e)
    {
        Pause();
        SetTime(_project.Duration, true);
        ScrollToTime();
    }

    // ------------------------------------------------------------------ driving the player

    void SeekPlayer(double t)
    {
        var clip = _project.ClipAt(t);
        if (clip == null)
        {
            _curClip = null;
            _curSource = "";
            _mediaReady = false;
            try { Player.Stop(); } catch { }
            PreviewNote.Text = "";
            return;
        }
        var within = Math.Max(0, Math.Min(clip.Length, t - clip.Start));
        LoadClip(clip, Math.Min(clip.In + within * clip.Speed, clip.Out));
    }

    void LoadClip(ClipItem clip, double mediaPos)
    {
        var media = _project.MediaOf(clip);
        var path = MediaService.PreviewPath(media);
        _curClip = clip;

        if (path == "")
        {
            // the small preview copy isn't ready yet, or the file is gone
            _curSource = "";
            _mediaReady = false;
            try { Player.Stop(); } catch { }
            PreviewNote.Text = media != null && !File.Exists(media.Path)
                ? "This file is missing:\n" + media.Name
                : "Getting the preview ready…";
            return;
        }

        PreviewNote.Text = "";
        Player.Stretch = clip.Fill ? Stretch.UniformToFill : Stretch.Uniform;
        Player.SpeedRatio = clip.Speed;
        Player.Volume = Math.Min(1.0, clip.Volume);

        if (path != _curSource)
        {
            _curSource = path;
            _mediaReady = false;
            _pendingSeek = mediaPos;
            Player.IsMuted = true;                      // no blip of sound while the file opens
            Player.Source = new Uri(path);
            Player.Play();
        }
        else if (_mediaReady)
        {
            Player.Position = TimeSpan.FromSeconds(mediaPos);
            if (_playing) Player.Play(); else Player.Pause();
        }
        else
        {
            _pendingSeek = mediaPos;
        }
    }

    void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        Player.Position = TimeSpan.FromSeconds(_pendingSeek);
        Player.IsMuted = false;
        _mediaReady = true;
        if (_playing) Player.Play();
    }

    void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false;
        _curSource = "";
        var media = _project.MediaOf(_curClip);
        Pause();
        if (media != null && File.Exists(media.Path) && !File.Exists(MediaService.ProxyPath(media.Id)))
        {
            // Windows can't play this one directly: make a compatible copy
            media.NeedsProxy = true;
            PreviewNote.Text = "Getting the preview ready…";
            _ = PrepareMediaAsync(media);
        }
        else
        {
            PreviewNote.Text = "This clip can't be previewed. It will still export.";
        }
    }

    void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_playing) Advance();
    }

    void Advance()
    {
        var next = _project.ClipAfter(_curClip);
        if (next == null)
        {
            Pause();
            SetTime(_project.Duration, false);
            return;
        }
        SetTime(next.Start, false);
        LoadClip(next, next.In);
    }

    void Tick(object sender, EventArgs e)
    {
        if (!_playing || _curClip == null || !_mediaReady) return;
        var pos = Player.Position.TotalSeconds;
        if (pos >= _curClip.Out - 0.015)
        {
            Advance();
            return;
        }
        var t = _curClip.Start + (pos - _curClip.In) / _curClip.Speed;
        SetTime(Math.Max(_curClip.Start, t), false);
        ScrollToTime();
    }

    // ------------------------------------------------------------------ play / pause

    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    void Preview_Click(object sender, MouseButtonEventArgs e)
    {
        if (_project.Clips.Count > 0) TogglePlay();
    }

    void TogglePlay()
    {
        if (_playing) Pause(); else Play();
    }

    void Play()
    {
        if (_project.Clips.Count == 0) return;
        if (_time >= _project.Duration - 0.05) SetTime(0, false);
        _playing = true;
        UpdatePlayButton();
        SeekPlayer(_time);
        if (_mediaReady) Player.Play();
    }

    void Pause()
    {
        _playing = false;
        try { Player.Pause(); } catch { }
        UpdatePlayButton();
    }

    void UpdatePlayButton()
    {
        BtnPlay.Content = _playing ? "\uE769" : "\uE768";
    }

    // ------------------------------------------------------------------ preview frame and text

    void AspectHost_SizeChanged(object sender, SizeChangedEventArgs e) => FitPreviewFrame();

    void FitPreviewFrame()
    {
        if (AspectHost == null) return;
        var w = AspectHost.ActualWidth - 24;
        var h = AspectHost.ActualHeight - 24;
        if (w < 20 || h < 20) return;
        var ratio = _project.AspectRatio;
        double fw, fh;
        if (w / h > ratio) { fh = h; fw = fh * ratio; }
        else { fw = w; fh = fw / ratio; }
        PreviewFrame.Width = fw;
        PreviewFrame.Height = fh;
        RebuildTextOverlays();
    }

    void RebuildTextOverlays()
    {
        TextLayer.Children.Clear();
        _textViews.Clear();
        var frameHeight = PreviewFrame.Height;
        if (double.IsNaN(frameHeight) || frameHeight <= 0) return;

        foreach (var t in _project.Texts)
        {
            Brush color;
            try { color = (Brush)new BrushConverter().ConvertFromString(t.Color); }
            catch { color = Brushes.White; }

            var block = new TextBlock
            {
                Text = t.Text,
                Foreground = color,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = Math.Max(6, frameHeight * t.Size / 100.0),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            if (!t.Box)
            {
                block.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 6, ShadowDepth = 1, Opacity = 0.9,
                };
            }

            var view = new Border
            {
                Child = block,
                Background = t.Box ? new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)) : Brushes.Transparent,
                Padding = t.Box ? new Thickness(14, 6, 14, 6) : new Thickness(0),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = PreviewFrame.Width * 0.92,
                Margin = new Thickness(0, frameHeight * 0.08, 0, frameHeight * 0.08),
                VerticalAlignment = t.Position == "top" ? VerticalAlignment.Top
                    : t.Position == "center" ? VerticalAlignment.Center : VerticalAlignment.Bottom,
            };
            TextLayer.Children.Add(view);
            _textViews.Add((t, view));
        }
        UpdateTextOverlayVisibility();
    }

    void UpdateTextOverlayVisibility()
    {
        foreach (var (item, view) in _textViews)
            view.Visibility = _time >= item.Start && _time < item.End ? Visibility.Visible : Visibility.Collapsed;
    }
}
