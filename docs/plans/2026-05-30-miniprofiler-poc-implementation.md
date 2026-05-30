# MiniProfiler POC — Implementation Plan

> **For Claude:** Execute task-by-task. After each code edit, run mcpRoslyn
> `get_compilation_errors` for fast feedback; run `dotnet build` at the end of each task.
> Invoke `xaf-efcore-entities` before writing business objects and
> `xaf-viewcontroller-patterns` before writing the controller.

**Goal:** Integrate StackExchange MiniProfiler into XAFProfiler (XAF Blazor Server),
proving HTTP+EF profiling, **Blazor circuit capture**, and **persistent SQL storage** —
the two deferred items from the WLNCentral design.

**Design doc:** `docs/plans/2026-05-30-miniprofiler-poc-design.md`

**Tech:** .NET 8, DX XAF 25.2.5, EF Core 8 (proxies for virtual nav props), SQL Server (localdb), MiniProfiler 4.3.x.

**Solution:** `XAFProfiler.slnx` (repo root; new XML format — needs .NET SDK 9.0.200+ to build via CLI)

---

## Phase 1 — Demo domain (gives the profiler something to show)

### Task 1.1: Create `Customer`, `Order`, `OrderLine` entities

**Files (new):**
- `XAFProfiler\XAFProfiler.Module\BusinessObjects\Demo\Customer.cs`
- `XAFProfiler\XAFProfiler.Module\BusinessObjects\Demo\Order.cs`
- `XAFProfiler\XAFProfiler.Module\BusinessObjects\Demo\OrderLine.cs`

XAF EF Core rules (per `xaf-efcore-entities`): `[DefaultClassOptions]` on roots,
`virtual` navigation properties, collections initialized in ctor, no `OwnsOne`,
`[Key] int Oid` or inherit BaseObject pattern consistent with the project, decimal
precision via `[Column(TypeName="decimal(18,2)")]` or fluent config.

- `Customer`: `Name`, `City`, `virtual IList<Order> Orders`.
- `Order`: `OrderDate`, `virtual Customer Customer`, `virtual IList<OrderLine> Lines`.
- `OrderLine`: `Product`, `Quantity`, `UnitPrice`, `virtual Order Order`.

**Verify:** mcpRoslyn `get_compilation_errors` (will fail until Task 1.2 registers DbSets — acceptable).

### Task 1.2: Register DbSets

**File:** `XAFProfiler\XAFProfiler.Module\BusinessObjects\XAFProfilerDbContext.cs`
Add `DbSet<Customer>`, `DbSet<Order>`, `DbSet<OrderLine>`; configure decimal precision +
relationships in `OnModelCreating` if not via attributes.

**Verify:** `dotnet build XAFProfiler.slnx` clean.

### Task 1.3: Seed demo data

**File:** `XAFProfiler\XAFProfiler.Module\DatabaseUpdate\Updater.cs`
In `UpdateDatabaseAfterUpdateSchema`, if no customers exist, generate ~200 customers,
each with 10–50 orders, each order with 1–10 lines (deterministic pseudo-random, no
`Random()` seed dependence on time). `ObjectSpace.CommitChanges()`.

**Verify:** build clean; run app once and confirm rows created (check via log / SSMS).

---

## Phase 2 — Layer A: HTTP + EF Core profiling

### Task 2.1: Add NuGet packages

**File:** `XAFProfiler\XAFProfiler.Blazor.Server\XAFProfiler.Blazor.Server.csproj`
```xml
<PackageReference Include="MiniProfiler.AspNetCore.Mvc" Version="4.3.8" />
<PackageReference Include="MiniProfiler.EntityFrameworkCore" Version="4.3.8" />
```
**Verify:** `dotnet restore` + `dotnet build` succeed.

### Task 2.2: Config flag

**Files:** `appsettings.json` (`"Profiling": { "Enabled": false }`),
`appsettings.Development.json` (`true`). Create the Development file if absent.

### Task 2.3: Register + middleware in `Startup.cs`

