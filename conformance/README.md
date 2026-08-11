# Conformance suite

Shared fixtures that every implementation must pass. The point is that "compatible with the spec"
means the same thing in every language, and is checked rather than asserted.

There are two kinds of case:

| Kind | Key | Tests | Against |
| --- | --- | --- | --- |
| Payload | `snapshot` | What JSON body an exporter produces from a given registry state. | [`spec/v1.md`](../spec/v1.md) |
| Aggregate | `operations` | How counter handles and lifetime-bound aggregates behave in memory. | [`spec/client-model.md`](../spec/client-model.md) |

Both say as little as possible about API shape, so each language stays free to be idiomatic.

## Payload cases

Each file in [`cases/`](cases/) is a single JSON object:

```json
{
  "name": "kebab-case-identifier",
  "description": "What this case pins down.",
  "options": { "retainMinutes": 5, "removeMinutes": 1440 },
  "snapshot": {
    "counters": [ { "object": "Queue", "counter": "Depth", "instance": "inbound", "value": 5 } ],
    "statuses": [ { "key": "Importer", "value": "OK", "message": "idle" } ],
    "events":   [ { "message": "Started" } ],
    "data":     [ { "id": "t", "type": "table", "columns": ["A"], "rows": [["1"]] } ]
  },
  "expect": { "...": "the exact JSON body the exporter must POST" }
}
```

`snapshot` is the abstract input — the state of the registry at flush time. An implementation adapts
it to its own types, runs its exporter, and compares the result to `expect`.

Some cases carry `rejects` instead of `expect`: inputs the library must refuse locally rather than
send. These exist because the receiver discards malformed statuses and events **silently**, so a
library that forwards them loses data with no error anywhere.

## Aggregate cases

A case with an `operations` array is a script applied in order, with assertions interleaved. The
intermediate states are the point — an aggregate that ends up correct having passed through a wrong
value is still broken.

```json
{
  "name": "kebab-case-identifier",
  "kind": "aggregate",
  "description": "What this case pins down.",
  "operations": [
    { "op": "counter",   "object": "Cache", "counter": "Entries", "set": 1000 },
    { "op": "partCount", "id": "shard", "object": "Cache", "counter": "Entries" },
    { "op": "set",       "id": "shard", "value": 5 },
    { "op": "assert",    "object": "Cache", "counter": "Entries", "value": 1005 },
    { "op": "dispose",   "id": "shard" },
    { "op": "assert",    "object": "Cache", "counter": "Entries", "value": 1000 }
  ]
}
```

| `op` | Meaning |
| --- | --- |
| `counter` | Set a plain counter directly, to establish a baseline the aggregates work against. |
| `selfCount`, `partCount`, `category` | Create an aggregate and bind it to `id` for the rest of the script. |
| `set` | `PartCount` takes a number; `CategoryCount` takes an instance name or `null`. Dispatch on what `id` refers to. |
| `dispose` | Dispose the aggregate bound to `id`. Repeats are deliberate — disposal is required to be idempotent. |
| `assert` | The counter at `object` / `counter` / optional `instance` currently holds `value`. |

An aggregate case MAY also carry a case-level `expect`, checked against the payload once the script
finishes — that is what ties in-memory behaviour back to what actually goes over the wire.

**Skipping is not passing.** An implementation whose language cannot support the aggregates
(`spec/client-model.md` §3) skips these cases and must report them as skipped. Reporting them as
passed, or omitting them from the count, would make an unimplemented feature look verified.

## Comparison rules

- Object member order is not significant.
- `counters` array order is not significant; match on `path`.
- Numbers compare by value (`5` and `5.0` are equal).
- An implementation MUST NOT emit members absent from `expect`.

## Running

Each language directory provides its own runner over these files. There is no shared harness — a
per-language runner is a few dozen lines and avoids making every implementation depend on a
scripting runtime it would not otherwise need.
