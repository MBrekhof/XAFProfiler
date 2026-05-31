#nullable enable
using System.Collections.Generic;
using System.ComponentModel;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;

namespace XAFProfiler.Blazor.Server.BusinessObjects
{
    // Non-persistent (in-memory) view object that surfaces captured EF Core profiling
    // data in a read-only XAF ListView. Rows are populated by ProfileSummaryController
    // via NonPersistentObjectSpace's ObjectsGetting / ObjectByKeyGetting events.
    //
    // IMPORTANT: this must NOT derive from a persistent base class (e.g. the EF Core
    // BaseObject). A persistent base makes XAF route the ListView to an
    // EFCoreObjectSpace instead of a NonPersistentObjectSpace, so ObjectsGetting never
    // fires and the grid is always empty. [DomainComponent] on a plain POCO with an
    // explicit key is the correct non-persistent pattern.
    //
    // The key MUST use DevExpress.ExpressApp.Data.KeyAttribute (NOT
    // System.ComponentModel.DataAnnotations.Key, which is the EF Core key and is ignored
    // by XAF's non-persistent key detection). A key is required for Blazor List Views and
    // for ObjectByKeyGetting. The ListView must also run in Client data access mode (set in
    // Model.xafml) — Queryable mode (the Blazor default) never raises ObjectsGetting.
    // See https://docs.devexpress.com/eXpressAppFramework/116516 (Key Property section).
    [DomainComponent]
    [DefaultClassOptions]
    [DefaultProperty(nameof(Operation))]
    public class ProfileSummary
    {
        [Browsable(false)]
        [DevExpress.ExpressApp.Data.Key]
        public Guid Id { get; set; }
        public string? Operation { get; set; }        // the profiler Name, e.g. "Customer · ListView load"
        public DateTime Started { get; set; }
        public double DurationMs { get; set; }
        public int QueryCount { get; set; }
        public double SlowestQueryMs { get; set; }

        [VisibleInListView(false)]
        public IList<ProfileQuery> Queries { get; set; } = new List<ProfileQuery>();
    }
}
