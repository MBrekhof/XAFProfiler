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
    /// [DomainComponent] POCO (it must NOT derive from a persistent base, or XAF builds a
    /// persistent collection source that queries EF and never raises ObjectsGetting — see
    /// https://docs.devexpress.com/eXpressAppFramework/113711). XAF therefore serves it via
    /// a NonPersistentObjectSpace and raises ObjectsGetting / ObjectByKeyGetting, which we
    /// handle here. Subscribe in OnActivated, unsubscribe in OnDeactivated (error 1021 safety).
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

        // Non-persistent objects must be created THROUGH the object space
        // (npos.CreateObject<T>()), not via `new`, so XAF tracks/binds them correctly.
        private static ProfileSummary ToSummary(NonPersistentObjectSpace npos, MiniProfiler profiler)
        {
            var s = npos.CreateObject<ProfileSummary>();
            s.Id = profiler.Id;
            s.Name = profiler.Name;
            s.Started = profiler.Started;
            s.DurationMs = (double)profiler.DurationMilliseconds;
            s.ResultsUrl = $"/profiler/results?id={profiler.Id}";
            return s;
        }

        private void ObjectSpace_ObjectsGetting(object? sender, ObjectsGettingEventArgs e)
        {
            var npos = (NonPersistentObjectSpace)ObjectSpace;
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
                    e.Object = ToSummary((NonPersistentObjectSpace)ObjectSpace, profiler);
                }
            }
        }
    }
}
