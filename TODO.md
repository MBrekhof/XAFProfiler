# XAFProfiler — TODO

Tracking the MiniProfiler POC. Design:
`docs/plans/2026-05-30-miniprofiler-poc-design.md`.

## Demo domain
- [ ] `Customer` / `Order` / `OrderLine` XAF EF Core entities (`BusinessObjects/Demo/`)
- [ ] Register DbSets in `XAFProfilerEFCoreDbContext`
- [ ] Seed ~200 customers / thousands of orders / tens of thousands of lines in `Updater`
- [ ] Customer ListView with a calculated order total (deliberate N+1 / slow aggregation)

## Layer A — HTTP + EF Core
- [ ] Add `MiniProfiler.AspNetCore.Mvc` + `MiniProfiler.EntityFrameworkCore` to Blazor.Server
- [ ] `Profiling:Enabled` flag in `appsettings.json` (false) / `appsettings.Development.json` (true)
- [ ] `AddMiniProfiler().AddEntityFramework()` + admin-gated authorize in `Startup`
- [ ] `app.UseMiniProfiler()` before `UseRouting()`
- [ ] Verify popup `<script>` injects into XAF Blazor host; manual `RenderIncludes()` if not

## Layer B — Circuit capture (core deliverable)
- [ ] `Services/CircuitProfilerService.cs` (scoped)
- [ ] `Controllers/ProfileViewController.cs` — "Profile This View" action
- [ ] Manual `StartNew()` / `.Step()` / stop + save over the SignalR circuit

## Layer C — Storage + browsing
- [ ] Configure `SqlServerStorage`
- [ ] Confirm profiler tables (auto-create vs. script)
- [ ] Custom read-only XAF view over stored profiles

## Verification
- [ ] `dotnet build` clean (+ mcpRoslyn `get_compilation_errors` between edits)
- [ ] Run + seed + log in as admin
- [ ] Layer A popup + EF SQL visible
- [ ] Layer B circuit profile with markers created
- [ ] Layer C profile survives restart, shows in index + XAF view
- [ ] Negative: flag off → no popup, endpoints 404/401
- [ ] Playwright smoke test

## Follow-up (not this POC)
- [ ] Port the proven circuit-capture + storage pattern back to WLNCentral `Profile` branch
