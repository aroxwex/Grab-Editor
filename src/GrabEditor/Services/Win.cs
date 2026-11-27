using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GrabEditor.Services;

public static class Win
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar tinted to match the window. Silently does nothing where unsupported.</summary>
    public static void StyleTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            int on = 1;
            DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));
            DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
            int caption = 0x0E0E0E;          // COLORREF (BGR) of #0E0E0E
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            int text = 0xEDEDED;
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        }
        catch
        {
            // cosmetic only
        }
    }

    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    public static void ShowInFolder(string file)
    {
        try
        {
            if (!string.IsNullOrEmpty(file) && File.Exists(file))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true });
            else
                Open(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        }
        catch
        {
            // ignore
        }
    }
}
