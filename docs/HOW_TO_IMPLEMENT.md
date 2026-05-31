# How to integrate MiniProfiler into a DevExpress XAF Blazor Server app

This is a step-by-step recipe for adding [StackExchange MiniProfiler](https://miniprofiler.com/)
to an XAF Blazor Server application, including the two hard parts most integrations skip:
**profiling over the SignalR circuit** and **persisting profiles to SQL Server** so you can
browse them inside the app.

All code below is taken from the working POC in this repository. File paths are relative to the
Blazor.Server host project unless noted.

---

## Contents

- [Prerequisites](#prerequisites)
- [The three layers](#the-three-layers)
- [Step 1 — Add the NuGet packages](#step-1--add-the-nuget-packages)
- [Step 2 — Register MiniProfiler in Startup (Layer A)](#step-2--register-miniprofiler-in-startup-layer-a)
- [Step 3 — Wire the HTTP pipeline + bootstrap storage (Layer C, part 1)](#step-3--wire-the-http-pipeline--bootstrap-storage-layer-c-part-1)
- [Step 4 — Create the SQL storage tables at startup](#step-4--create-the-sql-storage-tables-at-startup)
- [Step 5 — Profile over the SignalR circuit (Layer B)](#step-5--profile-over-the-signalr-circuit-layer-b)
- [Step 6 — Browse profiles in-app (non-persistent ListView)](#step-6--browse-profiles-in-app-non-persistent-listview)
- [Step 7 — Inject the popup (optional)](#step-7--inject-the-popup-optional)
- [Verification checklist](#verification-checklist)
- [Gotchas reference](#gotchas-reference)

---

## Prerequisites

- An XAF **Blazor Server** app on the **EF Core** provider (this recipe uses SQL Server).
- Admin/authenticated security so the profiler UI can be gated.
- A way to produce a measurable operation (this POC ships a deliberate N+1 via a calculated
  `Customer.OrdersTotal` property over a seeded `Customer → Order → OrderLine` domain).

---

## The three layers

| Layer | What it profiles | Why it's separate |
| --- | --- | --- |
| **A — HTTP + EF Core** | Normal HTTP requests + the EF SQL they run | MiniProfiler's out-of-the-box mode; works only where there is an `HttpContext`. |
| **B — Circuit capture** | A user operation triggered over the Blazor SignalR circuit | Circuit events have **no `HttpContext`**, so `MiniProfiler.Current` is null — you must start a profiler manually. |
| **C — Storage** | Persisting + browsing captured profiles | The default storage is in-memory and dies with the app; you want SQL + an in-app view. |

---

## Step 1 — Add the NuGet packages

Add to the **Blazor.Server** project:

```xml
<PackageReference Include="MiniProfiler.AspNetCore.Mvc" Version="4.3.8" />
<PackageReference Include="MiniProfiler.EntityFrameworkCore" Version="4.3.8" />
<PackageReference Include="MiniProfiler.Providers.SqlServer" Version="4.3.8" />
```

- `AspNetCore.Mvc` — middleware, tag helper, options.
- `EntityFrameworkCore` — captures EF Core command timings as child timings.
- `Providers.SqlServer` — the `SqlServerStorage` provider.

---

## Step 2 — Register MiniProfiler in Startup (Layer A)

In `Startup.ConfigureServices`, register MiniProfiler **behind a config flag** and point its
storage at the **same connection string XAF uses**, so profiles persist:

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

        // Gate the built-in UI. In Development, allow any authenticated user;
        // otherwise require the Administrators role.
        options.ResultsAuthorize     = req => IsProfilerAuthorized(req.HttpContext);
        options.ResultsListAuthorize = req => IsProfilerAuthorized(req.HttpContext);

        // Layer C: persist to SQL Server instead of the default in-memory cache.
        var profilerConn = Configuration.GetConnectionString("ConnectionString");
        options.Storage = new SqlServerStorage(profilerConn);
    })
    .AddEntityFramework(); // capture EF Core SQL as child timings
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

> **Why a flag?** Profiling has overhead and exposes timing internals. Keep it off in production
> unless you explicitly switch it on.

---

## Step 3 — Wire the HTTP pipeline + bootstrap storage (Layer C, part 1)

In `Startup.Configure`, call `UseMiniProfiler()` **before** `UseRouting()`, still behind the
flag, and bootstrap the storage tables (next step):

```csharp
// Startup.cs — Configure
if (Configuration.GetValue<bool>("Profiling:Enabled"))
{
    app.UseMiniProfiler();
    ProfilerStorageInitializer.EnsureTables(
        Configuration.GetConnectionString("ConnectionString"));
}

app.UseRouting();
app.UseXaf();                       // XAF after routing/auth as usual
app.UseEndpoints(endpoints =>
{
    endpoints.MapXafEndpoints();
    endpoints.MapBlazorHub();
    endpoints.MapFallbackToPage("/_Host");
    endpoints.MapControllers();
});
```

> **Note on the negative test:** with the flag off there is no `/profiler` route, so a request to
> `/profiler/results-index` returns **200** (XAF's `MapFallbackToPage("/_Host")` SPA catch-all
> serves the shell), **not 404**. Don't mistake that for "profiler is on."

---

## Step 4 — Create the SQL storage tables at startup

`SqlServerStorage` does **not** create its own tables, and XAF creates its database *lazily* on
first access — so at host-startup the catalog may not exist yet. This helper connects to
`master`, creates an empty database if needed, then runs MiniProfiler's official table-creation
scripts. It is idempotent and never throws (persistence is best-effort):

```csharp
// Services/ProfilerStorageInitializer.cs
public static class ProfilerStorageInitializer
{
    public static void EnsureTables(string connectionString)
    {
        try
        {
            var targetBuilder = new SqlConnectionStringBuilder(connectionString);
            var catalog = targetBuilder.InitialCatalog;
            if (!string.IsNullOrWhiteSpace(catalog))
                EnsureDatabase(connectionString, catalog); // CREATE DATABASE via master if missing

            using var connection = new SqlConnection(connectionString);
            connection.Open();

            using (var check = connection.CreateCommand())
            {
                check.CommandText = "IF OBJECT_ID('MiniProfilers') IS NULL SELECT 0 ELSE SELECT 1";
                if ((int)check.ExecuteScalar() == 1) return; // already created
            }

            var storage = new SqlServerStorage(connectionString); // throwaway: used for scripts
            foreach (var script in storage.TableCreationScripts)
            {
                using var create = connection.CreateCommand();
                create.CommandText = script;
                create.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProfilerStorageInitializer] {ex.Message}"); // never fatal
        }
    }
    // EnsureDatabase(...) connects to `master` and runs:
    //   IF DB_ID(@cat) IS NULL CREATE DATABASE [<validated-catalog>]
    // The catalog name is validated against ^[A-Za-z_][A-Za-z0-9_]*$ before injection.
}
```

This creates three tables: `MiniProfilers`, `MiniProfilerTimings`, `MiniProfilerClientTimings`.

---

## Step 5 — Profile over the SignalR circuit (Layer B)

This is the core deliverable. A button click in Blazor Server runs in a **circuit event with no
`HttpContext`**, so `MiniProfiler.Current` is null and the HTTP middleware never sees it. The fix:
a **scoped (per-circuit) service** that owns a manually-started profiler.

### 5a. The scoped service

```csharp
// Services/CircuitProfilerService.cs
public sealed class CircuitProfilerService
{
    public MiniProfiler? Current { get; private set; }

    public MiniProfiler Start(string name) => Current = MiniProfiler.StartNew(name);

    public IDisposable? Step(string name) => Current?.Step(name);

    public async Task StopAndSaveAsync()
    {
        if (Current != null)
        {
            await Current.StopAsync(discardResults: false); // saves to configured storage
            Current = null;
        }
    }
}
```

Register it (unconditionally, so controllers can always resolve it; it's inert without options):

```csharp
// Startup.ConfigureServices
services.AddScoped<CircuitProfilerService>();
```

> `MiniProfiler.StartNew` uses the **global options** registered in `AddMiniProfiler` — including
> `Storage`. If profiling is off, `StartNew` still runs but there's nowhere to persist; nothing
> throws.

### 5b. The XAF action that drives it

A platform-specific `ViewController` (it lives in **Blazor.Server** because it references the
profiler types) adds a "Profile This View" action to the target list view:

```csharp
// Controllers/ProfileViewController.cs
public sealed class ProfileViewController : ViewController
{
    private readonly SimpleAction profileAction;

    public ProfileViewController()
    {
        TargetViewType   = ViewType.ListView;
        TargetObjectType = typeof(Customer);
        TargetViewNesting = Nesting.Root;

        profileAction = new SimpleAction(this, "ProfileThisView", PredefinedCategory.Tools)
        {
            Caption = "Profile This View",
            ImageName = "Action_Debug_Start"
        };
        profileAction.Execute += ProfileAction_Execute;
    }

    private void ProfileAction_Execute(object? sender, SimpleActionExecuteEventArgs e)
    {
        var svc = Application.ServiceProvider.GetService<CircuitProfilerService>();
        if (svc == null) { /* show warning, return */ return; }

        MiniProfiler mp = svc.Start("Profile: Customer ListView");
        using (svc.Step("Reload + aggregate"))
        {
            View.ObjectSpace.Refresh();          // re-issue EF queries the profiler captures
            View.RefreshDataSource();

            var customers = View.ObjectSpace.GetObjects<Customer>();
            decimal grand = 0m;
            using (svc.Step("Sum OrdersTotal (N+1)"))
                foreach (var c in customers)
                    grand += c.OrdersTotal;       // walking this forces per-row lazy load → N+1
        }

        // Execute is synchronous — block on the async stop (no async void; see notes).
        svc.StopAndSaveAsync().GetAwaiter().GetResult();

        Application.ShowViewStrategy.ShowMessage(
            $"Profiled. View results at /profiler/results?id={mp.Id}", InformationType.Success);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) profileAction.Execute -= ProfileAction_Execute;
        base.Dispose(disposing);
    }
}
```

**Result:** clicking the action persists a profile (e.g. *"Profile: Customer ListView"*) with
nested `.Step()` markers, and — because `AddEntityFramework()` is active — the EF SQL issued
during the steps is attached as **child timings** (the N+1 step holds thousands of SQL
statements). All of it lands in `SqlServerStorage` and survives restarts.

> **Anti-patterns avoided** (per the XAF ViewController patterns): no `async void` Execute
> handler (exceptions after an `await` would crash the circuit); the action's event is
> unsubscribed in `Dispose`; the View's **own** `ObjectSpace` is used (no leaked object space).

---

## Step 6 — Browse profiles in-app (non-persistent ListView)

You can read the persisted profiles straight out of `SqlServerStorage` and surface them in an XAF
list view, without the built-in `/profiler` UI. The view object is **non-persistent**.

### 6a. The non-persistent object

```csharp
// BusinessObjects/ProfileSummary.cs
[DomainComponent]
[DefaultClassOptions]
[DefaultProperty(nameof(Name))]
public class ProfileSummary
{
    [Browsable(false)]
    [DevExpress.ExpressApp.Data.Key]   // ← XAF non-persistent key, NOT DataAnnotations.Key
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public DateTime Started { get; set; }
    public double DurationMs { get; set; }
    public string? ResultsUrl { get; set; }
}
```

### 6b. The controller — subscribe via `ObjectSpaceCreated`, from a `WindowController`

```csharp
// Controllers/ProfileSummaryController.cs
public sealed class ProfileSummaryController : WindowController
{
    public ProfileSummaryController() => TargetWindowType = WindowType.Main;

    protected override void OnActivated()
    {
        base.OnActivated();
        Application.ObjectSpaceCreated += Application_ObjectSpaceCreated;
    }

    protected override void OnDeactivated()
    {
        Application.ObjectSpaceCreated -= Application_ObjectSpaceCreated;
        base.OnDeactivated();
    }

    private void Application_ObjectSpaceCreated(object? sender, ObjectSpaceCreatedEventArgs e)
    {
        if (e.ObjectSpace is not NonPersistentObjectSpace npos) return;

        npos.ObjectsGetting     += ObjectSpace_ObjectsGetting;
        npos.ObjectByKeyGetting += ObjectSpace_ObjectByKeyGetting;

        void OnDisposed(object? s, EventArgs args) // detach so we don't pin the object space
        {
            npos.ObjectsGetting     -= ObjectSpace_ObjectsGetting;
            npos.ObjectByKeyGetting -= ObjectSpace_ObjectByKeyGetting;
            npos.Disposed           -= OnDisposed;
        }
        npos.Disposed += OnDisposed;
    }

    private static IAsyncStorage? GetStorage() => MiniProfiler.DefaultOptions?.Storage;

    private void ObjectSpace_ObjectsGetting(object? sender, ObjectsGettingEventArgs e)
    {
        if (e.ObjectType != typeof(ProfileSummary)) return;
        var npos = (NonPersistentObjectSpace)sender!;
        var list = new BindingList<ProfileSummary> { AllowNew = false, AllowRemove = false };
        var storage = GetStorage();
        if (storage != null)
            foreach (var id in storage.List(100))
            {
                var p = storage.Load(id);
                if (p != null) list.Add(ToSummary(npos, p)); // ToSummary uses npos.CreateObject<T>()
            }
        e.Objects = list;
    }

    private void ObjectSpace_ObjectByKeyGetting(object? sender, ObjectByKeyGettingEventArgs e)
    {
        if (e.ObjectType == typeof(ProfileSummary) && e.Key is Guid id)
        {
            var p = GetStorage()?.Load(id);
            if (p != null) e.Object = ToSummary((NonPersistentObjectSpace)sender!, p);
        }
    }
}
```

> **Always create rows with `((NonPersistentObjectSpace)sender).CreateObject<T>()`**, never
> `new` — otherwise XAF throws error 1021 ("object belongs to another ObjectSpace").

### 6c. The model — set the ListView to Client mode

```xml
<!-- Model.xafml -->
<Application>
  <Views>
    <ListView Id="ProfileSummary_ListView" DataAccessMode="Client" />
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
| 3 | Subscribe via `XafApplication.ObjectSpaceCreated` from a **`WindowController`**, not a per-view `ViewController.OnActivated` | The collection source requests objects **during view creation**, before per-view controllers activate — so `OnActivated` subscribes too late and the one-time population fires into nothing | Columns render, grid stays empty, no error, `ObjectsGetting` "never fires" |

References (DevExpress docs):
- List View Data Access Modes — <https://docs.devexpress.com/eXpressAppFramework/113683>
- Client Mode — <https://docs.devexpress.com/eXpressAppFramework/118449>
- Non-Persistent Objects (Key Property) — <https://docs.devexpress.com/eXpressAppFramework/116516>

---

## Step 7 — Inject the popup (optional)

To render the MiniProfiler popup in the XAF Blazor host, add the tag helper to the host page.
In `Pages/_ViewImports.cshtml`:

```cshtml
@addTagHelper *, MiniProfiler.AspNetCore.Mvc
```

In `Pages/_Host.cshtml` (inside `<body>`):

```cshtml
<mini-profiler />
```

When the flag is on, the browser console shows a "MiniProfiler Init" entry and the popup renders
for HTTP requests.

---

## Verification checklist

Verify against the **SQL store (ground truth)**, not just the UI — Blazor grids and Playwright
clicks can silently no-op.

- [ ] `dotnet build XAFProfiler.slnx` → 0 warnings / 0 errors.
- [ ] App runs; you can log in.
- [ ] **Layer A:** with the flag on, "MiniProfiler Init" appears in the browser console.
- [ ] **Layer B:** clicking "Profile This View" inserts a row in `MiniProfilers`
      (`SELECT COUNT(*) FROM MiniProfilers`), and the N+1 step holds many child SQL timings.
- [ ] **Layer C:** the profile count survives an app restart.
- [ ] **Browse view:** the **Profile Summary** ListView shows rows; cross-check one row's GUID
      against `MiniProfilers`.
- [ ] **Negative:** flag off → no `mini-profiler` script on the page.

---

## Gotchas reference

| Symptom | Cause | Fix |
| --- | --- | --- |
| Profile Summary grid empty, no error | Blazor default Queryable mode never raises `ObjectsGetting` | `DataAccessMode=Client` on the ListView node |
| Error 1037 / "without a public key property" | Used `DataAnnotations.Key` instead of XAF's key | `DevExpress.ExpressApp.Data.Key` |
| Grid empty even in Client mode | Subscribed in `ViewController.OnActivated` (too late) | Subscribe via `Application.ObjectSpaceCreated` in a `WindowController` |
| Error 1021 "belongs to another ObjectSpace" | Created rows with `new` | `objectSpace.CreateObject<T>()` on the event's `sender` |
| `/profiler/results-index` returns 200 with flag off | No `/profiler` route → SPA catch-all serves the shell | Expected; not a 404 |
| Built-in `/profiler/results?id=` shows "hidden" | `ResultsAuthorize` doesn't see the XAF auth cookie on a raw fetch | Use the in-app Profile Summary view, or adjust the authorize delegate |
| `MiniProfiler.Current` null in a button click | Circuit events have no `HttpContext` | Use the scoped `CircuitProfilerService` + manual `StartNew/Step/StopAsync` |
| `SqlServerStorage` "Invalid object name 'MiniProfilers'" | Tables/DB not created (XAF creates its DB lazily) | `ProfilerStorageInitializer.EnsureTables` at startup |

---

*Generated from the working POC in this repository. See [`SESSION_HANDOFF.md`](../SESSION_HANDOFF.md)
for the running state and the pre-port-back code-review items.*
