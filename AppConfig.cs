using System.Text.Json;

namespace BugFixer;

public sealed class AppConfig
{
    /// <summary>Your main clone of the repo. Never modified except for `git fetch` and new worktrees.</summary>
    public string RepoPath { get; set; } = "";
    public string BaseBranch { get; set; } = "develop";
    public string Remote { get; set; } = "origin";
    /// <summary>Where per-bug worktrees are created. Must be outside RepoPath.</summary>
    public string WorktreeRoot { get; set; } = "";
    /// <summary>State, logs and per-bug run folders.</summary>
    public string StateDir { get; set; } = ".bugfixer";
    public string BranchPrefix { get; set; } = "bugfix/ai-";
    /// <summary>PR title. Placeholders: {id}, {title} (the agent's suggested PR title).</summary>
    public string PrTitleFormat { get; set; } = "[AI] Bug #{id}: {title}";

    public AdoConfig Ado { get; set; } = new();
    public CopilotConfig Copilot { get; set; } = new();

    /// <summary>How long to sleep when no New + unassigned bugs are left.</summary>
    public int IdleMinutes { get; set; } = 10;
    public int PauseBetweenBugsSeconds { get; set; } = 15;
    /// <summary>A fix touching more files than this is not pushed; it is recorded as NEEDS_HUMAN.</summary>
    public int MaxFilesChanged { get; set; } = 15;
    /// <summary>Post a comment on the ADO bug with the outcome (PR link, or why it wasn't reproduced).</summary>
    public bool CommentOnBug { get; set; } = false;
    /// <summary>When the bug is bad/missing data rather than code: comment on the bug and add the tag for the
    /// environment it was reported in. Key = environment (QA, DEV), value = ADO tag.</summary>
    public Dictionary<string, string> DataIssueTags { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["QA"] = "Data Issue QA",
        ["DEV"] = "Data Issue Dev",
    };
    /// <summary>Used when the agent can't tell which environment the data issue is in.</summary>
    public string DefaultDataIssueEnvironment { get; set; } = "QA";
    /// <summary>Keep the worktree folder after a bug finishes (branches are always kept).</summary>
    public bool KeepWorktrees { get; set; } = false;
    /// <summary>After this many tooling failures in a row (auth expired, CLI crash...), back off.</summary>
    public int MaxConsecutiveInfraFailures { get; set; } = 3;
    public int InfraBackoffMinutes { get; set; } = 30;
    /// <summary>Read-only database MCP servers the fix agent may query to confirm bugs against real data.
    /// Key = MCP server name, value = what it is (e.g. "DEV"). Empty = no database access at all.</summary>
    public Dictionary<string, string> DatabaseMcpServers { get; set; } = new();
    /// <summary>Appended verbatim to the fix prompt (repo conventions, test commands, etc.).</summary>
    public string? ExtraFixInstructions { get; set; }

    /// <summary>--config wins; otherwise bugfixer.local.json, then bugfixer.json, in the current dir, then next to the exe.</summary>
    public static string ResolvePath(string? explicitPath)
    {
        if (explicitPath is not null) return explicitPath;
        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            foreach (var name in new[] { "bugfixer.local.json", "bugfixer.json" })
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return Path.Combine(Directory.GetCurrentDirectory(), "bugfixer.json");
    }

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config not found: {path}. Copy bugfixer.json next to the exe or pass --config <path>.");

        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json.Options)
                  ?? throw new InvalidOperationException("Config file is empty.");

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string Abs(string p) => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p));

        cfg.RepoPath = Abs(Require(cfg.RepoPath, nameof(RepoPath)));
        cfg.WorktreeRoot = Abs(Require(cfg.WorktreeRoot, nameof(WorktreeRoot)));
        cfg.StateDir = Abs(cfg.StateDir);
        Require(cfg.Ado.Organization, "Ado.Organization");
        Require(cfg.Ado.Project, "Ado.Project");
        Require(cfg.Ado.Repository, "Ado.Repository");

        if (cfg.Copilot.AdditionalMcpConfig is { Length: > 0 } mcp && !mcp.TrimStart().StartsWith('{'))
            cfg.Copilot.AdditionalMcpConfig = "@" + Abs(mcp.TrimStart('@'));

        if (cfg.WorktreeRoot.StartsWith(cfg.RepoPath.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WorktreeRoot must be outside RepoPath.");

        return cfg;
    }

    private static string Require(string? v, string name) =>
        string.IsNullOrWhiteSpace(v) ? throw new InvalidOperationException($"Config value '{name}' is required.") : v;
}

public sealed class AdoConfig
{
    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";
    public string Repository { get; set; } = "";
    /// <summary>Server name as Copilot CLI knows it (see `/mcp` in interactive copilot).</summary>
    public string McpServerName { get; set; } = "ado";
    /// <summary>Optional: only bugs under this area path.</summary>
    public string? AreaPath { get; set; }
}

public sealed class CopilotConfig
{
    /// <summary>`copilot` on PATH. On Windows an npm-installed `copilot` is a .cmd shim; this is handled.</summary>
    public string Command { get; set; } = "copilot";
    public string Model { get; set; } = "auto";
    /// <summary>JSON string, or a path to an MCP config file (relative to bugfixer.json). Optional if the
    /// ADO server is already in ~/.copilot/mcp-config.json.</summary>
    public string? AdditionalMcpConfig { get; set; }
    public int SelectTimeoutMinutes { get; set; } = 10;
    public int FixTimeoutMinutes { get; set; } = 90;
    public int PublishTimeoutMinutes { get; set; } = 10;
    /// <summary>Tools or whole MCP servers denied in EVERY Copilot call (e.g. database servers).</summary>
    public List<string> DenyTools { get; set; } = new();
    /// <summary>Extra raw CLI args added to every call.</summary>
    public List<string> ExtraArgs { get; set; } = new();
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Reads a JSON file an agent wrote; tolerates ```json fences and leading prose.</summary>
    public static T? ReadAgentFile<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonSerializer.Deserialize<T>(text[start..(end + 1)], Options); }
        catch (JsonException) { return null; }
    }
}
