using DevExpress.ExpressApp.ApplicationBuilder;
using DevExpress.ExpressApp.Blazor.ApplicationBuilder;
using DevExpress.ExpressApp.Blazor.Services;
using DevExpress.Persistent.Base;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using StackExchange.Profiling;
using StackExchange.Profiling.Storage;
using XAFProfiler.Blazor.Server.Services;

namespace XAFProfiler.Blazor.Server
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(typeof(Microsoft.AspNetCore.SignalR.HubConnectionHandler<>), typeof(ProxyHubConnectionHandler<>));

            services.AddRazorPages();
            services.AddServerSideBlazor();
            services.AddHttpContextAccessor();
            if (Configuration.GetValue<bool>("Profiling:Enabled"))
            {
                services.AddMiniProfiler(options =>
                {
                    options.RouteBasePath = "/profiler";
                    options.PopupRenderPosition = StackExchange.Profiling.RenderPosition.Left;
                    options.PopupShowTimeWithChildren = true;
                    options.TrackConnectionOpenClose = true;
                    options.ColorScheme = StackExchange.Profiling.ColorScheme.Auto;
                    options.ShouldProfile = request =>
                    {
                        var path = request.Path.Value ?? string.Empty;
                        return !(path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
                              || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
                              || path.StartsWith("/profiler", StringComparison.OrdinalIgnoreCase)
                              || path.Equals("/_Host", StringComparison.OrdinalIgnoreCase));
                    };
                    options.ResultsAuthorize = req => IsProfilerAuthorized(req.HttpContext);
                    options.ResultsListAuthorize = req => IsProfilerAuthorized(req.HttpContext);
                    // Layer C, Part A: persist profiles to SQL Server using the SAME
                    // connection string XAF uses, so captured profiles survive app restarts
                    // instead of living in the default in-memory MemoryCacheStorage.
                    // SqlServerStorage is the concrete storage from the
                    // MiniProfiler.Providers.SqlServer package. Its tables are created at
                    // startup by ProfilerStorageInitializer (see Configure).
                    var profilerConn = Configuration.GetConnectionString("ConnectionString");
                    options.Storage = new StackExchange.Profiling.Storage.SqlServerStorage(profilerConn);
                }).AddEntityFramework();
            }
            services.AddScoped<CircuitHandler, CircuitHandlerProxy>();
            services.AddXaf(Configuration, builder =>
            {
                builder.UseApplication<XAFProfilerBlazorApplication>();
                builder.Modules
                    .AddCloning()
                    .AddConditionalAppearance()
                    .AddOffice()
                    .AddValidation(options =>
                    {
                        options.AllowValidationDetailsAccess = false;
                    })
                    .AddViewVariants()
                    .Add<XAFProfiler.Module.XAFProfilerModule>()
                    .Add<XAFProfilerBlazorModule>();
                builder.ObjectSpaceProviders
                    .AddEFCore(options =>
                    {
                        options.PreFetchReferenceProperties();
                    })
                    .WithDbContext<XAFProfiler.Module.BusinessObjects.XAFProfilerEFCoreDbContext>((serviceProvider, options) =>
                    {
                        // Uncomment this code to use an in-memory database. This database is recreated each time the server starts. With the in-memory database, you don't need to make a migration when the data model is changed.
                        // Do not use this code in production environment to avoid data loss.
                        // We recommend that you refer to the following help topic before you use an in-memory database: https://docs.microsoft.com/en-us/ef/core/testing/in-memory
                        //options.UseInMemoryDatabase();
                        string connectionString = null;
                        if (Configuration.GetConnectionString("ConnectionString") != null)
                        {
                            connectionString = Configuration.GetConnectionString("ConnectionString");
                        }
#if EASYTEST
                        if(Configuration.GetConnectionString("EasyTestConnectionString") != null) {
                            connectionString = Configuration.GetConnectionString("EasyTestConnectionString");
                        }
#endif
                        ArgumentNullException.ThrowIfNull(connectionString);
                        options.UseConnectionString(connectionString);
                    })
                    .AddNonPersistent();
            });
        }

        private bool IsProfilerAuthorized(HttpContext ctx)
        {
            if (ctx == null) return false;
            // Dev: any authenticated user; otherwise require Administrators role.
            var env = ctx.RequestServices.GetService<IWebHostEnvironment>();
            if (env != null && env.IsDevelopment())
                return ctx.User?.Identity?.IsAuthenticated == true;
            return ctx.User?.IsInRole("Administrators") == true;
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. To change this for production scenarios, see: https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }
            app.UseHttpsRedirection();
            app.UseRequestLocalization();
            app.UseStaticFiles();
            if (Configuration.GetValue<bool>("Profiling:Enabled"))
            {
                app.UseMiniProfiler();
                // Layer C, Part A: SqlServerStorage does NOT auto-create its tables.
                // Idempotently create MiniProfilers / MiniProfilerTimings /
                // MiniProfilerClientTimings here, before the first profile is saved.
                // Guarded by Profiling:Enabled; failures are logged, not fatal.
                ProfilerStorageInitializer.EnsureTables(
                    Configuration.GetConnectionString("ConnectionString"));
            }
            app.UseRouting();
            app.UseXaf();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapXafEndpoints();
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
                endpoints.MapControllers();
            });
        }
    }
}
