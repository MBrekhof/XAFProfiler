#nullable enable
using System.ComponentModel;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;

namespace XAFProfiler.Blazor.Server.BusinessObjects
{
    // Non-persistent child of ProfileSummary representing one captured EF Core SQL statement
    // (grouped by command text). Same non-persistent key rules as ProfileSummary.
    [DomainComponent]
    [DefaultProperty(nameof(Sql))]
    public class ProfileQuery
    {
        [Browsable(false)]
        [DevExpress.ExpressApp.Data.Key]
        public Guid Id { get; set; }
        public string? Sql { get; set; }
        public double DurationMs { get; set; }
        public int ExecuteCount { get; set; }   // number of times this SQL ran (N+1 signal)
    }
}
