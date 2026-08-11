# NetCrunch Telemetry

Lightweight instrumentation libraries that push metrics, states and events from your application into NetCrunch.

> **Status: early.** The wire format is specified and implemented server-side in NetCrunch 16. The PowerShell module is alpha and passes the conformance suite; the other languages are not started, and the interfaces shown for them below are intended design rather than shipping API.

## Why not just OpenTelemetry?

Auto-instrumentation tells you which HTTP handler got slow. It does not tell you whether last night's billing run finished, how many connections are open right now, or what phase your importer is in.

The gap is structural: **OTLP has no notion of state.** It carries metrics, logs and traces, and you infer health downstream with query languages and alert rules. NetCrunch works the other way around — an object has a state, states drive alerts, alerts drive escalation. This library exposes that directly.

NetCrunch also accepts OTLP metrics and logs through a gateway. If you already have OpenTelemetry, use it. This is for the things it cannot express.

## The model

Three primitives:

- **Counters** — numbers. Queue depth, requests served, bytes written.
- **Statuses** — a state with a message. `"Error" / "Service not responding"`. This is what alerts fire on.
- **Events** — discrete things that happened, with a message.
- **Data objects** — a table or chart rendered on the sensor's page, with no dashboard to configure.

Four design choices that make it cheap enough to leave in production code:

**A counter is a handle, not a call.** You resolve the name once and keep the handle. The hot path is an atomic increment — no string hashing, no attribute-set allocation per observation.

```js
const pending = stats.counter("Queue", "Depth", "inbound");
pending.inc();   // this is the whole cost
```

**Snapshots, not streams.** Instrumentation only mutates memory. A separate exporter takes a snapshot on an interval and sends absolute current values. Nothing in your request path touches the network, and because every payload is absolute rather than incremental, **retries and duplicate delivery are safe**.

**Lifetime-bound aggregates.** "How many X are currently in state Y" is correct by construction — the decrement is tied to object lifetime rather than written by hand.

```js
using lease = stats.selfCount("Pool", "Leases Active");
// counter decrements at scope exit, including on throw
```

**States and timestamps are first-class.** `"phase: reindexing"` and `"last successful sync: 14:32"` are the things people actually put on a dashboard, and neither is a number.

```js
stats.status("Importer", "OK", { message: "batch 41/120" });
stats.event("Import failed", { severity: "error" });

stats.table("services", { columns: ["Name", "State"], rows: stopped });
```

## Repository layout

```
spec/          Normative wire format. The source of truth.
conformance/   Shared fixtures every implementation must pass.
js/            Node.js / TypeScript
python/        Python
dotnet/        .NET
go/            Go
powershell/    PowerShell module
```

One repository on purpose: the spec and every implementation move together, and the conformance suite is shared rather than reinvented per language.

## Implementation status

| Language | Status |
| --- | --- |
| Spec v1 | Draft — authentication still open before freeze |
| Client model | Draft — handles, aggregates, disposal |
| Conformance suite | Draft — 11 cases |
| [PowerShell](powershell/) | **Alpha** — passes conformance on 5.1 and 7 |
| [Node.js](js/) | **Alpha** — passes conformance; includes the aggregates |
| Python | Not started |
| .NET | Not started |
| Go | Not started |

Lifetime-bound aggregates depend on deterministic destruction. They map cleanly to Go (`defer`), Rust (`Drop`), C# (`IDisposable`), Python (context managers) and C++ (RAII). In JavaScript they require TC39 explicit resource management (`using` / `DisposableStack`, Node 24+ or the TypeScript downlevel) and carry an ownership obligation the runtime will not enforce for you — see [js/README.md](js/README.md).

## Getting data into NetCrunch

Create a **Telemetry** sensor on the node that should represent your application. The sensor form shows the endpoint URL to point the exporter at. See [spec/v1.md](spec/v1.md) for the transport details.

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
