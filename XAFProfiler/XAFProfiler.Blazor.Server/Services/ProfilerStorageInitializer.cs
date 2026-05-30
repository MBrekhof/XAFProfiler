using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using StackExchange.Profiling.Storage;

namespace XAFProfiler.Blazor.Server.Services;

/// <summary>
/// Ensures the target application database and the MiniProfiler SQL Server storage tables
/// exist before profiling begins.
/// <para>
/// This runs at host-startup time, BEFORE XAF lazily creates its database on first access.
/// Because the configured catalog (e.g. <c>XAFProfiler</c>) may not exist yet, this method
/// first connects to <c>master</c> and creates an EMPTY database if needed, then creates the
/// three MiniProfiler tables inside it. It deliberately does NOT create any XAF/business
/// schema — XAF still owns its own schema and runs its own DatabaseUpdate against the
/// (possibly pre-existing, empty) database.
/// </para>
/// </summary>
public static class ProfilerStorageInitializer
{
    // A safe SQL Server identifier for a database name: starts with a letter or underscore,
    // followed by letters, digits or underscores. We bracket-escape, but only ever inject
    // names that match this pattern into DDL to guard against injection.
    private static readonly Regex SafeIdentifier =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static void EnsureTables(string connectionString)
    {
        try
        {
            // 1. Parse the target catalog and build a parallel connection string to `master`,
            //    keeping all other settings (auth, integrated security, etc.) intact.
            var targetBuilder = new SqlConnectionStringBuilder(connectionString);
            var catalog = targetBuilder.InitialCatalog;

            if (string.IsNullOrWhiteSpace(catalog))
            {
                Console.WriteLine(
                    "[ProfilerStorageInitializer] No Initial Catalog in connection string; skipping database creation.");
            }
            else
            {
                EnsureDatabase(connectionString, catalog);
            }

            // 3. Connect to the target DB and create the MiniProfiler tables if missing.
            using var connection = new SqlConnection(connectionString);
            connection.Open();

            using (var checkCommand = connection.CreateCommand())
            {
                checkCommand.CommandText =
                    "IF OBJECT_ID('MiniProfilers') IS NULL SELECT 0 ELSE SELECT 1";
                var exists = (int)checkCommand.ExecuteScalar() == 1;
                if (exists)
                {
                    Console.WriteLine("[ProfilerStorageInitializer] MiniProfiler tables already exist.");
                    return;
                }
            }

            // Create the MiniProfiler tables using the official table creation scripts.
            // The storage instance is throwaway — used only for its TableCreationScripts.
            var storage = new SqlServerStorage(connectionString);
            foreach (var script in storage.TableCreationScripts)
            {
                using var createCommand = connection.CreateCommand();
                createCommand.CommandText = script;
                createCommand.ExecuteNonQuery();
            }

            Console.WriteLine("[ProfilerStorageInitializer] Ensured database + MiniProfiler tables.");
        }
        catch (Exception ex)
        {
            // Never throw at startup — profiling persistence is best-effort.
            Console.WriteLine($"[ProfilerStorageInitializer] Failed to create MiniProfiler tables: {ex.Message}");
        }
    }

    /// <summary>
    /// Connects to <c>master</c> using the same auth settings and creates an empty database
    /// for <paramref name="catalog"/> if it does not already exist. Idempotent.
    /// </summary>
    private static void EnsureDatabase(string connectionString, string catalog)
    {
        if (!SafeIdentifier.IsMatch(catalog))
        {
            // Cannot safely inject this name into CREATE DATABASE; fall through and let the
            // table-creation attempt surface any real problem.
            Console.WriteLine(
                $"[ProfilerStorageInitializer] Catalog name '{catalog}' is not a plain identifier; skipping database creation.");
            return;
        }

        var masterBuilder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master"
        };

        using var masterConnection = new SqlConnection(masterBuilder.ConnectionString);
        masterConnection.Open();

        using var command = masterConnection.CreateCommand();
        // DB_ID is parameterized; the bracket-escaped name is only injected after the
        // SafeIdentifier validation above.
        command.CommandText =
            $"IF DB_ID(@cat) IS NULL CREATE DATABASE [{catalog}]";
        command.Parameters.AddWithValue("@cat", catalog);
        command.ExecuteNonQuery();
    }
}
