# Session Handoff

**Last updated:** 2026-05-31

## Where things stand

**MiniProfiler POC complete and runtime-verified.** All three layers proven end-to-end
against the running app + SQL Server localdb. Solution builds clean (`dotnet build
XAFProfiler.slnx` → 0/0). App stopped, ports free, `Profiling:Enabled` restored to true.

## What was built (DX XAF 25.2.5, .NET 8)

- **Demo domain:** `Customer → Order → OrderLine` (`Module/BusinessObjects/Demo/`),
  `Customer.OrdersTotal` calculated property (N+1 generator). Seeded 200 / 5999 / 33065.
- **Layer A (HTTP+EF):** MiniProfiler packages + `AddMiniProfiler().AddEntityFramework()`
  behind `Profiling:Enabled`, admin-gated, `<mini-profiler />` in `_Host.cshtml`.
- **Layer B (circuit capture — core):** `Services/CircuitProfilerService.cs` +
  `Controllers/ProfileViewController.cs` ("Profile This View"). Captures `.Step()` markers
  over the SignalR circuit.
- **Layer C (storage + browse):** `SqlServerStorage` (`MiniProfiler.Providers.SqlServer`),
  `Services/ProfilerStorageInitializer.cs` (creates DB + tables), `ProfileSummary` +
  `ProfileSummaryController` read-only XAF browse view.

## Runtime proof (Playwright, light theme)

Core deliverable (Layer B) verified at the data layer (SQL store = ground truth): the
"Profile This View" action produced a circuit profile **"Profile: Customer ListView"
(~3947 ms)** with nested markers `Reload + aggregate` (~3946 ms) → `Sum OrdersTotal (N+1)`
(~3886 ms). **EF SQL is captured over the circuit** — attached as `CustomTimingsJson` child
timings; the N+1 step holds **thousands of SQL queries (7.4 MB JSON)**, the N+1 explosion
exactly as designed. Persists in SQL across app shutdown (8 circuit profiles accumulated
across verification runs). Screenshots `01`–`05` at repo root.

**Custom `ProfileSummary` XAF view CONFIRMED populated** (the previously-owed screenshot):
the grid renders Name / Started / Duration Ms / Results Url with "Profile: Customer
ListView" (3946.6 ms) rows read from `SqlServerStorage` — `05-profile-summary-view.png`.
This is the reliable in-app surface.

**Negative test done:** flag off → **no mini-profiler script** on the page (0 occurrences).
Nuance: `/profiler/results-index` returns **200, not 404** — with the middleware gone there's
no `/profiler` route, so XAF's `MapFallbackToPage("/_Host")` SPA catch-all serves the app
shell. No profiler data is served, but it isn't a clean 404. Flag restored to true.

## Findings / caveats (for the port-back)

1. **Startup DB-ordering:** table init must create the app DB itself (XAF creates it lazily
   after host start). Fixed via a `master` connection in `ProfilerStorageInitializer`.
2. **Built-in MiniProfiler UI is blocked under XAF auth (still open):**
   `/profiler/results?id=<real id>` returns **"hidden"** and `/profiler/results-index`
   "Unauthorized" — the endpoint loads the profile but `ResultsAuthorize` returns false
   (XAF auth cookie not seen as authenticated on the raw fetch). A bogus id returns "not
   found", proving the profile is genuinely loadable — purely the delegate withholding it.
   The custom XAF `ProfileSummary` view (which works) is the reliable surface.
3. **Popup injection** needed a manual `<mini-profiler />` tag helper (XAF doesn't auto-inject).

## Next steps

- Dark-theme Playwright variant (only light theme verified so far).
- Fix-before-port-back (from code review): sync-over-async stop in the controller, a
  null-connection-string guard in Startup, and `Console.WriteLine` → `ILogger`.
- Optionally fix `ResultsAuthorize` to accept XAF's authenticated principal (caveat #2).
- Final `finishing-a-development-branch`; a few early commits were duplicated/amended during
  parallel-agent races (`19eaa3a`/`3ba3959`, `99dfc76`/`85393cc`) — consider squashing
  before any port-back PR.
- Port the proven circuit-capture + storage pattern back to WLNCentral `Profile` branch.

## Git

Local repo (no remote). Branch: `master` (work done directly on it). `run.log` /
`build.log` / `.playwright-mcp/` gitignored. Tracked screenshots: `01`–`05`.
