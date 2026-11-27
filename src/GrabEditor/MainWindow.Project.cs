using System.Windows;
using System.Windows.Controls;
using GrabEditor.Models;
using GrabEditor.Services;
using Microsoft.Win32;

namespace GrabEditor;

// Saving and opening projects.
public partial class MainWindow
{
    static string SafeName(string name)
    {
        var s = string.IsNullOrWhiteSpace(name) ? "Untitled" : name;
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    static string UniqueFile(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path);
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = System.IO.Path.Combine(dir, stem + " (" + i + ")" + ext);
            if (!File.Exists(candidate)) return candidate;
        }
    }

    void Save_Click(object sender, RoutedEventArgs e) => SaveProject(false);
    void Open_Click(object sender, RoutedEventArgs e) => OpenProjectDialog();

    /// <summary>Asks to save unsaved work. Returns false if the user chose to cancel.</summary>
    bool ConfirmDiscard()
    {
        if (!_dirty || (_project.Clips.Count == 0 && _project.Texts.Count == 0)) return true;
        var r = MessageBox.Show(this, "Save the changes to \"" + _project.Name + "\" first?", "Grab Editor",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveProject(false);
        return true;
    }

    bool SaveProject(bool saveAs)
    {
        var path = _projectPath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            var dir = EditorSettings.Current.ProjectDir;
            try { Directory.CreateDirectory(dir); } catch { }
            var dlg = new SaveFileDialog
            {
                Title = "Save project",
                Filter = "Grab Editor project (*.grabedit)|*.grabedit",
                DefaultExt = ".grabedit",
                FileName = SafeName(_project.Name),
                InitialDirectory = Directory.Exists(dir) ? dir : "",
            };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }

        try
        {
            _project.Name = System.IO.Path.GetFileNameWithoutExtension(path);
            File.WriteAllText(path, _project.ToJson());
            _projectPath = path;
            _dirty = false;
            EditorSettings.Current.ProjectDir = System.IO.Path.GetDirectoryName(path);
            EditorSettings.AddRecent(path);
            BuildRecentList();
            UpdateTitle();
            UpdateInfoTexts();
            Toast("Saved");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The project couldn't be saved.\n\n" + ex.Message, "Grab Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    void OpenProjectDialog()
    {
        if (!ConfirmDiscard()) return;
        var dir = EditorSettings.Current.ProjectDir;
        var dlg = new OpenFileDialog
        {
            Title = "Open a project",
            Filter = "Grab Editor project (*.grabedit)|*.grabedit",
            InitialDirectory = Directory.Exists(dir) ? dir : "",
        };
        if (dlg.ShowDialog(this) == true) OpenProjectFile(dlg.FileName);
    }

    public void OpenProjectFile(string path)
    {
        try
        {
            var project = Project.FromJson(File.ReadAllText(path));
            project.Name = System.IO.Path.GetFileNameWithoutExtension(path);
            LoadProject(project, path);
            EditorSettings.AddRecent(path);
            BuildRecentList();

            var missing = project.Media.Count(m => !File.Exists(m.Path));
            if (missing > 0)
                Toast(missing + (missing == 1 ? " video is" : " videos are") + " missing from their old place");
        }
        catch
        {
            MessageBox.Show(this, "That project file couldn't be opened.", "Grab Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void LoadProject(Project project, string path)
    {
        Pause();
        _project = project;
        _projectPath = path;
        _dirty = false;
        _undo.Clear();
        Timeline.Project = _project;
        _selClip = null;
        _selText = null;
        _curClip = null;
        _curSource = "";
        _time = 0;
        Timeline.Time = 0;
        _project.Layout();
        RefreshAll();
        RebuildTextOverlays();
        foreach (var m in _project.Media)
        {
            MediaService.TouchCache(m.Id);
            _ = PrepareMediaAsync(m);
        }
        SeekPlayer(0);
        UpdateTitle();
    }

    void NewProject()
    {
        if (!ConfirmDiscard()) return;
        LoadProject(new Project(), "");
        Toast("New project");
    }

    void BuildRecentList()
    {
        RecentList.Children.Clear();
        var items = EditorSettings.Current.Recent.Where(File.Exists).ToList();
        RecentBox.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var path in items)
        {
            var p = path;
            var b = new Button
            {
                Content = System.IO.Path.GetFileNameWithoutExtension(p),
                Style = (Style)FindResource("GhostButton"),
                ToolTip = p,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                Padding = new Thickness(12, 5, 12, 5),
            };
            b.Click += (s, e) =>
            {
                if (ConfirmDiscard()) OpenProjectFile(p);
            };
            RecentList.Children.Add(b);
        }
    }
}
