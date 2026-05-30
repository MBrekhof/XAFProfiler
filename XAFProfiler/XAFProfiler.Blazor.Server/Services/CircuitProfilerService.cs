#nullable enable
using StackExchange.Profiling;

namespace XAFProfiler.Blazor.Server.Services
{
    /// <summary>
    /// Scoped (per-circuit) service that owns a manually-started MiniProfiler.
    /// Blazor SignalR circuit events (button clicks, grid loads) have no HttpContext,
    /// so <see cref="MiniProfiler.Current"/> is null for them. This service starts a
    /// profiler on demand for the current circuit and persists it to the globally
    /// configured MiniProfiler storage so the results show up under /profiler/results.
    ///
    /// It is null-safe and inert when MiniProfiler is not configured (Profiling:Enabled = false):
    /// <see cref="MiniProfiler.StartNew(string)"/> uses the global options registered in
    /// Startup's AddMiniProfiler; if those are absent the captured profiler simply won't
    /// be persisted, but no exception is thrown.
    /// </summary>
    public sealed class CircuitProfilerService
    {
        /// <summary>The profiler started for the current circuit operation, if any.</summary>
        public MiniProfiler? Current { get; private set; }

        /// <summary>
        /// Starts a new MiniProfiler for the current circuit. StartNew sets
        /// MiniProfiler.Current via the async-local provider; we capture the instance so
        /// callers don't depend on the ambient Current (which is unreliable across awaits
        /// in a circuit). Uses the globally configured options (storage, etc.).
        /// </summary>
        public MiniProfiler Start(string name)
        {
            Current = MiniProfiler.StartNew(name);
            return Current!;
        }

        /// <summary>
        /// Opens a timing step on the current profiler. No-op (returns null) if not started.
        /// MiniProfiler's Step returns a Timing, which implements IDisposable.
        /// </summary>
        public IDisposable? Step(string name) => Current?.Step(name);

        /// <summary>
        /// Stops the current profiler and persists it to the configured storage
        /// (discardResults: false). Safe to call when nothing was started.
        /// </summary>
        public async Task StopAndSaveAsync()
        {
            if (Current != null)
            {
                // StopAsync(false) stops the timing and saves to the configured storage.
                await Current.StopAsync(discardResults: false);
                Current = null;
            }
        }
    }
}
