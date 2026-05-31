#nullable enable
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XAFProfiler.Blazor.Server.BusinessObjects;
using XAFProfiler.Blazor.Server.Services;

namespace XAFProfiler.Blazor.Server.Controllers
{
    /// <summary>
    /// Automatically profiles every ListView data-load by bracketing the collection-source load
    /// with an <see cref="OperationCaptureRegistry"/> operation keyed by the view's EF Core
    /// <see cref="DbContext"/>.
    ///
    /// <para>
    /// <b>How SQL is captured (interceptor approach)</b><br/>
    /// MiniProfiler's own EF interceptor logs to the <c>AsyncLocal</c> <c>MiniProfiler.Current</c>,
    /// which is <c>null</c> on the DevExpress grid's materialisation chain — so it captured zero
    /// SQL. Instead, this controller calls <see cref="OperationCaptureRegistry.Begin"/> with the
    /// view's DbContext when the load starts; the singleton <see cref="QueryCaptureInterceptor"/>
    /// (registered on the DbContext) then appends every executed command onto the explicitly-held
    /// profiler for that DbContext instance. The profiler is stopped+saved via
    /// <see cref="OperationCaptureRegistry.End"/>. The controller never touches
    /// <c>MiniProfiler.Current</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Load boundaries (verified at runtime)</b><br/>
    /// In XAF Blazor Server (EF Core, Client mode) the observed sequence for a ListView load is:
    /// <list type="number">
    ///   <item><c>ListViewCreating</c> → we subscribe to the collection source.</item>
    ///   <item><c>CollectionChanging</c> → <see cref="OperationCaptureRegistry.Begin"/> (operation opens).</item>
    ///   <item><c>CollectionChanged</c> → the <c>IQueryable</c> exists; the grid then materialises it.</item>
    ///   <item>
    ///     The grid SELECT and XAF's prefetch N+1 follow-up SELECTs execute <b>synchronously on the
    ///     circuit thread, immediately after <c>CollectionChanged</c></b> and entirely before the
    ///     next top-level event — the interceptor attaches them all to the open profiler.
    ///   </item>
    /// </list>
    /// Crucially, the collection source's <c>Disposed</c> event does <b>not</b> fire when the user
    /// navigates between ListViews (XAF keeps the previous view alive), so it is unusable as the
    /// load-END boundary. Because each load's SQL is fully captured before any subsequent event, we
    /// instead <b>flush (End) the previously-open capture when the next load Begins</b> (the
    /// "deferred flush" below), and also flush on <c>CollectionReloaded</c> (Refresh) and
    /// <c>Disposed</c> (when it does fire). The very last view's profiler is flushed by the next
    /// navigation (e.g. opening Profile Summary to inspect results).
    /// </para>
    ///
    /// Gate: reads <c>Profiling:Enabled</c>; inert if absent/false. Skips <see cref="ProfileSummary"/>
    /// / <see cref="ProfileQuery"/> views (the profiler's own surface).
    /// </summary>
    public sealed class AmbientProfilingController : WindowController
    {
        private ILogger<AmbientProfilingController>? _logger;
        private OperationCaptureRegistry? _registry;

        // The single still-open capture (its DbContext), flushed when the next load begins.
        // Access is serialized on the Blazor circuit; a lock in the registry guards table mutation.
        private DbContext? _openContext;

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
            _registry = Application.ServiceProvider?.GetService<OperationCaptureRegistry>();
            if (_registry == null)
            {
                _logger?.LogWarning(
                    "AmbientProfilingController: OperationCaptureRegistry not resolvable; capture disabled.");
                return;
            }

            // ListViewCreating (before construction), not ListViewCreated: CollectionChanging/Changed
            // fire DURING ListView construction, so we must subscribe to the collection source first.
            Application.ListViewCreating += Application_ListViewCreating;
        }

