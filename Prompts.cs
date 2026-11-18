namespace BugFixer;

public static class Prompts
{
    public static string Wiql(AppConfig cfg, IReadOnlyCollection<int> exclude)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("SELECT [System.Id], [System.Title] FROM WorkItems WHERE ");
        sb.Append($"[System.TeamProject] = '{Esc(cfg.Ado.Project)}' ");
        sb.Append("AND [System.WorkItemType] = 'Bug' ");
        sb.Append("AND [System.State] = 'New' ");
        sb.Append("AND [System.AssignedTo] = '' ");
        if (!string.IsNullOrWhiteSpace(cfg.Ado.AreaPath))
            sb.Append($"AND [System.AreaPath] UNDER '{Esc(cfg.Ado.AreaPath!)}' ");
        if (exclude.Count > 0)
            sb.Append($"AND [System.Id] NOT IN ({string.Join(", ", exclude.OrderBy(i => i))}) ");
        sb.Append("ORDER BY [Microsoft.VSTS.Common.Priority] ASC, [System.CreatedDate] ASC");
        return sb.ToString();

        static string Esc(string s) => s.Replace("'", "''");
    }

    public static string Select(AppConfig cfg, string wiql, string bugJson) => $$"""
        # Intake step of an unattended bug-fixing loop

        You only READ from Azure DevOps in this step. Do NOT create, update, assign, tag, comment on,
        link or change the state of anything.

        Azure DevOps organization: `{{cfg.Ado.Organization}}`, project: `{{cfg.Ado.Project}}`.
        Use the Azure DevOps MCP server (`{{cfg.Ado.McpServerName}}`).

        1. Run this WIQL query (it already excludes bugs this loop has handled):

           ```
           {{wiql}}
           ```

        2. If it returns no work items, write exactly `{"id": null}` to `{{bugJson}}` and finish.

        3. Otherwise take ONLY THE FIRST result. Fetch its full details with all fields, including
           System.Title, System.State, System.AssignedTo, System.WorkItemType, System.Description,
           Microsoft.VSTS.TCM.ReproSteps, Microsoft.VSTS.TCM.SystemInfo,
           Microsoft.VSTS.Common.AcceptanceCriteria, System.AreaPath, System.IterationPath,
           Microsoft.VSTS.Common.Priority, Microsoft.VSTS.Common.Severity, System.Tags,
           ALL discussion comments, its relations (parent story / related items with their titles),
           and attachment names.

        4. Write `{{bugJson}}` as a single valid JSON object:

           ```json
           {
             "id": 1234,
             "title": "...",
             "workItemType": "Bug",
             "state": "New",
             "assignedTo": null,
             "priority": 2,
             "severity": "3 - Medium",
             "areaPath": "...",
             "iterationPath": "...",
             "tags": "...",
             "description": "plain text (HTML stripped)",
             "reproSteps": "plain text (HTML stripped)",
             "systemInfo": "...",
             "acceptanceCriteria": "...",
             "comments": [{ "author": "...", "date": "...", "text": "plain text" }],
             "related": [{ "id": 1, "type": "Parent", "title": "..." }],
             "attachments": ["file names"]
           }
           ```

        Write only that file. Do not touch any other file.
        """;

    public static string Fix(AppConfig cfg, int id, string worktree, string branch, string bugJson, string resultJson) => $$"""
        # Fix Azure DevOps Bug #{{id}} — unattended

        You are working completely unattended. Nobody will answer questions. Never stop to ask; make the
        best decision yourself and always finish by writing the result file in Step 4.

        - Repository (git worktree): `{{worktree}}`
        - Branch: `{{branch}}`, freshly created from `{{cfg.Remote}}/{{cfg.BaseBranch}}`
        - Full bug details (title, repro steps, description, comments): `{{bugJson}}` — read it first.

        ## Hard rules
        - Work only inside `{{worktree}}`. Do not switch branches, rebase, reset, merge, or create or
          remove worktrees. Do not change git config. You may push, but only this branch
          (`git push -u {{cfg.Remote}} {{branch}}`) and never with --force; never push to
          `{{cfg.BaseBranch}}` or any other branch. The PR is created after you finish.
        - Do NOT start the backend API or the Vite dev server. Tests use the existing InMemory/unit
          setups; never point tests or the app at a real database.
        {{DatabaseRule(cfg)}}
        - Run only scoped/focused tests for what you change (a specific test project, `--filter`, or test
          file). Never run the full backend or full frontend test suite.
        - Keep the change minimal and focused on this bug: no refactors, no reformatting, no dependency
          upgrades, no unrelated files. Never edit secrets, appsettings connection strings or pipeline YAML.
        - If the fix requires an EF migration / schema change, a product decision, real data or
          environment access, or touching more than {{cfg.MaxFilesChanged}} files, the verdict is NEEDS_HUMAN.

        ## Step 1 — Confirm the bug exists
        Trace the reported behaviour through the code (frontend and/or backend as relevant). Where
        practical, write a focused unit test that reproduces it and confirm it fails before the fix.
        Decide one of:
        - **NOT_REPRODUCIBLE** — the code already behaves correctly, it was already fixed on
          `{{cfg.BaseBranch}}`, or the described behaviour doesn't exist in this codebase.
        - **DATA_ISSUE** — the code behaves correctly, but the reported problem comes from the data in an
          environment (missing, wrong or insufficient records, test data that doesn't exist yet, bad
          configuration rows). Set `environment` to where it was reported: `QA` or `DEV` (read the bug's
          title, repro steps, tags, iteration and comments; if it's unclear use `{{cfg.DefaultDataIssueEnvironment}}`).
          Never fix data in code and never try to seed or change data.
        - **NEEDS_HUMAN** — real but ambiguous, too large, or needs something listed in the hard rules.
        - Otherwise continue to Step 2.

        For NOT_REPRODUCIBLE, DATA_ISSUE or NEEDS_HUMAN: make no commits, discard all edits
        (`git checkout -- .` and delete any untracked files you created), then go to Step 4.

        ## Step 2 — Fix
        Implement the minimal fix. Add or update tests that cover it. Build the affected project(s)
        (`dotnet build` on the affected csproj; for frontend, the relevant type-check/lint) and run the
        scoped tests until they pass. If frontend dependencies are missing, run `npm ci` in the frontend
        folder first.

        ## Step 3 — Commit
        Stage only the files you intentionally changed and commit:
        `fix: <short summary> (AB#{{id}})`
        Leave the working tree clean (`git status` shows nothing).

        ## Step 4 — Write the result (always)
        Write `{{resultJson}}` as a single valid JSON object:

        ```json
        {
          "verdict": "FIXED | NOT_REPRODUCIBLE | DATA_ISSUE | NEEDS_HUMAN",
          "environment": "QA | DEV (DATA_ISSUE only)",
          "summary": "1-3 sentences; for DATA_ISSUE, what data is missing or wrong and what's needed",
          "evidence": "files/lines or the failing test proving the bug exists (or doesn't)",
          "rootCause": "...",
          "changes": ["path: what changed"],
          "tests": ["command -> result"],
          "risk": "low | medium | high",
          "prTitle": "short imperative title (FIXED only)",
          "prDescription": "markdown: problem, root cause, fix, tests, risk (FIXED only)"
        }
        ```
        {{(string.IsNullOrWhiteSpace(cfg.ExtraFixInstructions) ? "" : "\n## Project-specific notes\n" + cfg.ExtraFixInstructions)}}
        """;

    private static string DatabaseRule(AppConfig cfg) => cfg.DatabaseMcpServers.Count == 0
        ? "- Do NOT connect to any SQL Server or other database."
        : "- To confirm the bug against real data you may query these databases through their MCP servers: " +
          string.Join(", ", cfg.DatabaseMcpServers.Select(kv => $"`{kv.Key}` ({kv.Value})")) +
          ". READ-ONLY: SELECT queries only, never INSERT/UPDATE/DELETE/DDL or stored procedures that change data. " +
          "Use only these servers; never connect to a database any other way. Data issues (bad or missing rows rather than bad code) are DATA_ISSUE, not code fixes.";

    public static string PublishPr(AppConfig cfg, int id, string branch, string title, string descriptionFile, string prJson) => $$"""
        # Create a DRAFT pull request — unattended

        Use the Azure DevOps MCP server (`{{cfg.Ado.McpServerName}}`). Never ask questions.
        Organization `{{cfg.Ado.Organization}}`, project `{{cfg.Ado.Project}}`, repository `{{cfg.Ado.Repository}}`.

        The branch `{{branch}}` has already been pushed.

        1. Create a pull request:
           - source: `refs/heads/{{branch}}`
           - target: `refs/heads/{{cfg.BaseBranch}}`
           - **it MUST be a draft** (isDraft = true). If the tool cannot create a draft, do NOT create
             the PR; write `{"error": "draft not supported"}` to `{{prJson}}` and finish.
           - title: `{{title.Replace("`", "'")}}`
           - description: the exact contents of the file `{{descriptionFile}}`
        2. Link work item #{{id}} to the pull request.
        {{(cfg.CommentOnBug ? $"3. Add a comment on work item #{id}: \"🤖 AI fix proposed in draft PR <PR link>. Needs human review before merge.\"" : "")}}

        Do NOT add reviewers, vote, approve, set auto-complete, publish the draft, or change the bug's
        state, assignee or tags.

        Finally write `{{prJson}}`: `{"prId": 123, "prUrl": "https://..."}`.
        """;

    public static string CommentAndTag(AppConfig cfg, int id, string commentFile, string tag, string doneJson) => $$"""
        # Comment on and tag a work item — unattended

        Use the Azure DevOps MCP server (`{{cfg.Ado.McpServerName}}`). Never ask questions.
        Organization `{{cfg.Ado.Organization}}`, project `{{cfg.Ado.Project}}`.

        1. Add a comment to work item #{{id}} with exactly the contents of the file `{{commentFile}}`.
        2. Add the tag `{{tag}}` to work item #{{id}}. Read its current `System.Tags` first and KEEP every
           existing tag: the new value is the existing tags plus `{{tag}}`, separated by "; ". If the tag is
           already there, leave the tags unchanged.

        Change nothing else: no state, assignee, iteration, area or other field changes, no links.

        Then write `{"ok": true}` to `{{doneJson}}`, or `{"ok": false, "error": "..."}` if either step failed.
        """;

    public static string CommentOnly(AppConfig cfg, int id, string comment, string doneJson) => $$"""
        # Comment on a work item — unattended

        Use the Azure DevOps MCP server (`{{cfg.Ado.McpServerName}}`). Never ask questions.
        Organization `{{cfg.Ado.Organization}}`, project `{{cfg.Ado.Project}}`.

        Add this comment to work item #{{id}} and change nothing else (no state, assignee or tag changes):

        {{comment}}

        Then write `{"ok": true}` to `{{doneJson}}`.
        """;
}
