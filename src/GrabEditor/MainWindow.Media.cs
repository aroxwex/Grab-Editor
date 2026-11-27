using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GrabEditor.Models;
using GrabEditor.Services;
using Microsoft.Win32;

namespace GrabEditor;

// The media library: importing, thumbnails, preview copies and the tiles on the left.
public partial class MainWindow
{
    readonly Dictionary<string, BitmapImage> _thumbs = new Dictionary<string, BitmapImage>();
    readonly Dictionary<string, (Border Box, TextBlock Text)> _tileStatus = new Dictionary<string, (Border, TextBlock)>();
    readonly Dictionary<string, string> _statusText = new Dictionary<string, string>();
    readonly SemaphoreSlim _prepGate = new SemaphoreSlim(1, 1);
    bool _tileDown;
    Point _tileDragStart;

    // ------------------------------------------------------------------ thumbnails and status

    ImageSource GetThumb(string id)
    {
        if (_thumbs.TryGetValue(id, out var cached)) return cached;
        var path = MediaService.ThumbPath(id);
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            _thumbs[id] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    void SetStatus(string id, string text)
    {
        _statusText[id] = text;
        if (_tileStatus.TryGetValue(id, out var t))
        {
            t.Text.Text = text;
            t.Box.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ------------------------------------------------------------------ the tiles

    void BuildMediaTiles()
    {
        MediaList.Children.Clear();
        _tileStatus.Clear();
        foreach (var m in _project.Media) MediaList.Children.Add(BuildTile(m));
        var n = _project.Media.Count;
        MediaEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaCountText.Text = n == 0 ? "" : n + (n == 1 ? " video" : " videos");
    }

    Button RoundButton(string glyph, string tip, Action action)
    {
        var b = new Button
        {
            Content = glyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 11,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            ToolTip = tip,
            Margin = new Thickness(5),
        };
        b.Click += (s, e) => action();
        return b;
    }

    UIElement BuildTile(MediaItem m)
    {
        var missing = !File.Exists(m.Path);

        var thumbBox = new Grid
        {
            Width = 124,
            Height = 70,
            Background = (Brush)FindResource("Panel2Brush"),
            Clip = new RectangleGeometry(new Rect(0, 0, 124, 70), 8, 8),
        };
        thumbBox.Children.Add(new Image { Stretch = Stretch.UniformToFill, Source = GetThumb(m.Id) });

        thumbBox.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 1, 5, 1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 5, 5),
            Child = new TextBlock { Text = Fmt.Time(m.Duration), FontSize = 11, Foreground = Brushes.White },
        });

        var statusText = new TextBlock
        {
            FontSize = 11,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6),
        };
        var statusBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
            Child = statusText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        thumbBox.Children.Add(statusBox);
        _tileStatus[m.Id] = (statusBox, statusText);

        var add = RoundButton("\uE710", "Add to the timeline", () => AddMediaToTimeline(m.Id, _project.Clips.Count));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.VerticalAlignment = VerticalAlignment.Top;
        var remove = RoundButton("\uE711", "Remove from the project", () => RemoveMedia(m));
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        remove.VerticalAlignment = VerticalAlignment.Top;
        thumbBox.Children.Add(add);
        thumbBox.Children.Add(remove);

