using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GrabEditor.Models;
using GrabEditor.Services;

namespace GrabEditor.Controls;

/// <summary>
/// The timeline: a ruler, a text lane and a video lane.
/// Click the ruler or empty space to move the playhead. Drag a clip to reorder it, drag its edges to trim it.
/// Drag a text block to move it, drag its right edge to change how long it stays.
/// Edges snap to each other and to the playhead (hold Alt to turn snapping off for one drag).
/// </summary>
public class TimelineView : FrameworkElement
{
    public const double TotalHeight = 172;
    const double RulerH = 30;
    const double Pad = 10;
    const double TextY = 38, TextH = 40;
    const double VideoY = 86, VideoH = 72;
    const double Edge = 7;
    const double SnapPx = 8;
    const double MinClip = 0.2;

    static Brush Make(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        b.Freeze();
        return b;
    }

    static readonly Brush LaneBrush = Make("#151515");
    static readonly Brush ClipBrush = Make("#2B2B2B");
    static readonly Brush TextBlockBrush = Make("#3A3A3A");
    static readonly Brush TextBlockSelBrush = Make("#505050");
    static readonly Brush TextBrush = Make("#EDEDED");
    static readonly Brush MutedBrush = Make("#8A8A8A");
    static readonly Brush WhiteBrush = Make("#FFFFFF");
    static readonly Brush ShadeBrush = Make("#99000000");
    static readonly Brush GhostBrush = Make("#66FFFFFF");
    static readonly Brush HandleBrush = Make("#FFFFFF");
    static readonly Pen MajorPen = MakePen("#5A5A5A", 1);
    static readonly Pen MinorPen = MakePen("#363636", 1);
    static readonly Pen SelectPen = MakePen("#FFFFFF", 2);
    static readonly Pen PlayheadPen = MakePen("#FFFFFF", 2);
    static readonly Pen SnapPen = MakePen("#AAFFFFFF", 1);
    static readonly Pen DropPen = MakePen("#FFFFFF", 3);

    static Pen MakePen(string hex, double w)
    {
        var p = new Pen(Make(hex), w);
        p.Freeze();
        return p;
    }

    enum Mode { None, Scrub, ClipMove, TrimLeft, TrimRight, TextMove, TextResize }

    Project _p;
    double _pps = 80;
    double _time;
    ClipItem _selClip;
    TextItem _selText;

    Mode _mode;
    Point _down;
    bool _moved;
    bool _editStarted;
    ClipItem _dragClip;
    TextItem _dragText;
    double _origA;          // the value being changed when the drag began
    double _origB;
    double _ghostX;
    int _dropIndex = -1;
    double _snapTime = -1;
    int _insertAt = -1;

    /// <summary>Gives the picture to draw inside a clip (by media id). May return null.</summary>
    public Func<string, ImageSource> ThumbProvider;
    public bool Magnet = true;

    public event Action<double> TimeRequested;
    public event Action SelectionChanged;
    public event Action EditStarting;
    public event Action Edited;
    public event Action EditFinished;
    public event Action<string, int> MediaDropped;

    public TimelineView()
    {
        Focusable = false;
        AllowDrop = true;
        SnapsToDevicePixels = true;
    }

    // ------------------------------------------------------------------ state

    public Project Project
    {
        get => _p;
        set
        {
            _p = value;
            _selClip = null;
            _selText = null;
            Refresh();
        }
    }

    public double Pps
    {
        get => _pps;
        set
        {
            _pps = Math.Max(8, Math.Min(600, value));
            Refresh();
        }
    }

    public double Time
    {
        get => _time;
        set
        {
            _time = Math.Max(0, value);
            InvalidateVisual();
        }
    }

    public ClipItem SelectedClip => _selClip;
    public TextItem SelectedText => _selText;

