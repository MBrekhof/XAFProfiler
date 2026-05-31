#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Profiling;

namespace XAFProfiler.Blazor.Server.Services
{
    /// <summary>
    /// Singleton registry that ties an in-flight "operation" (e.g. a ListView data-load) to an
    /// explicitly-held <see cref="MiniProfiler"/> instance, keyed by the EF Core
    /// <see cref="DbContext"/> that executes the operation's SQL.
    ///
    /// <para>
    /// <b>Why an explicit profiler (not <see cref="MiniProfiler.Current"/>)?</b><br/>
    /// In XAF Blazor Server the DevExpress grid materialises its query on a separate async chain
    /// where the <c>AsyncLocal</c> <see cref="MiniProfiler.Current"/> is <c>null</c>. MiniProfiler's
    /// own EF interceptor logs to <c>Current</c>, so it captures nothing for the grid. Instead we
    /// hold the profiler ourselves and attribute captured SQL by <see cref="DbContext"/> instance:
    /// each XAF ListView's object space owns its own DbContext, and the
    /// <see cref="QueryCaptureInterceptor"/> sees that same instance via
    /// <c>CommandExecutedEventData.Context</c>.
    /// </para>
    ///
    /// <para>
    /// A <see cref="ConditionalWeakTable{TKey,TValue}"/> keys the active capture by DbContext so a
    /// disposed/abandoned context cannot leak a profiler.
    /// </para>
    /// </summary>
    public sealed class OperationCaptureRegistry
    {
        /// <summary>The MiniProfiler custom-timing category for EF Core / ADO SQL statements.
        /// Shared with <see cref="ProfileProjection"/> via <see cref="ProfilingConstants.SqlTimingKey"/>
        /// — writer and reader MUST agree on this key.</summary>
        private const string SqlTimingKey = ProfilingConstants.SqlTimingKey;

        private const int RetentionLimit = 200;

        private readonly ILogger<OperationCaptureRegistry>? _logger;
        private readonly string? _connectionString;
        private readonly object _gate = new();
        private readonly ConditionalWeakTable<DbContext, OperationCapture> _active = new();

        public OperationCaptureRegistry(
            ILogger<OperationCaptureRegistry>? logger = null,
            IConfiguration? configuration = null)
        {
            _logger = logger;
            _connectionString = configuration?.GetConnectionString("ConnectionString");
        }

        /// <summary>
        /// Wraps the explicitly-held profiler for one operation and appends captured SQL onto it.
        /// </summary>
        public sealed class OperationCapture
        {
            private readonly ILogger? _logger;

            internal OperationCapture(MiniProfiler profiler, ILogger? logger)
            {
                Profiler = profiler;
                _logger = logger;
            }

            public MiniProfiler Profiler { get; }

            /// <summary>
            /// Appends a single executed SQL command as a "sql" custom timing on the profiler's
            /// root timing, so <c>ProfileProjection</c> can later read it via
            /// <c>profiler.Root.CustomTimings["sql"]</c>. Best-effort; never throws.
            /// </summary>
            /// <param name="commandText">The SQL command text.</param>
            /// <param name="durationMs">The measured execution time, in milliseconds.</param>
            /// <param name="executeType">
            /// The command kind ("Reader", "Scalar", "NonQuery"); recorded on the
            /// <see cref="CustomTiming.ExecuteType"/> for forward-looking correctness
            /// (ProfileProjection does not read it today).
            /// </param>
            public void AddSql(string commandText, double durationMs, string executeType)
            {
                // Safe without locking: EF Core executes commands serially per DbContext (DbContext
                // is not thread-safe by contract), so AddSql is never called concurrently for the
                // same OperationCapture; ProfileProjection only reads CustomTimings after StopAsync
                // has returned (a later user navigation), so there is no concurrent read/write.
                try
                {
                    var root = Profiler.Root;
                    if (root is null) return;

                    // CustomTiming(profiler, commandString[, minSaveMs, includeStackTrace]).
                    // Its constructor sets StartMilliseconds to "now"; we overwrite duration with
                    // the interceptor's measured value. AddCustomTiming creates the CustomTimings
                    // dictionary and the "sql" list if needed, matching what ProfileProjection reads.
                    var ct = new CustomTiming(Profiler, commandText)
                    {
                        DurationMilliseconds = (decimal)durationMs,
                        ExecuteType = executeType,
                    };
                    root.AddCustomTiming(SqlTimingKey, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "OperationCapture.AddSql failed for command of length {Len}.",
                        commandText?.Length ?? 0);
                }
            }
        }

