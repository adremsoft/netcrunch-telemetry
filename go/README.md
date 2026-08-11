# netcrunch-telemetry — Go

**Status: alpha.** Implements the v1 subset of [`spec/v1.md`](../spec/v1.md) — counters, statuses,
events and data objects — plus the lifetime-bound aggregates of
[`spec/client-model.md`](../spec/client-model.md). Passes the shared conformance suite. Standard
library only, no dependencies.

## Install

```bash
go get github.com/adremsoft/netcrunch-telemetry/go
```

Go 1.21+.

## Use

```go
import telemetry "github.com/adremsoft/netcrunch-telemetry/go"

stats, err := telemetry.New(telemetry.Options{
    Endpoint:      os.Getenv("NC_TELEMETRY_URL"),
    FlushInterval: time.Minute,
    OnError:       func(err error) { log.Printf("telemetry: %v", err) },
})
if err != nil {
    log.Fatal(err)
}
defer stats.Close(context.Background())

requests := stats.MustCounter("HTTP", "Requests", "")
requests.Inc()

stats.Status("Importer", "OK", telemetry.StatusOptions{Message: "batch 41/120"})
stats.Event("Nightly import completed")
```

## The model

**A counter is a handle, not a call.** `Counter` resolves a path once and always returns the same
handle, so the hot path is a single atomic operation — the value is stored as the bit pattern of a
float64 and updated with compare-and-swap, with no lock, no map lookup and no allocation.

`MustCounter` is the panicking form, for package-level declarations where a bad name is a
programming error rather than a condition to handle.

**Instrumentation only touches memory.** A separate flush snapshots the registry and sends absolute
current values. Nothing in a request path does I/O, and one request carries every value — which
matters because the receiver caps pending payloads per sensor and discards the overflow *silently*.

**Sending is idempotent.** Payloads carry absolute values rather than deltas, so a retry after a
timeout cannot double-count. Transport failures and 5xx responses are retried automatically; 4xx
responses are not, since repeating a rejected request will not change the answer.

**Statuses are what alerting acts on.** Counters are numbers you chart. If something can be wrong,
express it as a status — a counter alone will not raise anything.

## Lifetime-bound aggregates

These tie the decrement to a scope rather than to a line of code someone has to remember:

```go
lease := stats.SelfCount("Pool", "Leases Active", "")
defer lease.Close()
```

| Aggregate | Behaviour |
| --- | --- |
| `SelfCount(object, counter, instance)` | Holds 1 while open. |
| `PartCount(object, counter, instance)` | `Set(n)` moves its contribution; withdraws exactly what it added. |
| `Category(object, counter)` | `Set(instance)` moves 1 between instances of one counter. `Set("")` releases it. |

`Category` is the one worth knowing about. `phase.Set("parsing")` then `phase.Set("writing")` leaves
`Workers/By Phase.parsing` at 0 and `.writing` at 1, with no bookkeeping at the call site — and the
buckets are instances of a single counter rather than unrelated names.

`Close` is idempotent on all three. That matters more than it looks: `defer` and an explicit close
can both fire, and a double decrement drives the count *negative*, which drifts away from every
threshold rather than towards one, so nothing ever reports it.

### No leak detector, deliberately

The Node implementation ships a `FinalizationRegistry` that warns about undisposed aggregates,
because `using` only covers block scope and an aggregate held as a struct field needs every owner in
the chain to opt in.

Go does not need the equivalent. `defer x.Close()` is the same convention already used for files,
rows, locks and contexts, and `runtime.SetFinalizer` in a library people embed is a smell that buys
little here. If an aggregate outlives a function, it belongs to a struct that has its own `Close`,
which is ordinary Go.

## Data objects

A table or chart rendered on the sensor's page, with no dashboard to configure:

```go
stats.Table("services", telemetry.Table{
    Name:    "Stopped Services",
    Columns: []any{"Name", "StartType"},
    Rows:    [][]any{{"wuauserv", "Manual"}},
})

stats.CategoryChart("byOutcome", telemetry.CategoryChart{
    Name:       "Items by Outcome",
    SeriesName: "Items",
    Categories: []string{"imported", "skipped", "failed"},
    Values:     []float64{1204, 18, 3},
})
```

`CategoryChart` is deliberately not `Category`, which is the aggregate above. Same word in NetCrunch,
unrelated meanings.

A data object's `Status` is part of what is *displayed*. **Alerting acts on statuses** — a red table
is not an alert, so send one too if something should fire.

Parallel arrays must match in length, and rows must match the column count. The receiver checks
neither and will render the mismatch, so this package rejects it. Arrays are also capped at 1024
entries, above which the receiver silently truncates — rejected locally for the same reason.

## Errors, not panics

Everything that can be wrong returns an `error`; only `MustCounter` panics. Resolving a counter
returns `(*Counter, error)`, which is verbose exactly once, at the point where you were going to keep
the handle anyway.

`*telemetry.Error` carries the HTTP `StatusCode` (0 for a transport failure) and **never the
endpoint**. `net/http` puts the request URL into the errors it returns, so failures here are rebuilt
rather than wrapped — a wrapped cause would put the credential into every log that prints it. There
is a test asserting it.

## Keep the endpoint secret

The endpoint URL currently carries the sensor identity and is effectively the credential — see
[`spec/v1.md`](../spec/v1.md) §1, where this is flagged as unresolved before v1 can be frozen.

## Tests

```bash
go test ./...
```

Runs the shared fixtures in [`../conformance/cases`](../conformance/cases) plus unit tests for the
aggregates and the transport.

Two conformance cases are reported as **skipped**: a counter value that is a string, and a status
value that is a number. Both are unrepresentable here — the signatures take `float64` and `string` —
so there is nothing to reject. Skipping says that; passing would claim a check that does not exist.

## Known gaps

- **No `Timestamp` status value override.** The companion status is always `"OK"`; the other
  implementations let you choose.
- **Concurrent flushes serialise** rather than sharing one request, as the Node implementation does.
  Each still sends absolute state, so the result is correct either way.
- **No rate helper.** NetCrunch does not derive per-second values for telemetry counters, so a rate
  must be computed and sent as its own counter.
- **No authentication beyond the endpoint URL.** Blocked on the spec.
- **Not tagged for release** while the package is alpha; the module path carries no version suffix yet.