`ConfigureServices` (after `AddHttpContextAccessor`), behind the flag:
`AddMiniProfiler(opts => { RouteBasePath="/profiler"; ResultsAuthorize/ResultsListAuthorize = admin-or-dev; }).AddEntityFramework();`
`Configure`: `if(flag) app.UseMiniProfiler();` **before** `app.UseRouting()`.
Add `IsProfilerAuthorized(HttpContext)` helper (Dev → authenticated; else
`IsInRole("Administrators")`).

**Verify:** build clean.

### Task 2.4: Verify popup injection (XAF gotcha)

Run app, `curl` the page, grep for `mini-profiler`. If absent, add
`MiniProfiler.RenderIncludes()` to the XAF Blazor host layout / via middleware.
Document outcome in the design doc Risks section.

**Verify:** popup script present on initial load; `/profiler/results-index` returns
401/302/404 unauthenticated (never 500).

---

## Phase 3 — Layer B: Circuit capture (core deliverable)

### Task 3.1: `CircuitProfilerService`

**File (new):** `XAFProfiler\XAFProfiler.Module\Services\CircuitProfilerService.cs`
Scoped service wrapping `MiniProfiler.StartNew(...)`, exposing `Step(name)` and
`StopAndSave()` (calls `profiler.Stop()` + `options.Storage.Save(profiler)`). Register
`services.AddScoped<CircuitProfilerService>()` in `Startup` (behind flag is fine).

### Task 3.2: `ProfileViewController` with "Profile This View" action

**File (new):** `XAFProfiler\XAFProfiler.Module\Controllers\ProfileViewController.cs`
Per `xaf-viewcontroller-patterns`: `SimpleAction`; on execute, resolve the scoped
service, `StartNew`, wrap a `View.ObjectSpace.Reload()` / aggregation in `.Step()`,
`StopAndSave()`, then notify the user with the saved profile id / link.

**Verify:** build clean; action visible on Customer ListView.

### Task 3.3: Prove a circuit profile is captured

Run, log in, open Customer ListView, click "Profile This View". Confirm a profile with
`.Step()` markers **and child EF SQL** is created (visible at `/profiler/results?id=…`).
This is the key proof — the thing WLNCentral deferred.

---

## Phase 4 — Layer C: Persistent storage + browse view

### Task 4.1: Switch to `SqlServerStorage`

In `AddMiniProfiler` options, set
`options.Storage = new SqlServerStorage(connectionString)`. Ensure the MiniProfiler
tables exist (run `SqlServerStorage.TableCreationScripts` once, or document manual run).

**Verify:** restart app; a previously-captured profile still appears at
`/profiler/results-index`.

### Task 4.2: Custom XAF browse view

A read-only non-persistent (or DB-mapped) object surfaced in navigation listing stored
profiles (id, name, started, duration) with a link to the results page. Keep minimal.

**Verify:** profiles browsable inside the app's own navigation.

---

## Phase 5 — End-to-end verification

### Task 5.1: Full Playwright smoke (per webapp-testing)

- Flag on: popup present on load; trigger circuit action; assert a profile is stored.
- Restart: assert profile persists (Layer C).
- Flag off: no popup; `/profiler/results-index` 404/401.
- Screenshot key states (light + dark theme).

### Task 5.2: Update docs

Mark `TODO.md` items done; update `SESSION_HANDOFF.md`; record the popup-injection and
SqlServerStorage findings in the design doc. Update MEMORY.md per global memory rules.

---

## Verification Checklist (final)

- [ ] Demo data seeded; Customer ListView shows calculated total (N+1 visible)
- [ ] Layer A: popup + EF SQL on HTTP path
- [ ] Layer B: circuit action produces a stored profile with `.Step()` + child SQL
- [ ] Layer C: profile survives restart; browsable in index + XAF view
- [ ] Flag off → fully inert (no popup, endpoints 404/401)
- [ ] `dotnet build XAFProfiler.slnx` clean, no new warnings
- [ ] Playwright smoke green
