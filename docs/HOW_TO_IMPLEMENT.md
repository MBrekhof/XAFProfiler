# How to integrate ambient EF-Core profiling into a DevExpress XAF Blazor Server app

This is a step-by-step recipe for adding **automatic, app-wide EF-Core profiling** to an XAF
Blazor Server application using [StackExchange MiniProfiler](https://miniprofiler.com/) for
storage — including the two hard parts most integrations skip: **capturing SQL that runs over the
SignalR circuit** (where `MiniProfiler.Current` is null) and **persisting profiles to SQL Server**
so you can browse and drill into them inside the app.

There is **no "profile this" button**. Every XAF ListView data-load is captured automatically;
the user just opens **Profile Summary** to see what each screen ran.

All code below is taken from the working POC in this repository. File paths are relative to the
`Blazor.Server` host project unless noted.

---

## Contents

- [Prerequisites](#prerequisites)
- [Why ambient + interceptor (the core insight)](#why-ambient--interceptor-the-core-insight)
- [Step 1 — Add the NuGet packages](#step-1--add-the-nuget-packages)
- [Step 2 — Register MiniProfiler for storage only (Layer A/C)](#step-2--register-miniprofiler-for-storage-only-layer-ac)
- [Step 3 — Register the capture services + interceptor on the DbContext](#step-3--register-the-capture-services--interceptor-on-the-dbcontext)
- [Step 4 — Wire the HTTP pipeline + bootstrap storage](#step-4--wire-the-http-pipeline--bootstrap-storage)
- [Step 5 — The capture core: registry + interceptor](#step-5--the-capture-core-registry--interceptor)
- [Step 6 — The capture driver: AmbientProfilingController](#step-6--the-capture-driver-ambientprofilingcontroller)
- [Step 7 — Browse + drill into profiles (non-persistent ListView)](#step-7--browse--drill-into-profiles-non-persistent-listview)
- [Step 8 — Cleanup + retention](#step-8--cleanup--retention)
- [Verification checklist](#verification-checklist)
- [Gotchas reference](#gotchas-reference)

---

## Prerequisites

- An XAF **Blazor Server** app on the **EF Core** provider (this recipe uses SQL Server).
- Admin/authenticated security so the built-in profiler UI can be gated (optional — the in-app
  browse view is the primary surface and needs no gating).
- A way to produce a measurable operation (this POC ships a deliberate N+1 via a calculated
  `Customer.OrdersTotal` property over a seeded `Customer → Order → OrderLine` domain).

---

## Why ambient + interceptor (the core insight)

Two facts about XAF Blazor Server drive the entire design:

1. **No `HttpContext` over the circuit.** A grid load happens in a SignalR circuit event, not an
   HTTP request, so MiniProfiler's HTTP middleware never starts a profiler for it and
   `MiniProfiler.Current` is null.
2. **`MiniProfiler.Current` is `AsyncLocal`, and the grid runs on a different async chain.** Even
   if you `MiniProfiler.StartNew()` in your controller, the DevExpress grid materialises its query
   on a separate chain where `Current` is null again — so MiniProfiler's *own* EF interceptor
   (which logs to `Current`) captures **zero** SQL for the grid. (This was proven the hard way:
   ambient `· ListView load` profiles had 1 timing and 0 SQL timings.)

The fix is to stop relying on `MiniProfiler.Current` entirely:

- Hold a `MiniProfiler` **explicitly**, one per in-flight operation.
- Key it by the **`DbContext` instance** that runs the operation's SQL. Each XAF ListView's object
  space owns its own `DbContext`, and a custom `DbCommandInterceptor` sees that exact instance via
  `CommandExecutedEventData.Context`.
- The interceptor appends each command onto the right operation's held profiler as a `"sql"`
  custom timing — the same shape MiniProfiler's storage and our projection already understand.

---

## Step 1 — Add the NuGet packages

Add to the **Blazor.Server** project:

```xml
<PackageReference Include="MiniProfiler.AspNetCore.Mvc" Version="4.3.8" />
<PackageReference Include="MiniProfiler.EntityFrameworkCore" Version="4.3.8" />
<PackageReference Include="MiniProfiler.Providers.SqlServer" Version="4.3.8" />
```

- `AspNetCore.Mvc` — MiniProfiler options, storage plumbing, the `/profiler/*` result viewer.
- `Providers.SqlServer` — the `SqlServerStorage` provider (persistence).
- `EntityFrameworkCore` — only needed if you *also* want MiniProfiler's built-in EF capture for
  ordinary HTTP requests. The ambient capture in this recipe does **not** depend on it; we add our
  own interceptor. (The POC keeps `.AddEntityFramework()` registered but neutralised — see Step 2.)

---

## Step 2 — Register MiniProfiler for storage only (Layer A/C)

We use MiniProfiler purely for its **storage + result-viewer**, not its HTTP auto-profiling. In
`Startup.ConfigureServices`, register it **behind a config flag** and — critically — set
`ShouldProfile = _ => false` so no HTTP request ever creates a profiler row (this is what kills the
`GET /`, `GET /_Host`, `/_blazor` noise that otherwise litters the store):

```csharp
// Startup.cs — ConfigureServices
if (Configuration.GetValue<bool>("Profiling:Enabled"))
{
    services.AddMiniProfiler(options =>
    {
        options.RouteBasePath = "/profiler";
        options.PopupRenderPosition = RenderPosition.Left;
        options.PopupShowTimeWithChildren = true;
        options.TrackConnectionOpenClose = true;
        options.ColorScheme = ColorScheme.Auto;

        // Disable HTTP-request auto-profiling entirely. ALL profiling is done by the ambient
        // EF Core interceptor (Step 5), which is independent of MiniProfiler.Current and of the
        // HTTP middleware. The middleware is still registered (it serves /profiler/* result
        // endpoints) but ShouldProfile=false prevents any HTTP profiler row — including the
        // Blazor SPA host-page render — from being persisted.
        options.ShouldProfile = _ => false;

        // Gate the built-in /profiler UI. In Development, allow any authenticated user;
        // otherwise require the Administrators role.
        options.ResultsAuthorize     = req => IsProfilerAuthorized(req.HttpContext);
        options.ResultsListAuthorize = req => IsProfilerAuthorized(req.HttpContext);

        // Persist to SQL Server (the SAME connection string XAF uses) instead of the default
        // in-memory cache, so captured profiles survive restarts.
        var profilerConn = Configuration.GetConnectionString("ConnectionString");
        options.Storage = new SqlServerStorage(profilerConn);
    })
    .AddEntityFramework();
}
```

```csharp
private bool IsProfilerAuthorized(HttpContext ctx)
{
    if (ctx == null) return false;
    var env = ctx.RequestServices.GetService<IWebHostEnvironment>();
    if (env != null && env.IsDevelopment())
        return ctx.User?.Identity?.IsAuthenticated == true;
    return ctx.User?.IsInRole("Administrators") == true;
}
```

Add the config flag:

```jsonc
// appsettings.json
"Profiling": { "Enabled": false }
// appsettings.Development.json
"Profiling": { "Enabled": true }
```

> **Why a flag?** Profiling has overhead and exposes timing internals. When off, none of the
> capture services below are registered and the middleware is absent — zero overhead.

---

## Step 3 — Register the capture services + interceptor on the DbContext

Still in `ConfigureServices`, register the two capture singletons (behind the same flag), then add
the interceptor to the **XAF DbContext options** so it sees every command:

```csharp
// Startup.cs — ConfigureServices, after AddMiniProfiler
if (Configuration.GetValue<bool>("Profiling:Enabled"))
{
    // Singletons so the interceptor (added to the DbContext options) and the
    // AmbientProfilingController share ONE registry instance.
    services.AddSingleton<OperationCaptureRegistry>();
    services.AddSingleton<QueryCaptureInterceptor>();
}

services.AddXaf(Configuration, builder =>
{
    builder.UseApplication<MyBlazorApplication>();
    builder.Modules /* … your modules … */;

    builder.ObjectSpaceProviders
        .AddEFCore(options => options.PreFetchReferenceProperties())
        .WithDbContext<MyEFCoreDbContext>((serviceProvider, options) =>
        {
            options.UseConnectionString(connectionString);

            if (Configuration.GetValue<bool>("Profiling:Enabled"))
            {
                // Capture every EF SQL command onto the per-operation profiler held by
                // OperationCaptureRegistry, attributed by this DbContext instance.
                options.AddInterceptors(
                    serviceProvider.GetRequiredService<QueryCaptureInterceptor>());
            }
        })
        .AddNonPersistent(); // required: the browse objects are non-persistent (Step 7)
});
```

> `AddNonPersistent()` is what makes XAF serve `ProfileSummary` / `ProfileQuery` through a
> `NonPersistentObjectSpace`. Without it the browse view has nowhere to come from.

---

## Step 4 — Wire the HTTP pipeline + bootstrap storage

In `Startup.Configure`, call `UseMiniProfiler()` **before** `UseRouting()` (still behind the flag)
and bootstrap the storage tables (next step):

```csharp
// Startup.cs — Configure
if (Configuration.GetValue<bool>("Profiling:Enabled"))
{
    app.UseMiniProfiler();
    ProfilerStorageInitializer.EnsureTables(
        Configuration.GetConnectionString("ConnectionString"));
}

app.UseRouting();
app.UseXaf();
app.UseEndpoints(endpoints =>
{
    endpoints.MapXafEndpoints();
    endpoints.MapBlazorHub();
    endpoints.MapFallbackToPage("/_Host");
    endpoints.MapControllers();
});
```

`ProfilerStorageInitializer.EnsureTables` connects to `master`, creates the application database if
XAF hasn't yet (XAF creates it lazily on first access), then runs MiniProfiler's official
table-creation scripts. It is idempotent and never throws (persistence is best-effort). It creates
three tables: `MiniProfilers`, `MiniProfilerTimings`, `MiniProfilerClientTimings`. (See
`Services/ProfilerStorageInitializer.cs` for the full source, including the
`^[A-Za-z_][A-Za-z0-9_]*$` catalog-name validation before any `CREATE DATABASE`.)

> **Note on the negative test:** with the flag off there is no `/profiler` route, so a request to
> `/profiler/results-index` returns **200** (XAF's `MapFallbackToPage("/_Host")` SPA catch-all
> serves the shell), **not 404**. Don't mistake that for "profiler is on."

---

## Step 5 — The capture core: registry + interceptor

### 5a. A shared constant (writer ↔ reader contract)

The interceptor (writer) and the projection (reader, Step 7) must agree on the custom-timing key
or capture silently yields empty profiles:

```csharp
// Services/ProfilingConstants.cs
internal static class ProfilingConstants
{
    // MiniProfiler.EntityFrameworkCore uses "sql" for EF; we reuse it.
    internal const string SqlTimingKey = "sql";
}
```

### 5b. `OperationCaptureRegistry` — explicit profiler per `DbContext`

A singleton that ties an in-flight operation to an explicitly-held `MiniProfiler`, keyed by
`DbContext` in a `ConditionalWeakTable` (so an abandoned context can't leak a profiler):

```csharp
// Services/OperationCaptureRegistry.cs (abridged)
public sealed class OperationCaptureRegistry
{
    private const string SqlTimingKey = ProfilingConstants.SqlTimingKey;
    private const int RetentionLimit = 200;

    private readonly object _gate = new();
    private readonly ConditionalWeakTable<DbContext, OperationCapture> _active = new();
    private readonly string? _connectionString;
    private readonly ILogger<OperationCaptureRegistry>? _logger;

    public sealed class OperationCapture
    {
        public MiniProfiler Profiler { get; }
        // Appends one command as a "sql" custom timing on the profiler's ROOT timing, so the
        // projection can read it via profiler.Root.CustomTimings["sql"].
        public void AddSql(string commandText, double durationMs, string executeType)
        {
            var root = Profiler.Root;
            if (root is null) return;
            var ct = new CustomTiming(Profiler, commandText)
            {
                DurationMilliseconds = (decimal)durationMs,
                ExecuteType = executeType,
            };
            root.AddCustomTiming(SqlTimingKey, ct);
        }
    }

    // Begin: start an explicit profiler for this context's operation.
    public void Begin(DbContext ctx, string operationName)
    {
        var profiler = MiniProfiler.StartNew(operationName);
        if (profiler is null) return; // StartNew returns null if profiling is disabled.
        lock (_gate)
        {
            _active.Remove(ctx);                                   // replace any stale entry
            _active.Add(ctx, new OperationCapture(profiler, _logger));
        }
    }

    // End: stop+save the profiler, then trim storage to the newest 200.
    public void End(DbContext ctx)
    {
        OperationCapture? capture = null;
        lock (_gate) { if (_active.TryGetValue(ctx, out var f)) { capture = f; _active.Remove(ctx); } }
        if (capture is null) return;

        // MANDATORY offload: a direct sync-over-async (.GetAwaiter().GetResult()) on the Blazor
        // circuit's RendererSynchronizationContext deadlocks — StopAsync's continuation posts
        // back to the very thread you are blocking. Task.Run runs it with no ambient sync context.
        Task.Run(() => capture.Profiler.StopAsync(discardResults: false)).GetAwaiter().GetResult();

        // Best-effort retention on a background thread (don't block the circuit on extra SQL).
        if (_connectionString is { } cs)
            Task.Run(() => ProfileStore.TrimToNewest(cs, RetentionLimit, _logger));
    }

    public bool TryGetCurrent(DbContext ctx, out OperationCapture capture) { /* table lookup */ }
}
```

> **The `Task.Run` offload is not optional.** It is the documented fix for the circuit
> `RendererSynchronizationContext` deadlock — without it, the load hangs forever on the loading
> overlay.

### 5c. `QueryCaptureInterceptor` — the `DbCommandInterceptor`

A singleton interceptor that, on each executed command, looks up the command's owning `DbContext`
in the registry and appends the SQL. It never touches `MiniProfiler.Current` and never throws into
EF's pipeline:

```csharp
// Services/QueryCaptureInterceptor.cs (abridged)
public sealed class QueryCaptureInterceptor : DbCommandInterceptor
{
    private readonly OperationCaptureRegistry _registry;

    private void Capture(DbCommand command, CommandExecutedEventData e, string executeType)
    {
        try
        {
            if (e.Context is { } ctx && _registry.TryGetCurrent(ctx, out var capture))
                capture.AddSql(command.CommandText, e.Duration.TotalMilliseconds, executeType);
        }
        catch (Exception ex) { _logger?.LogError(ex, "QueryCaptureInterceptor: capture failed."); }
    }

    public override DbDataReader ReaderExecuted(DbCommand c, CommandExecutedEventData e, DbDataReader r)
    { Capture(c, e, "Reader"); return base.ReaderExecuted(c, e, r); }

    // …ReaderExecutedAsync, ScalarExecuted(+Async), NonQueryExecuted(+Async) all do the same.
}
```

The grid materialises **synchronously** in practice, but both sync and async overrides are
implemented for safety.

---

## Step 6 — The capture driver: AmbientProfilingController

A Main-window `WindowController` that brackets every ListView load. It subscribes to
`ListViewCreating` (not `ListViewCreated` — the collection events fire *during* construction), then
brackets the load on the collection source's events. The subtle part is **where the load ends**:

```csharp
// Controllers/AmbientProfilingController.cs (abridged)
public sealed class AmbientProfilingController : WindowController
{
    private OperationCaptureRegistry? _registry;
    private DbContext? _openContext; // the single still-open capture, flushed at the next load

    public AmbientProfilingController() => TargetWindowType = WindowType.Main;

    protected override void OnActivated()
    {
        base.OnActivated();
        var config = Application.ServiceProvider?.GetService<IConfiguration>();
        if (config?.GetValue<bool>("Profiling:Enabled") != true) return;   // inert when off
        _registry = Application.ServiceProvider?.GetService<OperationCaptureRegistry>();
        Application.ListViewCreating += Application_ListViewCreating;
    }

    protected override void OnDeactivated()
    {
        Application.ListViewCreating -= Application_ListViewCreating;
        FlushOpen();                 // don't lose the last view's profile on shutdown
        base.OnDeactivated();
    }

    private void FlushOpen()
    {
        var ctx = _openContext; if (ctx == null) return;
        _openContext = null; _registry?.End(ctx);
    }

    private void Application_ListViewCreating(object? sender, ListViewCreatingEventArgs e)
    {
        var cs = e.CollectionSource; if (cs?.ObjectTypeInfo?.Type is not { } objectType) return;

        // Opening ANY view is a fresh interaction; the PREVIOUS load's SQL ran synchronously and
        // is fully captured by now. Flush it here — BEFORE the skip check — so navigating to the
        // profiler's own views still saves the pending operation.
        FlushOpen();

        // Skip the profiler's own surface so it doesn't profile itself.
        if (objectType == typeof(ProfileSummary) || objectType == typeof(ProfileQuery)) return;

        var caption = Application?.Model?.BOModel?.GetClass(objectType)?.Caption ?? objectType.Name;
        var profileName = $"{caption} · ListView load";

        // The view's object space is a plain EFCoreObjectSpace (no Security System here),
        // so we can reach the DbContext the grid will use.
        DbContext? Ctx() => (cs.ObjectSpace as EFCoreObjectSpace)?.DbContext;

        void Begin() { FlushOpen(); if (Ctx() is { } c) { _registry!.Begin(c, profileName); _openContext = c; } }
        void End()   { if (Ctx() is { } c) { if (ReferenceEquals(_openContext, c)) _openContext = null; _registry!.End(c); } }

        // Initial load: CollectionChanging opens; the grid SELECT + prefetch N+1 run synchronously
        // after CollectionChanged and are flushed by the NEXT Begin (deferred flush).
        cs.CollectionChanging  += (_, _) => Begin();
        // Refresh: Reloading/Reloaded bracket the round-trip, so End() on Reloaded saves a full profiler.
        cs.CollectionReloading += (_, _) => Begin();
        cs.CollectionReloaded  += (_, _) => End();
        cs.Disposed            += (_, _) => End();   // rarely fires on navigation, but flush if it does
    }
}
```

**Why the deferred flush?** The collection source's `Disposed` event does **not** fire when the
user navigates between ListViews (XAF keeps the previous view alive), so it can't be the load-END
boundary. But each load's SQL is fully captured synchronously before any subsequent event — so we
flush (`End`) the previous capture when the **next** load begins. The very last view's profiler is
flushed by the next navigation (e.g. opening Profile Summary).

---

## Step 7 — Browse + drill into profiles (non-persistent ListView)

Read the persisted profiles straight out of `SqlServerStorage` and surface them in an XAF list +
detail view. Both view objects are **non-persistent**.

### 7a. The non-persistent objects

```csharp
// BusinessObjects/ProfileSummary.cs
[DomainComponent, DefaultClassOptions, DefaultProperty(nameof(Operation))]
public class ProfileSummary
{
    [Browsable(false)]
    [DevExpress.ExpressApp.Data.Key]   // ← XAF non-persistent key, NOT DataAnnotations.Key
    public Guid Id { get; set; }
    public string? Operation { get; set; }     // profiler Name, e.g. "Customer · ListView load"
    public DateTime Started { get; set; }
    public double DurationMs { get; set; }
    public int QueryCount { get; set; }
    public double SlowestQueryMs { get; set; }

    [VisibleInListView(false)]
    public IList<ProfileQuery> Queries { get; set; } = new List<ProfileQuery>();
}

// BusinessObjects/ProfileQuery.cs
[DomainComponent, DefaultProperty(nameof(Sql))]
public class ProfileQuery
{
    [Browsable(false)]
    [DevExpress.ExpressApp.Data.Key]
    public Guid Id { get; set; }
    public string? Sql { get; set; }
    public double DurationMs { get; set; }
    public int ExecuteCount { get; set; }   // how many times this SQL ran (the N+1 signal)
}
```

> **Do not** derive these from a persistent base (e.g. EF Core `BaseObject`) — that routes the
> ListView to an `EFCoreObjectSpace` and `ObjectsGetting` never fires. A plain POCO with
> `[DomainComponent]` + an explicit XAF key is the correct non-persistent pattern.

### 7b. The projection (timing tree → view objects)

A pure helper walks the loaded `MiniProfiler`'s timing tree, reads every `CustomTimings["sql"]`
entry, and groups by command text so repeats become `ExecuteCount`:

```csharp
// Services/ProfileProjection.cs (shape)
public static ProfileSummary BuildSummary(NonPersistentObjectSpace npos, MiniProfiler p);  // list rows (Queries empty)
public static ProfileSummary BuildDetail (NonPersistentObjectSpace npos, MiniProfiler p);  // detail (Queries populated)
public static IList<ProfileQuery> BuildQueries(NonPersistentObjectSpace npos, MiniProfiler p);

// Every object is created via npos.CreateObject<T>() (NOT `new`), then:
private static void MarkExisting(NonPersistentObjectSpace npos, object obj)
    => npos.RemoveFromModifiedObjects(obj);   // ← fixes error 1057 (see gotcha)
```

`BuildQueries` groups SQL timings by command text, sets `DurationMs` to the **summed** duration
across executions and `ExecuteCount` to the group size, then orders slowest-total first — so a
query run 10×5ms outranks a single 45ms query (exactly the N+1 you want surfaced).

### 7c. The browse controller — subscribe via `ObjectSpaceCreated`, from a `WindowController`

```csharp
// Controllers/ProfileSummaryController.cs (shape)
public sealed class ProfileSummaryController : WindowController
{
    public ProfileSummaryController() => TargetWindowType = WindowType.Main;

    protected override void OnActivated()
    {
        base.OnActivated();
        Application.ObjectSpaceCreated += (s, e) =>
        {
            if (e.ObjectSpace is not NonPersistentObjectSpace npos) return;
            npos.ObjectsGetting     += ObjectsGetting;     // detach on npos.Disposed
            npos.ObjectByKeyGetting += ObjectByKeyGetting;
        };
    }

    private static IAsyncStorage? Storage => MiniProfiler.DefaultOptions?.Storage;

    private void ObjectsGetting(object? sender, ObjectsGettingEventArgs e)
    {
        if (e.ObjectType != typeof(ProfileSummary)) return;
        var npos = (NonPersistentObjectSpace)sender!;
        var list = new BindingList<ProfileSummary> { AllowNew = false, AllowRemove = false };
        foreach (var id in Storage!.List(200, orderBy: ListResultsOrder.Descending))
            if (Storage.Load(id) is { } p) list.Add(ProfileProjection.BuildSummary(npos, p));
        e.Objects = list;
    }

    private void ObjectByKeyGetting(object? sender, ObjectByKeyGettingEventArgs e)
    {
        if (e.ObjectType == typeof(ProfileSummary) && e.Key is Guid id && Storage?.Load(id) is { } p)
            e.Object = ProfileProjection.BuildDetail((NonPersistentObjectSpace)sender!, p);
    }
}
```

### 7d. The detail controller — fill the nested grid (the easy-to-miss bug)

When you open a DetailView by double-clicking a ListView row, XAF **reuses the list row's object**
(built by `BuildSummary`, whose `Queries` is empty) instead of re-fetching via `ObjectByKeyGetting`
→ `BuildDetail`. So the nested grid is empty unless a DetailView controller fills it on activation:

```csharp
// Controllers/ProfileSummaryDetailController.cs (shape)
public sealed class ProfileSummaryDetailController : ObjectViewController<DetailView, ProfileSummary>
{
    protected override void OnActivated()
    {
        base.OnActivated();
        if (View?.CurrentObject is not ProfileSummary s || s.Queries is { Count: > 0 }) return;
        if (ObjectSpace is not NonPersistentObjectSpace npos) return;
        if (MiniProfiler.DefaultOptions?.Storage?.Load(s.Id) is { } p)
            s.Queries = ProfileProjection.BuildQueries(npos, p);
    }
}
```

### 7e. The model — Client mode + read-only for all three views

```xml
<!-- Model.xafml -->
<Application>
  <Views>
    <ListView   Id="ProfileSummary_ListView"         DataAccessMode="Client"
                AllowEdit="False" AllowNew="False" AllowDelete="False" />
    <ListView   Id="ProfileSummary_Queries_ListView" DataAccessMode="Client"
                AllowEdit="False" AllowNew="False" AllowDelete="False" />
    <DetailView Id="ProfileSummary_DetailView"        AllowEdit="False" />
  </Views>
</Application>
```

### ⚠ The three-part gotcha (this is the part everyone gets wrong)

A non-persistent ListView in **XAF Blazor** silently shows **"No data to display"** unless **all
three** of these hold. Each was verified the hard way in this POC:

| # | Requirement | Why | If missing |
| --- | --- | --- | --- |
| 1 | `DataAccessMode=Client` on the ListView (`Model.xafml`) | Blazor defaults to **Queryable**, which builds an `IQueryable` against a data store and **never raises `ObjectsGetting`** for a storeless type | Columns render, grid stays empty, no error |
| 2 | Key uses `DevExpress.ExpressApp.Data.Key` | XAF's non-persistent key detection ignores `System.ComponentModel.DataAnnotations.Key` (that's the EF Core key) | Error **1037** / `ArgumentException` "without a public key property" during `CreateListView` |
| 3 | Subscribe via `XafApplication.ObjectSpaceCreated` from a **`WindowController`**, not a per-view `ViewController.OnActivated` | The collection source requests objects **during view creation**, before per-view controllers activate — so `OnActivated` subscribes too late | Columns render, grid stays empty, no error, `ObjectsGetting` "never fires" |

References (DevExpress docs):
- List View Data Access Modes — <https://docs.devexpress.com/eXpressAppFramework/113683>
- Client Mode — <https://docs.devexpress.com/eXpressAppFramework/118449>
- Non-Persistent Objects (Key Property) — <https://docs.devexpress.com/eXpressAppFramework/116516>
- Detail View reuses the List View's object — <https://docs.devexpress.com/eXpressAppFramework/401747>
- `CreateObject` marks objects new; `RemoveFromModifiedObjects` — <https://docs.devexpress.com/eXpressAppFramework/113471>

---

## Step 8 — Cleanup + retention

### 8a. Direct-SQL maintenance helper

`SqlServerStorage` has no bulk-delete API, so a small helper does parameterized direct SQL against
the three tables (children first, to respect FK constraints):

```csharp
// Services/ProfileStore.cs (API)
public static void ClearAll   (string connectionString, ILogger? logger = null);
public static void DeleteByIds(string connectionString, IEnumerable<Guid> ids, ILogger? logger = null);
public static void TrimToNewest(string connectionString, int keep, ILogger? logger = null); // keep<=0 ⇒ no-op
```

`TrimToNewest` keeps the newest `keep` by `Started` (guarding `keep<=0`, which would otherwise wipe
everything). It is called fire-and-forget after each save by `OperationCaptureRegistry.End`.

### 8b. The maintenance actions

A ViewController targeting the `ProfileSummary` ListView adds two `Tools` actions:

```csharp
// Controllers/ProfileMaintenanceController.cs (shape)
public sealed class ProfileMaintenanceController : ViewController
{
    public ProfileMaintenanceController()
    {
        TargetObjectType = typeof(ProfileSummary);
        TargetViewType   = ViewType.ListView;

        var clear = new SimpleAction(this, "ClearProfiles", "Tools")
        { Caption = "Clear Profiles", ConfirmationMessage = "Delete ALL stored profiles?" };
        clear.Execute += (s, e) => { ProfileStore.ClearAll(ConnStr); View.ObjectSpace.Refresh(); };

        var del = new SimpleAction(this, "DeleteSelectedProfiles", "Tools")
        { Caption = "Delete Selected", SelectionDependencyType = SelectionDependencyType.RequireMultipleObjects };
        del.Execute += (s, e) =>
        {
            var ids = e.SelectedObjects.OfType<ProfileSummary>().Select(p => p.Id).ToList();
            ProfileStore.DeleteByIds(ConnStr, ids); View.ObjectSpace.Refresh();
        };
    }
}
```

(Both unsubscribe in `Dispose`; the real controller resolves the connection string from
`IConfiguration` and shows success/error toasts.)

---

## Verification checklist

Verify against the **SQL store (ground truth)**, not just the UI — Blazor grids and Playwright
clicks can silently no-op.

- [ ] `dotnet build XAFProfiler.slnx` → 0 warnings / 0 errors.
- [ ] App runs; you can log in.
- [ ] **Capture:** open the Customer ListView, then navigate away. A `Customer · ListView load` row
      appears in `MiniProfilers` (`SELECT COUNT(*) FROM MiniProfilers`).
- [ ] **EF SQL captured:** that profile's `QueryCount` > 0; its DetailView lists SQL, and the N+1
      shows as an `OrderLines` SELECT with a high `ExecuteCount`.
- [ ] **No noise:** no `/_blazor`, `/_Host`, or `GET /` rows are persisted (`ShouldProfile=false`).
- [ ] **Persistence:** the profile count survives an app restart.
- [ ] **Drill-down:** double-clicking a row opens the DetailView with a populated Queries grid (no
      error 1057, no empty nested grid).
- [ ] **Cleanup:** "Clear Profiles" → `SELECT COUNT(*) FROM MiniProfilers` = 0. Save > 200 profiles
      → count caps at 200.

---

## Gotchas reference

| Symptom | Cause | Fix |
| --- | --- | --- |
| Ambient profiles created but **0 SQL captured** | MiniProfiler's EF interceptor logs to `AsyncLocal` `MiniProfiler.Current`, which is null on the grid's async chain | Capture with a custom `DbCommandInterceptor` onto an **explicitly-held** profiler keyed by `DbContext` (Step 5) |
| `MiniProfiler.Current` null in a circuit event | Circuit events have no `HttpContext` | Don't use `Current`; hold the profiler explicitly via `OperationCaptureRegistry` |
| Load **hangs forever** on the loading overlay | `StopAsync().GetAwaiter().GetResult()` deadlocks on the circuit's `RendererSynchronizationContext` | `Task.Run(() => profiler.StopAsync(false)).GetAwaiter().GetResult()` |
| Last view's profile never appears | Collection source `Disposed` doesn't fire on navigation | Deferred flush: `End` the previous capture when the **next** load begins |
| Noise rows (`GET /`, `/_Host`, `/_blazor`) in storage | MiniProfiler HTTP auto-profiling is on | `options.ShouldProfile = _ => false` |
| Profile Summary grid empty, no error | Blazor default Queryable mode never raises `ObjectsGetting` | `DataAccessMode=Client` on the ListView node |
| Error **1037** / "without a public key property" | Used `DataAnnotations.Key` instead of XAF's key | `DevExpress.ExpressApp.Data.Key` |
| Grid empty even in Client mode | Subscribed in `ViewController.OnActivated` (too late) | Subscribe via `Application.ObjectSpaceCreated` in a `WindowController` |
| Error **1021** "belongs to another ObjectSpace" | Created rows with `new` | `npos.CreateObject<T>()` on the event's `sender` |
| Error **1057** "newly created record cannot be shown" on double-click | `CreateObject<T>()` marks the projection as a *new* object | `npos.RemoveFromModifiedObjects(obj)` after building it |
| Nested Queries grid empty on the DetailView | DetailView reuses the list row's object (Queries left empty), doesn't re-fetch | Populate `Queries` in a DetailView controller's `OnActivated` |
| `/profiler/results-index` returns 200 with flag off | No `/profiler` route → SPA catch-all serves the shell | Expected; not a 404 |
| `SqlServerStorage` "Invalid object name 'MiniProfilers'" | Tables/DB not created (XAF creates its DB lazily) | `ProfilerStorageInitializer.EnsureTables` at startup |

---

*Generated from the working POC in this repository. See [`SESSION_HANDOFF.md`](../SESSION_HANDOFF.md)
for the running state and the pre-port-back code-review items.*
