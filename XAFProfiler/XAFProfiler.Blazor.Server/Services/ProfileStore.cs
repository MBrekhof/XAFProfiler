#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace XAFProfiler.Blazor.Server.Services
{
    /// <summary>
    /// Static helper that performs direct-SQL maintenance operations against the three
    /// MiniProfiler storage tables: <c>MiniProfilerClientTimings</c>, <c>MiniProfilerTimings</c>,
    /// and <c>MiniProfilers</c>.
    ///
    /// <para>
    /// All methods use <see cref="Microsoft.Data.SqlClient"/> with parameterized queries,
    /// mirroring the style of <see cref="ProfilerStorageInitializer"/>. Children are always
    /// deleted before parent rows to respect FK constraints.
    /// </para>
    ///
    /// <para>
    /// Every method is best-effort: failures are logged (when a logger is supplied) and
    /// re-thrown so callers can decide whether to surface an error message.
    /// </para>
    /// </summary>
    public static class ProfileStore
    {
        // ── Public API ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Deletes ALL rows from the three MiniProfiler tables (children first).
        /// </summary>
        public static void ClearAll(string connectionString, ILogger? logger = null)
        {
            try
            {
                using var connection = new SqlConnection(connectionString);
                connection.Open();

                ExecuteNonQuery(connection, "DELETE FROM MiniProfilerClientTimings");
                ExecuteNonQuery(connection, "DELETE FROM MiniProfilerTimings");
                ExecuteNonQuery(connection, "DELETE FROM MiniProfilers");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "ProfileStore.ClearAll failed.");
                throw;
            }
        }

        /// <summary>
        /// Deletes the specified profiler ids from all three tables (children first).
        /// No-op if <paramref name="ids"/> is empty.
        /// </summary>
        public static void DeleteByIds(string connectionString, IEnumerable<Guid> ids, ILogger? logger = null)
        {
            var idList = ids?.ToList();
            if (idList == null || idList.Count == 0) return;

            try
            {
                using var connection = new SqlConnection(connectionString);
                connection.Open();

                // Build parameterized IN-list placeholders (@p0, @p1, ...).
                var placeholders = BuildPlaceholders(idList.Count);

                // Children first (FK child → MiniProfilers.Id).
                ExecuteWithIds(connection,
                    $"DELETE FROM MiniProfilerClientTimings WHERE MiniProfilerId IN ({placeholders})",
                    idList);
                ExecuteWithIds(connection,
                    $"DELETE FROM MiniProfilerTimings WHERE MiniProfilerId IN ({placeholders})",
                    idList);
                ExecuteWithIds(connection,
                    $"DELETE FROM MiniProfilers WHERE Id IN ({placeholders})",
                    idList);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "ProfileStore.DeleteByIds failed for {Count} ids.", idList?.Count ?? 0);
                throw;
            }
        }

        /// <summary>
        /// Keeps only the newest <paramref name="keep"/> profilers by <c>Started</c>, deleting
        /// the rest from all three tables. If the current count is &lt;= <paramref name="keep"/>
        /// this is a cheap no-op (no rows deleted; one SELECT). Best-effort — see remarks on
        /// this class.
        /// </summary>
        public static void TrimToNewest(string connectionString, int keep, ILogger? logger = null)
        {
            // Guard keep <= 0: TOP(0) would delete EVERY row. Retention should never wipe the
            // whole store, so treat a non-positive keep as a no-op (use ClearAll to wipe).
            if (keep <= 0)
            {
                logger?.LogWarning("ProfileStore.TrimToNewest called with keep={Keep}; skipping (would delete all profiles).", keep);
                return;
            }
            try
            {
                using var connection = new SqlConnection(connectionString);
                connection.Open();

                // Delete children where their profiler is NOT in the newest @keep by Started.
                // The subquery re-evaluates ordering once; the two child deletes + parent delete
                // form one logical batch but are issued as three separate commands so the FK
                // ordering is maintained.
                const string newestSubquery =
                    "(SELECT TOP(@keep) Id FROM MiniProfilers ORDER BY Started DESC)";

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText =
                        $"DELETE FROM MiniProfilerClientTimings WHERE MiniProfilerId NOT IN {newestSubquery}";
                    cmd.Parameters.AddWithValue("@keep", keep);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText =
                        $"DELETE FROM MiniProfilerTimings WHERE MiniProfilerId NOT IN {newestSubquery}";
                    cmd.Parameters.AddWithValue("@keep", keep);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText =
                        $"DELETE FROM MiniProfilers WHERE Id NOT IN {newestSubquery}";
                    cmd.Parameters.AddWithValue("@keep", keep);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "ProfileStore.TrimToNewest(keep={Keep}) failed.", keep);
                throw;
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────────────────

        private static void ExecuteNonQuery(SqlConnection connection, string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>Builds a comma-separated parameter placeholder list: @p0, @p1, ... @pN-1.</summary>
        private static string BuildPlaceholders(int count)
            => string.Join(", ", Enumerable.Range(0, count).Select(i => $"@p{i}"));

        /// <summary>Executes a parameterized command with one Guid parameter per id in the list.</summary>
        private static void ExecuteWithIds(SqlConnection connection, string sql, IList<Guid> ids)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            for (int i = 0; i < ids.Count; i++)
            {
                cmd.Parameters.AddWithValue($"@p{i}", ids[i]);
            }
            cmd.ExecuteNonQuery();
        }
    }
}
