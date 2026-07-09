using System;
using System.Reflection;
using Astrovault.Core;

namespace CloudUploadPlugin.Tests.Core
{
    [TestFixture]
    [Category("Resilience")]
    public class CircuitBreakerTests
    {
        private CircuitBreaker sut;

        [SetUp]
        public void Setup()
        {
            sut = new CircuitBreaker();
        }

        [Test]
        public void NewCircuitBreaker_IsNotOpen()
        {
            Assert.That(sut.IsOpen, Is.False);
        }

        [Test]
        public void ShouldAttempt_WhenClosed_ReturnsTrue()
        {
            Assert.That(sut.ShouldAttempt(), Is.True);
        }

        [Test]
        public void RecordFailure_BelowThreshold_KeepsCircuitClosed()
        {
            for (int i = 0; i < 4; i++)
            {
                sut.RecordFailure();
            }

            Assert.That(sut.IsOpen, Is.False);
        }

        [Test]
        public void RecordFailure_AtThreshold_OpensCircuit()
        {
            for (int i = 0; i < 5; i++)
            {
                sut.RecordFailure();
            }

            Assert.That(sut.IsOpen, Is.True);
        }

        [Test]
        public void ShouldAttempt_WhenOpen_ReturnsFalse()
        {
            for (int i = 0; i < 5; i++)
            {
                sut.RecordFailure();
            }

            Assert.That(sut.ShouldAttempt(), Is.False);
        }

        [Test]
        public void RecordSuccess_AfterCircuitOpen_ClosesCircuit()
        {
            // Open the circuit
            for (int i = 0; i < 5; i++)
            {
                sut.RecordFailure();
            }
            Assert.That(sut.IsOpen, Is.True);

            // Success should close the circuit
            sut.RecordSuccess();
            Assert.That(sut.IsOpen, Is.False);
        }

        [Test]
        public void RecordSuccess_ResetsFailureCount()
        {
            // Accumulate 4 failures (one below threshold)
            for (int i = 0; i < 4; i++)
            {
                sut.RecordFailure();
            }

            // Success resets count
            sut.RecordSuccess();

            // 4 more failures should NOT open circuit (count was reset)
            for (int i = 0; i < 4; i++)
            {
                sut.RecordFailure();
            }

            Assert.That(sut.IsOpen, Is.False);
        }

        [Test]
        public void ShouldAttempt_WhenOpenAndRecoveryTimeoutElapsed_ReturnsTrue()
        {
            // Open the circuit
            for (int i = 0; i < 5; i++)
            {
                sut.RecordFailure();
            }
            Assert.That(sut.IsOpen, Is.True);

            // Set openedAt to 61 seconds ago via reflection
            SetOpenedAt(DateTime.UtcNow.AddSeconds(-61));

            // Should allow a probe attempt (HalfOpen)
            Assert.That(sut.ShouldAttempt(), Is.True);
        }

        [Test]
        public void ShouldAttempt_WhenOpenAndRecoveryTimeoutNotElapsed_ReturnsFalse()
        {
            // Open the circuit
            for (int i = 0; i < 5; i++)
            {
                sut.RecordFailure();
            }

            // openedAt is just now, so timeout has NOT elapsed
            Assert.That(sut.ShouldAttempt(), Is.False);
        }

        [Test]
        public void RecordFailure_DuringHalfOpen_ReopensCircuit()
        {
            // Open the circuit
            for (int i = 0; i < 5; i++)
            {
                sut.RecordFailure();
            }

            // Move to HalfOpen by expiring the timeout
            SetOpenedAt(DateTime.UtcNow.AddSeconds(-61));
            sut.ShouldAttempt(); // Transitions to HalfOpen

            // Probe fails
            sut.RecordFailure();

            // Circuit should be back to Open
            Assert.That(sut.IsOpen, Is.True);
            Assert.That(sut.ShouldAttempt(), Is.False); // Just re-opened, no timeout yet
        }

        [Test]
        public void RecordSuccess_DuringHalfOpen_ClosesCircuit()
        {
            // Open the circuit
            for (int i = 0; i < 5; i++)
            {
                sut.RecordFailure();
            }

            // Move to HalfOpen by expiring the timeout
            SetOpenedAt(DateTime.UtcNow.AddSeconds(-61));
            sut.ShouldAttempt(); // Transitions to HalfOpen

            // Probe succeeds
            sut.RecordSuccess();

            // Circuit should be closed
            Assert.That(sut.IsOpen, Is.False);
            Assert.That(sut.ShouldAttempt(), Is.True);
        }

        /// <summary>
        /// Uses reflection to set the open-timestamp field for testing recovery timeout.
        /// WR-04: the field is now a long holding UTC ticks (volatile-accessed) rather than a
        /// DateTime, so we write value.Ticks. This still drives the recovery-timeout transitions
        /// the tests assert on.
        /// </summary>
        private void SetOpenedAt(DateTime value)
        {
            var field = typeof(CircuitBreaker).GetField("openedAtTicks", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "CircuitBreaker must have an 'openedAtTicks' field");
            field.SetValue(sut, value.Ticks);
        }
    }
}
