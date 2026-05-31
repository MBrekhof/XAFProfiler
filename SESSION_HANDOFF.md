# Session Handoff

**Last updated:** 2026-05-31
**HEAD:** `da74b04` · **Build:** `dotnet build XAFProfiler.slnx` → 0 warnings / 0 errors ·
**App:** stopped, ports 5000/5001 free · **Flag:** `Profiling:Enabled` = true (dev)

## TL;DR

MiniProfiler-in-XAF-Blazor POC. The two things WLNCentral's design deferred —
**Blazor SignalR circuit profiling** and **persistent storage** — are built and **proven at
the data layer (SQL store = ground truth)**. One piece is still broken: the custom
**ProfileSummary XAF browse view renders EMPTY**. A doc-backed candidate fix is identified
but UNVERIFIED (see Open Bugs #1).

## What was built (DX XAF 25.2.5, .NET 8; solution `XAFProfiler.slnx` at repo root)

- **Demo domain:** `Customer → Order → OrderLine` (`Module/BusinessObjects/Demo/`),
  `Customer.OrdersTotal` non-persistent calc property = deliberate N+1. Seeded 200 / 5999 / 33065.
- **Layer A (HTTP+EF):** `MiniProfiler.AspNetCore.Mvc` + `.EntityFrameworkCore` 4.3.8,
  `AddMiniProfiler().AddEntityFramework()` behind `Profiling:Enabled`, admin-gated,
  `<mini-profiler />` injected via `_Host.cshtml` + new `Pages/_ViewImports.cshtml`.
- **Layer B (circuit capture — the core deliverable):** `Services/CircuitProfilerService.cs`
  (scoped) + `Controllers/ProfileViewController.cs` "Profile This View" SimpleAction on the
  Customer ListView. Manual `StartNew()` / `.Step()` / `StopAsync(false)` over the circuit.
- **Layer C (storage):** `SqlServerStorage` (`MiniProfiler.Providers.SqlServer` 4.3.8) +
  `Services/ProfilerStorageInitializer.cs` (creates the DB then the 3 MiniProfiler tables) +
  `BusinessObjects/ProfileSummary.cs` + `Controllers/ProfileSummaryController.cs` (read-only
  browse view — code complete but renders empty, see Open Bugs).

## VERIFIED (ground truth: SQL store + curl)

- **Layer B circuit capture:** action persists `Profile: Customer ListView` (~3947 ms) with
  nested markers `Reload + aggregate` (~3946 ms) → `Sum OrdersTotal (N+1)` (~3886 ms). **EF SQL
  IS captured over the circuit** as `CustomTimingsJson` child timings — the N+1 step holds
  thousands of SQL statements (7.4 MB JSON). 8 profiles accumulated; persist across app shutdown.
- **Layer A popup:** "MiniProfiler Init" appears in the browser console when flag on.
- **Negative test (flag off):** 0 mini-profiler scripts on the page. Nuance: `/profiler/results-index`
  returns **200, not 404** — no `/profiler` route, so XAF's `MapFallbackToPage("/_Host")` SPA
  catch-all serves the shell (no profiler data served). Flag restored to true.
- Honest screenshots `01`–`04` at repo root (login, home, Customer ListView w/ N+1 column, ribbon).

## OPEN BUGS / NOT confirmed

1. **ProfileSummary XAF view renders EMPTY** ("No data to display" when run in VS).
   Instrumentation (now reverted) proved: it IS a `NonPersistentObjectSpace`, the handler IS
   subscribed, but `ObjectsGetting` **never fires** → 0 rows. So the earlier "derived from
   BaseObject" theory was WRONG (it was already non-persistent); the POCO + `CreateObject<T>()`
   change in commit `87a6e76` is correct practice (DX 113711) but did NOT fix it.
   **Candidate fix, per DX docs, UNVERIFIED:** XAF Blazor non-persistent ListViews need
   **`DataAccessMode = Client`** (default mode doesn't raise `ObjectsGetting`). Set it on the
   `ProfileSummary_ListView` node in `Model.xafml`, run, and confirm rows appear BEFORE claiming
   success. Use the **dxdocs MCP** for specifics.
2. **Built-in `/profiler/results?id=<real id>`** returns "hidden" in-browser — `ResultsAuthorize`
   returns false (XAF auth cookie not seen as authenticated on the raw fetch). A bogus id returns
   "not found", so the profile IS loadable; purely the delegate withholding it. Low priority (the
   custom XAF view is meant to be the in-app surface, once #1 is fixed).
3. Dark-theme variant not exercised (only light theme).

## Code-review items to fix before the WLNCentral port-back

- `ProfileViewController` stops the profiler sync-over-async (`.GetAwaiter().GetResult()`).
- `Startup` passes the connection string to `SqlServerStorage`/`EnsureTables` without a null guard.
- Diagnostics use `Console.WriteLine` instead of `ILogger`.

## Next steps (suggested order)

1. Fix Open Bug #1 (`DataAccessMode=Client`), verify the grid actually shows rows.
2. Then the code-review items above.
3. `finishing-a-development-branch`: early commits were duplicated/amended during parallel-agent
   races (`19eaa3a`/`3ba3959`, `99dfc76`/`85393cc`) — consider squashing before any PR.
4. Port the proven circuit-capture + `SqlServerStorage` + DB-bootstrap pattern back to the
   WLNCentral `Profile` branch (replacing its deferred items). Carry the findings above.

## Key files

| Area | File |
| --- | --- |
| Circuit service | `XAFProfiler.Blazor.Server/Services/CircuitProfilerService.cs` |
| Profile action | `XAFProfiler.Blazor.Server/Controllers/ProfileViewController.cs` |
| Storage bootstrap | `XAFProfiler.Blazor.Server/Services/ProfilerStorageInitializer.cs` |
| Browse view (empty bug) | `XAFProfiler.Blazor.Server/BusinessObjects/ProfileSummary.cs` + `Controllers/ProfileSummaryController.cs` |
| Registration/middleware/storage | `XAFProfiler.Blazor.Server/Startup.cs` |
| Flag | `appsettings.json` (false) / `appsettings.Development.json` (true) |
| Demo + seed | `XAFProfiler.Module/BusinessObjects/Demo/*` + `DatabaseUpdate/Updater.cs` |
| Design + plan | `docs/plans/2026-05-30-miniprofiler-poc-design.md` (+ `-implementation.md`) |

## How to run / verify

- Build: `dotnet build XAFProfiler.slnx` (SDK 10.0.300 handles the `.slnx`).
- Run: `dotnet run --project XAFProfiler/XAFProfiler.Blazor.Server` → https://localhost:5001 (admin / blank password).
- DB: SQL Server localdb `(localdb)\mssqllocaldb`, catalog `XAFProfiler`. Inspect profiles:
  `sqlcmd -S "(localdb)\mssqllocaldb" -E -d XAFProfiler -W -Q "SELECT COUNT(*) FROM MiniProfilers"`
- **Verify UI effects via SQL/logs, not screenshots** — Playwright `browser_click` silently
  no-ops when given a quoted label as `target` (use a bare ref or role-based click), and XAF's
  `xaf-loading` overlay can intercept clicks. (Lesson learned the hard way this session.)

## Git

Local repo, no remote, branch `master` (work done directly on it). `run.log` / `build.log` /
`.playwright-mcp/` are gitignored. Tracked screenshots: `01`–`04`. Project memory updated under
`~/.claude/projects/C--Projects-XAFProfiler/memory/`.
