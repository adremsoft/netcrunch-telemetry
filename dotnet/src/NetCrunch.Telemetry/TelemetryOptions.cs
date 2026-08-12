namespace NetCrunch.Telemetry;

/// <summary>Configures a <see cref="Telemetry"/>.</summary>
public sealed class TelemetryOptions
{
    /// <summary>
    /// The URL from the Telemetry sensor form. Treat it as a secret: this library never writes it to
    /// an exception or a log.
    /// </summary>
    public required string Endpoint { get; init; }

    /// <summary>
    /// The bearer token from the Telemetry sensor, sent as an <c>Authorization</c> header. Optional
    /// because a sensor need not have one configured, and because servers before NetCrunch 16.0 do
    /// not check it; see spec/v1.md section 1.1. Never logged.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    /// Starts a background flush loop when greater than zero. Disposal stops it. <see cref="TimeSpan.Zero"/>
    /// — the default — flushes only when asked.
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// How long values stay live after arriving. Must exceed <see cref="FlushInterval"/>, or they
    /// expire between sends. Defaults to five minutes.
    /// </summary>
    public TimeSpan Retain { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long an object survives with no data. Defaults to a day.</summary>
    public TimeSpan Remove { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Timeout for a single request. Defaults to thirty seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Retries for transport failures and 5xx responses. Defaults to three. Retrying is safe because
    /// payloads carry absolute values rather than deltas.
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Receives failures from background flushes, which have nowhere else to go. Explicit
    /// <see cref="Telemetry.FlushAsync"/> calls throw instead.
    /// </summary>
    public Action<Exception>? OnError { get; init; }

    /// <summary>
    /// The client to send with. When null, one is created and owned by the <see cref="Telemetry"/>.
    /// Supply your own to participate in <c>IHttpClientFactory</c> lifetimes.
    /// </summary>
    public HttpClient? HttpClient { get; init; }
}
