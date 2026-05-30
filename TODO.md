# XAFProfiler — TODO

Tracking the MiniProfiler POC. Design:
`docs/plans/2026-05-30-miniprofiler-poc-design.md`.

**Status: POC complete and runtime-verified (2026-05-31).** All three layers proven
end-to-end against a running app + SQL Server localdb.

## Demo domain
- [x] `Customer` / `Order` / `OrderLine` XAF EF Core entities (`BusinessObjects/Demo/`)
- [x] Register DbSets in `XAFProfilerEFCoreDbContext`
- [x] Seed ~200 customers / orders / lines in `Updater` (seeded 200 / 5999 / 33065)
- [x] Customer ListView with a calculated `OrdersTotal` (deliberate N+1 / slow aggregation)

## Layer A — HTTP + EF Core
- [x] Add `MiniProfiler.AspNetCore.Mvc` + `MiniProfiler.EntityFrameworkCore` to Blazor.Server
- [x] `Profiling:Enabled` flag in `appsettings.json` (false) / `appsettings.Development.json` (true)
- [x] `AddMiniProfiler().AddEntityFramework()` + admin-gated authorize in `Startup`
- [x] `app.UseMiniProfiler()` before `UseRouting()`
- [x] Popup injects into XAF Blazor host via `<mini-profiler />` in `_Host.cshtml` + `_ViewImports.cshtml`

## Layer B — Circuit capture (core deliverable)
- [x] `Services/CircuitProfilerService.cs` (scoped)
- [x] `Controllers/ProfileViewController.cs` — "Profile This View" action
- [x] Manual `StartNew()` / `.Step()` / `StopAsync(false)` over the SignalR circuit
- [x] PROVEN: clicking the action persists a profile with `.Step()` markers
      ("Profile: Customer ListView" → "Reload + aggregate" → "Sum OrdersTotal (N+1)")

## Layer C — Storage + browsing
- [x] Configure `SqlServerStorage` (`MiniProfiler.Providers.SqlServer` 4.3.8)
- [x] Profiler tables bootstrapped by `ProfilerStorageInitializer` (creates DB if absent, then tables)
- [x] Custom read-only XAF view over stored profiles (`ProfileSummary` + controller)
- [x] PROVEN: 2 profiles / 17 timings in SQL; browse view + `/profiler/results?id=` render them

## Verification
- [x] `dotnet build XAFProfiler.slnx` clean (0/0)
- [x] Run + seed + log in as admin (admin/blank)
- [x] Layer A popup present on home
- [x] Layer B circuit profile with markers created (notification + SQL rows)
- [x] Layer C profile persisted to SQL, shows in custom XAF view + individual results page
- [x] Playwright smoke (light theme) — screenshots `01`–`07` at repo root
- [ ] Negative: flag off → no popup, endpoints 404/401 (NOT yet run — see handoff)
- [ ] Dark-theme variant (only light theme verified)

## Known findings (document for the port-back)
- **Startup DB ordering:** `EnsureTables` must create the app DB itself (XAF creates it
  lazily, after host start). Fixed in `ProfilerStorageInitializer` via a `master` connection.
- **Built-in `/profiler/results-index` shows empty under XAF auth:** the MVC list endpoint is
  gated by `ResultsListAuthorize`, and XAF's auth cookie isn't seen as `IsAuthenticated` by the
  raw endpoint, so it returns nothing. The **custom XAF browse view** sidesteps this (reads
  storage directly) — a point in favour of the custom view for the WLNCentral port-back.

## Follow-up (not this POC)
- [ ] Run the negative (flag-off) check
- [ ] Decide whether to fix built-in index auth or rely solely on the custom XAF view
- [ ] Port the proven circuit-capture + storage pattern back to WLNCentral `Profile` branch
