#nullable enable
using DevExpress.ExpressApp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Profiling;
using XAFProfiler.Blazor.Server.BusinessObjects;

namespace XAFProfiler.Blazor.Server.Controllers
{
    /// <summary>
    /// Automatically profiles every ListView data-load by wrapping the collection-source
    /// lifetime with a <see cref="MiniProfiler"/>.
    ///
    /// <para>
    /// <b>Blazor Server EF Core data-load architecture</b><br/>
    /// In XAF Blazor Server (EF Core, Client mode) the initial data load is split across two
    /// phases:
    /// <list type="number">
    ///   <item>
    ///     <c>CollectionChanging</c> / <c>CollectionChanged</c> fire when the
    ///     <see cref="CollectionSourceBase"/> calls <c>ResetCollection</c> and creates an
    ///     <c>IQueryable&lt;T&gt;</c>. No SQL has run yet.
    ///   </item>
    ///   <item>
    ///     The DxGrid materialises the <c>IQueryable</c> asynchronously after
    ///     <c>CollectionChanged</c> — this is when EF actually executes the SQL SELECT.
    ///   </item>
    /// </list>
    /// Stopping the profiler on <c>CollectionChanged</c> therefore closes the window before
    /// the SQL runs and the MiniProfiler EF interceptor cannot attach any timings.
    /// </para>
    ///
    /// <para>
    /// <b>Strategy</b><br/>
    /// Start a profiler on <c>CollectionChanging</c> (first load) or
    /// <c>CollectionReloading</c> (Refresh button). Keep it alive — <c>MiniProfiler.Current</c>
    /// flows via <c>AsyncLocal</c> to the grid's async EF call — and stop+save it when the
    /// collection source is <em>disposed</em> (view navigated away). This captures all EF SQL
    /// emitted during the view's lifetime while the profiler is open.
    ///
    /// For the Refresh path (which calls <c>Reload()</c>) we start a fresh profiler on
    /// <c>CollectionReloading</c> and stop it on <c>CollectionReloaded</c>: those two events
    /// bracket the actual database round-trip synchronously.
    /// </para>
    ///
    /// <para>
    /// <b>Why <c>ListViewCreating</c>, not <c>ListViewCreated</c>?</b><br/>
    /// <c>ListViewCreated</c> fires AFTER the ListView is built and
    /// <c>CollectionChanging/Changed</c> have already fired. We need to subscribe to the
    /// collection source BEFORE it creates its first collection, so we use
    /// <c>ListViewCreating</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Thread-safety / deadlock note</b><br/>
    /// <see cref="MiniProfiler.StopAsync"/> is called via
    /// <c>Task.Run(...).GetAwaiter().GetResult()</c>. Calling it directly on the Blazor circuit's
    /// <c>RendererSynchronizationContext</c> deadlocks.
    /// </para>
    ///
    /// Gate: reads <c>Profiling:Enabled</c> from <see cref="IConfiguration"/>; if absent or
    /// false the controller stays completely inert.
    ///
    /// Meta-noise guard: <see cref="ProfileSummary"/> and <see cref="ProfileQuery"/> views
    /// are skipped.
    /// </summary>
    public sealed class AmbientProfilingController : WindowController
    {
        private ILogger<AmbientProfilingController>? _logger;

        public AmbientProfilingController()
        {
            TargetWindowType = WindowType.Main;
        }

        protected override void OnActivated()
        {
            base.OnActivated();

            var config = Application.ServiceProvider?.GetService<IConfiguration>();
            if (config == null || !config.GetValue<bool>("Profiling:Enabled"))
            {
                return;
            }

            _logger = Application.ServiceProvider?.GetService<ILogger<AmbientProfilingController>>();

            // IMPORTANT: ListViewCreating (before construction), not ListViewCreated.
            // CollectionChanging/Changed fire DURING ListView construction — subscribing here
            // lets us attach before the first collection-recreation event fires.
            Application.ListViewCreating += Application_ListViewCreating;
        }

        protected override void OnDeactivated()
        {
            Application.ListViewCreating -= Application_ListViewCreating;
            _logger = null;
            base.OnDeactivated();
        }

        private static readonly HashSet<Type> _skippedTypes = new()
        {
            typeof(ProfileSummary),
            typeof(ProfileQuery),
        };

