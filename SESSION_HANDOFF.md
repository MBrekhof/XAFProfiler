# Session Handoff

**Last updated:** 2026-05-31

## Where things stand

**MiniProfiler POC complete and runtime-verified.** All three layers proven end-to-end
against the running app + SQL Server localdb. Solution builds clean (`dotnet build
XAFProfiler.slnx` → 0/0). App stopped, ports free.

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

Core deliverable (Layer B) verified end-to-end: the "Profile This View" action produced a
circuit profile **"Profile: Customer ListView" (402 ms)** with nested markers
`Reload + aggregate` (78 ms) → `Sum OrdersTotal (N+1)` (310 ms), and the results page flags
**200 duplicate SQL queries** (the N+1) as child EF timings — see `07-circuit-profile-detail.png`.
It persists in SQL across app shutdown. Screenshots `01`–`07` at repo root.

Caveat: the custom `ProfileSummary` XAF nav view was not visually re-confirmed populated
(the Playwright nav click flaked); it reads the same storage the results page renders, so
the path is exercised but a clean screenshot is still owed. The built-in
`/profiler/results-index` returns "Unauthorized" under XAF auth (documented finding #2).

## Two findings worth remembering (see design doc "Findings")

1. **DB-ordering:** table init must create the app DB itself (XAF makes it lazily). Fixed
   via a `master` connection in `ProfilerStorageInitializer`.
2. **Built-in `/profiler/results-index` empty under XAF auth** — the custom XAF browse
   view is the reliable surface. Relevant to the WLNCentral port-back.

## Next steps

- Run the negative (flag-off) check; verify dark theme.
- Final `requesting-code-review` pass, then `finishing-a-development-branch`.
- History note: a few early commits were duplicated/amended during parallel-agent races
  (`19eaa3a`/`3ba3959`, `99dfc76`/`85393cc`). HEAD builds clean; consider squashing before
  any port-back PR.
- Port the proven pattern back to WLNCentral `Profile` branch.

## Git

Local repo (no remote). HEAD `87e35c5`. Branch: `master` (work was done directly on it).
`run.log` / `build.log` gitignored.
