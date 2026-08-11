# netcrunch-telemetry — .NET

**Status: alpha.** Implements the v1 subset of [`spec/v1.md`](../spec/v1.md) — counters, statuses,
events and data objects — plus the lifetime-bound aggregates of
[`spec/client-model.md`](../spec/client-model.md). Passes the shared conformance suite. No runtime
dependencies beyond the base class library.

## Install

```bash
dotnet add package NetCrunch.Telemetry
```

Targets `net8.0`.

## Use

```csharp
using NetCrunch.Telemetry;

await using var stats = new Telemetry(new TelemetryOptions
{
    Endpoint      = Environment.GetEnvironmentVariable("NC_TELEMETRY_URL")!,
    FlushInterval = TimeSpan.FromMinutes(1),
    OnError       = error => logger.LogWarning("telemetry: {Message}", error.Message),
});

var requests = stats.Counter("HTTP", "Requests");
requests.Increment();

stats.Status("Importer", "OK", message: "batch 41/120");
stats.Event("Nightly import completed");
```

`await using` matters: `DisposeAsync` stops the flush loop **and flushes what is still staged**.
The synchronous `Dispose` only stops the loop, because a blocking send in a finaliser path is worse
than the values it would save.

## The model

**A counter is a handle, not a call.** `Counter` resolves a path once and always returns the same
instance, so the hot path is a single interlocked compare-and-swap — no lock, no dictionary lookup,
no allocation per observation.

**Instrumentation only touches memory.** A separate flush snapshots the registry and sends absolute
current values. Nothing in a request path does I/O, and one request carries every value — which
matters because the receiver caps pending payloads per sensor and discards the overflow *silently*.

**Sending is idempotent.** Payloads carry absolute values rather than deltas, so a retry after a
timeout cannot double-count. Transport failures and 5xx responses are retried automatically; 4xx
responses are not, since repeating a rejected request will not change the answer.

**Statuses are what alerting acts on.** Counters are numbers you chart. If something can be wrong,
express it as a status — a counter alone will not raise anything.

## Lifetime-bound aggregates

`IDisposable`, so the decrement rides the language's own convention:

```csharp
using var lease = stats.SelfCount("Pool", "Leases Active");
```

| Aggregate | Behaviour |
| --- | --- |
| `SelfCount(object, counter, instance?)` | Holds 1 while undisposed. |
| `PartCount(object, counter, instance?)` | `Set(n)` moves its contribution; withdraws exactly what it added. |
| `Category(object, counter)` | `Set(instance)` moves 1 between instances of one counter. `Set(null)` releases it. |

`Category` is the one worth knowing about. `phase.Set("parsing")` then `phase.Set("writing")` leaves
`Workers/By Phase.parsing` at 0 and `.writing` at 1, with no bookkeeping at the call site — and the
buckets are instances of a single counter rather than unrelated names.

Disposal is idempotent on all three. That matters more than it looks: `using` and an explicit
`Dispose` can both fire, and a double decrement drives the count *negative*, which drifts away from
every threshold rather than towards one, so nothing ever reports it.

Held as a field, an aggregate belongs to a type that has its own `Dispose` — the ordinary chain,
which analyzers such as CA2213 already check. That is why there is no leak detector here; the Node
implementation needs one because `using` covers only block scope and nothing enforces the chain.

## Data objects

A table or chart rendered on the sensor's page, with no dashboard to configure:

```csharp
stats.Table("services", new TableData
{
    Name    = "Stopped Services",
    Columns = ["Name", "StartType"],
    Rows    = [["wuauserv", "Manual"]],
});

stats.CategoryChart("byOutcome", new CategoryChartData
{
    Name       = "Items by Outcome",
    SeriesName = "Items",
    Categories = ["imported", "skipped", "failed"],
    Values     = [1204, 18, 3],
});
```

`CategoryChart` is deliberately not `Category`, which is the aggregate above. Same word in NetCrunch,
unrelated meanings.

A data object's `Status` is part of what is *displayed*. **Alerting acts on statuses** — a red table
is not an alert, so send one too if something should fire.

Parallel arrays must match in length, and rows must match the column count. The receiver checks
neither and will render the mismatch, so this package throws. Arrays are also capped at 1024 entries,
above which the receiver silently truncates — rejected locally for the same reason.

## Exceptions

Validation throws `ArgumentException` / `ArgumentOutOfRangeException` at the call site, so a bad name
or a ragged table surfaces where it was written.

`TelemetryException` carries the HTTP `StatusCode` (0 for a transport failure) and **never the
endpoint**. `HttpClient` puts the request URI into the exceptions it raises, so failures here are
rebuilt rather than wrapped, and no inner exception is attached — `ToString()` would otherwise print
the credential into every log that captures it. Two tests assert this, one for an HTTP status and one
for a transport failure.

## Authentication

Pass the sensor's token alongside the endpoint; it goes out as `Authorization: Bearer`:

```csharp
await using var stats = new Telemetry(new TelemetryOptions
{
    Endpoint = Environment.GetEnvironmentVariable("NC_TELEMETRY_URL")!,
    Token    = Environment.GetEnvironmentVariable("NC_TELEMETRY_TOKEN"),
});
```

**The NetCrunch receiver does not verify the token yet.** Today the endpoint URL is itself the whole
credential: anyone who can reach the web server and knows the sensor name and node id can write to
that sensor. Sending a token now costs nothing and makes the client forward-compatible with the
receiver that enforces it. Until then treat **both** URL and token as secrets — neither reaches a log
or an exception from this package. See [`spec/v1.md`](../spec/v1.md) §1.1.

## Tests

```bash
dotnet test
```

Runs the shared fixtures in [`../conformance/cases`](../conformance/cases) plus unit tests for the
aggregates and the transport. HTTP behaviour is exercised through a stub `HttpMessageHandler` rather
than a listening socket.

Two rejection cases are reported in the test output as **UNREPRESENTABLE**: a counter value that is a
string, and a status value that is a number. Both are impossible here — the signatures take `double`
and `string` — so there is nothing to reject. The Go suite marks the equivalent cases as skipped;
xunit 2.x has no runtime skip, so they are logged instead. They are not counted as passing checks.

### Note on the test host

The library targets `net8.0`. The test project sets `RollForward=LatestMajor` so the host runs on
whatever major runtime is installed — a machine may have 6.0 and 9.0 but not 8.0, and without it
`dotnet test` fails before running anything.

## Known gaps

- **`net8.0` only.** No `netstandard2.0` target, so .NET Framework 4.8 applications cannot use this
  yet. That would mean taking a `System.Text.Json` package reference, which is the trade to weigh.
- **Concurrent flushes serialise** rather than sharing one request, as the Node implementation does.
  Each still sends absolute state, so the result is correct either way.
- **No rate helper.** NetCrunch does not derive per-second values for telemetry counters, so a rate
  must be computed and sent as its own counter.
- **The receiver does not enforce the token yet.** The client half is settled
  ([`spec/v1.md`](../spec/v1.md) §1.1); NetCrunch must issue tokens and verify the header before v1
  can be frozen. No client change is expected when that lands.
- **Not published to NuGet** while the package is alpha.
