# Session Handoff

**Last updated:** 2026-05-30

## Where things stand

Fresh XAF Blazor Server scaffold (`XAFProfiler`, DX 25.2.5, .NET 8). No domain or
profiling code written yet. This session: **brainstormed and designed** the MiniProfiler
POC and created the project docs.

## What this project is

A POC to integrate StackExchange MiniProfiler into XAF Blazor — proving the two things
the WLNCentral MiniProfiler design deferred: **Blazor circuit profiling** and
**persistent storage**. "WLNCentral, the other way around": prove the hard pattern here,
then port back.

- Design: `docs/plans/2026-05-30-miniprofiler-poc-design.md`
- Architecture: `ARCHITECTURE.md`
- Task list: `TODO.md`
- Reference (the deferred-items doc we're solving): `C:\Projects\WLNCentral\DOCS\plans\2026-04-23-miniprofiler-integration-design.md`

## Decisions made

- Demo domain: **Customer → Order → OrderLine**, seeded heavily to produce N+1 / slow
  aggregations worth profiling.
- Scope: **full POC + storage** — all three layers (HTTP+EF, circuit capture, SQL
  storage + custom XAF browse view).
- Mode: **build & run fully**, Playwright-verify end to end.
- Build tooling: **`dotnet build`** for real builds; **mcpRoslyn `get_compilation_errors`**
  for fast in-loop diagnostics (the Roslyn MCP server has no build tool).

## Next step

Implementation plan via the `writing-plans` skill, then execute task-by-task.

## Watch out for

- Popup `<script>` may not inject into XAF's Blazor host — verify, manual
  `RenderIncludes()` if needed.
- `SqlServerStorage` may need its tables created.
- Module `.csproj` is fine (verified) — the earlier "duplicated `</Project>`" was a
  display artifact, not real.
