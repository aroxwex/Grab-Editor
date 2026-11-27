using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GrabEditor.Models;
using GrabEditor.Services;

namespace GrabEditor;

// Download a video from a link and add it to the project. It lives here, away from the main screen.
public partial class MainWindow
{
    bool _downloading;
    CancellationTokenSource _dlCts;
    string _dlUrl = "";
    int _dlHeight = 1080;

    void ImportLink_Click(object sender, RoutedEventArgs e)
    {
        ImportPopup.IsOpen = false;
        OpenDownload(null);
    }

    void OpenDownload(string prefill)
    {
        DownloadOverlay.Visibility = Visibility.Visible;
        DlDetails.Visibility = Visibility.Collapsed;
        DlError.Visibility = Visibility.Collapsed;
        DlProgPanel.Visibility = Visibility.Collapsed;
        BtnDlStart.Visibility = Visibility.Visible;
        BtnDlStart.IsEnabled = false;
        BtnDlFetch.IsEnabled = true;
        BtnDlFetch.Content = "Look up";
        DlHint.Text = "Saved to " + EditorSettings.Current.DownloadDir;
        DlHint.Visibility = Visibility.Visible;

        var text = prefill;
        if (string.IsNullOrEmpty(text))
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var clip = Clipboard.GetText().Trim();
                    if (Uri.TryCreate(clip, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) text = clip;
                }
            }
            catch
            {
                // clipboard can be busy
            }
        }
        DlUrl.Text = text ?? "";
        DlUrl.Focus();
        DlUrl.CaretIndex = DlUrl.Text.Length;

        if (Tools.Find("yt-dlp") == null)
            ShowDlError("yt-dlp.exe is missing from the bin folder, so downloading is not available.");
    }

    void ShowDlError(string text)
    {
        DlError.Text = text;
        DlError.Visibility = Visibility.Visible;
    }

    void DlUrl_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        DlFetch_Click(sender, e);
        e.Handled = true;
    }

    async void DlFetch_Click(object sender, RoutedEventArgs e)
    {
        var url = DlUrl.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            ShowDlError("Paste a full link that starts with https://");
            return;
        }

        DlError.Visibility = Visibility.Collapsed;
        DlDetails.Visibility = Visibility.Collapsed;
        BtnDlStart.IsEnabled = false;
        BtnDlFetch.IsEnabled = false;
        BtnDlFetch.Content = "Looking up…";

        var r = await YtDlp.GetInfoAsync(url);

        BtnDlFetch.IsEnabled = true;
        BtnDlFetch.Content = "Look up";
        if (r.Info == null)
        {
            ShowDlError(r.Error);
            return;
        }

        _dlUrl = url;
        DlTitle.Text = r.Info.Title;
        DlMeta.Text = string.Join("   ·   ", new[] { r.Info.Channel, r.Info.Duration > 0 ? Fmt.Time(r.Info.Duration) : "" }
            .Where(x => !string.IsNullOrEmpty(x)));

        var heights = r.Info.Heights.Where(h => h <= 2160).Distinct().Take(8).ToList();
        if (heights.Count == 0) heights.Add(1080);
        _dlHeight = heights.FirstOrDefault(h => h <= 1080);
        if (_dlHeight == 0) _dlHeight = heights[0];

        DlRes.Children.Clear();
        foreach (var h in heights)
        {
            var height = h;
            var rb = new RadioButton
            {
                GroupName = "dlres",
                Style = (Style)FindResource("ChipButton"),
                Tag = height,
                Content = new TextBlock
                {
                    Text = height >= 2160 ? "4K" : height >= 1440 ? "2K" : height + "p",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                IsChecked = height == _dlHeight,
            };
            rb.Click += (s, ev) =>
            {
                _dlHeight = height;
                RefreshDlChips();
            };
            DlRes.Children.Add(rb);
        }
        RefreshDlChips();
        DlDetails.Visibility = Visibility.Visible;
        BtnDlStart.IsEnabled = true;
    }

    void RefreshDlChips()
    {
        foreach (var child in DlRes.Children)
        {
            if (child is RadioButton rb && rb.Content is TextBlock label)
                label.Foreground = rb.IsChecked == true ? (Brush)FindResource("DarkBrush") : (Brush)FindResource("TextBrush");
        }
    }

    void SetDlProgress(double? fraction, string text)
    {
        if (fraction.HasValue)
        {
            SetBar(DlColA, DlColB, fraction.Value);
            DlProgText.Text = "Downloading  " + (int)Math.Round(fraction.Value * 100) + "%";
        }
        else
        {
            DlProgText.Text = text.StartsWith("Downloading") ? "Downloading…" : text;
        }
        DlProgInfo.Text = text.StartsWith("Downloading") && text.Length > 11 ? text.Substring(11).TrimStart(' ', '·') : "";
    }

    async void DlStart_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading || string.IsNullOrEmpty(_dlUrl)) return;
        DlError.Visibility = Visibility.Collapsed;
        DlHint.Visibility = Visibility.Collapsed;

        var dir = EditorSettings.Current.DownloadDir;
        var problem = DiskSpace.Check(dir, 1500000000.0);
        if (problem != "")
        {
            ShowDlError(problem);
            return;
        }

        _downloading = true;
        _dlCts = new CancellationTokenSource();
        BtnDlStart.Visibility = Visibility.Collapsed;
        DlProgPanel.Visibility = Visibility.Visible;
        DlProgText.Text = "Starting…";
        DlProgInfo.Text = "";
        SetBar(DlColA, DlColB, 0);

        var result = await YtDlp.DownloadAsync(_dlUrl, _dlHeight, dir, SetDlProgress, _dlCts.Token);

        _downloading = false;
        DlProgPanel.Visibility = Visibility.Collapsed;
        BtnDlStart.Visibility = Visibility.Visible;

        if (!string.IsNullOrEmpty(result.Error))
        {
            if (result.Error != "Cancelled.") ShowDlError(result.Error);
            DlHint.Visibility = Visibility.Visible;
            return;
        }

        DownloadOverlay.Visibility = Visibility.Collapsed;
        await ImportFilesAsync(new[] { result.File }, true);
    }

    void DlCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _dlCts?.Cancel(); } catch { }
    }

    void DlClose_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            try { _dlCts?.Cancel(); } catch { }
        }
        DownloadOverlay.Visibility = Visibility.Collapsed;
    }
}
