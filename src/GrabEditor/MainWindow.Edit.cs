using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GrabEditor.Models;
using GrabEditor.Services;

namespace GrabEditor;

// Editing: split, delete, duplicate, text, the inspector on the right and timeline zoom.
public partial class MainWindow
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------ helpers

    static string FormatSpeed(double v) => v.ToString("0.##", Inv) + "×";
    static string FormatPercent(double v) => ((int)Math.Round(v * 100)).ToString(Inv) + "%";
    static string FormatSeconds(double v) => v.ToString("0.0", Inv) + " s";

    static bool TryNumber(string text, out double value)
    {
        var s = (text ?? "").Replace(",", ".").Replace("×", "").Replace("x", "").Replace("%", "").Replace("s", "").Trim();
        return double.TryParse(s, NumberStyles.Float, Inv, out value);
    }

    /// <summary>Called after the structure of the timeline changed (split, delete, add, undo...).</summary>
    void AfterStructureEdit()
    {
        _project.Layout();
        if (_time > _project.Duration) _time = _project.Duration;
        Timeline.Time = _time;
        MarkDirty();
        RefreshAll();
        SeekPlayer(_time);
    }

    /// <summary>Called after a value of the selected clip changed.</summary>
    void AfterClipEdit()
    {
        _project.Layout();
        Timeline.Refresh();
        UpdateInfoTexts();
        UpdateClipInfoLine();
        MarkDirty();
        if (_time > _project.Duration) { _time = _project.Duration; Timeline.Time = _time; }
        UpdateTimeText();
        if (_selClip != null && _curClip == _selClip)
        {
            Player.SpeedRatio = _selClip.Speed;
            Player.Volume = Math.Min(1.0, _selClip.Volume);
            Player.Stretch = _selClip.Fill ? System.Windows.Media.Stretch.UniformToFill : System.Windows.Media.Stretch.Uniform;
        }
    }

    // ------------------------------------------------------------------ split / delete / duplicate

    void Split_Click(object sender, RoutedEventArgs e) => SplitAtPlayhead();
    void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();
    void DupClip_Click(object sender, RoutedEventArgs e) => DuplicateSelected();
    void AddText_Click(object sender, RoutedEventArgs e) => AddTextAtPlayhead();

    ClipItem ClipUnderPlayhead()
    {
        bool Inside(ClipItem c) => _time > c.Start + 0.05 && _time < c.End - 0.05;
        if (_selClip != null && Inside(_selClip)) return _selClip;
        return _project.Clips.FirstOrDefault(Inside);
    }

    void SplitAtPlayhead()
    {
        if (_project.Clips.Count == 0) return;
        var clip = ClipUnderPlayhead();
        if (clip == null)
        {
            Toast("Move the playhead onto a clip to split it");
            return;
        }
        PushUndo();
        var cut = clip.In + (_time - clip.Start) * clip.Speed;
        var second = clip.Copy();
        second.In = cut;
        second.FadeIn = 0;
        clip.Out = cut;
        clip.FadeOut = 0;
        _project.Clips.Insert(_project.Clips.IndexOf(clip) + 1, second);
        AfterStructureEdit();
        Timeline.Select(second, null);
        Toast("Split");
    }

    void DeleteSelection()
    {
        if (_selText != null)
        {
            PushUndo();
            _project.Texts.Remove(_selText);
            Timeline.Select(null, null);
            AfterStructureEdit();
            Toast("Text deleted");
        }
        else if (_selClip != null)
        {
            PushUndo();
            _project.Clips.Remove(_selClip);
            Timeline.Select(null, null);
            AfterStructureEdit();
            Toast("Clip deleted");
        }
        else
        {
            Toast("Click a clip or a text first");
        }
    }

    void DuplicateSelected()
    {
        if (_selClip == null)
        {
            Toast("Click a clip first");
            return;
        }
        PushUndo();
        var copy = _selClip.Copy();
        _project.Clips.Insert(_project.Clips.IndexOf(_selClip) + 1, copy);
        AfterStructureEdit();
        Timeline.Select(copy, null);
        Toast("Duplicated");
    }

    void TrimToPlayhead(bool start)
    {
        var clip = ClipUnderPlayhead();
        if (clip == null)
        {
            Toast("Move the playhead onto a clip first");
            return;
        }
        PushUndo();
        var cut = clip.In + (_time - clip.Start) * clip.Speed;
        if (start)
        {
            clip.In = cut;
            _project.Layout();
            _time = clip.Start;
        }
        else
        {
            clip.Out = cut;
            _project.Layout();
        }
        Timeline.Select(clip, null);
        AfterStructureEdit();
    }

    void AddTextAtPlayhead()
    {
        if (_project.Clips.Count == 0)
        {
            Toast("Add a clip first, then put text on it");
            return;
        }
        PushUndo();
        var start = _time;
        if (_project.Duration - start < 0.6) start = Math.Max(0, _project.Duration - 3);
        var item = new TextItem
        {
            Start = start,
            Duration = Math.Max(0.5, Math.Min(3, _project.Duration - start)),
        };
        _project.Texts.Add(item);
        Timeline.Select(null, item);
        AfterStructureEdit();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TextEdit.Focus();
            TextEdit.SelectAll();
        }), DispatcherPriority.Input);
    }

    // ------------------------------------------------------------------ project settings

    void Aspect_Click(object sender, RoutedEventArgs e)
    {
        var aspect = (string)((RadioButton)sender).Tag;
        if (aspect == _project.Aspect) return;
        PushUndo();
        _project.Aspect = aspect;
        MarkDirty();
        FitPreviewFrame();
        UpdateInfoTexts();
        Toast("Frame shape " + aspect + ". Clips are fitted with bars; choose Fill on a clip to crop instead.");
    }

    // ------------------------------------------------------------------ the inspector

    void UpdateClipInfoLine()
    {
        if (_selClip == null) return;
        ClipInfo.Text = Fmt.TimeTenths(_selClip.Length) + " on the timeline   ·   source " +
                        Fmt.TimeTenths(_selClip.In) + " – " + Fmt.TimeTenths(_selClip.Out);
    }

    void UpdateInspector()
    {
        if (!_ready) return;
        _sync = true;
        try
        {
            InspectorEmpty.Visibility = _selClip == null && _selText == null ? Visibility.Visible : Visibility.Collapsed;
            ClipPanel.Visibility = _selClip != null ? Visibility.Visible : Visibility.Collapsed;
            TextPanel.Visibility = _selText != null ? Visibility.Visible : Visibility.Collapsed;

            if (_selClip != null)
            {
                var media = _project.MediaOf(_selClip);
                ClipTitle.Text = media != null ? media.Name : "Clip";
                UpdateClipInfoLine();
                SpeedSlider.Value = _selClip.Speed;
                SpeedBox.Text = FormatSpeed(_selClip.Speed);
                VolSlider.Value = Math.Round(_selClip.Volume * 100);
                VolBox.Text = FormatPercent(_selClip.Volume);
                FadeInSlider.Value = _selClip.FadeIn;
                FadeInBox.Text = FormatSeconds(_selClip.FadeIn);
                FadeOutSlider.Value = _selClip.FadeOut;
                FadeOutBox.Text = FormatSeconds(_selClip.FadeOut);
                RbFit.IsChecked = !_selClip.Fill;
                RbFill.IsChecked = _selClip.Fill;
            }

            if (_selText != null)
            {
                if (TextEdit.Text != _selText.Text) TextEdit.Text = _selText.Text;
                TextSizeSlider.Value = _selText.Size;
                TextSizeBox.Text = _selText.Size.ToString("0.#", Inv);
                RbPosTop.IsChecked = _selText.Position == "top";
                RbPosMid.IsChecked = _selText.Position == "center";
                RbPosBottom.IsChecked = _selText.Position != "top" && _selText.Position != "center";
                foreach (var sw in new[] { Sw1, Sw2, Sw3, Sw4, Sw5, Sw6 })
                    sw.IsChecked = string.Equals((string)sw.Tag, _selText.Color, StringComparison.OrdinalIgnoreCase);
                BoxSwitch.IsChecked = _selText.Box;
                TextStartBox.Text = Fmt.TimeTenths(_selText.Start);
                TextDurBox.Text = FormatSeconds(_selText.Duration);
            }
        }
        finally
        {
            _sync = false;
        }
    }

    // one undo step per gesture: the snapshot is taken when the mouse goes down on a slider / the box gets focus
    void Gesture_Start(object sender, MouseButtonEventArgs e) => PushUndo();
    void Gesture_Start2(object sender, KeyboardFocusChangedEventArgs e) => PushUndo();

    void NumberBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    // ---- clip values

    void SpeedSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync || _selClip == null) return;
        _selClip.Speed = Math.Round(e.NewValue, 2);
        _sync = true; SpeedBox.Text = FormatSpeed(_selClip.Speed); _sync = false;
        AfterClipEdit();
    }

    void SpeedBox_Commit(object sender, RoutedEventArgs e)
    {
        if (_sync || _selClip == null) return;
        if (TryNumber(SpeedBox.Text, out var v))
        {
            PushUndo();
            _selClip.Speed = Math.Max(0.25, Math.Min(4, Math.Round(v, 2)));
            AfterClipEdit();
        }
        UpdateInspector();
    }

    void VolSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync || _selClip == null) return;
        _selClip.Volume = Math.Round(e.NewValue) / 100.0;
        _sync = true; VolBox.Text = FormatPercent(_selClip.Volume); _sync = false;
        AfterClipEdit();
    }

    void VolBox_Commit(object sender, RoutedEventArgs e)
    {
        if (_sync || _selClip == null) return;
        if (TryNumber(VolBox.Text, out var v))
        {
            PushUndo();
            _selClip.Volume = Math.Max(0, Math.Min(200, Math.Round(v))) / 100.0;
            AfterClipEdit();
        }
        UpdateInspector();
    }

    void FadeInSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync || _selClip == null) return;
        _selClip.FadeIn = Math.Min(Math.Round(e.NewValue, 1), Math.Max(0, _selClip.Length - _selClip.FadeOut));
        _sync = true; FadeInBox.Text = FormatSeconds(_selClip.FadeIn); _sync = false;
        AfterClipEdit();
    }

    void FadeInBox_Commit(object sender, RoutedEventArgs e)
    {
        if (_sync || _selClip == null) return;
        if (TryNumber(FadeInBox.Text, out var v))
        {
            PushUndo();
            _selClip.FadeIn = Math.Max(0, Math.Min(Math.Min(10, v), Math.Max(0, _selClip.Length - _selClip.FadeOut)));
            AfterClipEdit();
        }
        UpdateInspector();
    }

    void FadeOutSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync || _selClip == null) return;
        _selClip.FadeOut = Math.Min(Math.Round(e.NewValue, 1), Math.Max(0, _selClip.Length - _selClip.FadeIn));
        _sync = true; FadeOutBox.Text = FormatSeconds(_selClip.FadeOut); _sync = false;
        AfterClipEdit();
    }

    void FadeOutBox_Commit(object sender, RoutedEventArgs e)
    {
        if (_sync || _selClip == null) return;
        if (TryNumber(FadeOutBox.Text, out var v))
        {
            PushUndo();
            _selClip.FadeOut = Math.Max(0, Math.Min(Math.Min(10, v), Math.Max(0, _selClip.Length - _selClip.FadeIn)));
            AfterClipEdit();
        }
        UpdateInspector();
    }

    void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (_sync || _selClip == null) return;
        PushUndo();
        _selClip.Fill = RbFill.IsChecked == true;
        AfterClipEdit();
    }

    // ---- text values

    void TextEdit_Changed(object sender, TextChangedEventArgs e)
    {
        if (_sync || _selText == null) return;
        _selText.Text = TextEdit.Text;
        RebuildTextOverlays();
        Timeline.Refresh();
        MarkDirty();
    }

    void TextSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync || _selText == null) return;
        _selText.Size = Math.Round(e.NewValue * 2) / 2;
        _sync = true; TextSizeBox.Text = _selText.Size.ToString("0.#", Inv); _sync = false;
        RebuildTextOverlays();
        MarkDirty();
    }

    void TextSizeBox_Commit(object sender, RoutedEventArgs e)
    {
        if (_sync || _selText == null) return;
        if (TryNumber(TextSizeBox.Text, out var v))
        {
            PushUndo();
            _selText.Size = Math.Max(2, Math.Min(20, v));
            RebuildTextOverlays();
            MarkDirty();
        }
        UpdateInspector();
    }

    void TextPos_Click(object sender, RoutedEventArgs e)
    {
        if (_sync || _selText == null) return;
        PushUndo();
        _selText.Position = (string)((RadioButton)sender).Tag;
        RebuildTextOverlays();
        MarkDirty();
    }

    void TextColor_Click(object sender, RoutedEventArgs e)
    {
        if (_sync || _selText == null) return;
        PushUndo();
        _selText.Color = (string)((RadioButton)sender).Tag;
        RebuildTextOverlays();
        MarkDirty();
    }

    void BoxSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_sync || _selText == null) return;
        PushUndo();
        _selText.Box = BoxSwitch.IsChecked == true;
        RebuildTextOverlays();
        MarkDirty();
    }

    void TextTime_Commit(object sender, RoutedEventArgs e)
    {
        if (_sync || _selText == null) return;
        PushUndo();
        if (Fmt.TryParseTime(TextStartBox.Text.Replace(",", "."), out var start))
            _selText.Start = Math.Max(0, start);
        if (TryNumber(TextDurBox.Text, out var dur))
            _selText.Duration = Math.Max(0.3, dur);
        RebuildTextOverlays();
        Timeline.Refresh();
        MarkDirty();
        UpdateInspector();
    }

    // ------------------------------------------------------------------ timeline view

    void Snap_Click(object sender, RoutedEventArgs e)
    {
        Timeline.Magnet = SnapSwitch.IsChecked == true;
        EditorSettings.Current.Snap = Timeline.Magnet;
        EditorSettings.Save();
        Toast(Timeline.Magnet ? "Snapping on" : "Snapping off");
    }

    void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        var anchor = Timeline.XOf(_time) - TimelineScroll.HorizontalOffset;
        Timeline.Pps = Math.Exp(ZoomSlider.Value);
        Timeline.UpdateLayout();
        TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, Timeline.XOf(_time) - anchor));
    }

    void ZoomBy(double factor)
    {
        ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, Math.Min(ZoomSlider.Maximum, Math.Log(Timeline.Pps * factor)));
    }

    void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(0.8);

    void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        var available = Math.Max(100, TimelineScroll.ViewportWidth - 40);
        var duration = Math.Max(1, _project.Duration);
        ZoomSlider.Value = Math.Max(ZoomSlider.Minimum, Math.Min(ZoomSlider.Maximum, Math.Log(Math.Max(8, available / duration))));
        TimelineScroll.ScrollToHorizontalOffset(0);
    }

    void TimelineScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15);
        else
            TimelineScroll.ScrollToHorizontalOffset(TimelineScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
