# bugfixer

An unattended loop that works on Azure DevOps bugs one at a time until you stop it, using GitHub Copilot CLI.

For each bug it:

1. **Picks** the next bug that is **New and unassigned** (highest priority first, then oldest), using Copilot CLI and the ADO MCP server. This step only reads from ADO.
2. **Branches** `bugfix/ai-<id>-<slug>` from `origin/develop` in a separate git worktree, so your own checkout is never touched.
3. **Confirms the bug exists** by reading the code, and writes a failing unit test where it can. If the bug can't be confirmed, it records that and moves on to the next one.
4. **Fixes** the bug, runs scoped tests only, and commits `fix: … (AB#<id>)`. In this step Copilot has **no ADO access**; it may push its own branch (never force, never develop).
5. **Checks the agent's work** before anything leaves your machine: the branch is unchanged, the working tree is clean, there's at least one commit, and no more than `MaxFilesChanged` files changed.
6. **Pushes** the branch and opens a **draft PR** to `develop` through ADO MCP, linked to the bug. It adds no reviewers, doesn't publish the draft and doesn't change the bug's state.

Every Copilot call runs with `--yolo --autopilot --no-ask-user --model auto`, and stdin is closed, so nothing can ever wait for your input. Each step has a timeout and the whole process tree is killed when it runs out.

## Setup (Windows)

1. Install Copilot CLI and sign in once: `npm i -g @github/copilot`, then `copilot` → `/login`.
2. Make sure Copilot CLI can see the ADO MCP server. Either add it to `~/.copilot/mcp-config.json`, or copy `mcp-config.sample.json` to `mcp-config.json` next to the exe and set your org. Then run `copilot`, type `/mcp` and check the server name matches `Ado.McpServerName`.
3. Copy `bugfixer.json` to `bugfixer.local.json` (git-ignored, picked up automatically) and edit it: `RepoPath`, `WorktreeRoot` (must be outside the repo), and `Ado.Organization`, `Ado.Project` and `Ado.Repository`.
4. Build: `dotnet publish -c Release -o C:\tools\bugfixer`

## Run

```powershell
bugfixer --once --no-publish   # first trial: one bug, fixed locally only, no push/PR/ADO writes; exits after that bug
bugfixer                       # runs 24/7
bugfixer --status              # what it has done so far
bugfixer --forget 12345        # make it pick up bug 12345 again
```

**Stopping:** press Ctrl+C once to finish the current bug and exit, or twice to abort right away. You can also create a file named `STOP` in `.bugfixer\` to stop after the current bug.

## Outcomes (`.bugfixer/state.json`)

| Outcome | Meaning |
|---|---|
| `PR_CREATED` | Draft PR opened |
| `NOT_REPRODUCIBLE` | Agent couldn't confirm the bug in code; skipped |
| `DATA_ISSUE` | Code is correct; the problem is missing or wrong data. The bug gets a comment and a `DataIssueTags` tag for its environment (e.g. `Data Issue QA`). No state or assignee change |
| `OBSOLETE` | Bug is older than `StaleBugDays` (21) and no longer matches current business logic (checked against the current related stories and git history since it was logged). Commented on the bug if `CommentOnObsolete` |
| `NEEDS_HUMAN` | Real but ambiguous, too large, needs a migration or data, or left uncommitted changes. The local branch is kept |
| `FAILED` | Timeout, no result file, or the push succeeded but the PR wasn't created |
| `SKIPPED` | No longer New/unassigned at pick-up, or the branch already existed on the remote |

Each bug is attempted **once**. Use `--forget` to retry one. Each run's prompts, `bug.json`, `result.json` and full Copilot logs are kept in `.bugfixer/runs/<id>-<timestamp>/`.

If Copilot or git fails 3 times in a row (for example an expired login or an MCP server that's down), it backs off for 30 minutes instead of burning through the queue.

## Config tips

- `Copilot.DenyTools` blocks tools or whole MCP servers in every Copilot call. Use it for anything in your global `~/.copilot/mcp-config.json` the agent must never touch, such as database servers.
- `PrTitleFormat` controls the PR title (`{id}`, `{title}`). Set it to `{title}` if your repo has its own title convention and you tell the agent about it in `ExtraFixInstructions`.
- `DatabaseMcpServers` lets the fix agent query real data to confirm a bug, e.g. `{ "sql-mcp-dev": "DEV", "sql-mcp-qa": "QA" }`. Use read-only servers (such as `@bilims/mcp-sqlserver`, which only allows SELECT) with a least-privilege login. Put their definitions in your git-ignored `mcp-config.json`.
