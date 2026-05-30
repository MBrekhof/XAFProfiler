#nullable enable
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace XAFProfiler.Module.BusinessObjects.Demo
{
    [DefaultClassOptions]
    public class Order : BaseObject
    {
        public virtual DateTime OrderDate { get; set; }

        public virtual Guid? CustomerId { get; set; }

        [ForeignKey(nameof(CustomerId))]
        public virtual Customer? Customer { get; set; }

        [Aggregated]
        public virtual IList<OrderLine> Lines { get; set; } = new ObservableCollection<OrderLine>();
    }
}