        protected override void OnDeactivated()
        {
            Application.ListViewCreating -= Application_ListViewCreating;
            // Flush any still-open capture so the last view's profile is not lost on shutdown.
            FlushOpen();
            _registry = null;
            _logger = null;
            base.OnDeactivated();
        }

        private static readonly HashSet<Type> _skippedTypes = new()
        {
            typeof(ProfileSummary),
            typeof(ProfileQuery),
        };

        /// <summary>Stops+saves the previously-open capture, if any. Best-effort.</summary>
        private void FlushOpen()
        {
            var ctx = _openContext;
            if (ctx == null) return;
            _openContext = null;
            _registry?.End(ctx);
        }

        private void Application_ListViewCreating(object? sender, ListViewCreatingEventArgs e)
        {
            var collectionSource = e.CollectionSource;
            if (collectionSource == null) return;

            var objectType = collectionSource.ObjectTypeInfo?.Type;
            if (objectType == null || _skippedTypes.Contains(objectType)) return;

            var registry = _registry;
            if (registry == null) return;

            // Human-readable profile name. Prefer the model class caption; fall back to type name.
            var caption = objectType.Name;
            try
            {
                if (Application?.Model?.BOModel?.GetClass(objectType)?.Caption is { Length: > 0 } modelCaption)
                {
                    caption = modelCaption;
                }
            }
            catch { /* caption is best-effort; type name is a fine fallback */ }
            var profileName = $"{caption} · ListView load";

            // Resolve the EF Core DbContext for this load. This app has NO Security System, so the
            // collection source's ObjectSpace is always a plain EFCoreObjectSpace (cast succeeds).
            DbContext? ResolveDbContext()
            {
                try
                {
                    if (collectionSource.ObjectSpace is EFCoreObjectSpace efos)
                    {
                        return efos.DbContext;
                    }
                    _logger?.LogDebug(
                        "AmbientProfilingController: ObjectSpace for '{Name}' is not EFCoreObjectSpace ({Type}).",
                        profileName, collectionSource.ObjectSpace?.GetType().Name);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "AmbientProfilingController: failed to resolve DbContext for '{Name}'.", profileName);
                }
                return null;
            }

            void Begin()
            {
                // Deferred flush: the previous load's SQL is fully captured by now (it ran
                // synchronously before this event), so stop+save it before opening the new one.
                FlushOpen();

                var ctx = ResolveDbContext();
                if (ctx != null)
                {
                    registry.Begin(ctx, profileName);
                    _openContext = ctx;
                }
            }

            void End()
            {
                var ctx = ResolveDbContext();
                if (ctx != null)
                {
                    if (ReferenceEquals(_openContext, ctx))
                    {
                        _openContext = null;
                    }
                    registry.End(ctx);
                }
            }

            // Initial load: CollectionChanging opens the operation. The grid SELECT + prefetch N+1
            // run synchronously after CollectionChanged; they are flushed by the next Begin (or by
            // CollectionReloaded / Disposed / controller deactivation).
            void OnCollectionChanging(object? s, EventArgs a) => Begin();

            // Refresh path: CollectionReloading/Reloaded bracket the round-trip; the round-trip's
            // SQL runs between them, so End() on Reloaded saves a fully-populated profiler.
            void OnCollectionReloading(object? s, EventArgs a) => Begin();
            void OnCollectionReloaded(object? s, EventArgs a) => End();

            void OnDisposed(object? s, EventArgs a)
            {
                collectionSource.CollectionChanging -= OnCollectionChanging;
                collectionSource.CollectionReloading -= OnCollectionReloading;
                collectionSource.CollectionReloaded -= OnCollectionReloaded;
                collectionSource.Disposed -= OnDisposed;

                End();   // Flush if this source's capture is still open (rarely fires on navigation).
            }

            collectionSource.CollectionChanging += OnCollectionChanging;
            collectionSource.CollectionReloading += OnCollectionReloading;
            collectionSource.CollectionReloaded += OnCollectionReloaded;
            collectionSource.Disposed += OnDisposed;
        }
    }
}
