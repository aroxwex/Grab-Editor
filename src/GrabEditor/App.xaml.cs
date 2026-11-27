using System.Windows;
using GrabEditor.Services;

namespace GrabEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!AppPaths.EnsureDataDir(out var error))
        {
            MessageBox.Show("Grab Editor can't write to its own folder:\n" + AppPaths.BaseDir + "\n\n" + error +
                            "\n\nMove the app to a folder you can write to (like your Desktop or Documents).",
                "Grab Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        EditorSettings.Load();
        try { if (Directory.Exists(AppPaths.TempDir)) Directory.Delete(AppPaths.TempDir, true); } catch { }
        MediaService.CleanOldCache(30);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // opening a project file by double-click / "Open with"
        if (e.Args.Length > 0 && File.Exists(e.Args[0])) window.OpenProjectFile(e.Args[0]);
    }
}
