namespace BugFixer;

public sealed class Git(AppConfig cfg)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    private string BaseRef => $"{cfg.Remote}/{cfg.BaseBranch}";

    private async Task<string> Run(string dir, CancellationToken ct, params string[] args)
    {
        var r = await ProcessRunner.RunAsync("git", args, dir, Timeout, ct,
            Path.Combine(cfg.StateDir, "logs", "git.log"));
        if (!r.Ok) throw new GitException($"git {string.Join(' ', args)} failed ({r.ExitCode}): {r.StdErr.Trim()}");
        return r.StdOut.Trim();
    }

    public Task FetchAsync(CancellationToken ct) =>
        Run(cfg.RepoPath, ct, "fetch", "--prune", cfg.Remote, cfg.BaseBranch);

    public async Task<bool> RemoteBranchExistsAsync(string branch, CancellationToken ct) =>
        (await Run(cfg.RepoPath, ct, "ls-remote", "--heads", cfg.Remote, branch)).Length > 0;

    /// <summary>Fresh worktree on a new branch cut from remote develop. Your main checkout is untouched.</summary>
    public async Task<string> CreateWorktreeAsync(int bugId, string branch, CancellationToken ct)
    {
        Directory.CreateDirectory(cfg.WorktreeRoot);
        var path = Path.Combine(cfg.WorktreeRoot, $"bug-{bugId}");
        await RemoveWorktreeAsync(path, ct);
        await Run(cfg.RepoPath, ct, "worktree", "add", "--force", "-B", branch, path, BaseRef);
        return path;
    }

    public async Task RemoveWorktreeAsync(string path, CancellationToken ct)
    {
        if (Directory.Exists(path))
        {
            try { await Run(cfg.RepoPath, ct, "worktree", "remove", "--force", path); }
            catch (GitException) { TryDeleteDir(path); }
        }
        try { await Run(cfg.RepoPath, ct, "worktree", "prune"); } catch (GitException) { }
    }

    public async Task<int> CommitsAheadAsync(string wt, CancellationToken ct) =>
        int.Parse(await Run(wt, ct, "rev-list", "--count", $"{BaseRef}..HEAD"));

    public async Task<string> CurrentBranchAsync(string wt, CancellationToken ct) =>
        await Run(wt, ct, "rev-parse", "--abbrev-ref", "HEAD");

    public async Task<string[]> DirtyFilesAsync(string wt, CancellationToken ct) =>
        (await Run(wt, ct, "status", "--porcelain")).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public async Task<string[]> ChangedFilesAsync(string wt, CancellationToken ct) =>
        (await Run(wt, ct, "diff", "--name-only", $"{BaseRef}...HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public Task PushAsync(string wt, string branch, CancellationToken ct) =>
        Run(wt, ct, "push", "-u", cfg.Remote, $"{branch}:refs/heads/{branch}");

    private static void TryDeleteDir(string path)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch { /* best effort; prune handles the rest next time */ }
    }
}

public sealed class GitException(string message) : Exception(message);
