# Session Handoff

**Last updated:** 2026-05-31

## Where things stand

**MiniProfiler POC — core proven, some UI surfaces unconfirmed.** The hard parts WLNCentral
deferred (circuit profiling + persistent storage) are verified at the data layer (SQL store =
ground truth). Build clean (`dotnet build XAFProfiler.slnx` → 0/0). App stopped, ports free,
`Profiling:Enabled` restored to true.

## What was built (DX XAF 25.2.5, .NET 8)

- **Demo domain:** `Customer → Order → OrderLine`, `Customer.OrdersTotal` N+1 generator. Seeded 200 / 5999 / 33065.
- **Layer A (HTTP+EF):** MiniProfiler packages + `AddMiniProfiler().AddEntityFramework()` behind
  `Profiling:Enabled`, admin-gated, `<mini-profiler />` in `_Host.cshtml`.
- **Layer B (circuit capture — core):** `Services/CircuitProfilerService.cs` +
  `Controllers/ProfileViewController.cs` ("Profile This View"). Captures `.Step()` markers over the circuit.
- **Layer C (storage + browse):** `SqlServerStorage` (`MiniProfiler.Providers.SqlServer`),
  `Services/ProfilerStorageInitializer.cs` (creates DB + tables), `ProfileSummary` +
  `ProfileSummaryController` read-only XAF browse view (code complete).

## Verified (ground truth: SQL store + curl)

- **Layer B circuit capture:** the action persists "Profile: Customer ListView" (~3947 ms) with
  nested `Reload + aggregate` (~3946 ms) → `Sum OrdersTotal (N+1)` (~3886 ms). **EF SQL captured
  over the circuit** as `CustomTimingsJson` child timings — N+1 step holds thousands of queries
  (7.4 MB JSON). 8 profiles accumulated across runs. Persists across app shutdown.
- **Layer A popup:** "MiniProfiler Init" in the browser console when flag on.
- **Negative test:** flag off → no mini-profiler script (0 occurrences). Nuance:
  `/profiler/results-index` returns **200 not 404** (no `/profiler` route → XAF SPA catch-all
  serves the shell; no profiler data served). Flag restored to true.
- Honest screenshots `01`–`04` at repo root (login, home, Customer ListView w/ N+1 column, ribbon).

## NOT yet confirmed (be honest in the port-back)

1. **`ProfileSummary` XAF view RENDER** — across ~3 Playwright attempts the nav click never swapped
   the content pane in a captured screenshot (kept showing the Customer ListView). Code path is
   exercised (reads the same storage as the verified profiles) but the populated grid has NOT been
   seen. A real screenshot is owed — likely needs a more robust wait for the XAF view swap.
2. **Built-in `/profiler/results?id=<real id>`** returns **"hidden"** in-browser — `ResultsAuthorize`
   returns false (XAF auth cookie not seen as authenticated on the raw fetch). A bogus id returns
   "not found", so the profile IS loadable; purely the delegate withholding it.
3. Dark-theme variant (only light theme exercised).

## Findings (for the port-back)

1. **DB-ordering:** table init must create the app DB itself (XAF creates it lazily after host
   start). Fixed via a `master` connection in `ProfilerStorageInitializer`.
2. **Built-in MiniProfiler UI blocked under XAF auth** (caveat #2 above) — once the custom XAF
   view's render is confirmed, it's the reliable in-app surface.
3. **Popup injection** needed a manual `<mini-profiler />` tag helper (XAF doesn't auto-inject).

## Next steps

- Capture the `ProfileSummary` view render (robust wait); confirm/fix the built-in UI auth.
- Dark-theme Playwright variant.
- Fix-before-port-back (code review): sync-over-async stop, null connection-string guard, ILogger.
- `finishing-a-development-branch`: a few early commits were duplicated/amended during
  parallel-agent races (`19eaa3a`/`3ba3959`, `99dfc76`/`85393cc`) — consider squashing first.
- Port the proven circuit-capture + storage pattern back to WLNCentral `Profile` branch.

## Git

Local repo (no remote). Branch `master`. `run.log` / `build.log` / `.playwright-mcp/` gitignored.
Tracked screenshots: `01`–`04`.
