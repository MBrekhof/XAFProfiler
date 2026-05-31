# Design — Ambient EF-Core profiling with an identifiable, drillable, cleanable browse view

**Date:** 2026-05-31
**Status:** Approved (brainstorming)
**Supersedes:** the manual "Profile This View" action (Layer B as a user-triggered button)

## Problem / motivation

The POC proved circuit-level capture + SQL storage, but the in-app surface has three usability gaps the user hit:

1. **Identification** — the browse list shows a GUID; you can't tell which session/operation a profile is.
2. **DetailView errors** — double-clicking a row throws XAF **error 1057** ("a newly created record cannot be shown until it is saved") because `ProfileSummary` is non-persistent, so the per-profile detail (where the EF queries live) never opens.
3. **No cleanup** — stored profiles grow unbounded with no way to clear them.

Underlying all three: the user is **mainly interested in EF Core** performance, and "Profile This *View*" should really be "profile the *app*." In Blazor Server the interesting EF queries run over the SignalR circuit (no `HttpContext`), so HTTP-level MiniProfiler can't see them — which is exactly why ambient circuit-level capture is needed.

## Decisions (from brainstorming)

| Question | Decision |
| --- | --- |
| Capture scope | **App-wide (ambient)** — automatic, not a manual button |
| Browse model | **Flat per-run now**, aggregated "worst offenders" rollup later |
| Capture mechanism | **Per data-load via a global controller** (reuse MiniProfiler + AddEntityFramework) |
| Manual action | **Remove** "Profile This View" (ambient supersedes it) |
| Retention | **Keep newest 200** on save + manual clear actions |
| What to capture | **App view loads only** (drop `/_blazor` + negotiate transport noise) |

## Architecture

### 1. Ambient capture controller (replaces the manual action)

A Main-window `WindowController` (`AmbientProfilingController`, Blazor.Server), active only when `Profiling:Enabled`:

- `OnActivated` → subscribe `Application.ListViewCreated` (and `DetailViewCreated`).
- For each created view, get its `CollectionSourceBase` and subscribe:
  - `CollectionReloading` → `profiler = MiniProfiler.StartNew("<ViewId> · load")` (capture the circuit-scoped profiler).
  - `CollectionReloaded` → stop + save via **`Task.Run(() => profiler.StopAsync(false)).GetAwaiter().GetResult()`** (the documented fix for the circuit `RendererSynchronizationContext` deadlock — see `xaf-blazor-sync-over-async-deadlock`).
- EF queries executed between the two events are captured automatically by the existing `AddEntityFramework` interceptor into `MiniProfiler.Current`.
- Unsubscribe on view `Disposing` / controller `OnDeactivated`.

Hooks verified against dxdocs: `XafApplication.ListViewCreated`, `CollectionSourceBase.CollectionReloading`/`CollectionReloaded` (raised by `CollectionSourceBase.Reload`).

**Naming:** `"<ModelClass> · <ViewType> load"` (e.g. `Customer · ListView load`), so the browse list is self-describing.

**Noise reduction:** the MiniProfiler options gain an `ShouldProfile`/ignore filter so transport requests (`/_blazor`, `/_blazor/negotiate`, `/_Host`, `/profiler/*`) are **not persisted**; only the named app operations above are saved. (The existing per-HTTP-request capture that produced those rows is suppressed.)

### 2. Data model (browse + drill-down)

Both non-persistent `[DomainComponent]` POCOs, served via `NonPersistentObjectSpace` (Client mode, `DevExpress.ExpressApp.Data.Key`, subscribed through `ObjectSpaceCreated` — per `xaf-nonpersistent-listview-empty`).

`ProfileSummary` (reworked):
- `Id : Guid` (hidden key)
- `Operation : string` — the profiler Name
- `Started : DateTime` ("When")
- `DurationMs : double`
- `QueryCount : int` — number of SQL custom timings
- `SlowestQueryMs : double`
- `Queries : IList<ProfileQuery>` — child collection (populated on detail open)

`ProfileQuery` (new, non-persistent child):
- `Id : Guid` (hidden key)
- `Sql : string` — command text
- `DurationMs : double`
- `ExecuteCount : int` — repeats (N+1 signal)

Both projected from `storage.Load(id)`'s MiniProfiler timing tree (walk `Root` timings + their `CustomTimings["sql"]`).

### 3. Browse + DetailView (fixes 1057)

- **ListView** columns: Operation · Started · DurationMs · QueryCount · SlowestQueryMs.
- **Custom read-only DetailView**: header fields + the `Queries` child grid (Sql · DurationMs · ExecuteCount), sorted slowest-first.
- **1057 fix**: opening a non-persistent object's DetailView from the ListView trips `ListViewProcessCurrentObjectController` ("new object can't be shown"). Resolve by serving the object through `ObjectByKeyGetting` (already wired) and deactivating/avoiding the default new-object guard for this view (e.g. set the row's object as not-new, or deactivate `ListViewProcessCurrentObjectController` and open a custom read-only DetailView by key). Exact mechanism verified during implementation against dxdocs.
- Read-only throughout: `AllowEdit/New/Delete = false` (except the cleanup actions below).

