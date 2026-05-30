# MiniProfiler POC in XAF Blazor — Design

**Date:** 2026-05-30
**Status:** Approved, pending implementation plan
**Repo:** `C:\Projects\XAFProfiler` (solution at `XAFProfiler.slnx`, repo root)

## Goal

Prove that [StackExchange MiniProfiler](https://miniprofiler.com/) can be made to work
**fully** inside a DevExpress XAF Blazor Server app — including the two things the
WLNCentral integration design (`C:\Projects\WLNCentral\DOCS\plans\2026-04-23-miniprofiler-integration-design.md`)
**explicitly deferred**:

1. **SignalR / Blazor circuit profiling** — capturing timings for button clicks and
   grid loads that run over the circuit, where there is no `HttpContext`.
2. **Persistent storage** — profiles that survive an app restart and are browsable.

This is the WLNCentral work done "the other way around": build and prove the hard
pattern here in a clean sandbox, then port the proven approach back to WLNCentral.

The HTTP + EF Core "quick win" from the WLNCentral doc is table stakes here; the POC's
value is Layers B and C below.

## Scope

- **In:** Demo domain with seeded data; HTTP+EF Core profiling; circuit capture via a
  XAF ViewController action; SQL Server persistent storage; a custom XAF view to browse
  stored profiles; admin-gated access; config toggle.
- **Out:** Hangfire/background-job profiling; production hardening; the port-back to
  WLNCentral itself (separate follow-up).

## Demo Domain

`Customer` (1) → `Order` (N) → `OrderLine` (N), as XAF EF Core business objects
following the project's entity rules (virtual properties, collection init, BaseObject
pattern, decimal precision). DbSets added to `XAFProfilerEFCoreDbContext`.

Seeded in `DatabaseUpdate/Updater.cs`: ~200 customers, thousands of orders, tens of
thousands of order lines. A `Customer` ListView with a calculated order total
deliberately produces an N+1 access pattern and a slow aggregation — giving the profiler
something obviously worth showing.

## Architecture — Three Layers

### Layer A — HTTP + EF Core (quick win)

- NuGet (Blazor.Server project): `MiniProfiler.AspNetCore.Mvc`,
  `MiniProfiler.EntityFrameworkCore`.
- `Startup.ConfigureServices`: `AddMiniProfiler(options => …).AddEntityFramework()`
  behind `Configuration.GetValue<bool>("Profiling:Enabled")`.
  - `RouteBasePath = "/profiler"`, admin-gated `ResultsAuthorize` /
    `ResultsListAuthorize` (Development → any authenticated user; otherwise
    `IsInRole("Administrators")`).
- `Startup.Configure`: `app.UseMiniProfiler()` **before** `app.UseRouting()`.
- Config: `Profiling:Enabled=false` in `appsettings.json`, `true` in
  `appsettings.Development.json`.

**Known XAF gotcha (from WLN doc Risks):** XAF's Blazor host may not inject the popup
`<script>` include. Verify; if absent, emit `MiniProfiler.RenderIncludes()` manually in
the host layout / via a small middleware. Document the outcome.

Captures: initial document load, EF Core SQL, OData/Web API hits.

### Layer B — Circuit capture (the hard part)

Blazor UI events run over a SignalR circuit with **no `HttpContext`**, so
`MiniProfiler.Current` is `null` for button clicks and grid loads. Approach:

- A **circuit-scoped profiling service** (`Services/CircuitProfilerService.cs`,
  registered `Scoped`) that owns the lifecycle of a manually-started profiler.
- A XAF `ViewController` (`Controllers/ProfileViewController.cs`) exposing a
  **"Profile This View"** action. On activation it calls `MiniProfiler.StartNew()`,
  wraps the profiled operation (e.g. a reload / aggregation) in `.Step("…")` blocks,
  then **explicitly stops and saves** the profile to storage.
- This is the pattern that does not exist anywhere yet and is the core deliverable.

### Layer C — Persistent storage + browsing

- Configure `SqlServerStorage` (same connection string / DB) instead of the default
  in-memory `MemoryCacheStorage`, so circuit + HTTP profiles survive restarts.
- Browse two ways:
  1. Built-in `/profiler/results-index`.
  2. A **custom read-only XAF view** over the stored profiles, so profiling lives
     inside the app's own navigation (this is also the shape WLNCentral would want).

## Files

