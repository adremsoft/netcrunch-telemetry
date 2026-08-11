using System.Net;
using System.Text.Json;
using Xunit;

namespace NetCrunch.Telemetry.Tests;

public sealed class TelemetryTests
{
    private const string Endpoint = "https://netcrunch.example/api/rest/1/sensors/example@1/update";

    // A recognisable secret in the URL, so a leak into an exception is visible.
    private const string SecretEndpoint = "https://netcrunch.example/api/rest/1/sensors/SENSORSECRET@1/update";

    /// <summary>Stands in for the receiver, without binding a port.</summary>
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<string> _bodies = [];

        public IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_gate)
                {
                    return [.. _bodies];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            int count;
            lock (_gate)
            {
                _bodies.Add(body);
                count = _bodies.Count;
            }

            return respond(count);
        }
    }

    private static (Telemetry Stats, StubHandler Handler) Connect(
        Func<int, HttpResponseMessage> respond,
        string endpoint = Endpoint,
        int maxRetries = 3)
    {
        var handler = new StubHandler(respond);
        var stats = new Telemetry(new TelemetryOptions
        {
            Endpoint = endpoint,
            MaxRetries = maxRetries,
            HttpClient = new HttpClient(handler),
        });
        return (stats, handler);
    }

    private static Telemetry Offline() => new(new TelemetryOptions { Endpoint = Endpoint });

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    // -- construction -------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://host/x")]
    public void ConstructorRejectsBadEndpoint(string endpoint)
        => Assert.Throws<ArgumentException>(() => new Telemetry(new TelemetryOptions { Endpoint = endpoint }));

    [Fact]
    public void RetainMustOutlastFlushInterval()
    {
        // A 60s flush against a 1 minute retain would let values expire between sends.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Telemetry(new TelemetryOptions
        {
            Endpoint = Endpoint,
            FlushInterval = TimeSpan.FromMinutes(1),
            Retain = TimeSpan.FromMinutes(1),
        }));
    }

    // -- counter handles ----------------------------------------------------

    [Fact]
    public void SamePathResolvesToSameHandle()
    {
        using var stats = Offline();

        Assert.Same(
            stats.Counter("Queue", "Depth", "inbound"),
            stats.Counter("Queue", "Depth", "inbound"));
        Assert.NotSame(
            stats.Counter("Queue", "Depth", "inbound"),
            stats.Counter("Queue", "Depth", "outbound"));
    }

    [Fact]
    public void MaxAndMinMoveOneWay()
    {
        using var stats = Offline();

        var peak = stats.Counter("SNMP", "Peak ms");
        peak.Max(120);
        peak.Max(90);
        peak.Max(200);
        Assert.Equal(200, peak.Value);

        var floor = stats.Counter("SNMP", "Floor ms");
        floor.Set(100);
        floor.Min(150);
        floor.Min(40);
        floor.Min(70);
        Assert.Equal(40, floor.Value);
    }

    // -- lifetime-bound aggregates -----------------------------------------

    [Fact]
    public void SelfCountDisposesIdempotently()
    {
        // `using` and an explicit Dispose can both fire on the same instance. A double decrement
        // drives the value negative, which drifts away from every threshold rather than towards
        // one, so nothing would ever report it.
        using var stats = Offline();
        var handle = stats.Counter("Pool", "Leases Active");

        var lease = stats.SelfCount("Pool", "Leases Active");
        Assert.Equal(1, handle.Value);

        lease.Dispose();
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, handle.Value);
    }

    [Fact]
    public void PartCountWithdrawsItsContribution()
    {
        using var stats = Offline();
        var handle = stats.Counter("Cache", "Entries");
        handle.Set(1000);

        var shard = stats.PartCount("Cache", "Entries");
        shard.Set(5);
        shard.Set(9);
        shard.Set(3);
        Assert.Equal(1003, handle.Value);
        Assert.Equal(3, shard.Contribution);

        shard.Dispose();
        Assert.Equal(1000, handle.Value);
        Assert.Throws<ObjectDisposedException>(() => shard.Set(1));
    }

    [Fact]
    public void CategoryMovesBetweenInstances()
    {
        using var stats = Offline();
        var phase = stats.Category("Workers", "By Phase");

        phase.Set("parsing");
        Assert.Equal(1, stats.Counter("Workers", "By Phase", "parsing").Value);

        phase.Set("writing");
        Assert.Equal(0, stats.Counter("Workers", "By Phase", "parsing").Value);
        Assert.Equal(1, stats.Counter("Workers", "By Phase", "writing").Value);

        phase.Set("writing");
        Assert.Equal(1, stats.Counter("Workers", "By Phase", "writing").Value);

        phase.Dispose();
        Assert.Equal(0, stats.Counter("Workers", "By Phase", "writing").Value);
    }

    // -- payload ------------------------------------------------------------

    [Fact]
    public void EmptyCollectionsAreOmitted()
    {
        using var stats = Offline();
        Assert.Equal("{\"retain\":5,\"remove\":1440}", stats.BuildPayload());
    }

    [Fact]
    public void TimestampMessageCarriesNoMilliseconds()
    {
        using var stats = Offline();
        var observed = new DateTimeOffset(2026, 8, 10, 9, 14, 22, 512, TimeSpan.Zero);
        stats.Timestamp("Sync", "Age s", "Last Sync", observed);

        var payload = JsonDocument.Parse(
            stats.BuildPayload(new DateTimeOffset(2026, 8, 10, 9, 15, 54, TimeSpan.Zero))).RootElement;

        Assert.Equal(
            "2026-08-10T09:14:22Z",
            payload.GetProperty("statuses").GetProperty("Last Sync").GetProperty("message").GetString());

        // Milliseconds are dropped from the message but still count towards the age:
        // 91.488s rounds to 91, not the 92 a whole-second observation would give.
        Assert.Equal(91, payload.GetProperty("counters")[0].GetProperty("value").GetDouble());
    }

    // -- sending ------------------------------------------------------------

    [Fact]
    public async Task FlushClearsEventsButKeepsCountersAndStatuses()
    {
        var (stats, handler) = Connect(_ => Status(HttpStatusCode.OK));
        using (stats)
        {
            stats.Counter("Job", "Items").Set(7);
            stats.Status("Job", "OK");
            stats.Event("started");

            await stats.FlushAsync();
            await stats.FlushAsync();
        }

        Assert.Equal(2, handler.Bodies.Count);

        var first = JsonDocument.Parse(handler.Bodies[0]).RootElement;
        var second = JsonDocument.Parse(handler.Bodies[1]).RootElement;

        Assert.True(first.TryGetProperty("events", out _));
        Assert.False(second.TryGetProperty("events", out _));
        Assert.True(second.TryGetProperty("counters", out _));
        Assert.True(second.TryGetProperty("statuses", out _));
    }

    [Fact]
    public async Task ExceptionsNeverCarryTheEndpoint()
    {
        var (stats, _) = Connect(_ => Status(HttpStatusCode.Unauthorized), SecretEndpoint);
        using (stats)
        {
            stats.Status("Job", "OK");

            var error = await Assert.ThrowsAsync<TelemetryException>(() => stats.FlushAsync());

            Assert.Equal(401, error.StatusCode);
            // The URL is currently the credential; it must reach neither a message nor a stack.
            Assert.DoesNotContain("SENSORSECRET", error.ToString(), StringComparison.Ordinal);
            Assert.Null(error.InnerException);
        }
    }

    [Fact]
    public async Task TransportFailuresDoNotLeakTheEndpointEither()
    {
        // What HttpClient itself raises names the endpoint, which is why it is never wrapped.
        var (stats, _) = Connect(
            _ => throw new HttpRequestException($"No connection could be made to {SecretEndpoint}"),
            SecretEndpoint,
            maxRetries: 0);

        using (stats)
        {
            stats.Status("Job", "OK");

            var error = await Assert.ThrowsAsync<TelemetryException>(() => stats.FlushAsync());

            Assert.Equal(0, error.StatusCode);
            Assert.DoesNotContain("SENSORSECRET", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RejectedRequestsAreNotRetried()
    {
        var (stats, handler) = Connect(_ => Status(HttpStatusCode.BadRequest), maxRetries: 3);
        using (stats)
        {
            stats.Status("Job", "OK");
            await Assert.ThrowsAsync<TelemetryException>(() => stats.FlushAsync());
        }

        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task ServerErrorsAreRetried()
    {
        var (stats, handler) = Connect(
            count => Status(count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK),
            maxRetries: 1);

        using (stats)
        {
            stats.Status("Job", "OK");
            await stats.FlushAsync();
        }

        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task NothingStagedMeansNothingSent()
    {
        var (stats, handler) = Connect(_ => Status(HttpStatusCode.OK));
        using (stats)
        {
            await stats.FlushAsync();
        }

        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task ConcurrentUseIsSafe()
    {
        var (stats, _) = Connect(_ => Status(HttpStatusCode.OK));
        using (stats)
        {
            var handle = stats.Counter("Load", "Operations");

            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    handle.Increment();
                    using (stats.SelfCount("Load", "In Flight"))
                    {
                        stats.Status("Worker", "OK");
                    }
                }

                await stats.FlushAsync();
            })));

            Assert.Equal(1600, handle.Value);
            Assert.Equal(0, stats.Counter("Load", "In Flight").Value);
        }
    }
}
