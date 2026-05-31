# Session Handoff

**Last updated:** 2026-05-31
**Build:** `dotnet build XAFProfiler.slnx` → 0 warnings / 0 errors (verified 2026-05-31) ·
**App:** stopped, ports 5000/5001 free · **Flag:** `Profiling:Enabled` = true (dev)
**Branch:** `master` · **Remote:** `origin` (github.com/MBrekhof/XAFProfiler), master pushed & up to date

## TL;DR (newest first)

**Docs brought up to date (2026-05-31).** README, `docs/HOW_TO_IMPLEMENT.md`, and ARCHITECTURE.md
were rewritten around the **ambient EF-Core** design (they had still described the removed manual
"Profile This View" action). README now documents the automatic flow + a current Mermaid diagram;
HOW_TO_IMPLEMENT is a full ambient recipe (interceptor + registry + capture controller + browse +
drill-down + maintenance). The `docs/architecture.png` / `.excalidraw` diagram was redrawn to the
ambient flow (via the Excalidraw canvas) and is now the README hero image.

**Ambient EF-Core profiling — built, verified, MERGED to `master`, pushed to `origin`.** Automatic
capture of every XAF ListView data-load's EF SQL, surfaced in an identifiable, drillable, cleanable
browse view. Verified end-to-end against the SQL store: `Customer · ListView load` captured
**1,125** queries; the read-only DetailView shows the N+1 (`SELECT … FROM [OrderLines]`,
ExecuteCount **1,089**); Clear Profiles empties the store; no `/_blazor`/`_Host` noise; 0 errors.

- **Capture:** a custom EF `DbCommandInterceptor` (`QueryCaptureInterceptor`) records each SQL
  command onto an explicitly-held `MiniProfiler` (NOT the AsyncLocal `Current`, which is null on the
  grid's async chain), attributed by **DbContext instance** (`OperationCaptureRegistry`'s
  `ConditionalWeakTable`). The Main `AmbientProfilingController` brackets each load
  (`registry.Begin/End`). HTTP auto-profiling is off (`ShouldProfile=false`) so only named ambient
  ops persist. See memory `ambient-ef-capture-interceptor`.
- **Browse:** `ProfileSummary` (Operation/Started/DurationMs/QueryCount/SlowestQueryMs) + read-only
  DetailView with a `ProfileQuery` grid (Sql/DurationMs/ExecuteCount). Cleanup actions
  (`ProfileMaintenanceController`: Clear Profiles / Delete Selected) + newest-200 retention. The
  manual action is removed.
- **Flush model:** a load's profile is flushed when you navigate to ANY other view (the collection
  source's `Disposed` doesn't fire on nav, so flush happens at the next view's load-start). The
  flush runs BEFORE the ProfileSummary/ProfileQuery skip, so opening the Profile Summary view itself
  flushes the operation you just did. The currently-viewed list's own profile isn't saved until you
  navigate away — intrinsic to the no-Disposed-on-nav model; acceptable for the POC.
- Design + plan: `docs/plans/2026-05-31-ambient-ef-profiling-design.md` / `-ambient-ef-profiling.md`.

## What was built (DX XAF 25.2.5, .NET 8; solution `XAFProfiler.slnx` at repo root)

- **Demo domain:** `Customer → Order → OrderLine` (`Module/BusinessObjects/Demo/`),
  `Customer.OrdersTotal` non-persistent calc property = deliberate N+1. Seeded 200 / 5999 / 33065.
- **Storage:** `SqlServerStorage` (`MiniProfiler.Providers.SqlServer` 4.3.8) +
  `Services/ProfilerStorageInitializer.cs` (creates the DB then the 3 MiniProfiler tables).
- **Capture pipeline:** `Services/QueryCaptureInterceptor.cs` + `Services/OperationCaptureRegistry.cs`
  + `Controllers/AmbientProfilingController.cs`. Registered as singletons in `Startup`; interceptor
  added to the DbContext via `options.AddInterceptors`.
- **Projection + browse:** `Services/ProfileProjection.cs`, `BusinessObjects/ProfileSummary.cs` +
  `ProfileQuery.cs`, `Controllers/ProfileSummaryController.cs` + `ProfileSummaryDetailController.cs`,
  `Controllers/ProfileMaintenanceController.cs`, `Services/ProfileStore.cs`.

