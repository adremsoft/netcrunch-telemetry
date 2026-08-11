# Conformance suite

Shared fixtures that every implementation must pass. The point is that "compatible with the spec"
means the same thing in every language, and is checked rather than asserted.

Fixtures are specified at the **payload** level — given a snapshot of instrumented values, what JSON
body must the exporter produce. They deliberately say nothing about API shape, so each language is
free to be idiomatic above the exporter.

## Case format

Each file in [`cases/`](cases/) is a single JSON object:

```json
{
  "name": "kebab-case-identifier",
  "description": "What this case pins down.",
  "options": { "retainMinutes": 5, "removeMinutes": 1440 },
  "snapshot": {
    "counters": [ { "object": "Queue", "counter": "Depth", "instance": "inbound", "value": 5 } ],
    "statuses": [ { "key": "Importer", "value": "OK", "message": "idle" } ],
    "events":   [ { "message": "Started" } ]
  },
  "expect": { "...": "the exact JSON body the exporter must POST" }
}
```

`snapshot` is the abstract input — the state of the registry at flush time. An implementation adapts
it to its own types, runs its exporter, and compares the result to `expect`.

Some cases carry `rejects` instead of `expect`: inputs the library must refuse locally rather than
send. These exist because the receiver discards malformed statuses and events **silently**, so a
library that forwards them loses data with no error anywhere.

## Comparison rules

- Object member order is not significant.
- `counters` array order is not significant; match on `path`.
- Numbers compare by value (`5` and `5.0` are equal).
- An implementation MUST NOT emit members absent from `expect`.

## Running

Each language directory provides its own runner over these files. There is no shared harness — a
per-language runner is a few dozen lines and avoids making every implementation depend on a
scripting runtime it would not otherwise need.
