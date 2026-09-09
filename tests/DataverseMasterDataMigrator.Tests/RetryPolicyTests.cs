using System;
using DataverseMasterDataMigrator.Core.Migration;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class RetryPolicyTests
    {
        [Fact]
        public void NonTransientError_NeverRetries()
        {
            var policy = new RetryPolicy(maxAttempts: 5);

            var decision = policy.Evaluate(new RetryContext { AttemptNumber = 1, IsTransient = false });

            Assert.False(decision.ShouldRetry);
        }

        [Fact]
        public void TransientError_RetriesUntilMaxAttempts()
        {
            var policy = new RetryPolicy(maxAttempts: 3, jitterSource: new Random(1));

            var first = policy.Evaluate(new RetryContext { AttemptNumber = 1, IsTransient = true });
            var second = policy.Evaluate(new RetryContext { AttemptNumber = 2, IsTransient = true });
            var third = policy.Evaluate(new RetryContext { AttemptNumber = 3, IsTransient = true });

            Assert.True(first.ShouldRetry);
            Assert.True(second.ShouldRetry);
            Assert.False(third.ShouldRetry); // ya alcanzó maxAttempts = 3
        }

        [Fact]
        public void ExplicitRetryAfter_IsRespectedVerbatim()
        {
            var policy = new RetryPolicy(maxAttempts: 5, jitterSource: new Random(1));
            var requested = TimeSpan.FromSeconds(37);

            var decision = policy.Evaluate(new RetryContext
            {
                AttemptNumber = 1,
                IsTransient = true,
                RetryAfter = requested
            });

            Assert.True(decision.ShouldRetry);
            Assert.Equal(requested, decision.Delay);
        }

        [Fact]
        public void Backoff_GrowsWithEachAttempt()
        {
            var policy = new RetryPolicy(maxAttempts: 10, baseDelay: TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromMinutes(5), jitterSource: new Random(42));

            var d1 = policy.Evaluate(new RetryContext { AttemptNumber = 1, IsTransient = true }).Delay;
            var d2 = policy.Evaluate(new RetryContext { AttemptNumber = 2, IsTransient = true }).Delay;
            var d3 = policy.Evaluate(new RetryContext { AttemptNumber = 3, IsTransient = true }).Delay;

            // Con jitter +/-20% el crecimiento no es exacto, pero debe ser claramente creciente
            // entre intentos consecutivos dado que la base se duplica cada vez.
            // TimeSpan no tiene operator* con double en .NET Framework (si en .NET Core/5+), por
            // eso se multiplica en Ticks explícitamente.
            Assert.True(d2 > TimeSpan.FromTicks((long)(d1.Ticks * 1.2)), $"d1={d1}, d2={d2}");
            Assert.True(d3 > TimeSpan.FromTicks((long)(d2.Ticks * 1.2)), $"d2={d2}, d3={d3}");
        }

        [Fact]
        public void Delay_NeverExceedsMaxDelay_EvenAtHighAttemptCount()
        {
            var maxDelay = TimeSpan.FromSeconds(60);
            var policy = new RetryPolicy(maxAttempts: 20, baseDelay: TimeSpan.FromSeconds(1), maxDelay: maxDelay, jitterSource: new Random(7));

            var decision = policy.Evaluate(new RetryContext { AttemptNumber = 15, IsTransient = true });

            // Con jitter hasta 1.2x, el tope real es maxDelay * 1.2
            Assert.True(decision.Delay <= TimeSpan.FromSeconds(maxDelay.TotalSeconds * 1.2 + 0.001));
        }
    }
}
