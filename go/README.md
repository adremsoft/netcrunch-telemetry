# netcrunch-telemetry — Go

**Status: not started.**

## Planned surface

```go
stats := telemetry.New(endpoint, telemetry.FlushEvery(60*time.Second))

pending := stats.Counter("Queue", "Depth", "inbound")
pending.Inc()

stats.Status("Importer", "OK", telemetry.Message("batch 41/120"))
stats.Event("Nightly import completed")
```

Lifetime-bound aggregates map to `defer`, which is deterministic:

```go
lease := stats.SelfCount("Pool", "Leases Active")
defer lease.Close()
```

Go has no destructor, so the field-on-a-long-lived-object case relies on the owner's own `Close`
being called — the same discipline Go already applies to files, connections and contexts. That is
familiar rather than novel, but it is still explicit, and `go vet`-style lostcancel checking does not
cover it.

## Notes

`sync/atomic` for counter mutation. The exporter should take a `context.Context` and stop with it.
Standard library `net/http` only — no third-party dependencies in the client.
