using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GrabEditor.Models;
using GrabEditor.Services;
using Microsoft.Win32;

namespace GrabEditor;

public partial class MainWindow : Window
{
    Project _project = new Project();
    string _projectPath = "";
    bool _dirty;
    bool _ready;
    bool _sync;                    // true while the inspector is being filled from the model
    readonly UndoManager _undo = new UndoManager();
    ClipItem _selClip;
    TextItem _selText;
    double _time;
    DispatcherTimer _toastTimer;
    readonly DispatcherTimer _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };

    static string AutosavePath => System.IO.Path.Combine(AppPaths.DataDir, "autosave.grabedit");

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (s, e) => Win.StyleTitleBar(this);
        Loaded += OnLoaded;

        Timeline.ThumbProvider = GetThumb;
        Timeline.Magnet = EditorSettings.Current.Snap;
        SnapSwitch.IsChecked = Timeline.Magnet;
        Timeline.TimeRequested += t => { Pause(); SetTime(t, true); };
        Timeline.SelectionChanged += OnTimelineSelection;
        Timeline.EditStarting += () => PushUndo();
        Timeline.Edited += OnTimelineEdited;
        Timeline.EditFinished += OnTimelineEditFinished;
        Timeline.MediaDropped += (id, index) => AddMediaToTimeline(id, index);
        Timeline.Project = _project;
        Timeline.Pps = Math.Exp(ZoomSlider.Value);

        _tick.Tick += Tick;
        _tick.Start();
        _autosave.Tick += (s, e) => Autosave();
        _autosave.Start();

        _ready = true;
        RefreshAll();
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        var missing = new List<string>();
        if (Tools.Find("ffmpeg") == null) missing.Add("ffmpeg.exe");
        if (Tools.Find("ffprobe") == null) missing.Add("ffprobe.exe");
        if (missing.Count > 0)
        {
            ToolWarn.Text = "Missing in the bin folder: " + string.Join(", ", missing);
            ToolWarn.Visibility = Visibility.Visible;
        }

        var version = typeof(MainWindow).Assembly.GetName().Version;
        AboutText.Text = "Grab Editor " + (version != null ? version.ToString(3) : "") + "  ·  MIT License";

        FitPreviewFrame();
        BuildRecentList();
        OfferRecovery();
    }

    // ------------------------------------------------------------------ messages

    void Toast(string text)
    {
        ToastText.Text = text;
        ToastBorder.Visibility = Visibility.Visible;
        if (_toastTimer == null)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                ToastBorder.Visibility = Visibility.Collapsed;
            };
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ------------------------------------------------------------------ state

    void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
    }

    void UpdateTitle()
    {
        ProjectTitle.Text = _project.Name + (_dirty ? "  •" : "");
        Title = "Grab Editor  -  " + _project.Name + (_dirty ? " *" : "");
    }

    void PushUndo()
    {
        _undo.Push(_project.ToJson());
        UpdateButtons();
    }

    void UpdateButtons()
    {
        if (!_ready) return;
        BtnUndo.IsEnabled = _undo.CanUndo;
        BtnRedo.IsEnabled = _undo.CanRedo;
        var hasClips = _project.Clips.Count > 0;
        BtnExport.IsEnabled = hasClips;
        BtnSplit.IsEnabled = hasClips;
        BtnAddText.IsEnabled = hasClips;
        BtnDelete.IsEnabled = _selClip != null || _selText != null;
        BtnPlay.IsEnabled = hasClips;
        BtnStart.IsEnabled = hasClips;
        BtnEnd.IsEnabled = hasClips;
    }

    /// <summary>Refreshes everything that depends on the project after a structural change.</summary>
    void RefreshAll()
    {
        if (!_ready) return;
        _project.Layout();
        Timeline.Refresh();
        var hasClips = _project.Clips.Count > 0;
        WelcomePanel.Visibility = hasClips ? Visibility.Collapsed : Visibility.Visible;
        PreviewFrame.Visibility = hasClips ? Visibility.Visible : Visibility.Collapsed;
        AspectBar.Visibility = hasClips ? Visibility.Visible : Visibility.Collapsed;
        SyncAspectButtons();
        FitPreviewFrame();
        UpdateInfoTexts();
        UpdateInspector();
        UpdateTimeText();
        UpdateButtons();
        UpdateTitle();
        BuildMediaTiles();
    }

    void UpdateInfoTexts()
    {
        var n = _project.Clips.Count;
        TimelineInfo.Text = n == 0 ? "" : n + (n == 1 ? " clip" : " clips") + "  ·  " + Fmt.TimeTenths(_project.Duration);
        ProjectInfo.Text = _project.Name + "\n" + _project.Aspect + "  ·  " + n + (n == 1 ? " clip" : " clips") +
                           "  ·  " + Fmt.TimeTenths(_project.Duration) + "\n" + _project.Media.Count + " in the library";
    }

    void SyncAspectButtons()
    {
        foreach (var rb in new[] { RbA169, RbA916, RbA11, RbA45 }) rb.IsChecked = (string)rb.Tag == _project.Aspect;
    }

    // ------------------------------------------------------------------ selection

    void OnTimelineSelection()
    {
        _selClip = Timeline.SelectedClip;
        _selText = Timeline.SelectedText;
        UpdateInspector();
        UpdateButtons();
    }

    void OnTimelineEdited()
    {
        // called live while a drag changes the timeline
        UpdateInfoTexts();
        if (_selClip != null) UpdateInspector();
        if (_selText != null) UpdateInspector();
        RebuildTextOverlays();
        MarkDirty();
        if (_time > _project.Duration) { _time = _project.Duration; Timeline.Time = _time; UpdateTimeText(); }
    }

    void OnTimelineEditFinished()
    {
        UpdateButtons();
        SeekPlayer(_time);
    }

    // ------------------------------------------------------------------ undo / redo

    void Undo_Click(object sender, RoutedEventArgs e) => DoUndo();
    void Redo_Click(object sender, RoutedEventArgs e) => DoRedo();

    void DoUndo()
    {
        var snapshot = _undo.Undo(_project.ToJson());
        if (snapshot == null) { Toast("Nothing to undo"); return; }
        Restore(snapshot);
        Toast("Undone");
    }

    void DoRedo()
    {
        var snapshot = _undo.Redo(_project.ToJson());
        if (snapshot == null) { Toast("Nothing to redo"); return; }
        Restore(snapshot);
        Toast("Redone");
    }

    void Restore(string json)
    {
        Pause();
        var restored = Project.FromJson(json);
        restored.Media = _project.Media;                // the library isn't part of undo
        _project = restored;
        Timeline.Project = _project;
        _selClip = null;
        _selText = null;
        _curClip = null;
        _curSource = "";
        _time = Math.Min(_time, _project.Duration);
        Timeline.Time = _time;
        MarkDirty();
        RefreshAll();
        RebuildTextOverlays();
        SeekPlayer(_time);
    }

    // ------------------------------------------------------------------ shortcuts

    bool AnyOverlayOpen => ExportOverlay.Visibility == Visibility.Visible
                           || DownloadOverlay.Visibility == Visibility.Visible
                           || HelpOverlay.Visibility == Visibility.Visible;

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (AnyOverlayOpen)
        {
            if (key == Key.Escape)
            {
                if (HelpOverlay.Visibility == Visibility.Visible) HelpOverlay.Visibility = Visibility.Collapsed;
                else if (ExportOverlay.Visibility == Visibility.Visible && !_exporting) ExportOverlay.Visibility = Visibility.Collapsed;
                else if (DownloadOverlay.Visibility == Visibility.Visible && !_downloading) DownloadOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            return;
        }

        var typing = Keyboard.FocusedElement is TextBox;
        if (typing)
        {
            if (ctrl && key == Key.S) { SaveProject(shift); e.Handled = true; }
            else if (ctrl && key == Key.E) { OpenExport(); e.Handled = true; }
            return;
        }

        e.Handled = true;
        if (ctrl)
        {
            switch (key)
            {
                case Key.Z: if (shift) DoRedo(); else DoUndo(); break;
                case Key.Y: DoRedo(); break;
                case Key.S: SaveProject(shift); break;
                case Key.O: OpenProjectDialog(); break;
                case Key.N: NewProject(); break;
                case Key.I: ImportFiles_Click(this, e); break;
                case Key.E: OpenExport(); break;
                case Key.D: DuplicateSelected(); break;
                default: e.Handled = false; break;
            }
            return;
        }

        switch (key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.S:
            case Key.B: SplitAtPlayhead(); break;
            case Key.Delete:
            case Key.Back: DeleteSelection(); break;
            case Key.T: AddTextAtPlayhead(); break;
            case Key.Left: Step(shift ? -1.0 : -1.0 / 30.0); break;
            case Key.Right: Step(shift ? 1.0 : 1.0 / 30.0); break;
            case Key.Home: Pause(); SetTime(0, true); ScrollToTime(); break;
            case Key.End: Pause(); SetTime(_project.Duration, true); ScrollToTime(); break;
            case Key.OemOpenBrackets: TrimToPlayhead(true); break;
            case Key.OemCloseBrackets: TrimToPlayhead(false); break;
            case Key.OemPlus:
            case Key.Add: ZoomBy(1.25); break;
            case Key.OemMinus:
            case Key.Subtract: ZoomBy(0.8); break;
            case Key.F1: HelpOverlay.Visibility = Visibility.Visible; break;
            case Key.Escape: Timeline.Select(null, null); break;
            default: e.Handled = false; break;
        }
    }

    // ------------------------------------------------------------------ help

    void Help_Click(object sender, RoutedEventArgs e) => HelpOverlay.Visibility = Visibility.Visible;
    void HelpClose_Click(object sender, RoutedEventArgs e) => HelpOverlay.Visibility = Visibility.Collapsed;
    void HelpBackdrop_Click(object sender, MouseButtonEventArgs e) => HelpOverlay.Visibility = Visibility.Collapsed;
    void HelpCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
    void GitHub_Click(object sender, RoutedEventArgs e) => Win.Open("https://github.com/aroxwex/Grab-Editor");

    void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var size = MediaService.CacheBytes();
        MediaService.ClearCache();
        _thumbs.Clear();
        Timeline.Refresh();
        BuildMediaTiles();
        Toast("Cleared " + Fmt.Size(size) + ". Preview copies are made again when needed.");
        foreach (var m in _project.Media) _ = PrepareMediaAsync(m);
    }

    // ------------------------------------------------------------------ closing and recovery

    void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exporting)
        {
            var r = MessageBox.Show(this, "An export is still running. Cancel it and close?", "Grab Editor",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _expCancelled = true;
            Proc.Kill(_expProc);
        }
        if (!ConfirmDiscard()) { e.Cancel = true; return; }

        try { _dlCts?.Cancel(); } catch { }
        try { Player.Close(); } catch { }
        try { if (File.Exists(AutosavePath)) File.Delete(AutosavePath); } catch { }
    }

    /// <summary>Saves a copy now and then so a crash or power cut doesn't lose the work.</summary>
    void Autosave()
    {
        if (!_dirty || _project.Clips.Count == 0) return;
        try
        {
            File.WriteAllText(AutosavePath, _project.ToJson());
        }
        catch
        {
            // not critical
        }
    }

    void OfferRecovery()
    {
        try
        {
            if (!File.Exists(AutosavePath) || _project.Clips.Count > 0) return;
            var json = File.ReadAllText(AutosavePath);
            var r = MessageBox.Show(this, "Grab Editor didn't close properly last time.\n\nDo you want to get your unsaved project back?",
                "Grab Editor", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                LoadProject(Project.FromJson(json), "");
                _dirty = true;
                UpdateTitle();
            }
            File.Delete(AutosavePath);
        }
        catch
        {
            // ignore a damaged recovery file
        }
    }
}
