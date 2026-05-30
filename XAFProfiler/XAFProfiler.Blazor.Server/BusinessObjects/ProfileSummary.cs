#nullable enable
using System.ComponentModel;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;

namespace XAFProfiler.Blazor.Server.BusinessObjects
{
    // Non-persistent (in-memory) view object that surfaces stored MiniProfiler
    // profiles in a read-only XAF ListView. Rows are populated from the configured
    // MiniProfiler storage by ProfileSummaryController via NonPersistentObjectSpace.
    //
    // [DomainComponent] marks it for XAF discovery; it is exported in
    // XAFProfilerBlazorModule.AdditionalExportedTypes.
    [DomainComponent]
    [DefaultClassOptions]
    [DefaultProperty(nameof(Name))]
    public class ProfileSummary
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public DateTime Started { get; set; }
        public double DurationMs { get; set; }
        public string? ResultsUrl { get; set; }
    }
}