| File | Purpose |
| --- | --- |
| `XAFProfiler.Module/BusinessObjects/Demo/Customer.cs` | Demo aggregate root |
| `XAFProfiler.Module/BusinessObjects/Demo/Order.cs` | Order entity |
| `XAFProfiler.Module/BusinessObjects/Demo/OrderLine.cs` | Order line entity |
| `XAFProfiler.Module/BusinessObjects/XAFProfilerDbContext.cs` | Add DbSets |
| `XAFProfiler.Module/DatabaseUpdate/Updater.cs` | Bulk seed demo data |
| `XAFProfiler.Module/Services/CircuitProfilerService.cs` | Layer B scoped service |
| `XAFProfiler.Module/Controllers/ProfileViewController.cs` | Layer B "Profile This View" action |
| `XAFProfiler.Blazor.Server/Startup.cs` | Registration, middleware, storage |
| `XAFProfiler.Blazor.Server/appsettings*.json` | `Profiling:Enabled` flag |

## Data Flow

**HTTP path (A):** request → middleware starts profiler → EF interceptor records SQL →
response stored → popup links to `/profiler/results?id=…`.

**Circuit path (B):** user clicks "Profile This View" → controller `StartNew()` →
`.Step()` blocks wrap the operation, EF interceptor still attaches child SQL timings →
controller stops + saves to `SqlServerStorage` → profile appears in the index and the
custom XAF view.

## Error Handling

- All MiniProfiler registration is behind the `Profiling:Enabled` flag; flag off → no
  middleware, endpoints 404, no popup.
- `.Step()` / `MiniProfiler.Current` calls are null-safe, so profiling code is inert
  when disabled.
- `ResultsAuthorize` false → 401, never 500.

## Testing Plan

1. `dotnet build XAFProfiler.slnx` clean. Use mcpRoslyn
   `get_compilation_errors` for fast in-loop checks between edits.
2. Run app, seed localdb, log in as admin.
3. **Layer A:** popup visible on load; click → EF SQL listed with durations.
4. **Layer B:** trigger "Profile This View" on the Customer ListView; confirm a circuit
   profile with `.Step()` markers + child SQL is created.
5. **Layer C:** restart app; confirm the profile still appears in
   `/profiler/results-index` and the custom XAF view.
6. **Negative:** flag off → no popup, `/profiler/results-index` 404/401.
7. Playwright smoke test for the popup-present / popup-absent assertions.

## Outcome (verified 2026-05-31)

All three layers proven at runtime against the running app + SQL Server localdb:

- **Layer A:** `<mini-profiler />` popup renders on the XAF Blazor host (added to
  `_Host.cshtml` + a new `Pages/_ViewImports.cshtml` registering the tag helper).
- **Layer B (core):** the "Profile This View" action on the Customer ListView captures a
  circuit profile with nested `.Step()` markers — confirmed both by the in-app
  notification and by the persisted rows
  (`Profile: Customer ListView` → `Reload + aggregate` → `Sum OrdersTotal (N+1)`).
- **Layer C:** `SqlServerStorage` (`MiniProfiler.Providers.SqlServer` 4.3.8) persists
  profiles (verified: 2 profiles / 17 timings in SQL); the custom `ProfileSummary` XAF
  view lists them and `/profiler/results?id=` renders the full timing tree.

## Findings (carry these to the WLNCentral port-back)

1. **Startup DB-ordering bug (found by running it).** `SqlServerStorage` tables must be
   created, but at host-startup time the app database does not exist yet — XAF creates it
   lazily after `app.Run()`. The table initializer therefore failed with
   *"Cannot open database 'XAFProfiler'"*. Fix: `ProfilerStorageInitializer` now connects
   to `master`, `CREATE DATABASE` if absent (name validated + bracket-escaped), then
   creates the MiniProfiler tables. XAF still owns its own schema on the now-existing DB.
2. **Built-in `/profiler/results-index` is empty under XAF auth.** That MVC endpoint is
   gated by `ResultsListAuthorize`; XAF's auth cookie is not recognised as
   `IsAuthenticated` by the raw endpoint, so the list returns nothing even though SQL has
   rows. The **custom XAF browse view** avoids this entirely (reads storage directly) —
   a concrete reason to prefer the custom view in WLNCentral.
3. **Popup injection** needed a manual `<mini-profiler />` tag helper (the XAF host does
   not auto-inject it) — exactly the risk the WLNCentral design flagged.

## Still open (not blocking the POC)

- Negative path (flag off → no popup, endpoints 404/401) not yet exercised.
- Only light theme verified via Playwright.
- `StopAsync` is called sync-over-async from the controller; fine for SQL/local but worth
  revisiting under load.

## Port-back (follow-up, not this POC)

Fold the circuit-capture service + `SqlServerStorage` config + the DB-bootstrap and
custom browse view back into the WLNCentral `Profile` branch, replacing that design's
deferred items. Note finding #2 when wiring auth.
