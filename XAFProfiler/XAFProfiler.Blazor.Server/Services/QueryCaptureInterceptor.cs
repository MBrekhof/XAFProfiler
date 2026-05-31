#nullable enable
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace XAFProfiler.Blazor.Server.Services
{
    /// <summary>
    /// EF Core <see cref="DbCommandInterceptor"/> (singleton) that captures every executed SQL
    /// command onto the <see cref="OperationCaptureRegistry"/>'s active profiler for the command's
    /// <see cref="System.Data.Common.DbCommand"/> owning <c>DbContext</c>.
    ///
    /// <para>
    /// Registered via <c>options.AddInterceptors(...)</c> on the XAF DbContext. Attribution is by
    /// <c>DbContext</c> instance: each XAF ListView's object space owns its own context, so SQL the
    /// grid (and XAF prefetch) executes is correctly attributed to that view's in-flight operation.
    /// </para>
    ///
    /// <para>
    /// Both sync (<c>*Executed</c>) and async (<c>*ExecutedAsync</c>) overrides are implemented:
    /// the grid materialises sync in practice, but async is covered for safety. Every override is
    /// best-effort and never throws into EF's pipeline.
    /// </para>
    /// </summary>
    public sealed class QueryCaptureInterceptor : DbCommandInterceptor
    {
        private readonly OperationCaptureRegistry _registry;
        private readonly ILogger<QueryCaptureInterceptor>? _logger;

        public QueryCaptureInterceptor(
            OperationCaptureRegistry registry,
            ILogger<QueryCaptureInterceptor>? logger = null)
        {
            _registry = registry;
            _logger = logger;
        }

        private void Capture(DbCommand command, CommandExecutedEventData eventData)
        {
            try
            {
                if (eventData.Context is { } ctx &&
                    _registry.TryGetCurrent(ctx, out var capture))
                {
                    capture.AddSql(command.CommandText, eventData.Duration.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "QueryCaptureInterceptor: failed to capture SQL command.");
            }
        }

        // ── Reader (SELECT) ─────────────────────────────────────────────────────────────

        public override DbDataReader ReaderExecuted(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Capture(command, eventData);
            return base.ReaderExecuted(command, eventData, result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Capture(command, eventData);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        // ── Scalar ──────────────────────────────────────────────────────────────────────

        public override object? ScalarExecuted(
            DbCommand command, CommandExecutedEventData eventData, object? result)
        {
            Capture(command, eventData);
            return base.ScalarExecuted(command, eventData, result);
        }

        public override ValueTask<object?> ScalarExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, object? result,
            CancellationToken cancellationToken = default)
        {
            Capture(command, eventData);
            return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
        }

        // ── NonQuery (INSERT/UPDATE/DELETE) ──────────────────────────────────────────────

        public override int NonQueryExecuted(
            DbCommand command, CommandExecutedEventData eventData, int result)
        {
            Capture(command, eventData);
            return base.NonQueryExecuted(command, eventData, result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Capture(command, eventData);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
