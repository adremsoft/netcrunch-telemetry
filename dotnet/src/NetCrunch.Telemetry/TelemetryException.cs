namespace NetCrunch.Telemetry;

/// <summary>
/// A send failure. It deliberately carries no endpoint.
/// </summary>
/// <remarks>
/// The endpoint URL currently carries the sensor identity and is effectively the credential
/// (spec/v1.md section 1). <see cref="HttpClient"/> puts the request URI into the exceptions it
/// raises, so failures here are rebuilt from scratch rather than wrapped — and no inner exception is
/// attached, because <c>ToString</c> would then print the credential into every log that captures it.
/// </remarks>
public sealed class TelemetryException : Exception
{
    internal TelemetryException(string message, int statusCode = 0)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status code, or <c>0</c> for a transport-level failure.</summary>
    public int StatusCode { get; }
}
