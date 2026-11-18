using System.Diagnostics;
using System.Text;

namespace BugFixer;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Cancelled)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !Cancelled;
}

public static class ProcessRunner
{
    public static async Task<ProcResult> RunAsync(
        string fileName, IEnumerable<string> args, string workDir,
        TimeSpan timeout, CancellationToken ct, string? logFile = null)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // closed immediately: nothing can ever wait on stdin
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // npm-installed CLIs on Windows are .cmd shims that Process can't launch directly.
        if (OperatingSystem.IsWindows() && !Path.HasExtension(fileName) && fileName != "git")
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(fileName);
        }
        else psi.FileName = fileName;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        StreamWriter? log = null;
        if (logFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            log = new StreamWriter(logFile, append: true, Encoding.UTF8) { AutoFlush = true };
            log.WriteLine($"=== {DateTimeOffset.Now:u} $ {fileName} {string.Join(' ', args.Select(Quote))}");
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var logLock = new object();

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is null) return; stdout.AppendLine(e.Data); lock (logLock) log?.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is null) return; stderr.AppendLine(e.Data); lock (logLock) log?.WriteLine("[err] " + e.Data); };

        proc.Start();
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        bool timedOut = false, cancelled = false;
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            cancelled = !timedOut;
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        }

        var code = proc.HasExited ? proc.ExitCode : -1;
        lock (logLock)
        {
            log?.WriteLine($"=== exit {code}{(timedOut ? " (TIMED OUT)" : "")}{(cancelled ? " (CANCELLED)" : "")}");
            log?.Dispose();
        }
        return new ProcResult(code, stdout.ToString(), stderr.ToString(), timedOut, cancelled);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
