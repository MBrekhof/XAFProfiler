#nullable enable
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
    /// Populates the <see cref="ProfileSummary.Queries"/> collection on the object shown in the
    /// ProfileSummary DetailView.
    ///
    /// WHY THIS IS NEEDED. When a DetailView is opened by double-clicking a ListView row, XAF does
    /// NOT re-fetch the object via NonPersistentObjectSpace.ObjectByKeyGetting; it reuses the
    /// object that already exists in the ListView's Object Space. That object was created by
    /// <see cref="ProfileProjection.BuildSummary"/>, which intentionally leaves Queries empty for
    /// list display. As a result the nested Queries grid is empty even though QueryCount is set.
    /// (Documented behavior: https://docs.devexpress.com/eXpressAppFramework/401747 — "when you
    /// open a Detail View from a List View ... the Obj argument is set to the object from the
    /// List View's Object Space".)
    ///
    /// This controller closes that gap: on DetailView activation it lazily fills Queries from the
    /// MiniProfiler storage (keyed by the row's Id) when the collection is empty. It also covers
    /// the by-key navigation path harmlessly (there Queries is already populated by BuildDetail, so
    /// the guard short-circuits).
    /// </summary>
    public sealed class ProfileSummaryDetailController : ObjectViewController<DetailView, ProfileSummary>
    {
        private ILogger<ProfileSummaryDetailController>? _logger;

        protected override void OnActivated()
        {
            base.OnActivated();
            _logger = Application.ServiceProvider?.GetService<ILogger<ProfileSummaryDetailController>>();
            PopulateQueries();
        }

        protected override void OnDeactivated()
        {
            _logger = null;
            base.OnDeactivated();
        }

        private void PopulateQueries()
        {
            if (View?.CurrentObject is not ProfileSummary summary)
            {
                return;
            }

            // Already populated (e.g. via ObjectByKeyGetting -> BuildDetail) — nothing to do.
            if (summary.Queries is { Count: > 0 })
            {
                return;
            }

            if (ObjectSpace is not NonPersistentObjectSpace npos)
            {
                return;
            }

            var storage = MiniProfiler.DefaultOptions?.Storage;
            if (storage == null)
            {
                return;
            }

            try
            {
                var profiler = storage.Load(summary.Id);
                if (profiler != null)
                {
                    summary.Queries = ProfileProjection.BuildQueries(npos, profiler);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to populate Queries for ProfileSummary {ProfilerId} on the detail view.", summary.Id);
            }
        }
    }
}
