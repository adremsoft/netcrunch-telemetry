# netcrunch-telemetry — Python

**Status: alpha.** Implements the v1 subset of [`spec/v1.md`](../spec/v1.md) — counters, statuses,
events and data objects — plus the lifetime-bound aggregates of
[`spec/client-model.md`](../spec/client-model.md). Passes the shared conformance suite. Standard
library only, no dependencies.

## Install

```bash
pip install netcrunch-telemetry
```

Python 3.9+.

## Use

```python
from netcrunch_telemetry import Telemetry

stats = Telemetry(
    os.environ["NC_TELEMETRY_URL"],
    flush_seconds=60,
    on_error=lambda error: log.warning("telemetry: %s", error),
)

requests = stats.counter("HTTP", "Requests")
requests.inc()

stats.status("Importer", "OK", message="batch 41/120")
stats.event("Nightly import completed")

stats.close()   # stops the flush thread and sends what is staged
```

`Telemetry` is also a context manager, which is the better shape for a script:

```python
with Telemetry(url, retain_minutes=1500) as stats:
    stats.status("Nightly Backup", "OK", message=f"{count} files")
```

See [`examples/service.py`](examples/service.py).

## The model

**A counter is a handle, not a call.** `counter()` resolves a path once and always returns the same
object, so the hot path is a numeric mutation with no name lookup. Resolve once, keep it, mutate it.

**Instrumentation only touches memory.** A separate flush snapshots the registry and sends absolute
current values. Nothing in a request path does I/O, and one request carries every value — which
matters because the receiver caps pending payloads per sensor and discards the overflow *silently*.

**Sending is idempotent.** Payloads carry absolute values rather than deltas, so a retry after a
timeout cannot double-count. Transport failures and 5xx responses are retried automatically; 4xx
responses are not, since repeating a rejected request will not change the answer.

**Statuses are what alerting acts on.** Counters are numbers you chart. If something can be wrong,
express it as a status — a counter alone will not raise anything.

## Lifetime-bound aggregates

Context managers, so the decrement rides the language's own convention:

```python
with stats.self_count("Pool", "Leases Active"):
    do_work()      # decrements on the way out, including on an exception
```

| Aggregate | Behaviour |
| --- | --- |
| `self_count(obj, counter, instance=None)` | Holds 1 while open. |
| `part_count(obj, counter, instance=None)` | `.set(n)` moves its contribution; withdraws exactly what it added. |
| `category(obj, counter)` | `.set(instance)` moves 1 between instances of one counter. `.set(None)` releases it. |

`category` is the one worth knowing about. `phase.set("parsing")` then `phase.set("writing")` leaves
`Workers/By Phase.parsing` at 0 and `.writing` at 1, with no bookkeeping at the call site — and the
buckets are instances of a single counter rather than unrelated names.

`close()` is idempotent on all three. That matters more than it looks: a `with` block and an explicit
`close()` can both fire, and a double decrement drives the count *negative*, which drifts away from
every threshold rather than towards one, so nothing ever reports it.

### Leak detection uses ResourceWarning

An aggregate collected without being closed warns:

```
ResourceWarning: SelfCount on Pool/Leases Active was collected without being closed,
so its contribution is stuck. Use it as a context manager, or call close().
```

This is a diagnostic, **not** the decrement. It fires from `__del__`, and CPython's refcounting makes
that prompt in practice — but that is an implementation detail rather than a language guarantee, and
it does not hold on PyPy. The context manager is the contract.

`ResourceWarning` is the right category and it is what Python already uses for unclosed files and
sockets. It is silenced by default and surfaces under `python -X dev` or in a test run — on where it
helps, quiet where it would be noise. Pass `detect_leaks=False` to disable it entirely.

## Data objects

A table or chart rendered on the sensor's page, with no dashboard to configure:

```python
stats.table(
    "services",
    name="Stopped Services",
    columns=["Name", "StartType"],
    rows=[[s.name, s.start_type] for s in stopped],
)

stats.category_chart(
    "byOutcome",
    name="Items by Outcome",
    series_name="Items",
    categories=["imported", "skipped", "failed"],
    values=[1204, 18, 3],
)
```

`category_chart` is deliberately not `category`, which is the aggregate above. Same word in NetCrunch,
unrelated meanings.

A data object's `status` is part of what is *displayed*. **Alerting acts on statuses** — a red table
is not an alert, so send one too if something should fire.

Parallel arrays must match in length, and rows must match the column count. The receiver checks
neither and will render the mismatch, so this library rejects it. Arrays are also capped at 1024
entries, above which the receiver silently truncates — rejected locally for the same reason.

## Errors

Validation raises `TypeError` or `ValueError` at the call site. Notably, `True` is **not** accepted as
a counter value: `bool` subclasses `int`, and a flag silently becoming 1 is never what was meant.

`TelemetryError` carries `status_code` (0 for a transport failure) and **never the endpoint**.
`urllib` puts the URL on the exceptions it raises, so failures here are rebuilt and raised
`from None` — a chained cause would print the credential whenever the traceback is formatted. A test
asserts both the message and `__context__`.

## Authentication

Pass the sensor's token alongside the endpoint; it goes out as `Authorization: Bearer`:

```python
stats = Telemetry(
    os.environ["NC_TELEMETRY_URL"],
    token=os.environ.get("NC_TELEMETRY_TOKEN"),
)
```

**The NetCrunch receiver does not verify the token yet.** Today the endpoint URL is itself the whole
credential: anyone who can reach the web server and knows the sensor name and node id can write to
that sensor. Sending a token now costs nothing and makes the client forward-compatible with the
receiver that enforces it. Until then treat **both** URL and token as secrets — neither reaches a log
or an exception from this library. See [`spec/v1.md`](../spec/v1.md) §1.1.

## Tests

```bash
python -m unittest discover -s tests
```

Runs the shared fixtures in [`../conformance/cases`](../conformance/cases) plus unit tests for the
aggregates and the transport. Uses `unittest` from the standard library — no pytest, so verifying the
package needs nothing installed.

Unlike Go and .NET, no case is skipped here. Their signatures make a string counter value impossible
to pass; Python's do not, so every check has to exist and every rejection case applies.

## Known gaps

- **No asyncio support.** `flush()` blocks and the background flusher is a thread. An async
  application can still use it — counters are thread-safe and the thread is a daemon — but a
  `run_in_executor` hop is needed to flush without blocking the loop. An `AsyncTelemetry` sharing the
  same registry is the obvious next step, and given how much Python is async now, this is the largest
  gap of the five implementations.
- **No rate helper.** NetCrunch does not derive per-second values for telemetry counters, so a rate
  must be computed and sent as its own counter.
- **The receiver does not enforce the token yet.** The client half is settled
  ([`spec/v1.md`](../spec/v1.md) §1.1); NetCrunch must issue tokens and verify the header before v1
  can be frozen. No client change is expected when that lands.
- **Not published to PyPI** while the package is alpha.
