# XAFProfiler — TODO

Tracking the MiniProfiler POC. Design:
`docs/plans/2026-05-30-miniprofiler-poc-design.md`.

**Status: POC complete (2026-05-31).** All three layers verified, including the custom
ProfileSummary XAF view (now confirmed populated). One minor item open (built-in
`/profiler/results` auth) + dark-theme variant.

## Demo domain
- [x] `Customer` / `Order` / `OrderLine` XAF EF Core entities (`BusinessObjects/Demo/`)
- [x] Register DbSets in `XAFProfilerEFCoreDbContext`
- [x] Seed ~200 customers / orders / lines in `Updater` (seeded 200 / 5999 / 33065)
- [x] Customer ListView with a calculated `OrdersTotal` (deliberate N+1 / slow aggregation) — screenshot `03`

## Layer A — HTTP + EF Core
- [x] Add `MiniProfiler.AspNetCore.Mvc` + `MiniProfiler.EntityFrameworkCore` to Blazor.Server
- [x] `Profiling:Enabled` flag in `appsettings.json` (false) / `appsettings.Development.json` (true)
- [x] `AddMiniProfiler().AddEntityFramework()` + admin-gated authorize in `Startup`
- [x] `app.UseMiniProfiler()` before `UseRouting()`
- [x] Popup injects into XAF Blazor host (`<mini-profiler />` in `_Host.cshtml` + `_ViewImports.cshtml`);
      "MiniProfiler Init" appears in the browser console when flag on.

## Layer B — Circuit capture (core deliverable)
- [x] `Services/CircuitProfilerService.cs` (scoped)
- [x] `Controllers/ProfileViewController.cs` — "Profile This View" action
- [x] Manual `StartNew()` / `.Step()` / `StopAsync(false)` over the SignalR circuit
- [x] PROVEN at runtime (verified against the SQL store): clicking the action persists a
      circuit profile "Profile: Customer ListView" (~3947 ms) with nested `.Step()` markers
      "Reload + aggregate" (~3946 ms) → "Sum OrdersTotal (N+1)" (~3886 ms). EF SQL **is**
      captured over the circuit — attached as `CustomTimingsJson` child timings: the N+1 step
      holds **thousands of SQL queries (7.4 MB of timing JSON)**, "Reload" a handful (2.2 KB).
      8 such profiles accumulated across verification runs.

## Layer C — Storage + browsing
- [x] Configure `SqlServerStorage` (`MiniProfiler.Providers.SqlServer` 4.3.8)
- [x] Profiler tables bootstrapped by `ProfilerStorageInitializer` (creates DB if absent, then tables)
- [x] Profiles persist in SQL across app shutdown (count holds with app stopped)
- [x] Custom read-only XAF view (`ProfileSummary` + `ProfileSummaryController`) — CONFIRMED
      populated: grid shows 8 rows (Name / Id / Started / Duration Ms / Results Url) reading
      from `SqlServerStorage`. Screenshot `05-profile-summary-view.png`.
      **Root-cause fix:** `ProfileSummary` originally derived from the EF Core persistent
      `BaseObject`, which made XAF build a *persistent* collection source (queried EF → 0 rows)
      so `ObjectsGetting` never fired. Per DX docs (eXpressAppFramework/113711) a non-persistent
      `[DomainComponent]` must be a plain POCO with `[Key]`; rows must be created via
      `npos.CreateObject<T>()`, not `new`. Verified by instrumentation: ObjectsGetting fired →
      "loaded 8 profiles" → grid shows 8 rows.

## Verification
- [x] `dotnet build XAFProfiler.slnx` clean (0/0)
- [x] Run + seed + auto-login as admin
- [x] Layer A popup present (console "MiniProfiler Init")
- [x] Layer B circuit profile with markers + thousands of child SQL timings (verified in SQL)
- [x] Layer C profile persisted to SQL, survives app shutdown
- [x] Playwright smoke (light theme) — honest screenshots `01`–`05` at repo root (`05` = populated ProfileSummary grid)
- [x] Negative: flag off → **no mini-profiler script** on the page (0 occurrences). Nuance:
      `/profiler/results-index` returns **200, not 404** — with the middleware gone there's no
      `/profiler` route, so XAF's `MapFallbackToPage("/_Host")` SPA catch-all serves the app
      shell (no profiler data served). Flag restored to true.
- [ ] Built-in `/profiler/results?id=<real id>` renders in-browser — currently returns **"hidden"**
      (the `ResultsAuthorize` delegate; a bogus id returns "not found", so the profile IS loadable).
- [x] `ProfileSummary` XAF view render (confirmed — see Layer C above)
- [ ] Dark-theme variant (only light theme verified)

## Known findings (document for the port-back)
- **Non-persistent view object must be a plain POCO.** A `[DomainComponent]` that derives from
  the EF Core persistent `BaseObject` makes XAF build a persistent collection source (queries
  EF → empty) and `ObjectsGetting` never fires. Use a POCO with `[Key]` + `npos.CreateObject<T>()`
  (DX docs eXpressAppFramework/113711). This bit the ProfileSummary view; now fixed.
- **Startup DB ordering:** `EnsureTables` must create the app DB itself (XAF creates it lazily,
  after host start). Fixed in `ProfilerStorageInitializer` via a `master` connection.
- **Built-in MiniProfiler UI blocked under XAF auth:** `/profiler/results-index` "Unauthorized",
  `/profiler/results?id=` "hidden" — `ResultsAuthorize`/`ResultsListAuthorize` don't recognise
  XAF's auth cookie. The custom XAF view reads storage directly and sidesteps this — and is now
  confirmed working, so it's the reliable in-app surface.

## Follow-up (not this POC)
- [ ] Capture the `ProfileSummary` view render; decide whether to fix the built-in UI auth
- [ ] Fix-before-port-back (code review): sync-over-async stop, null connection-string guard, ILogger
- [ ] Port the proven circuit-capture + storage pattern back to WLNCentral `Profile` branch
