#nullable enable
using DevExpress.Persistent.BaseImpl.EF;
using System.ComponentModel.DataAnnotations.Schema;

namespace XAFProfiler.Module.BusinessObjects.Demo
{
    public class OrderLine : BaseObject
    {
        public virtual string Product { get; set; } = string.Empty;
        public virtual int Quantity { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public virtual decimal UnitPrice { get; set; }

        public virtual Guid? OrderId { get; set; }

        [ForeignKey(nameof(OrderId))]
        public virtual Order? Order { get; set; }
    }
}
