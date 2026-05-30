#nullable enable
using Microsoft.Data.SqlClient;

namespace XAFProfiler.Blazor.Server.Services
{
    /// <summary>
    /// Idempotently bootstraps MiniProfiler's SQL Server tables (MiniProfilers,
    /// MiniProfilerTimings, MiniProfilerClientTimings).
    ///
    /// Layer C, Part A is BLOCKED: MiniProfiler's concrete
    /// <c>StackExchange.Profiling.Storage.SqlServerStorage</c> (and its
    /// <c>TableCreationScripts</c> DDL) live in the separate
    /// <c>MiniProfiler.Providers.SqlServer</c> NuGet package, which is NOT referenced by
    /// this project (only MiniProfiler.AspNetCore[.Mvc], MiniProfiler.EntityFrameworkCore
    /// and MiniProfiler.Shared are; the latter exposes only the abstract
    /// <c>SqlServerStorageBase</c>). Adding a NuGet package is out of scope for this task.
    ///
    /// This helper therefore only performs the idempotent table-existence CHECK. Once the
    /// SQL Server provider package is referenced and
    /// <c>options.Storage = new SqlServerStorage(conn)</c> is set in Startup, replace the
    /// TODO below with a loop over <c>SqlServerStorage.TableCreationScripts</c> and call
    /// this from <c>Startup.Configure</c> behind the Profiling:Enabled flag. Failures are
    /// logged to the console, not thrown (acceptable storage fallback for a POC).
    /// </summary>
    public static class ProfilerStorageInitializer
    {
        public static void EnsureTables(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.WriteLine("[ProfilerStorageInitializer] No connection string supplied; skipping table creation.");
                return;
            }
            try
            {
                using var connection = new SqlConnection(connectionString);
                connection.Open();

                using var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT OBJECT_ID('MiniProfilers')";
                var result = checkCmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                {
                    // Tables already exist; nothing to do.
                    return;
                }

                // TODO (unblock Part A): once MiniProfiler.Providers.SqlServer is referenced:
                //     foreach (var script in SqlServerStorage.TableCreationScripts) {
                //         using var createCmd = connection.CreateCommand();
                //         createCmd.CommandText = script;
                //         createCmd.ExecuteNonQuery();
                //     }
                Console.WriteLine(
                    "[ProfilerStorageInitializer] MiniProfilers table not found, but no " +
                    "SqlServerStorage provider is referenced; cannot create tables. " +
                    "Profiles are not persisted to SQL Server (Part A BLOCKED).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProfilerStorageInitializer] Table check failed: {ex.Message}");
            }
        }
    }
}
