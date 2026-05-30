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

Core deliverable (Layer B) verified at the data layer (SQL store = ground truth): the
"Profile This View" action produced a circuit profile **"Profile: Customer ListView"
(~3947 ms)** with nested markers `Reload + aggregate` (~3946 ms) → `Sum OrdersTotal (N+1)`
(~3886 ms). **EF SQL is captured over the circuit** — attached as `CustomTimingsJson` child
timings; the N+1 step holds **thousands of SQL queries (7.4 MB JSON)**, the N+1 explosion
exactly as designed. The profile persists in SQL across app shutdown. Login/ListView
screenshots `01`–`04` at repo root (the ListView shows the N+1 "Orders Total" column).

Two honest caveats (both unresolved, both documented in the design doc):
1. The built-in `/profiler/results?id=<real id>` returns **"hidden"** in-browser — the
   endpoint loads the profile but `ResultsAuthorize` returns false (XAF auth cookie not seen
   as authenticated on the raw fetch); `/profiler/results-index` likewise "Unauthorized". A
   bogus id returns "not found", proving the real profile is loadable — purely the authorize
   delegate withholding it. Fix the delegate or use the custom XAF view.
2. The custom `ProfileSummary` XAF nav view was not visually re-confirmed populated (nav
   click flaked); it reads the same storage that holds the verified profile.

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
