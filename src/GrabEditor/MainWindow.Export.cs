using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GrabEditor.Models;
using GrabEditor.Services;
using Microsoft.Win32;

namespace GrabEditor;

// Exporting: the timeline becomes one ffmpeg command (see ExportBuilder).
public partial class MainWindow
{
    bool _exporting;
    bool _expCancelled;
    Process _expProc;
    string _expOutput = "";

    static string Quote(string a) => a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a;

    static void SetBar(ColumnDefinition a, ColumnDefinition b, double fraction)
    {
        var f = Math.Max(0.0001, Math.Min(0.9999, fraction));
        a.Width = new GridLength(f, GridUnitType.Star);
        b.Width = new GridLength(1 - f, GridUnitType.Star);
    }

    int SelectedExportHeight()
    {
        foreach (var rb in new[] { ExpR1080, ExpR720, ExpR480, ExpRDiscord })
            if (rb.IsChecked == true) return int.Parse((string)rb.Tag, Inv);
        return 1080;
    }

    string SelectedQuality()
    {
        foreach (var rb in new[] { ExpQFast, ExpQBalanced, ExpQHigh })
            if (rb.IsChecked == true) return (string)rb.Tag;
        return "balanced";
    }

    void Export_Click(object sender, RoutedEventArgs e) => OpenExport();

    void OpenExport()
    {
        if (_project.Clips.Count == 0)
        {
            Toast("Add a clip to the timeline first");
            return;
        }
        Pause();
        var s = EditorSettings.Current;
        ExpR1080.IsChecked = s.ExportHeight == 1080;
        ExpR720.IsChecked = s.ExportHeight == 720;
        ExpR480.IsChecked = s.ExportHeight == 480;
        ExpRDiscord.IsChecked = s.ExportHeight == 0;
        if (s.ExportHeight != 720 && s.ExportHeight != 480 && s.ExportHeight != 0) ExpR1080.IsChecked = true;
        ExpQFast.IsChecked = s.Quality == "fast";
        ExpQHigh.IsChecked = s.Quality == "high";
        ExpQBalanced.IsChecked = s.Quality != "fast" && s.Quality != "high";

        ExpPath.Text = UniqueFile(System.IO.Path.Combine(s.ExportDir, SafeName(_project.Name) + ".mp4"));
        ExpError.Visibility = Visibility.Collapsed;
        BtnExpStart.Visibility = Visibility.Visible;
        ExpProgPanel.Visibility = Visibility.Collapsed;
        ExpDonePanel.Visibility = Visibility.Collapsed;
        BtnExpClose.IsEnabled = true;
        UpdateExportSummary();
        ExportOverlay.Visibility = Visibility.Visible;
    }

    void ExpOption_Click(object sender, RoutedEventArgs e)
    {
        EditorSettings.Current.ExportHeight = SelectedExportHeight();
        EditorSettings.Current.Quality = SelectedQuality();
        EditorSettings.Save();
        UpdateExportSummary();
    }

    void UpdateExportSummary()
    {
        _project.Layout();
        var height = SelectedExportHeight();
        var h = height == 0 ? 720 : height;
        var w = (int)Math.Round(h * _project.AspectRatio);
        if (w % 2 != 0) w++;
        var n = _project.Clips.Count;
        ExpSummary.Text = n + (n == 1 ? " clip" : " clips") + "  ·  " + Fmt.TimeTenths(_project.Duration) + "  ·  " + w + " × " + h
                          + (height == 0 ? "  ·  aims for under 25 MB" : "");
        var quality = ExpQFast.Parent as UIElement;
        ExpQFast.IsEnabled = ExpQBalanced.IsEnabled = ExpQHigh.IsEnabled = height != 0;
    }

    void ExpBrowse_Click(object sender, RoutedEventArgs e)
    {
        var current = ExpPath.Text.Trim();
        var dlg = new SaveFileDialog
        {
            Title = "Save the video",
            Filter = "MP4 video (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            FileName = System.IO.Path.GetFileName(current),
        };
        try
        {
            var dir = System.IO.Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        catch { }
        if (dlg.ShowDialog(this) == true) ExpPath.Text = dlg.FileName;
    }

    void ExpClose_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        ExportOverlay.Visibility = Visibility.Collapsed;
    }

    void ExpCancel_Click(object sender, RoutedEventArgs e)
    {
        _expCancelled = true;
        Proc.Kill(_expProc);
    }

    void ExpShow_Click(object sender, RoutedEventArgs e) => Win.ShowInFolder(_expOutput);

    void ShowExpError(string text)
    {
        ExpError.Text = text;
        ExpError.Visibility = Visibility.Visible;
    }