    public void Select(ClipItem clip, TextItem text)
    {
        _selClip = clip;
        _selText = text;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    public double XOf(double seconds) => X(seconds);

    double X(double t) => Pad + t * _pps;
    double T(double x) => (x - Pad) / _pps;

    Rect ClipRect(ClipItem c) => new Rect(X(c.Start) + 1, VideoY + 3, Math.Max(3, c.Length * _pps - 2), VideoH - 6);
    Rect TextRect(TextItem t) => new Rect(X(t.Start), TextY + 3, Math.Max(12, t.Duration * _pps), TextH - 6);

    protected override Size MeasureOverride(Size availableSize)
    {
        var duration = _p != null ? _p.Duration : 0;
        return new Size(Pad * 2 + (Math.Max(duration, 5) + 20) * _pps, TotalHeight);
    }

    FormattedText Txt(string s, double size, Brush brush, double maxWidth, bool bold = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        ft.MaxTextWidth = Math.Max(1, maxWidth);
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        return ft;
    }

    // ------------------------------------------------------------------ drawing

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        dc.DrawRoundedRectangle(LaneBrush, null, new Rect(0, TextY, w, TextH), 8, 8);
        dc.DrawRoundedRectangle(LaneBrush, null, new Rect(0, VideoY, w, VideoH), 8, 8);
        DrawRuler(dc, w);

        if (_p == null || _p.Clips.Count == 0)
        {
            var hint = Txt("Drop clips here, or double-click one in the Media panel", 13, MutedBrush, w - 40);
            dc.DrawText(hint, new Point(20, VideoY + VideoH / 2 - 9));
        }

        if (_p != null)
        {
            foreach (var c in _p.Clips) DrawClip(dc, c);
            foreach (var t in _p.Texts) DrawText(dc, t);
        }

        // drag helpers
        if (_mode == Mode.ClipMove && _moved && _dragClip != null)
        {
            var r = ClipRect(_dragClip);
            r.X += _ghostX - _down.X;
            dc.PushOpacity(0.6);
            dc.DrawRoundedRectangle(ClipBrush, SelectPen, r, 6, 6);
            dc.Pop();
            DrawInsertLine(dc, _insertAt);
        }
        if (_dropIndex >= 0) DrawInsertLine(dc, _dropIndex);

        if (_snapTime >= 0)
        {
            var sx = X(_snapTime);
            dc.DrawLine(SnapPen, new Point(sx, RulerH), new Point(sx, h));
        }

        // playhead
        var px = X(_time);
        dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(px - 7, 2), true, true);
            ctx.LineTo(new Point(px + 7, 2), true, false);
            ctx.LineTo(new Point(px, 15), true, false);
        }
        head.Freeze();
        dc.DrawGeometry(WhiteBrush, null, head);
    }

    void DrawInsertLine(DrawingContext dc, int index)
    {
        if (_p == null || index < 0) return;
        var clips = _dragClip != null && _mode == Mode.ClipMove ? _p.Clips.Where(c => c != _dragClip).ToList() : _p.Clips;
        var t = index <= 0 ? 0 : (index - 1 < clips.Count ? clips[index - 1].End : _p.Duration);
        if (_dragClip != null && _mode == Mode.ClipMove)
        {
            // positions of the other clips close up around the dragged one, so measure them without it
            double acc = 0;
            for (var i = 0; i < index && i < clips.Count; i++) acc += clips[i].Length;
            t = acc;
        }
        var x = X(t);
        dc.DrawLine(DropPen, new Point(x, VideoY - 2), new Point(x, VideoY + VideoH + 2));
    }

    void DrawRuler(DrawingContext dc, double w)
    {
        double[] steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800 };
        var step = steps[steps.Length - 1];
        foreach (var s in steps)
        {
            if (s * _pps >= 80) { step = s; break; }
        }
        var minor = step / 5;
        var count = (int)((w - Pad) / (minor * _pps)) + 1;
        if (count > 6000) { minor = step; count = (int)((w - Pad) / (minor * _pps)) + 1; }
        var stride = Math.Abs(minor - step) < 1e-9 ? 1 : 5;

        for (var k = 0; k <= count; k++)
        {
            var t = k * minor;
            var x = X(t);
            var major = k % stride == 0;
            dc.DrawLine(major ? MajorPen : MinorPen, new Point(x, major ? 16 : 23), new Point(x, RulerH - 3));
            if (major)
                dc.DrawText(Txt(step < 1 ? Fmt.TimeTenths(t) : Fmt.Time(t), 11, MutedBrush, 90), new Point(x + 4, 1));
        }
    }

    void DrawClip(DrawingContext dc, ClipItem c)
    {
        var r = ClipRect(c);
        var selected = c == _selClip;

        dc.DrawRoundedRectangle(ClipBrush, null, r, 6, 6);

        dc.PushClip(new RectangleGeometry(r, 6, 6));
        var thumb = ThumbProvider?.Invoke(c.MediaId);
        if (thumb != null && thumb.Width > 0 && thumb.Height > 0)
        {
            var tileW = Math.Max(20, r.Height * (thumb.Width / thumb.Height));
            for (var x = r.Left; x < r.Right; x += tileW)
                dc.DrawImage(thumb, new Rect(x, r.Top, tileW, r.Height));
        }
        dc.DrawRectangle(ShadeBrush, null, new Rect(r.Left, r.Bottom - 22, r.Width, 22));
        dc.Pop();

        if (r.Width > 36)
        {
            var media = _p.MediaOf(c);
            var label = (media != null ? media.Name : "clip");
            if (Math.Abs(c.Speed - 1.0) > 0.001) label += "  ·  " + c.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
            dc.DrawText(Txt(label, 11.5, TextBrush, r.Width - 14, true), new Point(r.Left + 8, r.Bottom - 19));
        }

        if (selected)
        {
            dc.DrawRoundedRectangle(null, SelectPen, r, 6, 6);
            var hh = Math.Min(28, r.Height - 12);
            dc.DrawRoundedRectangle(HandleBrush, null, new Rect(r.Left + 2, r.Top + (r.Height - hh) / 2, 4, hh), 2, 2);
            dc.DrawRoundedRectangle(HandleBrush, null, new Rect(r.Right - 6, r.Top + (r.Height - hh) / 2, 4, hh), 2, 2);
        }

        if (c.FadeIn > 0.01 || c.FadeOut > 0.01)
            dc.DrawText(Txt("fade", 10, MutedBrush, 40), new Point(r.Left + 8, r.Top + 3));
    }

    void DrawText(DrawingContext dc, TextItem t)
    {
        var r = TextRect(t);
        var selected = t == _selText;
        dc.DrawRoundedRectangle(selected ? TextBlockSelBrush : TextBlockBrush, selected ? SelectPen : null, r, 6, 6);
        if (r.Width > 28)
        {
            var first = (t.Text ?? "").Replace("\r", "").Split('\n')[0];
            dc.DrawText(Txt("T  " + first, 11.5, TextBrush, r.Width - 16), new Point(r.Left + 8, r.Top + (r.Height - 15) / 2));
        }
        if (selected)
            dc.DrawRoundedRectangle(HandleBrush, null, new Rect(r.Right - 6, r.Top + 6, 4, r.Height - 12), 2, 2);
    }

    // ------------------------------------------------------------------ hit tests and snapping

    ClipItem HitClip(double x)
    {
        if (_p == null) return null;
        foreach (var c in _p.Clips)
        {
            var r = ClipRect(c);
            if (x >= r.Left - 1 && x <= r.Right + 1) return c;
        }
        return null;
    }

    TextItem HitText(double x)
    {
        if (_p == null) return null;
        // the last one drawn is on top
        for (var i = _p.Texts.Count - 1; i >= 0; i--)
        {
            var r = TextRect(_p.Texts[i]);
            if (x >= r.Left && x <= r.Right) return _p.Texts[i];
        }
        return null;
    }

    List<double> SnapTargets(ClipItem exceptClip, TextItem exceptText, bool includePlayhead)
    {
        var list = new List<double> { 0 };
        if (includePlayhead) list.Add(_time);
        if (_p != null)
        {
            foreach (var c in _p.Clips) { if (c == exceptClip) continue; list.Add(c.Start); list.Add(c.End); }
            foreach (var t in _p.Texts) { if (t == exceptText) continue; list.Add(t.Start); list.Add(t.End); }
        }
        return list;
    }

    double Snap(double time, List<double> targets)
    {
        _snapTime = -1;
        if (!Magnet || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return time;
        var best = time;
        var bestDist = SnapPx;
        foreach (var s in targets)
        {
            var d = Math.Abs(X(s) - X(time));
            if (d < bestDist) { bestDist = d; best = s; _snapTime = s; }
        }
        return best;
    }

    int ReorderIndex(ClipItem dragged, double centerX)
    {
        var index = 0;
        foreach (var c in _p.Clips)
        {
            if (c == dragged) continue;
            var r = ClipRect(c);
            if (r.Left + r.Width / 2 < centerX) index++;
        }
        return index;
    }

    // ------------------------------------------------------------------ mouse

    void ScrubTo(double x)
    {
        var duration = _p != null ? _p.Duration : 0;
        var t = Math.Max(0, Math.Min(duration, Snap(T(x), SnapTargets(null, null, false))));
        _time = t;
        InvalidateVisual();
        TimeRequested?.Invoke(t);
    }

    void BeginEdit()
    {
        if (_editStarted) return;
        _editStarted = true;
        EditStarting?.Invoke();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_p == null) return;
        var p = e.GetPosition(this);
        _down = p;
        _moved = false;
        _editStarted = false;
        _snapTime = -1;
        _dragClip = null;
        _dragText = null;
        _mode = Mode.None;
        CaptureMouse();

        if (p.Y < RulerH)
        {
            _mode = Mode.Scrub;
            ScrubTo(p.X);
        }
        else if (p.Y >= TextY && p.Y <= TextY + TextH)
        {
            var t = HitText(p.X);
            if (t != null)
            {
                Select(null, t);
                _dragText = t;
                var r = TextRect(t);
                _origA = t.Start;
                _origB = t.Duration;
                _mode = p.X >= r.Right - Math.Min(Edge, r.Width / 3) ? Mode.TextResize : Mode.TextMove;
            }
            else
            {
                Select(null, null);
                _mode = Mode.Scrub;
                ScrubTo(p.X);
            }
        }
        else if (p.Y >= VideoY && p.Y <= VideoY + VideoH)
        {
            var c = HitClip(p.X);
            if (c != null)
            {
                Select(c, null);
                _dragClip = c;
                var r = ClipRect(c);
                var edge = Math.Min(Edge, r.Width / 3);
                _origA = c.In;
                _origB = c.Out;
                if (p.X <= r.Left + edge) _mode = Mode.TrimLeft;
                else if (p.X >= r.Right - edge) _mode = Mode.TrimRight;
                else _mode = Mode.ClipMove;
            }
            else
            {
                Select(null, null);
                _mode = Mode.Scrub;
                ScrubTo(p.X);
            }
        }
        else
        {
            _mode = Mode.Scrub;
            ScrubTo(p.X);
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_mode == Mode.None || !IsMouseCaptured)
        {
            UpdateCursor(p);
            return;
        }

        var dx = p.X - _down.X;
        switch (_mode)
        {
            case Mode.Scrub:
                ScrubTo(p.X);
                break;

            case Mode.TrimRight:
                if (Math.Abs(dx) < 2 && !_editStarted) break;
                BeginEdit();
                {
                    var c = _dragClip;
                    var media = _p.MediaOf(c);
                    var end = Snap(c.Start + (_origB - _origA) / c.Speed + dx / _pps, SnapTargets(c, null, true));
                    var length = Math.Max(MinClip, end - c.Start);
                    var newOut = c.In + length * c.Speed;
                    if (media != null) newOut = Math.Min(newOut, media.Duration);
                    c.Out = Math.Max(c.In + MinClip * c.Speed, newOut);
                    Changed();
                }
                break;

            case Mode.TrimLeft:
                if (Math.Abs(dx) < 2 && !_editStarted) break;
                BeginEdit();
                {
                    var c = _dragClip;
                    var target = Snap(c.Start + dx / _pps, SnapTargets(c, null, true));
                    var newIn = _origA + (target - c.Start) * c.Speed;
                    c.In = Math.Max(0, Math.Min(newIn, c.Out - MinClip * c.Speed));
                    Changed();
                }
                break;

            case Mode.TextMove:
                if (Math.Abs(dx) < 2 && !_editStarted) break;
                BeginEdit();
                {
                    var t = _dragText;
                    var targets = SnapTargets(null, t, true);
                    var start = Math.Max(0, _origA + dx / _pps);
                    var snapped = Snap(start, targets);
                    if (_snapTime >= 0) start = snapped;
                    else
                    {
                        var endSnapped = Snap(start + t.Duration, targets);
                        if (_snapTime >= 0) start = Math.Max(0, endSnapped - t.Duration);
                    }
                    t.Start = start;
                    Changed();
                }
                break;

            case Mode.TextResize:
                if (Math.Abs(dx) < 2 && !_editStarted) break;
                BeginEdit();
                {
                    var t = _dragText;
                    var end = Snap(t.Start + _origB + dx / _pps, SnapTargets(null, t, true));
                    t.Duration = Math.Max(0.3, end - t.Start);
                    Changed();
                }
                break;

            case Mode.ClipMove:
                if (!_moved && Math.Abs(dx) < 5) break;
                _moved = true;
                _ghostX = p.X;
                {
                    var r = ClipRect(_dragClip);
                    _insertAt = ReorderIndex(_dragClip, r.Left + r.Width / 2 + dx);
                }
                InvalidateVisual();
                break;
        }
    }

    void Changed()
    {
        _p.Layout();
        Edited?.Invoke();
        Refresh();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_mode == Mode.ClipMove && _moved && _dragClip != null)
        {
            var from = _p.Clips.IndexOf(_dragClip);
            var to = _insertAt;
            if (to >= 0 && to != from)
            {
                BeginEdit();
                _p.Clips.RemoveAt(from);
                _p.Clips.Insert(Math.Min(to, _p.Clips.Count), _dragClip);
                Changed();
            }
        }

        var finished = _editStarted;
        _mode = Mode.None;
        _moved = false;
        _editStarted = false;
        _snapTime = -1;
        _insertAt = -1;
        _dragClip = null;
        _dragText = null;
        ReleaseMouseCapture();
        InvalidateVisual();
        if (finished) EditFinished?.Invoke();
    }

    void UpdateCursor(Point p)
    {
        Cursor = Cursors.Arrow;
        if (_p == null) return;
        if (p.Y >= VideoY && p.Y <= VideoY + VideoH)
        {
            var c = HitClip(p.X);
            if (c == null) return;
            var r = ClipRect(c);
            var edge = Math.Min(Edge, r.Width / 3);
            Cursor = p.X <= r.Left + edge || p.X >= r.Right - edge ? Cursors.SizeWE : Cursors.Hand;
        }
        else if (p.Y >= TextY && p.Y <= TextY + TextH)
        {
            var t = HitText(p.X);
            if (t == null) return;
            var r = TextRect(t);
            Cursor = p.X >= r.Right - Math.Min(Edge, r.Width / 3) ? Cursors.SizeWE : Cursors.Hand;
        }
    }

    // ------------------------------------------------------------------ dropping media from the library

    protected override void OnDragOver(DragEventArgs e)
    {
        if (e.Data.GetDataPresent("grab-media") && _p != null)
        {
            e.Effects = DragDropEffects.Copy;
            var x = e.GetPosition(this).X;
            var index = 0;
            foreach (var c in _p.Clips)
            {
                var r = ClipRect(c);
                if (r.Left + r.Width / 2 < x) index++;
            }
            if (index != _dropIndex)
            {
                _dropIndex = index;
                InvalidateVisual();
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        _dropIndex = -1;
        InvalidateVisual();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        var index = _dropIndex;
        _dropIndex = -1;
        InvalidateVisual();
        if (e.Data.GetDataPresent("grab-media") && index >= 0)
        {
            var id = e.Data.GetData("grab-media") as string;
            if (!string.IsNullOrEmpty(id)) MediaDropped?.Invoke(id, index);
            e.Handled = true;
        }
    }
}