### 4. Cleanup + retention

- **"Clear Profiles"** SimpleAction (Tools) → `storage` delete-all (truncate the 3 MiniProfiler tables, or storage API), then refresh the view.
- **"Delete Selected"** → delete the selected profiles by id.
- **Auto-retention:** after each save, trim to the **newest 200** profiles (delete older ids). Implemented in the capture controller's save path or a small maintenance helper.

### 5. Out of scope (follow-ups)

- Aggregated "worst offenders" rollup (per-operation avg/max/total).
- Wrapping action executions (only view data-loads are captured now).
- Built-in `/profiler/results` auth fix (the custom view is the surface).

## Error handling

- Capture is best-effort: a failed `StartNew`/save logs and never breaks the user's operation.
- Retention/cleanup failures log and are non-fatal.
- All diagnostics via `ILogger` (replacing `Console.WriteLine` — also a pending code-review item).

## Testing / verification (against the SQL store = ground truth)

- Build 0/0.
- Load Customer ListView → a `Customer · ListView load` profile appears with QueryCount > 0; no `/_blazor*` rows persisted.
- Open a profile's DetailView → no 1057; the `Queries` grid lists SQL with the N+1 step showing a high ExecuteCount.
- "Clear Profiles" → `SELECT COUNT(*) FROM MiniProfilers` = 0.
- Save > 200 profiles → count caps at 200.
- All UI claims verified via SQL + grid row data, not screenshots alone.

## REVISION 2026-05-31 — capture via EF Core interceptor (Approach 2)

**Why:** The originally-approved mechanism (Approach 1: `MiniProfiler.StartNew` around the
ListView `CollectionChanging`/`Changed` events) was implemented and runtime-tested. It creates
correctly-named profiles BUT captures **zero EF SQL**: MiniProfiler's EF interceptor logs to the
`AsyncLocal` `MiniProfiler.Current`, and the DevExpress grid materializes its query on a separate
async chain where `Current` is null. Verified: ambient `· ListView load` profiles had 1 timing
row and 0 custom (SQL) timings. The EF-Core goal requires reliable capture, so we pivot to a
custom `DbCommandInterceptor` (brainstorming Approach 2). User-approved 2026-05-31.

**Capture mechanism (revised):**
- `QueryCaptureInterceptor : DbCommandInterceptor` (singleton), registered on the app DbContext
  via `options.AddInterceptors(...)` in Startup's `WithDbContext`. Overrides the
  `*Executed`/`*ExecutedAsync` callbacks; on each, reads the `DbContext` from
  `CommandExecutedEventData.Context` and looks up that context's *current operation* in a
  `ConditionalWeakTable<DbContext, OperationCapture>`. If an operation is active, it appends the
  command (text + duration) onto that operation's explicitly-held `MiniProfiler` instance as a
  `"sql"` custom timing — NOT via `MiniProfiler.Current`. This sidesteps the AsyncLocal problem.
- Attribution is by **DbContext instance**, because the grid loads through the View's
  object-space DbContext — the same context the interceptor sees.
- The existing ambient controller (`AmbientProfilingController`, from the Approach-1 commit) is
  retained but repurposed: on `CollectionChanging` it calls `capture.Begin(dbContext, "<Class> ·
  ListView load")` (which `MiniProfiler.StartNew`s an explicit instance and registers it in the
  weak table); on `CollectionChanged` it calls `capture.End(dbContext)` which stops+saves
  (`Task.Run(() => p.StopAsync(false)).GetAwaiter().GetResult()` — mandatory offload) and clears
  the table entry. View-skip (ProfileSummary/ProfileQuery) and the `Profiling:Enabled` gate stay.
- Reaching the DbContext from an XAF `EFCoreObjectSpace`: verified during the spike (Task 6a).

**Storage unchanged:** still MiniProfiler `SqlServerStorage`; the browse list, projection,
DetailView, cleanup, and retention (Tasks 2–5, 7) are untouched and keep working, because the
interceptor produces the same MiniProfiler-with-sql-custom-timings shape the projection reads.

**De-risking:** Task 6a is a spike proving (1) we can obtain the DbContext from the ListView's
object space, and (2) the interceptor captures the grid's actual SQL onto our held profiler. Only
after the spike confirms both do we build the full interceptor + controller wiring.

## Port-back notes (WLNCentral)

Carry: the ambient capture controller, the `Task.Run` stop, the noise filter, the non-persistent browse+detail pattern (Client mode + DX key + ObjectSpaceCreated), and the cleanup/retention. This replaces WLNCentral's deferred items with a working, EF-focused profiler.
