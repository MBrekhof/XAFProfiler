# XAFProfiler — Architecture

## What this is

A DevExpress XAF Blazor Server **proof-of-concept** for integrating StackExchange
MiniProfiler into an XAF app — specifically to crack the two problems the WLNCentral
MiniProfiler design deferred: **profiling over the Blazor SignalR circuit** and
**persistent storage** of profiles. Built here first in a clean sandbox, then ported
back to WLNCentral.

See `docs/plans/2026-05-30-miniprofiler-poc-design.md` for the full design.

## Solution layout

```
C:\Projects\XAFProfiler\
├── XAFProfiler.slnx                    ← solution (XML format, at repo root) — build this
├── XAFProfiler\
│   ├── XAFProfiler.Module\             ← shared module: business objects, controllers, services
│   │   ├── BusinessObjects\
│   │   │   ├── XAFProfilerDbContext.cs ← EF Core DbContext (XAFProfilerEFCoreDbContext)
│   │   │   └── Demo\                    ← Customer / Order / OrderLine (demo domain)
│   │   ├── Controllers\                 ← ProfileViewController ("Profile This View")
│   │   ├── Services\                    ← CircuitProfilerService (circuit-scoped)
│   │   └── DatabaseUpdate\Updater.cs    ← seeds demo data
│   └── XAFProfiler.Blazor.Server\       ← host app
│       ├── Startup.cs                   ← XAF + MiniProfiler registration, middleware
│       └── appsettings*.json            ← Profiling:Enabled flag, connection string
└── docs\plans\                          ← design docs
```

## Tech stack

- .NET 8, C#, nullable + implicit usings enabled
- DevExpress XAF **25.2.5** (EFCore provider, Blazor Server)
- EF Core, SQL Server (localdb): `(localdb)\mssqllocaldb`, catalog `XAFProfiler`
- StackExchange MiniProfiler 4.x (`MiniProfiler.AspNetCore.Mvc`,
  `MiniProfiler.EntityFrameworkCore`) — added during implementation

## Profiling design (three layers)

- **A — HTTP + EF Core:** `AddMiniProfiler().AddEntityFramework()`, middleware before
  `UseRouting()`, admin-gated, behind `Profiling:Enabled`.
- **B — Circuit capture:** a scoped `CircuitProfilerService` + a XAF ViewController
  action that manually `StartNew()` / `.Step()` / saves, because circuit events have no
  `HttpContext`.
- **C — Storage:** `SqlServerStorage` so profiles persist; browsable via
  `/profiler/results-index` and a custom XAF view.

## Build & verify

- Build: `dotnet build XAFProfiler.slnx` (from repo root)
- Fast in-loop diagnostics: **mcpRoslyn `get_compilation_errors`** (the Roslyn MCP
  server has no build tool — use it for diagnostics, `dotnet build` for the real build).
- Run: `dotnet run --project XAFProfiler\XAFProfiler.Blazor.Server`
- DB updates automatically when debugging (see `Startup` `AddBuildStep`).

## Conventions

- XAF EF Core entities: virtual nav properties, initialized collections, no `OwnsOne`,
  explicit decimal precision (see global `xaf-efcore-entities` rules).
- ViewControllers: proper `OnActivated`/`OnDeactivated` subscription + ObjectSpace
  discipline (see global `xaf-viewcontroller-patterns` rules).
