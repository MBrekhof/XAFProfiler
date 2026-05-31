# Ambient EF-Core Profiling Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the manual "Profile This View" action with automatic (ambient) EF-Core profiling of every XAF view data-load, surfaced in an identifiable, drillable, cleanable in-app browse view.

**Architecture:** A Main-window `WindowController` starts/stops a MiniProfiler around each ListView/DetailView data-load (`CollectionReloading`/`CollectionReloaded`); the existing `AddEntityFramework` interceptor captures the EF SQL. Profiles persist to `SqlServerStorage`. A reworked non-persistent `ProfileSummary` (list) + `ProfileQuery` (child) projects each stored profile's timing tree into readable rows; a custom read-only DetailView shows the SQL with execution counts. Cleanup actions + newest-200 retention keep the store small.

**Tech Stack:** .NET 8, DevExpress XAF 25.2.5 (Blazor Server, EF Core), StackExchange MiniProfiler 4.3.8, SQL Server LocalDB.

**Design doc:** `docs/plans/2026-05-31-ambient-ef-profiling-design.md`

## Conventions for this project (read first)

- **No unit-test harness exists** and XAF controllers/UI are not unit-testable in isolation. "Verification" here = `dotnet build XAFProfiler.slnx` (0/0) **plus runtime checks against the SQL store (ground truth)** and the rendered grid — never screenshots alone (see memory `verify-ui-via-data-not-screenshots`).
- Always invoke `xaf-viewcontroller-patterns` before writing/editing a controller and `xaf-efcore-entities`/`xaf-blazor-startup` for objects/registration. Verify any DevExpress behavior against the **dxdocs MCP** (global directive) — do not assume.
- Stop the profiler async work with the `Task.Run(...).GetAwaiter().GetResult()` offload (memory `xaf-blazor-sync-over-async-deadlock`) — never `.GetAwaiter().GetResult()` directly on the circuit.
- Run: `dotnet run --project XAFProfiler/XAFProfiler.Blazor.Server`; LocalDB `(localdb)\mssqllocaldb`, catalog `XAFProfiler`. Always free ports 5000/5001 before starting and stop the app after.
- Commit after each task.

---

### Task 1: Remove the manual "Profile This View" action and its scoped service

**Files:**
- Delete: `XAFProfiler/XAFProfiler.Blazor.Server/Controllers/ProfileViewController.cs`
- Delete: `XAFProfiler/XAFProfiler.Blazor.Server/Services/CircuitProfilerService.cs`
- Modify: `XAFProfiler/XAFProfiler.Blazor.Server/Startup.cs` — remove `services.AddScoped<CircuitProfilerService>();` and the `using ...Services;` if now unused.

**Step 1:** Delete the two files; remove the registration line in `Startup.ConfigureServices`.
**Step 2:** Build. `dotnet build XAFProfiler.slnx` → expect 0/0 (no remaining references). If a reference remains, fix it.
**Step 3:** Commit.
```bash
git commit -am "refactor: remove manual Profile This View action (superseded by ambient capture)"
```

---

### Task 2: Suppress transport/framework profiling noise

**Files:**
- Modify: `XAFProfiler/XAFProfiler.Blazor.Server/Startup.cs` (the `AddMiniProfiler(options => …)` block)

**Step 1:** In the options lambda, stop the HTTP middleware from profiling framework/transport paths so only our named ambient profiles persist:
```csharp
options.ShouldProfile = request =>
{
    var path = request.Path.Value ?? string.Empty;
    // Don't profile SignalR transport, the host page, or the profiler's own routes.
    return !(path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
          || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
          || path.StartsWith("/profiler", StringComparison.OrdinalIgnoreCase)
          || path.Equals("/_Host", StringComparison.OrdinalIgnoreCase));
};
```
(Verify the `ShouldProfile` delegate signature against dxdocs/MiniProfiler options before building.)
**Step 2:** Build → 0/0.
**Step 3:** Runtime check: clear the store (`sqlcmd ... "DELETE FROM MiniProfilerClientTimings; DELETE FROM MiniProfilerTimings; DELETE FROM MiniProfilers"`), run, log in, navigate. Expect **no** new `/_blazor*` / `/_Host` rows in `MiniProfilers`. Stop app.
**Step 4:** Commit. `git commit -am "feat: stop persisting transport/framework profiling noise"`

