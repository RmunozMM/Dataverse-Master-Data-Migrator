using System;

namespace DataverseMasterDataMigrator.Core.Migration
{
    /// <summary>
    /// Dataverse does NOT have a bulk "UpsertMultiple" message. The real bulk messages are
    /// <c>CreateMultipleRequest</c> and <c>UpdateMultipleRequest</c> — each requires a
    /// same-operation batch (all creates, or all updates), so using them for a
    /// preserve-source-GUID profile (section 11) requires first checking, per record, whether
    /// its GUID already exists in Target, then splitting the batch accordingly.
    /// <c>UpsertRequest</c> exists only as a single-record message; wrapping many of them in an
    /// <c>ExecuteMultipleRequest</c> avoids the pre-existence check at the cost of one HTTP
    /// round trip per record server-side (still one round trip client-side).
    /// </summary>
    public enum WriteStrategy
    {
        /// <summary>Pre-check existence, split the batch, call CreateMultiple + UpdateMultiple.</summary>
        BulkCreateThenUpdate,

        /// <summary>Wrap individual UpsertRequest calls in one ExecuteMultipleRequest.</summary>
        ExecuteMultipleUpsert,

        /// <summary>Sequential individual UpsertRequest calls — used only if ExecuteMultiple
        /// itself is unavailable (e.g. disabled by an organization setting).</summary>
        IndividualRequests
    }

    /// <summary>
    /// Entrada a la política: el resultado de UN intento fallido. La clasificación de si el
    /// error es transitorio y el <see cref="RetryAfter"/> (si el servicio lo indicó, típicamente
    /// un 429) los resuelve el adaptador XrmToolBox, que sí conoce
    /// <c>FaultException&lt;OrganizationServiceFault&gt;</c>. El Core solo aplica la política
    /// (ver ARCHITECTURE.md sección 8).
    /// </summary>
    public sealed class RetryContext
    {
        public int AttemptNumber { get; set; } // 1-based: el intento que acaba de fallar
        public bool IsTransient { get; set; }
        public TimeSpan? RetryAfter { get; set; }
    }

    public sealed class RetryDecision
    {
        public bool ShouldRetry { get; set; }
        public TimeSpan Delay { get; set; }

        public static RetryDecision Stop() => new RetryDecision { ShouldRetry = false, Delay = TimeSpan.Zero };

        public static RetryDecision RetryAfter(TimeSpan delay) =>
            new RetryDecision { ShouldRetry = true, Delay = delay };
    }

    public sealed class RetryPolicy
    {
        private readonly int _maxAttempts;
        private readonly TimeSpan _baseDelay;
        private readonly TimeSpan _maxDelay;
        private readonly Random _jitterSource;

        /// <param name="maxAttempts">Total de intentos permitidos, incluido el primero (no solo
        /// los reintentos). Ej: 4 significa 1 intento inicial + hasta 3 reintentos.</param>
        /// <param name="baseDelay">Base del backoff exponencial (delay del primer reintento).</param>
        /// <param name="maxDelay">Tope superior del delay calculado, sin contar un
        /// <c>Retry-After</c> explícito del servicio (ese siempre se respeta tal cual, sección 19:
        /// "respeto explícito de Retry-After").</param>
        /// <param name="jitterSource">Inyectable para tests deterministas.</param>
        public RetryPolicy(int maxAttempts = 4, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null, Random jitterSource = null)
        {
            if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

            _maxAttempts = maxAttempts;
            _baseDelay = baseDelay ?? TimeSpan.FromSeconds(2);
            _maxDelay = maxDelay ?? TimeSpan.FromSeconds(60);
            _jitterSource = jitterSource ?? new Random();
        }

        public RetryDecision Evaluate(RetryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (!context.IsTransient)
                return RetryDecision.Stop();

            if (context.AttemptNumber >= _maxAttempts)
                return RetryDecision.Stop();

            if (context.RetryAfter.HasValue)
                return RetryDecision.RetryAfter(context.RetryAfter.Value);

            // Backoff exponencial: baseDelay * 2^(intento-1), con jitter de +/-20% para evitar
            // que reintentos de varios chunks en paralelo converjan en la misma ventana de tiempo.
            var exponent = context.AttemptNumber - 1;
            var rawMillis = _baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
            var cappedMillis = Math.Min(rawMillis, _maxDelay.TotalMilliseconds);

            var jitterFactor = 0.8 + (_jitterSource.NextDouble() * 0.4); // [0.8, 1.2)
            var finalMillis = cappedMillis * jitterFactor;

            return RetryDecision.RetryAfter(TimeSpan.FromMilliseconds(finalMillis));
        }
    }
}