        /// <summary>
        /// Begins capturing for <paramref name="ctx"/> under a freshly-started profiler named
        /// <paramref name="operationName"/>. Any pre-existing capture for the same context is
        /// replaced (its profiler is abandoned, not saved). Best-effort; never throws.
        /// </summary>
        public void Begin(DbContext ctx, string operationName)
        {
            if (ctx is null) return;
            try
            {
                var profiler = MiniProfiler.StartNew(operationName);
                if (profiler is null) return; // StartNew can return null if profiling is disabled.

                lock (_gate)
                {
                    if (_active.Remove(ctx))
                    {
                        _logger?.LogWarning(
                            "Replacing an unsaved capture for the same DbContext; previous operation profile is discarded. This is unexpected in single-window use.");
                    }
                    _active.Add(ctx, new OperationCapture(profiler, _logger));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "OperationCaptureRegistry.Begin failed for '{Name}'.", operationName);
            }
        }

        /// <summary>
        /// Ends capturing for <paramref name="ctx"/>: removes the entry and stops+saves its
        /// profiler. The <see cref="MiniProfiler.StopAsync"/> call is offloaded via
        /// <c>Task.Run(...).GetAwaiter().GetResult()</c> — MANDATORY on the Blazor circuit, where a
        /// direct sync-over-async on the <c>RendererSynchronizationContext</c> deadlocks.
        /// Best-effort; never throws.
        /// </summary>
        public void End(DbContext ctx)
        {
            if (ctx is null) return;

            OperationCapture? capture = null;
            try
            {
                lock (_gate)
                {
                    if (_active.TryGetValue(ctx, out var found))
                    {
                        capture = found;
                        _active.Remove(ctx);
                    }
                }

                if (capture is null) return;

                // MANDATORY Task.Run offload — see remarks above.
                Task.Run(() => capture.Profiler.StopAsync(discardResults: false)).GetAwaiter().GetResult();

                // Best-effort retention: trim storage to the newest RetentionLimit profiles
                // so the table doesn't grow unboundedly. Runs on a background thread so the
                // circuit is not blocked by the extra SQL round-trip. Never throws into the
                // user's path; any failure is logged only.
                if (_connectionString != null)
                {
                    var connStr = _connectionString;
                    var logger = _logger;
                    Task.Run(() =>
                    {
                        try
                        {
                            ProfileStore.TrimToNewest(connStr, RetentionLimit, logger);
                        }
                        catch (Exception trimEx)
                        {
                            logger?.LogWarning(trimEx,
                                "OperationCaptureRegistry: post-save retention trim failed (best-effort).");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "OperationCaptureRegistry.End failed for profiler '{Name}'.",
                    capture?.Profiler?.Name);
            }
        }

        /// <summary>
        /// Returns the active capture for <paramref name="ctx"/>, if any. Used by the interceptor.
        /// </summary>
        public bool TryGetCurrent(DbContext ctx, out OperationCapture capture)
        {
            if (ctx is not null)
            {
                lock (_gate)
                {
                    if (_active.TryGetValue(ctx, out var found))
                    {
                        capture = found;
                        return true;
                    }
                }
            }

            capture = null!;
            return false;
        }
    }
}
