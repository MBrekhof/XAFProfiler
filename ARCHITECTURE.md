# XAFProfiler — Architecture

## What this is

A DevExpress XAF Blazor Server **proof-of-concept** for integrating StackExchange
MiniProfiler into an XAF app — specifically to crack the two problems the WLNCentral
MiniProfiler design deferred: **profiling over the Blazor SignalR circuit** and
**persistent storage** of profiles. Built here first in a clean sandbox, then ported
back to WLNCentral.

The current design is **ambient EF-Core profiling**: every XAF ListView data-load is captured
automatically (no manual action), via a custom EF Core `DbCommandInterceptor`. See
`docs/plans/2026-05-31-ambient-ef-profiling-design.md` for the full design and
`docs/HOW_TO_IMPLEMENT.md` for the step-by-step recipe. The earlier manual
"Profile This View" approach (`docs/plans/2026-05-30-*`) has been **superseded and removed**.

## Solution layout

```
C:\Projects\XAFProfiler\
├── XAFProfiler.slnx                          ← solution (XML format, at repo root) — build this
├── XAFProfiler\
│   ├── XAFProfiler.Module\                   ← shared module: demo domain, DbContext, seed
│   │   ├── BusinessObjects\
│   │   │   ├── XAFProfilerDbContext.cs        ← EF Core DbContext (XAFProfilerEFCoreDbContext)
│   │   │   └── Demo\                          ← Customer / Order / OrderLine (demo domain)
│   │   └── DatabaseUpdate\Updater.cs          ← seeds 200 customers / ~6k orders / ~33k lines
│   └── XAFProfiler.Blazor.Server\            ← host app
│       ├── Startup.cs                         ← XAF + MiniProfiler registration, interceptor wiring
│       ├── Model.xafml                        ← Client mode + read-only for the profiler views
│       ├── Services\
│       │   ├── QueryCaptureInterceptor.cs     ← DbCommandInterceptor — captures every SQL command
│       │   ├── OperationCaptureRegistry.cs    ← explicit MiniProfiler per DbContext + retention trim
│       │   ├── ProfileProjection.cs           ← MiniProfiler timing tree → ProfileSummary/ProfileQuery
│       │   ├── ProfileStore.cs                ← direct-SQL Clear / Delete / Trim maintenance
│       │   ├── ProfilerStorageInitializer.cs  ← bootstraps DB + the 3 MiniProfiler tables
│       │   └── ProfilingConstants.cs          ← shared "sql" custom-timing key (writer↔reader)
│       ├── Controllers\
│       │   ├── AmbientProfilingController.cs   ← brackets each ListView load (capture driver)
│       │   ├── ProfileSummaryController.cs     ← populates the browse ListView from storage
│       │   ├── ProfileSummaryDetailController.cs ← fills the drill-down Queries grid
│       │   └── ProfileMaintenanceController.cs  ← Clear Profiles / Delete Selected actions
│       ├── BusinessObjects\
│       │   ├── ProfileSummary.cs               ← non-persistent browse object
│       │   └── ProfileQuery.cs                 ← non-persistent per-query child
│       └── appsettings*.json                   ← Profiling:Enabled flag, connection string
└── docs\
    ├── HOW_TO_IMPLEMENT.md                     ← integration recipe (ambient design)
    └── plans\                                  ← design docs (2026-05-30 POC + 2026-05-31 ambient)
```

> `CircuitHandlerProxy.cs` / `ProxyHubConnectionHandler.cs` in `Services/` are stock XAF Blazor
> scaffolding (SignalR / scoped-circuit plumbing from the project template), unrelated to profiling.

## Tech stack

- .NET 8, C#, nullable + implicit usings enabled
- DevExpress XAF **25.2.5** (EFCore provider, Blazor Server)
- EF Core, SQL Server (localdb): `(localdb)\mssqllocaldb`, catalog `XAFProfiler`
- StackExchange MiniProfiler 4.3.8 (`MiniProfiler.AspNetCore.Mvc`,
  `MiniProfiler.EntityFrameworkCore`, `MiniProfiler.Providers.SqlServer`) — used for storage +
  result viewer; capture is our own interceptor

