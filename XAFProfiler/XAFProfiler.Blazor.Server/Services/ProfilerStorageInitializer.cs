#nullable enable
using Microsoft.Data.SqlClient;
using StackExchange.Profiling.Storage;

namespace XAFProfiler.Blazor.Server.Services
{
    /// <summary>
    /// Idempotently bootstraps MiniProfiler's SQL Server tables (MiniProfilers,
    /// MiniProfilerTimings, MiniProfilerClientTimings).
    ///
    /// <see cref="SqlServerStorage"/> (from MiniProfiler.Providers.SqlServer) does NOT
    /// auto-create its schema, so we run the DDL it exposes via
    /// <see cref="SqlServerStorage.TableCreationScripts"/> only when the MiniProfilers
    /// table is absent. Safe to call on every startup. Failures are logged to the
    /// console, not thrown: for a POC, falling back to no persistence is acceptable
    /// rather than crashing the app.
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

                using (var checkCmd = connection.CreateCommand())
                {
                    checkCmd.CommandText = "SELECT OBJECT_ID('MiniProfilers')";
                    var result = checkCmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                    {
                        // Tables already exist; nothing to do.
                        return;
                    }
                }

                // MiniProfilers table is absent -> create the full schema. The DDL is
                // exposed by SqlServerStorage as an INSTANCE property (TableCreationScripts),
                // so we create a throwaway instance just to read the scripts. Each entry is
                // a standalone CREATE TABLE statement.
                var storage = new SqlServerStorage(connectionString);
                foreach (var script in storage.TableCreationScripts)
                {
                    using var createCmd = connection.CreateCommand();
                    createCmd.CommandText = script;
                    createCmd.ExecuteNonQuery();
                }
                Console.WriteLine("[ProfilerStorageInitializer] MiniProfiler SQL Server tables created.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProfilerStorageInitializer] Failed to create MiniProfiler tables: {ex.Message}");
            }
        }
    }
}
