# netcrunch-telemetry — Node.js / TypeScript

**Status: alpha.** Implements the v1 subset of [`spec/v1.md`](../spec/v1.md) — counters, statuses and
events — plus the lifetime-bound aggregates. Passes the shared conformance suite. No dependencies.

## Install

```bash
npm install @netcrunch/telemetry
```

Node 20+. ESM only (see [known gaps](#known-gaps)). Types are bundled.

## Use

```js
import { Telemetry } from "@netcrunch/telemetry";

const stats = new Telemetry({
  endpoint: process.env.NC_TELEMETRY_URL,
  flushSeconds: 60,
});

const pending = stats.counter("Queue", "Depth", "inbound");
pending.inc();

stats.status("Importer", "OK", { message: "batch 41/120" });
stats.event("Nightly import completed");

await stats.close();   // flush once more and stop the timer
```

See [`examples/service.js`](examples/service.js) for a long-running process.

## The model

**A counter is a handle, not a call.** `counter()` resolves the path once and always returns the same
handle, so the hot path is a numeric mutation — no name lookup, no attribute-set allocation per
observation. Resolve once, keep it, mutate it.

**Instrumentation only touches memory.** A separate flush snapshots the registry and sends absolute
current values. Nothing in your request path does I/O, and one request carries every value — which
matters because the receiver caps pending payloads per sensor and discards the overflow *silently*.

**Sending is idempotent.** Payloads carry absolute values rather than deltas, so a retry after a
timeout cannot double-count. Transport failures and 5xx responses are retried automatically; 4xx
responses are not, since repeating a rejected request will not change the answer.

**Statuses are what alerting acts on.** Counters are numbers you chart. If something can be wrong,
express it as a status — a counter alone will not raise anything.

## Lifetime-bound aggregates

These make "how many X are currently in state Y" correct by construction: the decrement is tied to
object lifetime rather than written by hand in a `finally` someone will eventually forget.

```js
{
  using lease = stats.selfCount("Pool", "Leases Active");
  await doWork();
}   // decrements here, including on throw
```

| Aggregate | Behaviour |
| --- | --- |
| `selfCount(object, counter, instance?)` | Holds +1 while alive. |
| `partCount(object, counter, instance?)` | `.set(n)` moves its contribution; withdraws exactly what it added. |
| `category(object, counter)` | `.set(instance)` moves +1 between instances of one counter. |

`category` is the one worth knowing about. `phase.set("parsing")` then `phase.set("writing")` leaves
`Workers/By Phase.parsing` at 0 and `.writing` at 1, with no bookkeeping at the call site — and the
instances are siblings under one counter rather than unrelated names.

Disposal is idempotent, so an explicit `dispose()` and a `using` block firing on the same object is
harmless.

### The part JavaScript makes awkward

`using` is block-scoped. That covers the easy half.

It does **not** cover the common case — an aggregate held as a field on a long-lived object, where
the counter's lifetime is the owner's lifetime. There, every class in the ownership chain has to opt
in:

```js
class Connection {
  #stack = new DisposableStack();
  constructor(stats) {
    this.#stack.use(stats.selfCount("Pool", "Connections Active"));
  }
  [Symbol.dispose]() { this.#stack.dispose(); }
}
```

…and whoever owns the `Connection` must `using` it, or adopt it into its own stack, recursively. It
is correct, and it is viral: one class in the chain that forgets, and the gauge drifts upward forever
with nothing raised. In languages with refcounting or RAII this is transitive and free.

So the library ships a **leak detector**: a `FinalizationRegistry` that warns when an aggregate is
collected without having been disposed.

```
NetCrunch telemetry: SelfCount on Pool/Leases Active was garbage collected without
being disposed, so its contribution is stuck. Use `using`, or call dispose() explicitly.
```

This is *not* the decrement. `FinalizationRegistry` is explicitly non-deterministic and not
guaranteed to run, so it cannot be the source of truth for a live count. It turns a gauge that drifts
over days into a log line while you are still writing the code. On by default outside production;
`detectLeaks: false` to silence it.

## API

| Member | Purpose |
| --- | --- |
| `new Telemetry(options)` | `endpoint` required. `flushSeconds` 0 (default) means manual flush only. |
| `.counter(object, counter, instance?)` | Resolve a handle. `.set` `.add` `.inc` `.dec` `.max` `.min` `.reset` |
| `.status(key, value, {message, critical, data})` | Stage a state. |
| `.event(message, {severity})` | Stage an occurrence. Cleared once sent. |
| `.timestamp(object, counter, statusKey, {observedAt})` | Record when something last happened. |
| `.selfCount` `.partCount` `.category` | Lifetime-bound aggregates. |
| `.buildPayload({snapshotAt})` | Inspect what a flush would post. |
| `.flush({signal})` | Send once. Concurrent calls share one request. |
| `.start()` `.stop()` `.close()` | Timer control. The timer is `unref`'d and never holds the process open. |
| `.clear()` | Discard everything staged. |

Failures from automatic flushes have nowhere to propagate, so pass `onError` to see them.

### Timestamps

The wire format has no timestamp type, and a raw clock value means nothing outside the process that
produced it. `timestamp()` emits two things: an age in seconds you can set a threshold on, and a
status message carrying the absolute time for a person to read. Age is computed at flush time.

## Keep the endpoint secret

The endpoint URL currently carries the sensor identity and is effectively the credential — see
[`spec/v1.md`](../spec/v1.md) §1, where this is flagged as unresolved before v1 can be frozen.

`fetch` puts the request URL into the errors it raises, so this library never wraps them. Failures
are rebuilt as `TelemetryError` carrying only a status code, and there is a test asserting the
endpoint appears in neither the message nor the stack.

## Tests

```bash
npm test
```

Runs the shared fixtures in [`../conformance/cases`](../conformance/cases) plus unit tests for the
aggregates and the transport. Uses the built-in `node --test` — no framework.

The `using` tests live in their own file behind a support probe: explicit resource management is
syntax, so a runtime without it fails at parse time rather than at call time, and the rest of the
suite still has to run on Node 20.

## Known gaps

- **ESM only.** A CommonJS build needs a bundler, and the package deliberately has no build step yet.
  CJS consumers can `await import()` in the meantime.
- **No `maxInfo`/`minInfo`.** Capturing *what caused* a peak has no channel in v1 — counter
  `metadata` is discarded server-side. Pair a peak counter with a status message for now.
- **No rate helper.** NetCrunch does not derive per-second values for telemetry counters, so a rate
  must be computed and sent as its own counter.
- **No `data` objects.** Tables, time-series and category charts are deferred from v1.
- **No authentication beyond the endpoint URL.** Blocked on the spec.
- **Not published to npm** while the package is alpha.