---

### Task 3: Rework the ProfileSummary model + add ProfileQuery child

**Files:**
- Modify: `XAFProfiler/XAFProfiler.Blazor.Server/BusinessObjects/ProfileSummary.cs`
- Create: `XAFProfiler/XAFProfiler.Blazor.Server/BusinessObjects/ProfileQuery.cs`

**Step 1:** `ProfileSummary` — keep the `[DevExpress.ExpressApp.Data.Key] Guid Id` (hidden), replace fields with:
```csharp
public string? Operation { get; set; }      // profiler Name, e.g. "Customer · ListView load"
public DateTime Started { get; set; }
public double DurationMs { get; set; }
public int QueryCount { get; set; }
public double SlowestQueryMs { get; set; }
public IList<ProfileQuery> Queries { get; set; } = new List<ProfileQuery>();
```
Add `[VisibleInListView(false)]` to `Queries` (shown only on the DetailView).
**Step 2:** Create `ProfileQuery` — non-persistent `[DomainComponent]` POCO with `[DevExpress.ExpressApp.Data.Key] Guid Id` (hidden), `string? Sql`, `double DurationMs`, `int ExecuteCount`. `[DefaultProperty(nameof(Sql))]`.
**Step 3:** Build → 0/0.
**Step 4:** Commit. `git commit -am "feat: ProfileSummary identity fields + ProfileQuery child object"`

---

### Task 4: Projection helper (stored MiniProfiler → ProfileSummary/ProfileQuery)

**Files:**
- Create: `XAFProfiler/XAFProfiler.Blazor.Server/Services/ProfileProjection.cs`

**Step 1:** Static helper that takes a loaded `MiniProfiler` (from `storage.Load(id)`) and the target `NonPersistentObjectSpace`, and builds a `ProfileSummary`:
- Walk the timing tree from `profiler.Root` (recurse `Timing.Children`).
- Collect SQL custom timings: each `Timing.CustomTimings["sql"]` entry → command text + `DurationMilliseconds`.
- Group identical SQL → `ProfileQuery { Sql, DurationMs = sum, ExecuteCount = n }` (so N+1 shows a high count); also a flat list is acceptable for v1 — **group by command text**.
- `QueryCount` = total sql timings; `SlowestQueryMs` = max single; `Operation` = `profiler.Name`; `Started`, `DurationMs` from the profiler.
- Create all objects via `npos.CreateObject<T>()`.
Provide two entry points: `BuildSummary(npos, profiler)` (list, no Queries needed) and `BuildDetail(npos, profiler)` (includes Queries).
**Step 2:** Verify the MiniProfiler `Timing`/`CustomTiming` API shape (CustomTimings dictionary key for EF is `"sql"`; property `CommandString`) before building — check the MiniProfiler package.
**Step 3:** Build → 0/0.
**Step 4:** Commit. `git commit -am "feat: project stored MiniProfiler timing tree into ProfileSummary/ProfileQuery"`

---

### Task 5: Update ProfileSummaryController to use the projection + fix DetailView (error 1057)

**Files:**
- Modify: `XAFProfiler/XAFProfiler.Blazor.Server/Controllers/ProfileSummaryController.cs`

**Step 1:** In `ObjectsGetting` (list), build `ProfileSummary` rows via `ProfileProjection.BuildSummary` over `storage.List(200)` (ordered newest-first). In `ObjectByKeyGetting` (detail open), build via `BuildDetail` (includes `Queries`).
**Step 2:** Fix error 1057 (non-persistent "new object can't be shown"). Verify the mechanism against dxdocs, then apply one of:
- Deactivate the default `ListViewProcessCurrentObjectController` for the ProfileSummary ListView and open a read-only DetailView by key via a small custom action/handler, **or**
- Mark the row object as not-new so the default navigation opens it.
Prefer the smallest reliable option per dxdocs.
**Step 3:** Build → 0/0.
**Step 4:** Runtime check: run, open Profile Summary, double-click a row → DetailView opens with **no 1057**; stop app. (Full data check in Task 8.)
**Step 5:** Commit. `git commit -am "feat: ProfileSummary list/detail via projection; fix 1057 detail navigation"`