## Key resolved gotchas (carry to the WLNCentral port-back)

1. **Ambient capture must NOT use `MiniProfiler.Current`** — it's `AsyncLocal` and null on the
   grid's materialisation chain. Use a custom `DbCommandInterceptor` onto an explicitly-held
   profiler keyed by `DbContext`. (Approach 1 — `StartNew` around the collection events — was built
   and proved to capture 0 SQL; pivoted to the interceptor. See the design doc's REVISION section.)
2. **`Task.Run(() => p.StopAsync(false)).GetAwaiter().GetResult()` is mandatory** — a direct
   sync-over-async on the circuit's `RendererSynchronizationContext` deadlocks (load hangs on the
   loading overlay). Memory `xaf-blazor-sync-over-async-deadlock`.
3. **Non-persistent Blazor ListView needs THREE things together** or the grid is silently empty:
   `DataAccessMode=Client`; `DevExpress.ExpressApp.Data.Key` (NOT EF Core `DataAnnotations.Key`,
   else error 1037); subscribe `ObjectsGetting`/`ObjectByKeyGetting` via
   `XafApplication.ObjectSpaceCreated` from a `WindowController` (a per-view `OnActivated` is too
   late). Memory `xaf-nonpersistent-listview-empty`.
4. **Drill-down:** error **1057** on double-click → `npos.RemoveFromModifiedObjects(obj)` after
   `CreateObject<T>()` (marks the read-only projection as existing). The nested Queries grid is
   empty because XAF reuses the list row's object → fill `Queries` in a DetailView controller's
   `OnActivated` (DX 401747).
5. **Noise:** `options.ShouldProfile = _ => false` suppresses the `GET /` / `/_Host` / `/_blazor`
   zero-query rows that HTTP auto-profiling otherwise persists.

## OPEN / NOT confirmed

1. **Built-in `/profiler/results?id=<real id>`** returns "hidden" in-browser — `ResultsAuthorize`
   returns false (XAF auth cookie not seen as authenticated on the raw fetch). The profile IS
   loadable (a bogus id returns "not found"); purely the delegate withholding it. Low priority — the
   custom XAF view is the in-app surface.
2. Dark-theme variant not exercised (only light theme).

## Code-review items to fix before the WLNCentral port-back

- `ProfilerStorageInitializer.EnsureTables` takes the connection string without a null guard.
- `ProfilerStorageInitializer` still uses `Console.WriteLine` (the rest of the pipeline uses
  `ILogger`). Swap it for `ILogger`.
- Retention trim (`OperationCaptureRegistry.End` → `ProfileStore.TrimToNewest`) is fire-and-forget
  on every load — fine for the POC, revisit cadence/debounce before the port.

## Next steps (suggested order)

1. Try the current solution out (user request — port-back is not urgent).
2. Remaining code-review items above.
3. Port the proven ambient pattern back to the WLNCentral `Profile` branch.

## How to run / verify

- Build: `dotnet build XAFProfiler.slnx` (SDK 10.0.300 handles the `.slnx`).
- Run: `dotnet run --project XAFProfiler/XAFProfiler.Blazor.Server` → https://localhost:5001 (admin / blank password).
- Capture is automatic: open a ListView (e.g. Customer), navigate away (flushes the profile), then
  open **Profile Summary** to see it; double-click a row to drill into its SQL.
- DB: SQL Server localdb `(localdb)\mssqllocaldb`, catalog `XAFProfiler`. Inspect profiles:
  `sqlcmd -S "(localdb)\mssqllocaldb" -E -d XAFProfiler -W -Q "SELECT COUNT(*) FROM MiniProfilers"`
- **Verify UI effects via SQL/logs, not screenshots** — Playwright `browser_click` silently
  no-ops when given a quoted label as `target` (use a bare ref or role-based click), and XAF's
  `xaf-loading` overlay can intercept clicks.

## Git

Remote `origin` = https://github.com/MBrekhof/XAFProfiler.git; `master` pushed & up to date. The
ambient feature branch (`feat/ambient-ef-profiling`) has been merged into `master` and no longer
exists. `run.log` / `build.log` / `.playwright-mcp/` are gitignored. Project memory under
`~/.claude/projects/C--Projects-XAFProfiler/memory/`.
