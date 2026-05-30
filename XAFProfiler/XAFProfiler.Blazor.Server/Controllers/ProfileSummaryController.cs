#nullable enable
using System.ComponentModel;
using DevExpress.ExpressApp;
using StackExchange.Profiling;
using StackExchange.Profiling.Storage;
using XAFProfiler.Blazor.Server.BusinessObjects;

namespace XAFProfiler.Blazor.Server.Controllers
{
    /// <summary>
    /// Populates the read-only <see cref="ProfileSummary"/> ListView from the
    /// configured MiniProfiler storage. ProfileSummary is a non-persistent
    /// [DomainComponent], so XAF raises ObjectsGetting / ObjectByKeyGetting on the
    /// NonPersistentObjectSpace and we supply the rows here. Subscribing in
    /// OnActivated and unsubscribing in OnDeactivated avoids the duplicated/leaked
    /// handlers that cause XAF error 1021.
    /// </summary>
    public sealed class ProfileSummaryController : ViewController
    {
        public ProfileSummaryController()
        {
            TargetObjectType = typeof(ProfileSummary);
        }

        protected override void OnActivated()
        {
            base.OnActivated();
            if (ObjectSpace is NonPersistentObjectSpace npos)
            {
                npos.ObjectsGetting += ObjectSpace_ObjectsGetting;
                npos.ObjectByKeyGetting += ObjectSpace_ObjectByKeyGetting;
            }
        }

        protected override void OnDeactivated()
        {
            if (ObjectSpace is NonPersistentObjectSpace npos)
            {
                npos.ObjectsGetting -= ObjectSpace_ObjectsGetting;
                npos.ObjectByKeyGetting -= ObjectSpace_ObjectByKeyGetting;
            }
            base.OnDeactivated();
        }

        // The storage configured in Startup's AddMiniProfiler (SqlServerStorage).
        private static IAsyncStorage? GetStorage() => MiniProfiler.DefaultOptions?.Storage;

        private static ProfileSummary ToSummary(MiniProfiler profiler) => new ProfileSummary
        {
            Id = profiler.Id,
            Name = profiler.Name,
            Started = profiler.Started,
            DurationMs = (double)profiler.DurationMilliseconds,
            ResultsUrl = $"/profiler/results?id={profiler.Id}"
        };

        private void ObjectSpace_ObjectsGetting(object? sender, ObjectsGettingEventArgs e)
        {
            var list = new BindingList<ProfileSummary>();
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
                            list.Add(ToSummary(profiler));
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
                    e.Object = ToSummary(profiler);
                }
            }
        }
    }
}
