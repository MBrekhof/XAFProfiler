using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EF;
using DevExpress.ExpressApp.Updating;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using Microsoft.Extensions.DependencyInjection;
using XAFProfiler.Module.BusinessObjects.Demo;

namespace XAFProfiler.Module.DatabaseUpdate
{
    // For more typical usage scenarios, be sure to check out https://docs.devexpress.com/eXpressAppFramework/DevExpress.ExpressApp.Updating.ModuleUpdater
    public class Updater : ModuleUpdater
    {
        public Updater(IObjectSpace objectSpace, Version currentDBVersion) :
            base(objectSpace, currentDBVersion)
        {
        }
        public override void UpdateDatabaseAfterUpdateSchema()
        {
            base.UpdateDatabaseAfterUpdateSchema();

            SeedDemoData();
        }

        private void SeedDemoData()
        {
            // Guard: don't duplicate on subsequent updater runs.
            if (ObjectSpace.GetObjectsCount(typeof(Customer), null) > 0)
            {
                return;
            }

            // Fixed seed -> reproducible data despite <Deterministic>false</Deterministic>.
            var rnd = new Random(12345);

            string[] cities =
            {
                "Amsterdam", "Rotterdam", "Utrecht", "Eindhoven", "The Hague",
                "Groningen", "Tilburg", "Almere", "Breda", "Nijmegen"
            };
            string[] firstNames =
            {
                "Anna", "Bram", "Cees", "Daan", "Eva", "Femke", "Gijs", "Hanna",
                "Iris", "Joost", "Karin", "Lars", "Maud", "Niels", "Otto", "Petra"
            };
            string[] lastNames =
            {
                "Jansen", "de Vries", "Bakker", "Visser", "Smit", "Meijer",
                "Mulder", "de Boer", "Bos", "Vos", "Peters", "Hendriks"
            };
            string[] products =
            {
                "Widget", "Gadget", "Sprocket", "Cog", "Bolt", "Bracket",
                "Panel", "Module", "Cable", "Connector"
            };

            const int customerCount = 200;
            var baseDate = new DateTime(2023, 1, 1);

            for (int c = 0; c < customerCount; c++)
            {
                var customer = ObjectSpace.CreateObject<Customer>();
                customer.Name = $"{firstNames[rnd.Next(firstNames.Length)]} " +
                                $"{lastNames[rnd.Next(lastNames.Length)]} #{c + 1}";
                customer.City = cities[rnd.Next(cities.Length)];

                int orderCount = rnd.Next(10, 51); // 10..50 orders
                for (int o = 0; o < orderCount; o++)
                {
                    var order = ObjectSpace.CreateObject<Order>();
                    order.OrderDate = baseDate.AddDays(rnd.Next(0, 730));
                    order.Customer = customer;
                    customer.Orders.Add(order);

                    int lineCount = rnd.Next(1, 11); // 1..10 lines
                    for (int l = 0; l < lineCount; l++)
                    {
                        var line = ObjectSpace.CreateObject<OrderLine>();
                        line.Product = products[rnd.Next(products.Length)];
                        line.Quantity = rnd.Next(1, 21); // 1..20
                        line.UnitPrice = Math.Round((decimal)(rnd.NextDouble() * 100) + 1, 2);
                        line.Order = order;
                        order.Lines.Add(line);
                    }
                }
            }

            // Single commit for the whole graph (acceptable for a POC seed).
            ObjectSpace.CommitChanges();
        }
        public override void UpdateDatabaseBeforeUpdateSchema()
        {
            base.UpdateDatabaseBeforeUpdateSchema();
        }
    }
}