        private void Application_ListViewCreating(object? sender, ListViewCreatingEventArgs e)
        {
            var collectionSource = e.CollectionSource;
            if (collectionSource == null) return;

            var objectType = collectionSource.ObjectTypeInfo?.Type;
            if (objectType == null || _skippedTypes.Contains(objectType)) return;

            // Human-readable profile name.
            var profileName = $"{objectType.Name} · ListView load";

            // Per-view mutable slot: simple array wrapper allows mutation inside closures.
            MiniProfiler?[] profilerSlot = { null };

            // ─── Helpers ───────────────────────────────────────────────────────────────

            void StartNew()
            {
                try
                {
                    // If somehow already running (shouldn't happen), discard it first.
                    var prev = profilerSlot[0];
                    if (prev != null)
                    {
                        profilerSlot[0] = null;
                        Task.Run(() => prev.StopAsync(discardResults: true)).GetAwaiter().GetResult();
                    }

                    var p = MiniProfiler.StartNew(profileName);
                    profilerSlot[0] = p;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "AmbientProfilingController: failed to start profiler for '{Name}'.", profileName);
                }
            }

            void SaveAndClear()
            {
                try
                {
                    var p = profilerSlot[0];
                    if (p == null) return;
                    profilerSlot[0] = null;

                    // MANDATORY Task.Run offload — avoids deadlock on Blazor
                    // RendererSynchronizationContext (StopAsync re-enters the sync context).
                    Task.Run(() => p.StopAsync(discardResults: false)).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "AmbientProfilingController: failed to stop profiler for '{Name}'.", profileName);
                }
            }

            void DiscardAndClear()
            {
                try
                {
                    var p = profilerSlot[0];
                    if (p == null) return;
                    profilerSlot[0] = null;
                    Task.Run(() => p.StopAsync(discardResults: true)).GetAwaiter().GetResult();
                }
                catch { /* best-effort on dispose */ }
            }

            // ─── Phase A: Initial load via CollectionChanging / CollectionChanged ──────
            //
            // In Blazor Server (EF Core, Client mode) the sequence is:
            //   1. CollectionChanging  → IQueryable<T> is about to be created.
            //   2. CollectionChanged   → IQueryable<T> created; SQL not yet run.
            //   3. DxGrid async enumerates IQueryable → EF SQL executes.
            //   4. collectionSource.Disposed → view navigated away.
            //
            // We start on CollectionChanging and keep the profiler OPEN until Disposed so
            // the EF interceptor (AddEntityFramework) can attach step-3 SQL timings to
            // MiniProfiler.Current while the profiler is alive.
            //
            // We do NOT stop on CollectionChanged — stopping there closes the window before
            // the SQL runs.

            void OnCollectionChanging(object? s, EventArgs a)
            {
                StartNew();
            }

            // CollectionChanged: IQueryable created, profiler already running — do nothing.
            // (No handler needed; the profiler stays open.)

            // ─── Phase B: Refresh-button path via Reload() ────────────────────────────
            //
            // If the user clicks Refresh, XAF calls CollectionSource.Reload(), which raises
            // CollectionReloading before the query and CollectionReloaded after. These bracket
            // the EF round-trip synchronously, so we can start/stop cleanly here.

            void OnCollectionReloading(object? s, EventArgs a)
            {
                StartNew();
            }

            void OnCollectionReloaded(object? s, EventArgs a)
            {
                SaveAndClear();
            }

            // ─── Cleanup: view navigated away ─────────────────────────────────────────
            //
            // Save (not discard) the Phase-A profiler here, because by the time the source
            // is disposed the EF queries have already run and been attached to the profiler.

            void OnDisposed(object? s, EventArgs a)
            {
                collectionSource.CollectionChanging -= OnCollectionChanging;
                collectionSource.CollectionReloading -= OnCollectionReloading;
                collectionSource.CollectionReloaded -= OnCollectionReloaded;
                collectionSource.Disposed -= OnDisposed;

                SaveAndClear();   // Save Phase-A profiler (EF SQL already attached by now).
            }

            collectionSource.CollectionChanging += OnCollectionChanging;
            collectionSource.CollectionReloading += OnCollectionReloading;
            collectionSource.CollectionReloaded += OnCollectionReloaded;
            collectionSource.Disposed += OnDisposed;
        }
    }
}
