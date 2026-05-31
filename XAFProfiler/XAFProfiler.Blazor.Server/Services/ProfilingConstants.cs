#nullable enable
namespace XAFProfiler.Blazor.Server.Services
{
    /// <summary>
    /// Shared constants for the ambient profiling pipeline.
    /// </summary>
    internal static class ProfilingConstants
    {
        /// <summary>
        /// MiniProfiler custom-timing category key for captured SQL commands. This is the
        /// load-bearing contract between the WRITER (<see cref="OperationCaptureRegistry"/>,
        /// which calls <c>Timing.AddCustomTiming(SqlTimingKey, …)</c>) and the READER
        /// (<see cref="ProfileProjection"/>, which reads <c>Timing.CustomTimings[SqlTimingKey]</c>).
        /// It MUST be a single shared value — if the two ever drift, capture silently yields
        /// empty profiles with no error. MiniProfiler.EntityFrameworkCore uses "sql" for EF.
        /// </summary>
        internal const string SqlTimingKey = "sql";
    }
}
