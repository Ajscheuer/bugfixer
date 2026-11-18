namespace BugFixer;

/// <summary>
/// Runs GitHub Copilot CLI fully unattended: all permissions, autopilot, auto model, no ask_user tool,
/// stdin closed. The prompt lives in a file so it isn't mangled by Windows command-line limits.
/// </summary>
public sealed class Copilot(AppConfig cfg)
{
    public Task<ProcResult> RunAsync(
        string workDir, string promptFile, IEnumerable<string> extraDirs, IEnumerable<string> denyTools,
        TimeSpan timeout, string logFile, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-p", $"Read the instructions in the file {promptFile} and carry them out completely. " +
                  "You are running unattended: never ask questions, never wait for confirmation, " +
                  "and finish by writing the requested result file.",
            "--yolo",                 // = --allow-all-tools --allow-all-paths --allow-all-urls
            "--autopilot",            // keep going until the task is complete
            "--no-ask-user",          // the agent has no way to ask you anything
            "--model", cfg.Copilot.Model,
            "--no-color",
        };
        foreach (var d in extraDirs) args.AddRange(["--add-dir", d]);
        // Deny rules still win over --yolo. A denied call just fails inside the agent; it never prompts.
        foreach (var t in denyTools.Concat(cfg.Copilot.DenyTools)) args.Add($"--deny-tool={t}");
        if (!string.IsNullOrWhiteSpace(cfg.Copilot.AdditionalMcpConfig))
            args.AddRange(["--additional-mcp-config", cfg.Copilot.AdditionalMcpConfig!]);
        args.AddRange(cfg.Copilot.ExtraArgs);

        return ProcessRunner.RunAsync(cfg.Copilot.Command, args, workDir, timeout, ct, logFile);
    }
}