        var stack = new StackPanel();
        stack.Children.Add(thumbBox);
        stack.Children.Add(new TextBlock
        {
            Text = m.Name,
            FontSize = 12,
            Margin = new Thickness(2, 6, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var tile = new Border
        {
            Width = 134,
            Margin = new Thickness(0, 0, 6, 10),
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("Panel2Brush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = m.Name + "\nDouble-click to add it to the timeline, or drag it there.",
            Child = stack,
        };
        tile.MouseLeftButtonDown += (s, e) =>
        {
            _tileDown = true;
            _tileDragStart = e.GetPosition(null);
            if (e.ClickCount == 2)
            {
                _tileDown = false;
                AddMediaToTimeline(m.Id, _project.Clips.Count);
                e.Handled = true;
            }
        };
        tile.MouseLeftButtonUp += (s, e) => _tileDown = false;
        tile.MouseLeave += (s, e) => { if (e.LeftButton != MouseButtonState.Pressed) _tileDown = false; };
        tile.MouseMove += (s, e) =>
        {
            if (!_tileDown || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - _tileDragStart.X) < 6 && Math.Abs(p.Y - _tileDragStart.Y) < 6) return;
            _tileDown = false;
            DragDrop.DoDragDrop(tile, new DataObject("grab-media", m.Id), DragDropEffects.Copy);
        };

        if (missing) SetStatusLater(m.Id, "File missing");
        else if (_statusText.TryGetValue(m.Id, out var status)) SetStatusLater(m.Id, status);
        return tile;
    }

    // the tile is added to the panel after this method returns, so apply the text straight away
    void SetStatusLater(string id, string text) => SetStatus(id, text);

    // ------------------------------------------------------------------ adding to the timeline

    void AddMediaToTimeline(string id, int index)
    {
        var m = _project.Media.FirstOrDefault(x => x.Id == id);
        if (m == null) return;
        if (!File.Exists(m.Path))
        {
            Toast("This file is missing: " + m.Name);
            return;
        }
        PushUndo();
        var clip = new ClipItem { MediaId = m.Id, In = 0, Out = m.Duration };
        _project.Clips.Insert(Math.Max(0, Math.Min(index, _project.Clips.Count)), clip);
        AfterStructureEdit();
        Timeline.Select(clip, null);
        Pause();
        SetTime(clip.Start, true);
        ScrollToTime();
    }

    void RemoveMedia(MediaItem m)
    {
        var uses = _project.Clips.Count(c => c.MediaId == m.Id);
        if (uses > 0)
        {
            var r = MessageBox.Show(this, "\"" + m.Name + "\" is used in " + uses + (uses == 1 ? " clip" : " clips") +
                                          " on the timeline. Remove those too?", "Grab Editor", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        PushUndo();
        _project.Clips.RemoveAll(c => c.MediaId == m.Id);
        _project.Media.Remove(m);
        Timeline.Select(null, null);
        AfterStructureEdit();
    }

    // ------------------------------------------------------------------ importing

    void Import_Click(object sender, RoutedEventArgs e)
    {
        ImportPopup.IsOpen = !ImportPopup.IsOpen;
    }

    async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        ImportPopup.IsOpen = false;
        var dlg = new OpenFileDialog
        {
            Title = "Choose videos",
            Multiselect = true,
            Filter = "Videos|*.mp4;*.mov;*.m4v;*.mkv;*.webm;*.avi;*.wmv;*.flv;*.mpg;*.mpeg;*.3gp;*.ts;*.mts;*.m2ts|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) await ImportFilesAsync(dlg.FileNames, false);
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(File.Exists).ToArray();
                var project = files.FirstOrDefault(f => f.EndsWith(".grabedit", StringComparison.OrdinalIgnoreCase));
                if (project != null)
                {
                    if (ConfirmDiscard()) OpenProjectFile(project);
                    return;
                }
                if (files.Length > 0) await ImportFilesAsync(files, true);
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = ((string)e.Data.GetData(DataFormats.UnicodeText)).Trim();
                if (Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
                    OpenDownload(text);
            }
        }
        catch
        {
            Toast("Couldn't open what you dropped");
        }
    }

    /// <summary>Reads the files and puts them in the library (and on the timeline when it is empty or asked for).</summary>
    async Task ImportFilesAsync(IList<string> paths, bool addToTimeline)
    {
        if (Tools.Find("ffprobe") == null || Tools.Find("ffmpeg") == null)
        {
            Toast("ffmpeg.exe and ffprobe.exe are missing from the bin folder");
            return;
        }

        var items = new List<MediaItem>();
        var unreadable = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var id = MediaService.MakeId(path);
            var existing = _project.Media.FirstOrDefault(m => m.Id == id);
            if (existing != null) { items.Add(existing); continue; }

            Toast("Reading " + System.IO.Path.GetFileName(path) + "…");
            var item = await MediaService.ProbeAsync(path);
            if (item == null) { unreadable++; continue; }
            _project.Media.Add(item);
            items.Add(item);
            _ = PrepareMediaAsync(item);
        }

        if (items.Count == 0)
        {
            if (unreadable > 0) Toast("Those files couldn't be read as video");
            return;
        }

        MarkDirty();
        var put = addToTimeline || _project.Clips.Count == 0;
        if (put)
        {
            PushUndo();
            foreach (var m in items) _project.Clips.Add(new ClipItem { MediaId = m.Id, In = 0, Out = m.Duration });
            AfterStructureEdit();
            Pause();
            SetTime(0, true);
            ScrollToTime();
            Timeline.Select(_project.Clips[_project.Clips.Count - items.Count], null);
        }
        else
        {
            BuildMediaTiles();
        }

        if (unreadable > 0) Toast(unreadable + " file(s) couldn't be read as video");
        else Toast(items.Count == 1 ? "Imported " + items[0].Name : "Imported " + items.Count + " videos");
    }

    // ------------------------------------------------------------------ thumbnails and preview copies

    async Task PrepareMediaAsync(MediaItem m)
    {
        if (!File.Exists(m.Path)) { SetStatus(m.Id, "File missing"); return; }
        var needsWork = !File.Exists(MediaService.ThumbPath(m.Id)) || (m.NeedsProxy && !File.Exists(MediaService.ProxyPath(m.Id)));
        if (needsWork) SetStatus(m.Id, "Preparing…");

        await _prepGate.WaitAsync();
        try
        {
            if (!File.Exists(MediaService.ThumbPath(m.Id)))
            {
                if (await MediaService.MakeThumbAsync(m))
                {
                    _thumbs.Remove(m.Id);
                    BuildMediaTiles();
                    Timeline.Refresh();
                }
            }

            if (m.NeedsProxy && !File.Exists(MediaService.ProxyPath(m.Id)))
            {
                SetStatus(m.Id, "Preview 0%");
                var error = await MediaService.MakeProxyAsync(m, CancellationToken.None,
                    f => SetStatus(m.Id, "Preview " + (int)((f ?? 0) * 100) + "%"));
                if (error != "") Toast(error);
            }

            SetStatus(m.Id, "");
            MediaService.TouchCache(m.Id);

            // the clip on screen may have been waiting for this copy
            if (_curClip != null && _curClip.MediaId == m.Id && _curSource == "") SeekPlayer(_time);
        }
        finally
        {
            _prepGate.Release();
        }
    }
}
