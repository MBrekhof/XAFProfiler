#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using DevExpress.ExpressApp;
using StackExchange.Profiling;
using XAFProfiler.Blazor.Server.BusinessObjects;

namespace XAFProfiler.Blazor.Server.Services
{
    /// <summary>
    /// Pure projection helper: converts a loaded <see cref="MiniProfiler"/> instance into the
    /// project's non-persistent view objects (<see cref="ProfileSummary"/> / <see cref="ProfileQuery"/>).
    ///
    /// All objects are created via <see cref="NonPersistentObjectSpace.CreateObject{T}"/> to
    /// satisfy XAF's non-persistent key registration requirements (avoids error 1021).
    /// </summary>
    public static class ProfileProjection
    {
        /// <summary>The MiniProfiler custom-timing category key for EF Core / ADO SQL statements.</summary>
        private const string SqlTimingKey = "sql";

        /// <summary>
        /// Builds a <see cref="ProfileSummary"/> suitable for list-view display.
        /// The <see cref="ProfileSummary.Queries"/> collection is left empty.
        /// </summary>
        public static ProfileSummary BuildSummary(NonPersistentObjectSpace npos, MiniProfiler profiler)
        {
            var sqlTimings = CollectSqlTimings(profiler);

            var summary = npos.CreateObject<ProfileSummary>();
            summary.Id = profiler.Id;
            summary.Operation = profiler.Name;
            summary.Started = profiler.Started;
            summary.DurationMs = (double)profiler.DurationMilliseconds;
            summary.QueryCount = sqlTimings.Count;
            summary.SlowestQueryMs = sqlTimings.Count > 0
                ? sqlTimings.Max(t => t.DurationMs)
                : 0d;

            return summary;
        }

        /// <summary>
        /// Builds a <see cref="ProfileSummary"/> with the <see cref="ProfileSummary.Queries"/>
        /// collection populated and ordered by grouped total duration descending — suitable for
        /// detail view. Because each row's <see cref="ProfileQuery.DurationMs"/> is the summed
        /// duration across executions, a query run 10×5ms outranks a single 45ms query.
        /// SQL timings with the same command text are grouped; <see cref="ProfileQuery.ExecuteCount"/>
        /// reflects how many times that query ran (the N+1 signal).
        /// </summary>
        public static ProfileSummary BuildDetail(NonPersistentObjectSpace npos, MiniProfiler profiler)
        {
            var sqlTimings = CollectSqlTimings(profiler);

            var summary = npos.CreateObject<ProfileSummary>();
            summary.Id = profiler.Id;
            summary.Operation = profiler.Name;
            summary.Started = profiler.Started;
            summary.DurationMs = (double)profiler.DurationMilliseconds;
            summary.QueryCount = sqlTimings.Count;
            summary.SlowestQueryMs = sqlTimings.Count > 0
                ? sqlTimings.Max(t => t.DurationMs)
                : 0d;

            // Group by command text, order by total duration desc.
            var queries = sqlTimings
                .GroupBy(t => t.CommandText ?? string.Empty)
                .Select(g =>
                {
                    var query = npos.CreateObject<ProfileQuery>();
                    query.Id = Guid.NewGuid();
                    query.Sql = g.Key.Length > 0 ? g.Key : null;
                    query.DurationMs = g.Sum(t => t.DurationMs);
                    query.ExecuteCount = g.Count();
                    return query;
                })
                .OrderByDescending(q => q.DurationMs)
                .ToList();

            summary.Queries = queries;
            return summary;
        }

        // ── Private helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Walks the entire timing tree starting at <see cref="MiniProfiler.Root"/> and returns
        /// a flat list of (CommandText, DurationMs) for every "sql" custom timing found.
        /// Null-safe: handles null Root, null Children, null CustomTimings, missing "sql" key,
        /// null CommandString, and null DurationMilliseconds.
        /// </summary>
        private static List<SqlTimingRecord> CollectSqlTimings(MiniProfiler profiler)
        {
            var results = new List<SqlTimingRecord>();
            if (profiler.Root is null)
                return results;

            // Iterative DFS over the timing tree to avoid stack overflow on deep trees.
            var stack = new Stack<Timing>();
            stack.Push(profiler.Root);

            while (stack.Count > 0)
            {
                var timing = stack.Pop();

                // Collect SQL custom timings from this node.
                if (timing.CustomTimings is not null &&
                    timing.CustomTimings.TryGetValue(SqlTimingKey, out var sqlList) &&
                    sqlList is not null)
                {
                    foreach (var ct in sqlList)
                    {
                        if (ct is null) continue;
                        results.Add(new SqlTimingRecord(
                            ct.CommandString,
                            (double)(ct.DurationMilliseconds ?? 0m)));
                    }
                }

                // Push children for traversal (Children is List<Timing>, may be null when
                // deserialized from storage with no child timings).
                if (timing.Children is not null)
                {
                    foreach (var child in timing.Children)
                    {
                        if (child is not null)
                            stack.Push(child);
                    }
                }
            }

            return results;
        }

        private readonly record struct SqlTimingRecord(string? CommandText, double DurationMs);
    }
}
