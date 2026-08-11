# netcrunch-telemetry — Python

**Status: not started.**

## Planned surface

```python
stats = Telemetry(endpoint=os.environ["NC_TELEMETRY_URL"], flush_seconds=60)

pending = stats.counter("Queue", "Depth", instance="inbound")
pending.inc()

stats.status("Importer", "OK", message="batch 41/120")
stats.event("Nightly import completed")
```

Lifetime-bound aggregates map to context managers, which are deterministic:

```python
with stats.self_count("Pool", "Leases Active"):
    ...   # decrements on exit, including on exception
```

CPython refcounting also makes the field-on-an-object case work transitively via `__del__`, but that
is an implementation detail rather than a language guarantee and does not hold on PyPy. The context
manager is the documented contract; `__del__` should be a safety net that warns, not the mechanism.

## Notes

Threading model needs deciding early: counters must be safe to mutate from any thread, and the
exporter runs on its own. Async applications should not be forced to adopt a thread — an
`asyncio`-native exporter alongside the threaded one is likely necessary rather than optional.
