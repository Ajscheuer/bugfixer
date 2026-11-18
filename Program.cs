using System.Text;
using System.Text.RegularExpressions;
using BugFixer;

// ── args ──────────────────────────────────────────────────────────────────────
string? configPath = null;
bool once = false, noPublish = false, status = false;
int? forget = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config": configPath = args[++i]; break;
        case "--once": once = true; break;                 // handle one bug, then exit
        case "--no-publish": noPublish = true; break;      // fix + commit locally, no push / PR / ADO writes
        case "--status": status = true; break;
        case "--forget": forget = int.Parse(args[++i]); break;
        case "-h" or "--help":
            Console.WriteLine("""
                bugfixer [--config path] [--once] [--no-publish] [--status] [--forget <bugId>]
                  Runs until stopped. Ctrl+C once = finish current bug then exit; twice = abort now.
                  Creating a file named STOP in the state dir also stops it after the current bug.
                """);
            return 0;
    }
}

var cfg = AppConfig.Load(AppConfig.ResolvePath(configPath));
var state = StateStore.Load(cfg.StateDir);
var logPath = Path.Combine(cfg.StateDir, "logs", "bugfixer.log");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

if (status)
{
    foreach (var b in state.All)
        Console.WriteLine($"{b.At.ToLocalTime():yyyy-MM-dd HH:mm}  #{b.Id,-7} {b.Outcome,-17} {b.PrUrl ?? b.Note}  {b.Title}");
    return 0;
}
if (forget is int f)
{
    Console.WriteLine(state.Forget(f) ? $"Bug #{f} will be picked up again." : $"Bug #{f} isn't in the state file.");
    return 0;
}

// ── stop handling ─────────────────────────────────────────────────────────────
using var hardStop = new CancellationTokenSource();   // abort running processes
using var softStop = new CancellationTokenSource();   // finish current bug, then exit
var stopFile = Path.Combine(cfg.StateDir, "STOP");
if (File.Exists(stopFile)) File.Delete(stopFile);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (!softStop.IsCancellationRequested) { Log("Stop requested — finishing the current bug. Ctrl+C again to abort now."); softStop.Cancel(); }
    else { Log("Aborting now."); hardStop.Cancel(); }
};
bool StopRequested() => softStop.IsCancellationRequested || File.Exists(stopFile);

var git = new Git(cfg);
var copilot = new Copilot(cfg);
int infraFailures = 0;

Log($"bugfixer started. Repo {cfg.RepoPath}, base {cfg.Remote}/{cfg.BaseBranch}, ADO {cfg.Ado.Organization}/{cfg.Ado.Project}/{cfg.Ado.Repository}" +
    (noPublish ? " [NO-PUBLISH]" : ""));
Log($"Already handled: {state.ProcessedIds.Count} bug(s). Logs: {Path.GetDirectoryName(logPath)}");

// ── main loop ─────────────────────────────────────────────────────────────────
while (!StopRequested() && !hardStop.IsCancellationRequested)
{
    if (infraFailures >= cfg.MaxConsecutiveInfraFailures)
    {
        Log($"{infraFailures} tooling failures in a row (Copilot auth/MCP/git?). Backing off {cfg.InfraBackoffMinutes} min.");
        await Sleep(TimeSpan.FromMinutes(cfg.InfraBackoffMinutes));
        infraFailures = 0;
        continue;
    }

    try
    {
        var handled = await ProcessNextBug();
        if (handled) { infraFailures = 0; if (once) break; await Sleep(TimeSpan.FromSeconds(cfg.PauseBetweenBugsSeconds)); }
        else
        {
            Log(once ? "No New + unassigned bugs." : $"No New + unassigned bugs. Sleeping {cfg.IdleMinutes} min.");
            if (once) break;
            await Sleep(TimeSpan.FromMinutes(cfg.IdleMinutes));
        }
    }
    catch (OperationCanceledException) when (hardStop.IsCancellationRequested) { break; }
    catch (InfraException ex) { infraFailures++; Log($"Tooling failure ({infraFailures}/{cfg.MaxConsecutiveInfraFailures}): {ex.Message}"); await Sleep(TimeSpan.FromMinutes(1)); }
    catch (Exception ex) { infraFailures++; Log($"Unexpected error ({infraFailures}/{cfg.MaxConsecutiveInfraFailures}): {ex}"); await Sleep(TimeSpan.FromMinutes(1)); }
}

Log("bugfixer stopped.");
return 0;