## Profiling design (current — ambient EF capture)

The DevExpress grid materialises its query on an async chain where `MiniProfiler.Current`
(`AsyncLocal`) is null, so MiniProfiler's built-in EF interceptor captures nothing for it. We hold
the profiler explicitly and attribute SQL by `DbContext` instance instead:

- **Capture (`QueryCaptureInterceptor`)** — a singleton EF Core `DbCommandInterceptor` registered
  on the XAF DbContext via `options.AddInterceptors`. On each executed command it looks up the
  command's owning `DbContext` in the registry and appends the SQL as a `"sql"` custom timing.
- **Registry (`OperationCaptureRegistry`)** — a singleton that holds one explicitly-started
  `MiniProfiler` per in-flight operation, keyed by `DbContext` in a `ConditionalWeakTable`. `End`
  stops+saves via a **mandatory `Task.Run(...).GetAwaiter().GetResult()`** offload (a direct
  sync-over-async deadlocks on the circuit's `RendererSynchronizationContext`) and trims storage to
  the newest 200.
- **Driver (`AmbientProfilingController`, Main `WindowController`)** — subscribes to
  `ListViewCreating`, then brackets the load on the collection source's
  `CollectionChanging`/`Reloading` (`Begin`) and flushes (`End`) when the next load begins, on
  `Reloaded`, on `Disposed`, or on deactivation. The deferred flush exists because the collection
  source's `Disposed` doesn't fire on navigation.
- **Storage (`SqlServerStorage` + `ProfilerStorageInitializer`)** — profiles persist to SQL Server
  (same connection string XAF uses); tables are bootstrapped at startup.
- **Browse + drill-down** — non-persistent `ProfileSummary` (Operation / Started / DurationMs /
  QueryCount / SlowestQueryMs) and child `ProfileQuery` (Sql / DurationMs / ExecuteCount), projected
  from the stored timing tree by `ProfileProjection`, surfaced read-only with Clear / Delete
  maintenance.
- **Noise control** — `options.ShouldProfile = _ => false` disables HTTP auto-profiling, so only the
  named ambient operations are persisted (no `/_blazor`, `/_Host`, `GET /` rows).

## Build & verify

- Build: `dotnet build XAFProfiler.slnx` (from repo root; SDK 10.0.300+ handles the `.slnx`)
- Fast in-loop diagnostics: **mcpRoslyn `get_compilation_errors`** (the Roslyn MCP
  server has no build tool — use it for diagnostics, `dotnet build` for the real build).
- Run: `dotnet run --project XAFProfiler\XAFProfiler.Blazor.Server`
- DB updates automatically when debugging (see `Startup` `AddBuildStep`).
- **Verify capture against the SQL store (ground truth)**, not screenshots:
  `sqlcmd -S "(localdb)\mssqllocaldb" -E -d XAFProfiler -W -Q "SELECT COUNT(*) FROM MiniProfilers"`

## Conventions

- XAF EF Core entities: virtual nav properties, initialized collections, no `OwnsOne`,
  explicit decimal precision (see global `xaf-efcore-entities` rules).
- ViewControllers: proper `OnActivated`/`OnDeactivated` subscription + ObjectSpace
  discipline (see global `xaf-viewcontroller-patterns` rules).
- Non-persistent view objects: `[DomainComponent]` POCO + `DevExpress.ExpressApp.Data.Key` +
  Client `DataAccessMode` + `ObjectSpaceCreated` subscription from a `WindowController` (see the
  three-part gotcha in `docs/HOW_TO_IMPLEMENT.md`).
- Capture/maintenance is best-effort: nothing in the profiling path is allowed to break a user's
  operation; failures are logged via `ILogger` and swallowed.
