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
- [x] PROVEN at runtime (verified against the SQL store, ground truth): clicking the action
      persists a circuit profile "Profile: Customer ListView" (~3947 ms) with nested
      `.Step()` markers "Reload + aggregate" (~3946 ms) → "Sum OrdersTotal (N+1)" (~3886 ms).
      EF SQL **is** captured over the circuit — attached as `CustomTimingsJson` child timings:
      the N+1 step holds **thousands of SQL queries (7.4 MB of timing JSON)**, "Reload" a
      handful (2.2 KB). The N+1 explosion captured exactly as intended.
- [ ] Built-in `/profiler/results?id=<real id>` returns **"hidden"** in-browser — the endpoint
      *loads* the profile but `ResultsAuthorize` returns false (XAF auth cookie not seen as
      authenticated on the raw fetch). A bogus id returns "not found", proving the profile is
      genuinely loadable. Finding #2; the in-app surface is the custom XAF `ProfileSummary` view.

## Layer C — Storage + browsing
- [x] Configure `SqlServerStorage` (`MiniProfiler.Providers.SqlServer` 4.3.8)
- [x] Profiler tables bootstrapped by `ProfilerStorageInitializer` (creates DB if absent, then tables)
- [x] Custom read-only XAF view over stored profiles (`ProfileSummary` + controller)
- [x] PROVEN: 2 profiles / 17 timings in SQL; browse view + `/profiler/results?id=` render them

## Verification
- [x] `dotnet build XAFProfiler.slnx` clean (0/0)
- [x] Run + seed + log in as admin (admin/blank)
- [x] Layer A popup present on home
- [x] Layer B circuit profile with markers + thousands of child SQL timings (verified in SQL)
- [x] Layer C profile persisted to SQL, survives app shutdown (count holds with app stopped)
- [x] Playwright smoke (light theme) — login/home/ListView screenshots `01`–`04` at repo root
- [ ] Built-in `/profiler/results` page rendering — returns "hidden" in-browser (authorize delegate)
- [x] Negative: flag off → **no mini-profiler script** on the page (verified, 0 occurrences).
      Note: `/profiler/results-index` returns **200, not 404** — with the middleware gone there
      is no `/profiler` route, so XAF's `MapFallbackToPage("/_Host")` SPA catch-all serves the
      app shell. No profiler data is served, but it isn't a clean 404. (Flag restored to true.)
- [ ] Dark-theme variant (only light theme verified)
- [x] Custom Profile Summary XAF view CONFIRMED populated — grid shows Name / Started /
      Duration Ms / Results Url with "Profile: Customer ListView" (3946.6 ms) rows reading
      from `SqlServerStorage`. Screenshot `05-profile-summary-view.png`. (8 circuit profiles in SQL.)

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
