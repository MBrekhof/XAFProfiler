#nullable enable
using System.ComponentModel;
using DevExpress.ExpressApp;
using StackExchange.Profiling;
using StackExchange.Profiling.Storage;
using XAFProfiler.Blazor.Server.BusinessObjects;

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
        public ProfileSummaryController()
        {
            // Subscribe once, on the main window, for the whole application lifetime.
            TargetWindowType = WindowType.Main;
        }

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

        // Non-persistent objects must be created THROUGH the object space that requested them
        // (npos.CreateObject<T>()), not via `new`, so XAF tracks/binds them correctly and we
        // avoid error 1021 ("object belongs to another ObjectSpace").
        private static ProfileSummary ToSummary(NonPersistentObjectSpace npos, MiniProfiler profiler)
        {
            var s = npos.CreateObject<ProfileSummary>();
            s.Id = profiler.Id;
            s.Operation = profiler.Name;
            s.Started = profiler.Started;
            s.DurationMs = (double)profiler.DurationMilliseconds;
            return s;
        }

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
                    foreach (var id in storage.List(100))
                    {
                        var profiler = storage.Load(id);
                        if (profiler != null)
                        {
                            list.Add(ToSummary(npos, profiler));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ProfileSummaryController] Failed to load profiles: {ex.Message}");
                }
            }
            e.Objects = list;
        }

        private void ObjectSpace_ObjectByKeyGetting(object? sender, ObjectByKeyGettingEventArgs e)
        {
            if (e.ObjectType == typeof(ProfileSummary) && e.Key is Guid id)
            {
                var profiler = GetStorage()?.Load(id);
                if (profiler != null)
                {
                    e.Object = ToSummary((NonPersistentObjectSpace)sender!, profiler);
                }
            }
        }
    }
}
