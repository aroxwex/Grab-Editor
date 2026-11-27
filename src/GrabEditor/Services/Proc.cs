namespace GrabEditor.Services;

public static class Proc
{
    public static ProcessStartInfo Info(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PATH"] = AppPaths.BinDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static void Kill(Process p)
    {
        try
        {
            if (p != null && !p.HasExited) p.Kill(true);
        }
        catch
        {
            // already gone
        }
    }

    /// <summary>Runs a process to completion and returns its exit code and output.</summary>
    public static async Task<(int Code, string Out, string Err)> RunAsync(string file, IEnumerable<string> args, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = Info(file, args) };
        p.Start();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using (ct.Register(() => Kill(p)))
        {
            await p.WaitForExitAsync();
        }
        return (p.ExitCode, await outTask, await errTask);
    }
}