// ── one bug ───────────────────────────────────────────────────────────────────
async Task<bool> ProcessNextBug()
{
    var ct = hardStop.Token;

    try { await git.FetchAsync(ct); }
    catch (GitException ex) { throw new InfraException(ex.Message); }

    // 1. Pick the next bug via ADO MCP (read-only step).
    var selectDir = Path.Combine(cfg.StateDir, "runs", "_select");
    if (Directory.Exists(selectDir)) Directory.Delete(selectDir, true);
    Directory.CreateDirectory(selectDir);
    var selectBugJson = Path.Combine(selectDir, "bug.json");
    var selectPrompt = Path.Combine(selectDir, "prompt.md");
    File.WriteAllText(selectPrompt, Prompts.Select(cfg, Prompts.Wiql(cfg, state.ProcessedIds), selectBugJson));

    var sel = await copilot.RunAsync(selectDir, selectPrompt, [], ["shell"],
        TimeSpan.FromMinutes(cfg.Copilot.SelectTimeoutMinutes), Path.Combine(selectDir, "copilot.log"), ct);
    var bug = Json.ReadAgentFile<BugDetails>(selectBugJson);
    if (bug is null)
        throw new InfraException($"Intake step produced no bug.json (exit {sel.ExitCode}{(sel.TimedOut ? ", timed out" : "")}). See {selectDir}/copilot.log");
    if (bug.Id is null) return false;

    int id = bug.Id.Value;
    var title = bug.Title ?? "(no title)";

    if (state.Contains(id))
        throw new InfraException($"Intake returned #{id}, which was already handled — the agent ignored the WIQL filter.");

    var runDir = Path.Combine(cfg.StateDir, "runs", $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}");
    Directory.CreateDirectory(runDir);
    foreach (var file in Directory.GetFiles(selectDir)) File.Copy(file, Path.Combine(runDir, "select-" + Path.GetFileName(file)));
    var bugJson = Path.Combine(runDir, "bug.json");
    File.Copy(selectBugJson, bugJson, true);

    Log($"▶ Bug #{id}: {title}");

    // Belt and braces: the query should guarantee these, re-check before spending a long run.
    if (!string.Equals(bug.State, "New", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(bug.AssignedTo) ||
        (bug.WorkItemType is not null && !bug.WorkItemType.Equals("Bug", StringComparison.OrdinalIgnoreCase)))
    {
        Done(id, title, Outcome.Skipped, note: $"Not a New/unassigned bug at pick-up (state={bug.State}, assignedTo={bug.AssignedTo})");
        return true;
    }

    // 2. Branch off develop in its own worktree.
    var branch = $"{cfg.BranchPrefix}{id}-{Slug(title)}";
    if (await git.RemoteBranchExistsAsync(branch, ct))
    {
        Done(id, title, Outcome.Skipped, branch, note: "Remote branch already exists");
        return true;
    }
    string wt;
    try { wt = await git.CreateWorktreeAsync(id, branch, ct); }
    catch (GitException ex) { throw new InfraException(ex.Message); }

    try
    {
        // 3. Verify + fix. ADO is denied entirely in this step, and so is pushing.
        var resultJson = Path.Combine(runDir, "result.json");
        var fixPrompt = Path.Combine(runDir, "fix-prompt.md");
        int? ageDays = bug.CreatedDate is DateTimeOffset c ? (int)(DateTimeOffset.Now - c).TotalDays : null;
        File.WriteAllText(fixPrompt, Prompts.Fix(cfg, id, wt, branch, bugJson, resultJson, ageDays, bug.CreatedDate));
        var stale = ageDays > cfg.StaleBugDays ? $", {ageDays} days old → validity check first" : "";
        Log($"  working in {wt} on {branch} (timeout {cfg.Copilot.FixTimeoutMinutes} min{stale})…");

        var fix = await copilot.RunAsync(wt, fixPrompt, [runDir],
            [cfg.Ado.McpServerName, "shell(git reset)", "shell(git rebase)",
             "shell(git worktree)", "shell(git config)", "shell(git switch)"],
            TimeSpan.FromMinutes(cfg.Copilot.FixTimeoutMinutes), Path.Combine(runDir, "copilot-fix.log"), ct);

        var result = Json.ReadAgentFile<FixResult>(resultJson);
        if (result?.Verdict is null)
        {
            Done(id, title, Outcome.Failed, branch,
                note: fix.TimedOut ? "Agent timed out" : $"Agent finished without a result file (exit {fix.ExitCode})");
            if (!fix.TimedOut && fix.ExitCode != 0) throw new InfraException($"Copilot exited {fix.ExitCode} on #{id}");
            return true;
        }

        var verdict = result.Verdict.Trim().ToUpperInvariant();
        if (verdict == "NOT_REPRODUCIBLE")
        {
            Log($"  ✗ not reproducible: {result.Summary}");
            if (cfg.CommentOnBug && !noPublish)
                await Comment(id, runDir, $"🤖 Automated check: could not confirm this bug on `{cfg.BaseBranch}`.\n\n{result.Summary}\n\nEvidence: {result.Evidence}");
            Done(id, title, Outcome.NotReproducible, note: result.Summary);
            return true;
        }
        if (verdict == "OBSOLETE")
        {
            Log($"  ⊘ obsolete: {result.Summary}");
            var commented = false;
            if (cfg.CommentOnObsolete && !noPublish)
            {
                var text = $"🤖 Automated triage: this bug (logged {bug.CreatedDate:yyyy-MM-dd}) appears to be no longer valid under the current business logic.\n\n" +
                           $"{result.Summary}\n\n**Evidence:** {result.Evidence}\n\nNo code change was made. Please confirm and close if you agree.";
                await Comment(id, runDir, text);
                commented = true;
            }
            Done(id, title, Outcome.Obsolete, note: result.Summary + (commented ? " (commented)" : ""));
            return true;
        }
        if (verdict == "DATA_ISSUE")
        {
            var env = (result.Environment ?? "").Trim().ToUpperInvariant();
            if (!cfg.DataIssueTags.ContainsKey(env)) env = cfg.DefaultDataIssueEnvironment.ToUpperInvariant();
            var tag = cfg.DataIssueTags.TryGetValue(env, out var t) ? t : $"Data Issue {env}";
            Log($"  ◆ data issue ({env}): {result.Summary}");

            if (noPublish)
            {
                Done(id, title, Outcome.DataIssue, note: $"[{env}] {result.Summary} (--no-publish: not commented/tagged)");
                return true;
            }

            var commentFile = Path.Combine(runDir, "data-issue-comment.md");
            File.WriteAllText(commentFile,
                $"🤖 **Automated triage: data issue ({env}), not a code defect.**\n\n" +
                $"{result.Summary}\n\n" +
                (string.IsNullOrWhiteSpace(result.Evidence) ? "" : $"**How this was determined:** {result.Evidence}\n\n") +
                (string.IsNullOrWhiteSpace(result.RootCause) ? "" : $"**Details:** {result.RootCause}\n\n") +
                $"No code change was made. Tagged `{tag}` for follow-up.");
            var ok = await CommentAndTag(id, runDir, commentFile, tag);
            Done(id, title, Outcome.DataIssue,
                note: $"[{env}] {result.Summary}{(ok ? $" (commented, tagged '{tag}')" : " (ADO comment/tag FAILED — see copilot-ado.log)")}");
            return true;
        }
        if (verdict != "FIXED")
        {
            Log($"  ⚠ needs a human: {result.Summary}");
            Done(id, title, Outcome.NeedsHuman, branch, note: result.Summary);
            return true;
        }

        // 4. Check what the agent actually did before anything leaves the machine.
        var current = await git.CurrentBranchAsync(wt, ct);
        if (current != branch) { Done(id, title, Outcome.Failed, branch, note: $"Agent switched branch to {current}"); return true; }
        var dirty = await git.DirtyFilesAsync(wt, ct);
        if (dirty.Length > 0) { Done(id, title, Outcome.NeedsHuman, branch, note: $"Agent left {dirty.Length} uncommitted change(s)"); return true; }
        if (await git.CommitsAheadAsync(wt, ct) == 0) { Done(id, title, Outcome.Failed, branch, note: "Verdict FIXED but no commits"); return true; }
        var changed = await git.ChangedFilesAsync(wt, ct);
        if (changed.Length > cfg.MaxFilesChanged) { Done(id, title, Outcome.NeedsHuman, branch, note: $"{changed.Length} files changed (> {cfg.MaxFilesChanged})"); return true; }

        if (noPublish)
        {
            Log($"  ✓ fixed locally on {branch} ({changed.Length} files). --no-publish: not pushed.");
            Done(id, title, Outcome.NeedsHuman, branch, note: "Fixed locally (--no-publish)");
            return true;
        }

        // 5. Push and open a draft PR to develop (via ADO MCP).
        await git.PushAsync(wt, branch, ct);
        var prTitle = cfg.PrTitleFormat.Replace("{id}", id.ToString()).Replace("{title}", result.PrTitle ?? title);
        var descFile = Path.Combine(runDir, "pr-description.md");
        File.WriteAllText(descFile, PrDescription(id, result, changed));
        var prJson = Path.Combine(runDir, "pr.json");
        var prPrompt = Path.Combine(runDir, "pr-prompt.md");
        File.WriteAllText(prPrompt, Prompts.PublishPr(cfg, id, branch, prTitle, descFile, prJson));
        await copilot.RunAsync(runDir, prPrompt, [], ["shell"],
            TimeSpan.FromMinutes(cfg.Copilot.PublishTimeoutMinutes), Path.Combine(runDir, "copilot-pr.log"), ct);

        var pr = Json.ReadAgentFile<PublishResult>(prJson);
        if (pr?.PrUrl is null)
        {
            Done(id, title, Outcome.Failed, branch, note: $"Branch pushed but PR not created: {pr?.Error ?? "no pr.json"}");
            return true;
        }
        Log($"  ✓ draft PR: {pr.PrUrl}");
        Done(id, title, Outcome.PrCreated, branch, pr.PrUrl, result.Summary);
        return true;
    }
    finally
    {
        if (!cfg.KeepWorktrees) await git.RemoveWorktreeAsync(wt, CancellationToken.None);
    }
}

async Task<bool> CommentAndTag(int id, string runDir, string commentFile, string tag)
{
    var done = Path.Combine(runDir, "ado.json");
    var p = Path.Combine(runDir, "ado-prompt.md");
    File.WriteAllText(p, Prompts.CommentAndTag(cfg, id, commentFile, tag, done));
    await copilot.RunAsync(runDir, p, [], ["shell"],
        TimeSpan.FromMinutes(cfg.Copilot.PublishTimeoutMinutes), Path.Combine(runDir, "copilot-ado.log"), hardStop.Token);
    return Json.ReadAgentFile<AdoWriteResult>(done)?.Ok == true;
}

async Task Comment(int id, string runDir, string text)
{
    var done = Path.Combine(runDir, "comment.json");
    var p = Path.Combine(runDir, "comment-prompt.md");
    File.WriteAllText(p, Prompts.CommentOnly(cfg, id, text, done));
    await copilot.RunAsync(runDir, p, [], ["shell"],
        TimeSpan.FromMinutes(cfg.Copilot.PublishTimeoutMinutes), Path.Combine(runDir, "copilot-comment.log"), hardStop.Token);
}

void Done(int id, string title, string outcome, string? branch = null, string? prUrl = null, string? note = null)
{
    state.Record(new BugRecord(id, title, outcome, DateTimeOffset.Now, branch, prUrl, note));
    Log($"  ■ #{id} → {outcome}{(note is null ? "" : $" — {note}")}");
}

async Task Sleep(TimeSpan d)
{
    var end = DateTime.UtcNow + d;
    while (DateTime.UtcNow < end && !StopRequested() && !hardStop.IsCancellationRequested)
        await Task.Delay(TimeSpan.FromSeconds(2));
}

void Log(string msg)
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
    Console.WriteLine(line);
    try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
}

static string Slug(string s)
{
    var slug = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
}

static string PrDescription(int id, FixResult r, string[] changed)
{
    var sb = new StringBuilder();
    sb.AppendLine($"> 🤖 Draft opened by an unattended AI bug-fix loop for AB#{id}. Review carefully before publishing.");
    sb.AppendLine();
    sb.AppendLine(string.IsNullOrWhiteSpace(r.PrDescription) ? r.Summary : r.PrDescription);
    sb.AppendLine();
    if (!string.IsNullOrWhiteSpace(r.Evidence)) sb.AppendLine($"**How the bug was confirmed:** {r.Evidence}\n");
    if (!string.IsNullOrWhiteSpace(r.RootCause)) sb.AppendLine($"**Root cause:** {r.RootCause}\n");
    if (r.Tests is { Count: > 0 }) { sb.AppendLine("**Tests run:**"); foreach (var t in r.Tests) sb.AppendLine($"- `{t}`"); sb.AppendLine(); }
    sb.AppendLine($"**Risk:** {r.Risk ?? "unknown"} · **Files changed:** {changed.Length}");
    return sb.ToString();
}

sealed class InfraException(string message) : Exception(message);