---

### Task 6: Ambient capture controller

**Files:**
- Create: `XAFProfiler/XAFProfiler.Blazor.Server/Controllers/AmbientProfilingController.cs`

**Step 1:** `WindowController` (`TargetWindowType = Main`), gated by `Profiling:Enabled` (read config via `Application.ServiceProvider`). On `OnActivated`, subscribe `Application.ListViewCreated` and `Application.DetailViewCreated`. For each view, get the `CollectionSource` (ListView) and subscribe:
```csharp
// pseudo:
cs.CollectionReloading += (s,e) => current = MiniProfiler.StartNew($"{modelClass} · ListView load");
cs.CollectionReloaded  += (s,e) => {
    var p = current; current = null;
    if (p != null) Task.Run(() => p.StopAsync(false)).GetAwaiter().GetResult();
};
```
Use a per-view field (not a shared one) to avoid races; unsubscribe on view `Disposing` and controller `OnDeactivated`. Use `ILogger` for diagnostics. Confirm `CollectionReloading`/`CollectionReloaded` and how to reach the ListView's `CollectionSource` against dxdocs.
**Step 2:** Build → 0/0.
**Step 3:** Runtime check: clear store, run, log in, open Customer ListView, click Refresh → a `Customer · ListView load` row with `QueryCount > 0` appears in `MiniProfilers`/grid; no transport noise. Stop app.
**Step 4:** Commit. `git commit -am "feat: ambient per-view-load EF profiling controller"`

---

### Task 7: Custom read-only DetailView + cleanup/retention

**Files:**
- Modify: `XAFProfiler/XAFProfiler.Blazor.Server/Model.xafml` (ProfileSummary_DetailView layout w/ Queries grid; ProfileSummary_ListView + ProfileQuery views read-only, Client mode)
- Create: `XAFProfiler/XAFProfiler.Blazor.Server/Controllers/ProfileMaintenanceController.cs`
- Possibly Modify: `AmbientProfilingController` (retention trim on save)

**Step 1:** Model: ensure `ProfileSummary_ListView` and any `ProfileQuery_ListView` have `DataAccessMode="Client"`; make the DetailView read-only; show `Queries` as a nested grid sorted by `DurationMs` desc.
**Step 2:** `ProfileMaintenanceController` (`ViewController`, target `ProfileSummary` ListView): `SimpleAction "ClearProfiles"` (delete all from the 3 tables / storage API) and `"DeleteSelected"` (delete selected ids); refresh the view after. Use the connection string from config; verify deletion approach (storage API vs direct SQL) — direct `DELETE` on the 3 tables is acceptable.
**Step 3:** Retention: after each ambient save, trim to newest 200 (delete ids beyond the newest 200). Implement in a small helper called from the controller's save path.
**Step 4:** Build → 0/0.
**Step 5:** Commit. `git commit -am "feat: read-only profile DetailView, clear/delete actions, newest-200 retention"`

---

### Task 8: Full runtime verification (against SQL store)

**Files:** none (verification only).

**Step 1:** Free ports; clear store; `dotnet build` (0/0); run; log in.
**Step 2:** Verify each, recording evidence:
- Navigate a few views → rows are named operations (no GUID-only, no `/_blazor*`).
- Open a row → DetailView shows the `Queries` grid; the N+1 operation shows a high `ExecuteCount`. Cross-check `QueryCount` vs `SELECT COUNT(*)` of that profile's sql timings.
- "Clear Profiles" → `SELECT COUNT(*) FROM MiniProfilers` = 0.
- Generate > 200 profiles → count caps at 200.
**Step 3:** Stop app; confirm ports free.
**Step 4:** Update `SESSION_HANDOFF.md` / `TODO.md`; update memory if a new gotcha emerged. Commit.
**Step 5:** `requesting-code-review` before merge/push.

---

## Notes
- Keep `ProfileSummary`/`ProfileQuery` strictly read-only except the maintenance actions.
- If the 1057 fix or the projection API differs from assumptions, STOP and re-verify against dxdocs / the MiniProfiler package before proceeding (systematic-debugging).
