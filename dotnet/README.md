# netcrunch-telemetry — .NET

**Status: not started.**

## Planned surface

```csharp
var stats = new Telemetry(endpoint, flushSeconds: 60);

var pending = stats.Counter("Queue", "Depth", "inbound");
pending.Inc();

stats.Status("Importer", "OK", message: "batch 41/120");
stats.Event("Nightly import completed");
```

Lifetime-bound aggregates map to `IDisposable`, which is deterministic and already idiomatic:

```csharp
using var lease = stats.SelfCount("Pool", "Leases Active");
```

The field-on-a-long-lived-object case works through the standard dispose chain — a type holding an
aggregate implements `IDisposable` and disposes it. That is a convention .NET developers already
follow, so the pattern carries less friction here than in most languages.

## Notes

`Interlocked` for counter mutation; the hot path should be an atomic increment with no allocation.
Worth shipping an `IHostedService` for the exporter so it participates in the generic host lifetime
rather than needing manual start and shutdown.
