#nullable enable
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;

namespace XAFProfiler.Blazor.Server.BusinessObjects
{
    // Non-persistent (in-memory) view object that surfaces stored MiniProfiler
    // profiles in a read-only XAF ListView. Rows are populated from the configured
    // MiniProfiler storage by ProfileSummaryController via NonPersistentObjectSpace's
    // ObjectsGetting / ObjectByKeyGetting events.
    //
    // IMPORTANT: this must NOT derive from a persistent base class (e.g. the EF Core
    // BaseObject). A persistent base makes XAF route the ListView to an
    // EFCoreObjectSpace instead of a NonPersistentObjectSpace, so ObjectsGetting never
    // fires and the grid is always empty. [DomainComponent] on a plain POCO with an
    // explicit [Key] is the correct non-persistent pattern.
    [DomainComponent]
    [DefaultClassOptions]
    [DefaultProperty(nameof(Name))]
    public class ProfileSummary
    {
        [Key]
        [Browsable(false)]
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public DateTime Started { get; set; }
        public double DurationMs { get; set; }
        public string? ResultsUrl { get; set; }
    }
}
