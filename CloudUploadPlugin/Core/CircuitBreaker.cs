using System;
using System.Threading;

namespace Astrovault.Core
{
    /// <summary>
    /// Lightweight circuit breaker that pauses uploads after consecutive transient failures.
    /// Three states: Closed (normal), Open (blocking), HalfOpen (probing with single upload).
    /// Uses Interlocked operations for thread-safe state transitions.
    /// </summary>
    public class CircuitBreaker
    {
        private const int FailureThreshold = 5;
        private static readonly TimeSpan DefaultRecoveryTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Recovery timeout before an Open circuit is allowed to probe (transition to HalfOpen).
        /// Defaults to 60s. Exposed as an internal test seam so the deterministic TC-16 auto-recovery
        /// E2E test can shrink it (instead of sleeping a real minute) -- production behavior is unchanged
        /// at the default. Not part of the production behavior contract.
        /// </summary>
        internal TimeSpan RecoveryTimeout { get; set; } = DefaultRecoveryTimeout;

        /// <summary>
        /// Clock seam. Defaults to wall-clock UTC. Exposed internally so tests can advance breaker time
        /// deterministically (e.g. force the Open -> HalfOpen probe transition) without a real sleep.
        /// Production code never overrides this, so default behavior is unchanged.
        /// </summary>
        internal Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;

        // State: 0 = Closed, 1 = Open, 2 = HalfOpen
        private int state;
        private int consecutiveFailures;

        // WR-04: openedAt is read in ShouldAttempt() and written by the CAS winner in RecordFailure();
        // with the parallel-chunk model these can run on different threads. Store it as UTC ticks in a
        // long and access it via Volatile.Read / Volatile.Write so a torn/stale read is impossible on
        // weak memory models (a DateTime field provides no such barrier).
        private long openedAtTicks;

        /// <summary>
        /// Whether the circuit breaker is currently open (uploads paused due to API outage).
        /// Returns true for both Open and HalfOpen states.
        /// </summary>
        public bool IsOpen => Volatile.Read(ref state) != 0;

        /// <summary>
        /// Checks whether an upload attempt should proceed.
        /// Returns true when Closed, true once when transitioning Open to HalfOpen (probe),
        /// and false when Open and recovery timeout has not elapsed.
        /// </summary>
        public bool ShouldAttempt()
        {
            var currentState = Volatile.Read(ref state);
            if (currentState == 0) return true;  // Closed
            if (currentState == 2) return true;  // HalfOpen -- allow probe

            // Open -- check if recovery timeout elapsed (WR-04: volatile read of the ticks field)
            var openedAt = new DateTime(Volatile.Read(ref openedAtTicks), DateTimeKind.Utc);
            if (NowProvider() - openedAt >= RecoveryTimeout)
            {
                // Transition to HalfOpen -- only one thread wins the CAS
                if (Interlocked.CompareExchange(ref state, 2, 1) == 1)
                {
                    return true; // This thread is the probe
                }
            }

            return false;
        }

        /// <summary>
        /// Records a successful upload. Resets failure count and closes the circuit.
        /// </summary>
        public void RecordSuccess()
        {
            Interlocked.Exchange(ref consecutiveFailures, 0);
            Interlocked.Exchange(ref state, 0); // Close circuit
        }

        /// <summary>
        /// Records a transient upload failure. Opens circuit after reaching threshold.
        /// Permanent failures should NOT be recorded here.
        /// </summary>
        public void RecordFailure()
        {
            var failures = Interlocked.Increment(ref consecutiveFailures);
            if (failures >= FailureThreshold)
            {
                // Try Closed -> Open
                if (Interlocked.CompareExchange(ref state, 1, 0) == 0)
                {
                    Volatile.Write(ref openedAtTicks, NowProvider().Ticks);
                }
                // Try HalfOpen -> Open (probe failed)
                else if (Interlocked.CompareExchange(ref state, 1, 2) == 2)
                {
                    Volatile.Write(ref openedAtTicks, NowProvider().Ticks);
                }
            }
        }
    }
}
