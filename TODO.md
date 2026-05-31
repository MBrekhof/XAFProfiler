# XAFProfiler — TODO

MiniProfiler-in-XAF-Blazor POC. Current design: **ambient EF-Core profiling**
(`docs/plans/2026-05-31-ambient-ef-profiling-design.md`). The original 3-layer POC and its manual
"Profile This View" action (`docs/plans/2026-05-30-*`) are superseded.

**Status: ambient EF profiling complete, verified, MERGED to `master`, pushed to `origin`
(2026-05-31).** Automatic capture of every ListView data-load's EF SQL via a custom
`DbCommandInterceptor` (keyed by DbContext); identifiable / drillable / cleanable browse view;
manual action removed. Verified end-to-end (Customer load = 1,125 queries; N+1 OrderLines
ExecuteCount 1,089; Clear empties store; no transport noise; 0 build warnings/errors). Details in
SESSION_HANDOFF + memory `ambient-ef-capture-interceptor`. Docs (README / HOW_TO_IMPLEMENT /
ARCHITECTURE) updated to the ambient design 2026-05-31.

## Demo domain
- [x] `Customer` / `Order` / `OrderLine` XAF EF Core entities (`Module/BusinessObjects/Demo/`)
- [x] DbSets registered in `XAFProfilerEFCoreDbContext`; seeded 200 / 5999 / 33065 in `Updater`
- [x] Customer ListView with a calculated `OrdersTotal` (deliberate N+1 / slow aggregation)

## Storage (Layer C)
- [x] `SqlServerStorage` (`MiniProfiler.Providers.SqlServer` 4.3.8) as MiniProfiler storage
- [x] `ProfilerStorageInitializer` bootstraps the DB (via `master`) + 3 MiniProfiler tables at startup
- [x] Profiles persist across app shutdown

## Ambient capture (core deliverable)
- [x] `Services/QueryCaptureInterceptor.cs` — `DbCommandInterceptor`, registered on the DbContext
- [x] `Services/OperationCaptureRegistry.cs` — explicit `MiniProfiler` per `DbContext`
      (`ConditionalWeakTable`); `End` does the mandatory `Task.Run` stop + newest-200 retention trim
- [x] `Controllers/AmbientProfilingController.cs` (Main `WindowController`) — brackets each ListView
      load via `ListViewCreating` + `CollectionChanging`/`Reloading`/`Reloaded`/`Disposed`;
      deferred flush on next load (collection source `Disposed` doesn't fire on nav)
- [x] `options.ShouldProfile = _ => false` — HTTP auto-profiling off; no `/_blazor`/`_Host`/`GET /` noise
- [x] **Proven:** Approach 1 (`StartNew` around collection events) captured 0 SQL because
      MiniProfiler's EF interceptor logs to the AsyncLocal `Current` (null on the grid's chain).
      Pivoted to the custom interceptor (Approach 2). `Customer · ListView load` → 1,125 SQL commands.

## Browse + drill-down
- [x] `ProfileSummary` (Operation/Started/DurationMs/QueryCount/SlowestQueryMs) + `ProfileQuery`
      child (Sql/DurationMs/ExecuteCount) — non-persistent `[DomainComponent]`
- [x] `Services/ProfileProjection.cs` — walks the stored timing tree, groups SQL by command text →
      `ExecuteCount` (N+1 signal), orders by summed duration
- [x] `ProfileSummaryController` (browse list via `ObjectSpaceCreated` → `ObjectsGetting`/`ByKeyGetting`)
- [x] `ProfileSummaryDetailController` — fills the nested Queries grid (XAF reuses the list row's
      object, which has empty Queries; DX 401747)
- [x] **1057 fix:** `RemoveFromModifiedObjects` after `CreateObject<T>()` so the read-only projection
      opens a DetailView (DX 113471)
- [x] Non-persistent ListView render — the three-part fix (Client mode + DX key + `ObjectSpaceCreated`
      from a `WindowController`); verified against the SQL store

## Cleanup + retention
- [x] `ProfileMaintenanceController` — "Clear Profiles" + "Delete Selected" `Tools` actions
- [x] `Services/ProfileStore.cs` — direct-SQL `ClearAll` / `DeleteByIds` / `TrimToNewest` (children first)
- [x] Auto-retention: trim to newest 200 after each save

## Verification
- [x] `dotnet build XAFProfiler.slnx` clean (0/0, verified 2026-05-31)
- [x] Ambient capture → `Customer · ListView load` with QueryCount > 0; N+1 OrderLines ExecuteCount 1,089
- [x] No transport/framework noise persisted (`ShouldProfile=false`)
- [x] Profiles survive app restart; "Clear Profiles" empties the store; retention caps at 200
- [x] Drill-down DetailView opens (no 1057) with a populated Queries grid
- [ ] Dark-theme variant (only light theme verified)
- [ ] Built-in `/profiler/results?id=<real id>` in-browser — returns "hidden" (`ResultsAuthorize`
      doesn't recognise the XAF auth cookie). Low priority; the custom view is the surface.

## Docs
- [x] README.md — rewritten for the ambient flow + current Mermaid diagram (2026-05-31)
- [x] docs/HOW_TO_IMPLEMENT.md — full ambient recipe (2026-05-31)
- [x] ARCHITECTURE.md — ambient design + current solution layout (2026-05-31)
- [x] Regenerate `docs/architecture.png` / `.excalidraw` to the ambient flow (2026-05-31)

## Follow-up (before the WLNCentral port-back)
- [ ] Code review: null connection-string guard in `ProfilerStorageInitializer`; swap its
      `Console.WriteLine` for `ILogger`; revisit fire-and-forget retention cadence
- [ ] Port the proven ambient capture + storage + non-persistent browse/detail pattern back to the
      WLNCentral `Profile` branch
