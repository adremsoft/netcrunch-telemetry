# netcrunch-telemetry — Node.js / TypeScript

**Status: not started.**

## Planned surface

```ts
const stats = new Telemetry({ endpoint: process.env.NC_TELEMETRY_URL, flushSeconds: 60 });

const pending = stats.counter("Queue", "Depth", "inbound");
pending.inc();

stats.status("Importer", "OK", { message: "batch 41/120" });
stats.event("Nightly import completed");
```

## The disposal problem, stated up front

Lifetime-bound aggregates — the pattern where "how many X are currently active" is correct by
construction because the decrement is tied to object lifetime — need deterministic destruction.

JavaScript now has it. TC39 explicit resource management gives `using` / `await using`,
`Symbol.dispose` / `Symbol.asyncDispose`, and `DisposableStack`, available natively in Node 24+ (V8
13.4+) and via downlevel in TypeScript 5.2+.

```ts
{
  using lease = stats.selfCount("Pool", "Leases Active");
}   // decrements here, including on throw
```

That covers block-scoped use. It does **not** cover the common case, which is an aggregate held as a
field on a long-lived object — a connection, a session, a running job. There, the counter's lifetime
is the owner's lifetime, and every class in the ownership chain has to opt in:

```ts
class Connection {
  #stack = new DisposableStack();
  constructor(stats: Telemetry) {
    this.#stack.use(stats.selfCount("Pool", "Connections Active"));
  }
  [Symbol.dispose]() { this.#stack.dispose(); }
}
```

…and whoever owns the `Connection` must `using` it, or adopt it into its own stack, recursively. It
is correct, and it is viral: one class in the chain that forgets, and the gauge drifts upward
forever with nothing raised. In languages with refcounting or RAII this is transitive and free.

`FinalizationRegistry` cannot substitute — it is explicitly non-deterministic and not guaranteed to
run, so it cannot be the source of truth for a live count.

### Consequences for this implementation

- **Correctness through `using` / `DisposableStack`.** The documented, supported path.
- **Leak detection through `FinalizationRegistry`.** Not as the decrement — as a development-mode
  warning when an aggregate is collected without having been disposed. Turns silent drift into a log
  line while you are still writing the code.
- **Counters, statuses and events lead the documentation.** Lifetime-bound aggregates are presented
  as an advanced tool for codebases already using `using`, not as the headline.

## Requirements (planned)

Node 20+ for the core API. Node 24+, or TypeScript 5.2+ with downlevel, for the aggregates.