    void ResetExportUi()
    {
        ExpProgPanel.Visibility = Visibility.Collapsed;
        BtnExpStart.Visibility = Visibility.Visible;
        BtnExpClose.IsEnabled = true;
    }

    async void ExpStart_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        ExpError.Visibility = Visibility.Collapsed;

        var ffmpeg = Tools.Find("ffmpeg");
        if (ffmpeg == null)
        {
            ShowExpError("ffmpeg.exe is missing from the bin folder.");
            return;
        }

        var output = ExpPath.Text.Trim().Trim('"');
        if (output.Length == 0)
        {
            ShowExpError("Choose where to save the video.");
            return;
        }
        if (!output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) output += ".mp4";

        string dir;
        try
        {
            dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(output));
            Directory.CreateDirectory(dir);
        }
        catch
        {
            ShowExpError("That folder can't be used. Choose another place to save.");
            return;
        }
        output = UniqueFile(output);
        ExpPath.Text = output;

        var height = SelectedExportHeight();
        var quality = SelectedQuality();

        // make sure there is room before starting
        _project.Layout();
        var perSecond = height >= 1080 ? 900000.0 : height >= 720 ? 450000.0 : 220000.0;
        var needed = (height == 0 ? 26.0 * 1024 * 1024 : _project.Duration * perSecond) + 200000000.0;
        var problem = DiskSpace.Check(dir, needed);
        if (problem != "")
        {
            ShowExpError(problem);
            return;
        }

        var work = System.IO.Path.Combine(AppPaths.TempDir, "export-" + DateTime.Now.Ticks);
        Directory.CreateDirectory(work);
        var options = new ExportOptions { Output = output, Height = height, Quality = quality, TargetMb = height == 0 ? 24 : 0 };
        var plan = ExportBuilder.Build(_project, options, work);
        if (plan.Error != "")
        {
            ShowExpError(plan.Error);
            try { Directory.Delete(work, true); } catch { }
            return;
        }

        EditorSettings.Current.ExportDir = dir;
        EditorSettings.Save();

        _exporting = true;
        _expCancelled = false;
        _expOutput = output;
        BtnExpStart.Visibility = Visibility.Collapsed;
        ExpProgPanel.Visibility = Visibility.Visible;
        BtnExpClose.IsEnabled = false;
        ExpProgText.Text = "Starting…";
        ExpProgInfo.Text = "";
        SetBar(ExpColA, ExpColB, 0);

        var log = new StringBuilder();
        log.AppendLine(Quote(ffmpeg) + " " + string.Join(" ", plan.Args.Select(Quote)));
        log.AppendLine();
        var error = "";
        var started = DateTime.Now;
        try
        {
            using var p = Process.Start(Proc.Info(ffmpeg, plan.Args));
            _expProc = p;
            var errTask = p.StandardError.ReadToEndAsync();
            string line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                if (!line.StartsWith("out_time_us=")) continue;
                if (!double.TryParse(line.Substring(12), NumberStyles.Float, Inv, out var us)) continue;
                var f = plan.Duration > 0 ? Math.Min(us / 1000000.0 / plan.Duration, 0.99) : 0;
                SetBar(ExpColA, ExpColB, f);
                ExpProgText.Text = "Exporting  " + (int)Math.Round(f * 100) + "%";
                var spent = (DateTime.Now - started).TotalSeconds;
                ExpProgInfo.Text = f > 0.02
                    ? Fmt.Time(spent * (1 - f) / f) + " left   ·   " + Fmt.Time(spent) + " elapsed"
                    : Fmt.Time(spent) + " elapsed";
            }
            await p.WaitForExitAsync();
            var err = await errTask;
            log.AppendLine(err);
            if (!_expCancelled && p.ExitCode != 0)
            {
                var last = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                error = string.IsNullOrEmpty(last) ? "ffmpeg stopped unexpectedly." : last;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.AppendLine(ex.ToString());
        }
        finally
        {
            _expProc = null;
            try { File.WriteAllText(System.IO.Path.Combine(AppPaths.DataDir, "last-export.log"), log.ToString()); } catch { }
            try { Directory.Delete(work, true); } catch { }
        }

        _exporting = false;

        if (_expCancelled)
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            ResetExportUi();
            Toast("Export cancelled");
            return;
        }
        if (error == "" && !File.Exists(output)) error = "The video file wasn't created.";
        if (error != "")
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            ResetExportUi();
            ShowExpError("The export didn't work: " + error + "\nThe details are in data\\last-export.log");
            return;
        }

        ExpProgPanel.Visibility = Visibility.Collapsed;
        ExpDonePanel.Visibility = Visibility.Visible;
        ExpDoneFile.Text = System.IO.Path.GetFileName(output);
        BtnExpClose.IsEnabled = true;
    }
}
