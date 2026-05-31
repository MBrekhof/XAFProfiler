#nullable enable
using System.ComponentModel;
using DevExpress.ExpressApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Profiling;
using StackExchange.Profiling.Storage;
using XAFProfiler.Blazor.Server.BusinessObjects;
using XAFProfiler.Blazor.Server.Services;

namespace XAFProfiler.Blazor.Server.Controllers
{
    /// <summary>
    /// Populates the read-only <see cref="ProfileSummary"/> ListView from the configured
    /// MiniProfiler storage. <see cref="ProfileSummary"/> is a non-persistent
    /// [DomainComponent] POCO, so XAF serves it via a <see cref="NonPersistentObjectSpace"/>
    /// and raises ObjectsGetting / ObjectByKeyGetting to request its data.
    ///
    /// IMPORTANT — subscription timing. The ListView's collection source requests objects
    /// (raising ObjectsGetting) while the View is being created, BEFORE per-view
    /// ViewControllers are activated. Subscribing in a ViewController's OnActivated is
    /// therefore too late: the one-time population fires into a handler that is not attached
    /// yet, and the grid stays empty (ObjectsGetting "never fires" from the controller's
    /// point of view). The fix is to hook <see cref="XafApplication.ObjectSpaceCreated"/>
    /// from a WindowController so the handlers are attached the moment the object space is
    /// created — before any GetObjects call. (This is the documented pattern; see the
    /// xaf-blazor-startup skill and https://docs.devexpress.com/eXpressAppFramework/116516.)
    ///
    /// This works in concert with two other requirements for non-persistent Blazor ListViews:
    ///   • Model.xafml sets ProfileSummary_ListView DataAccessMode=Client — Queryable mode
    ///     (the Blazor default) never raises ObjectsGetting at all.
    ///   • ProfileSummary declares a DevExpress.ExpressApp.Data.Key (not the EF Core
    ///     DataAnnotations key), required for Blazor ListViews and ObjectByKeyGetting.
    /// </summary>
    public sealed class ProfileSummaryController : WindowController
    {
        private ILogger<ProfileSummaryController>? _logger;

        public ProfileSummaryController()
        {
            // Subscribe once, on the main window, for the whole application lifetime.
            TargetWindowType = WindowType.Main;
        }

        protected override void OnActivated()
        {
            base.OnActivated();
            _logger = Application.ServiceProvider?.GetService<ILogger<ProfileSummaryController>>();
            Application.ObjectSpaceCreated += Application_ObjectSpaceCreated;
        }

        protected override void OnDeactivated()
        {
            Application.ObjectSpaceCreated -= Application_ObjectSpaceCreated;
            _logger = null;
            base.OnDeactivated();
        }

        private void Application_ObjectSpaceCreated(object? sender, ObjectSpaceCreatedEventArgs e)
        {
            if (e.ObjectSpace is not NonPersistentObjectSpace npos)
            {
                return;
            }

            npos.ObjectsGetting += ObjectSpace_ObjectsGetting;
            npos.ObjectByKeyGetting += ObjectSpace_ObjectByKeyGetting;

            // Detach when this object space is disposed so we don't pin it (or leak handlers)
            // for the life of the main-window controller.
            void OnDisposed(object? s, EventArgs args)
            {
                npos.ObjectsGetting -= ObjectSpace_ObjectsGetting;
                npos.ObjectByKeyGetting -= ObjectSpace_ObjectByKeyGetting;
                npos.Disposed -= OnDisposed;
            }
            npos.Disposed += OnDisposed;
        }

        // The storage configured in Startup's AddMiniProfiler (SqlServerStorage).
        private static IAsyncStorage? GetStorage() => MiniProfiler.DefaultOptions?.Storage;

        private void ObjectSpace_ObjectsGetting(object? sender, ObjectsGettingEventArgs e)
        {
            // Fired for any non-persistent type — only handle ours.
            if (e.ObjectType != typeof(ProfileSummary))
            {
                return;
            }

            var npos = (NonPersistentObjectSpace)sender!;
            var list = new BindingList<ProfileSummary> { AllowNew = false, AllowRemove = false };
            var storage = GetStorage();
            if (storage != null)
            {
                try
                {
                    // List newest-first (Descending is the IAsyncStorage default; stated
                    // explicitly so the grid order does not silently change if it ever shifts).
                    foreach (var id in storage.List(200, orderBy: ListResultsOrder.Descending))
                    {
                        var profiler = storage.Load(id);
                        if (profiler != null)
                        {
                            // List rows: summary projection only (no Queries collection).
                            list.Add(ProfileProjection.BuildSummary(npos, profiler));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to load MiniProfiler results for the ProfileSummary list view.");
                }
            }
            e.Objects = list;
        }

        private void ObjectSpace_ObjectByKeyGetting(object? sender, ObjectByKeyGettingEventArgs e)
        {
            if (e.ObjectType == typeof(ProfileSummary) && e.Key is Guid id)
            {
                try
                {
                    var profiler = GetStorage()?.Load(id);
                    if (profiler != null)
                    {
                        // Detail row: full projection so the opened DetailView shows its Queries.
                        e.Object = ProfileProjection.BuildDetail((NonPersistentObjectSpace)sender!, profiler);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to load MiniProfiler result {ProfilerId} for the ProfileSummary detail view.", id);
                }
            }
        }
    }
}
