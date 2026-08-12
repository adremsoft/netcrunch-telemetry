# netcrunch-telemetry — Python

**Status: alpha.** Implements the v1 subset of [`spec/v1.md`](../spec/v1.md) — counters, statuses,
events and data objects — plus the lifetime-bound aggregates of
[`spec/client-model.md`](../spec/client-model.md). Passes the shared conformance suite. Standard
library only, no dependencies.

## Install

**Not published to PyPI yet.** pip can install straight from the repository, which carries the
package in a subdirectory:

```bash
pip install "git+https://github.com/adremsoft/netcrunch-telemetry#subdirectory=python"
```

Once `netcrunch-telemetry` is published this becomes `pip install netcrunch-telemetry`.

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

See [`examples/service.py`](examples/service.py), or
[`examples/async_service.py`](examples/async_service.py) for the asyncio form.

## asyncio

`AsyncTelemetry` shares its registry with `Telemetry`, so staging is identical — the same
`counter()`, `status()`, `event()`, data objects and aggregates, because none of them does I/O. Only
flushing differs.

```python
from netcrunch_telemetry import AsyncTelemetry

async with AsyncTelemetry(url, token=token, flush_seconds=60) as stats:
    stats.counter("HTTP", "Requests").inc()
    stats.status("Service", "OK")
```

Two differences worth knowing:

- **The flush loop is not started by the constructor.** Creating a task needs a running event loop,
  and there may not be one where the object is built. `async with` starts it; otherwise `await
  stats.start()` from inside the loop.
- **Aggregates stay synchronous.** `with stats.self_count(...)`, not `async with` — closing one only
  touches memory.

### What "async" means here, precisely

The standard library has no async HTTP client and `urllib` blocks, so the socket work goes to a
worker thread via `asyncio.to_thread`. That is one thread hop per *flush interval*, not per
observation.

What is genuinely async is everything around it. The retry backoff is a real `asyncio.sleep`, so a
retrying send does not hold a thread — or your loop — for the seconds it spends waiting. Measured on
a single 503 followed by success, with a 50 ms heartbeat running alongside:

| | Loop ticks during the 1 s backoff |
| --- | --- |
| `AsyncTelemetry` | 20 |
| `Telemetry.flush()` called from the loop | 0 |

If you already have `aiohttp` or `httpx` in the application, pass `send=` to route through it and
skip the thread entirely:

```python
async def sender(endpoint, body, *, timeout_seconds, max_retries, token=None):
    async with session.post(endpoint, data=body, headers=...) as response:
        ...

stats = AsyncTelemetry(url, send=sender)
```

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

- **No native async HTTP.** `AsyncTelemetry` keeps the loop responsive, but the socket work still
  goes to a worker thread, because the standard library has no async HTTP client and taking a
  dependency would break the zero-dependency promise the other four libraries keep. Pass `send=` to
  use one you already have.
- **No rate helper.** NetCrunch does not derive per-second values for telemetry counters, so a rate
  must be computed and sent as its own counter.
- **The receiver does not enforce the token yet.** The client half is settled
  ([`spec/v1.md`](../spec/v1.md) §1.1); NetCrunch must issue tokens and verify the header before v1
  can be frozen. No client change is expected when that lands.
- **Not published to PyPI** while the package is alpha.
