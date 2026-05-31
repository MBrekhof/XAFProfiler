# Session Handoff

**Last updated:** 2026-05-31
**Build:** `dotnet build XAFProfiler.slnx` → 0 warnings / 0 errors ·
**App:** stopped, ports 5000/5001 free · **Flag:** `Profiling:Enabled` = true (dev)

## TL;DR

MiniProfiler-in-XAF-Blazor POC. The two things WLNCentral's design deferred —
**Blazor SignalR circuit profiling** and **persistent storage** — are built and **proven at
the data layer (SQL store = ground truth)**. **Open Bug #1 (ProfileSummary browse view
rendered EMPTY) is now FIXED and verified (2026-05-31)** — the grid shows 100 rows of real
profile data, one GUID cross-checked against the SQL `MiniProfilers` table. The fix needed
THREE things together (see "ProfileSummary fix" below).

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

## ProfileSummary fix (2026-05-31, VERIFIED — grid renders 100 rows vs SQL store)

A Blazor non-persistent `[DomainComponent]` ListView needs **all three** of these, or it
silently shows "No data to display" (ObjectSpace IS non-persistent, handler subscribed, yet
`ObjectsGetting` never fires):

1. **`DataAccessMode=Client`** on `ProfileSummary_ListView` (`Model.xafml`). Blazor defaults to
   **Queryable**, which builds an `IQueryable` against a store and never raises `ObjectsGetting`
   for a storeless type. (dxdocs 113683 / 118449.)
2. **`DevExpress.ExpressApp.Data.Key`** on the key property — NOT the EF Core
   `System.ComponentModel.DataAnnotations.Key` (XAF's non-persistent key detection ignores it;
   you get error 1037 during `CreateListView`). (dxdocs 116516 "Key Property".)
3. **Subscribe `ObjectsGetting`/`ObjectByKeyGetting` via `XafApplication.ObjectSpaceCreated`
   from a `WindowController` (TargetWindowType=Main)** — NOT a per-view ViewController's
   `OnActivated`. The collection source requests objects during view creation, *before* per-view
   controllers activate, so OnActivated subscribes too late. (xaf-blazor-startup skill.)

Files: `Model.xafml`, `BusinessObjects/ProfileSummary.cs`, `Controllers/ProfileSummaryController.cs`.

## OPEN BUGS / NOT confirmed

1. **Built-in `/profiler/results?id=<real id>`** returns "hidden" in-browser — `ResultsAuthorize`
   returns false (XAF auth cookie not seen as authenticated on the raw fetch). A bogus id returns
   "not found", so the profile IS loadable; purely the delegate withholding it. Low priority (the
   custom XAF view is the in-app surface, and it now works).
2. Dark-theme variant not exercised (only light theme).

## Code-review items to fix before the WLNCentral port-back

- ~~`ProfileViewController` stops the profiler sync-over-async (`.GetAwaiter().GetResult()`).~~
  **FIXED 2026-05-31** — it was a real deadlock, not just a smell: on the Blazor circuit's
  `RendererSynchronizationContext`, `StopAsync`'s continuation posts back to the dispatcher
  thread that `.GetResult()` is blocking → "Profile This View" hung forever on the loading
  overlay. Fix: `Task.Run(() => svc.StopAndSaveAsync()).GetAwaiter().GetResult()` runs the async
  chain with no ambient sync context. Verified: action completes, new profile persisted to SQL.
- `Startup` passes the connection string to `SqlServerStorage`/`EnsureTables` without a null guard.
- Diagnostics use `Console.WriteLine` instead of `ILogger`.

## Next steps (suggested order)

1. ~~Fix Open Bug #1 (ProfileSummary empty grid).~~ **DONE 2026-05-31** — see "ProfileSummary fix".
2. ~~Fix the sync-over-async profiler-stop deadlock.~~ **DONE 2026-05-31** (Task.Run offload).
3. Remaining code-review items (null connection-string guard, ILogger).
4. Port the proven circuit-capture + `SqlServerStorage` + DB-bootstrap pattern + the three-part
   non-persistent-ListView fix + the Task.Run stop back to the WLNCentral `Profile` branch.

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
