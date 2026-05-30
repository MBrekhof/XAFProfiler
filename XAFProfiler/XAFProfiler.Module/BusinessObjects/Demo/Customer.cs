using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace XAFProfiler.Module.BusinessObjects.Demo
{
    [DefaultClassOptions]
    [DefaultProperty(nameof(Name))]
    public class Customer : BaseObject
    {
        public virtual string Name { get; set; } = string.Empty;
        public virtual string City { get; set; } = string.Empty;

        [Aggregated]
        public virtual IList<Order> Orders { get; set; } = new ObservableCollection<Order>();

        // Non-persistent calculated total. Deliberately walks Orders -> Lines so that
        // displaying this on a ListView triggers per-row lazy loading (N+1 pattern)
        // for the profiler to capture. Not [Aggregated], not persisted.
        [NotMapped]
        public decimal OrdersTotal =>
            Orders?.Sum(o => o.Lines?.Sum(l => l.Quantity * l.UnitPrice) ?? 0m) ?? 0m;
    }
}
