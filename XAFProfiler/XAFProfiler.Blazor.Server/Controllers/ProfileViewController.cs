#nullable enable
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Profiling;
using XAFProfiler.Blazor.Server.Services;
using XAFProfiler.Module.BusinessObjects.Demo;

namespace XAFProfiler.Blazor.Server.Controllers
{
    /// <summary>
    /// Platform-specific (Blazor.Server) ViewController that profiles a real data
    /// operation on the Customer ListView over the SignalR circuit. The action's
    /// Execute handler runs in a circuit event with no HttpContext, so MiniProfiler.Current
    /// would normally be null; we use <see cref="CircuitProfilerService"/> to start and
    /// persist a profiler manually.
    ///
    /// Lives in the Blazor.Server project (not the Module) because it references
    /// StackExchange.Profiling / CircuitProfilerService, which are only available there.
    /// </summary>
    public sealed class ProfileViewController : ViewController
    {
        private readonly SimpleAction profileAction;

        public ProfileViewController()
        {
            TargetViewType = ViewType.ListView;
            TargetObjectType = typeof(Customer);
            TargetViewNesting = Nesting.Root;

            profileAction = new SimpleAction(this, "ProfileThisView", PredefinedCategory.Tools)
            {
                Caption = "Profile This View",
                ToolTip = "Capture MiniProfiler timings for a reload + N+1 aggregate over the circuit.",
                ImageName = "Action_Debug_Start"
            };
            profileAction.Execute += ProfileAction_Execute;
        }

        // Synchronous handler (no async void per viewcontroller-patterns skill). The
        // profiler stop is awaited via GetAwaiter().GetResult() since Execute is sync.
        private void ProfileAction_Execute(object? sender, SimpleActionExecuteEventArgs e)
        {
            var svc = Application.ServiceProvider.GetService<CircuitProfilerService>();
            if (svc == null)
            {
                Application.ShowViewStrategy.ShowMessage(
                    "CircuitProfilerService is not available. Is Profiling:Enabled set?",
                    InformationType.Warning);
                return;
            }

            MiniProfiler mp = svc.Start("Profile: Customer ListView");

            using (svc.Step("Reload + aggregate"))
            {
                // Use the View's own ObjectSpace (do not create/leak a new one).
                // Refresh() reloads all objects from the data store so the subsequent
                // GetObjects + OrdersTotal walk re-issues EF queries the profiler captures.
                View.ObjectSpace.Refresh();
                View.RefreshDataSource();

                var customers = View.ObjectSpace.GetObjects<Customer>();
                decimal grand = 0m;
                using (svc.Step("Sum OrdersTotal (N+1)"))
                {
                    foreach (var c in customers)
                    {
                        // Walking OrdersTotal forces per-customer lazy loading -> N+1 queries.
                        grand += c.OrdersTotal;
                    }
                }
            }

            // XAF's SimpleAction.Execute is synchronous, but stopping the profiler persists
            // to SQL asynchronously. We are on the Blazor circuit's single-threaded
            // RendererSynchronizationContext; calling StopAndSaveAsync().GetAwaiter().GetResult()
            // directly DEADLOCKS — StopAsync's continuation is posted back to this same context,
            // which the blocking GetResult() is occupying. Offloading to a thread-pool thread via
            // Task.Run runs the whole async chain with no ambient SynchronizationContext, so its
            // continuations never need the dispatcher thread. We still block (the action is
            // inherently synchronous), but on a non-circular wait, so it completes.
            Task.Run(() => svc.StopAndSaveAsync()).GetAwaiter().GetResult();

            Application.ShowViewStrategy.ShowMessage(
                $"Profiled. View results at /profiler/results?id={mp.Id}",
                InformationType.Success);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                profileAction.Execute -= ProfileAction_Execute;
            }
            base.Dispose(disposing);
        }
    }
}
