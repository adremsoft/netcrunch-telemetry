# Contributing

## Ground rules for this repository

**Do not port proprietary source.** Parts of this design originate in AdRem's internal Delphi
codebase. The *design* is what is being published here; that internal source is not, and must not
be translated line by line, vendored, quoted, or committed in any form. Every implementation in
this repository is written fresh against [`spec/v1.md`](spec/v1.md).

**Do not commit deployment details.** No server hostnames, sensor names, node ids, tokens or
endpoint URLs from real installations — not in code, not in tests, not in fixtures. Use the
placeholders from the spec.

**The spec leads.** Behaviour is defined in `spec/v1.md` and demonstrated in `conformance/`. If an
implementation and the spec disagree, that is a spec bug or an implementation bug — never an
undocumented local convention. Change the spec first.

## Adding or changing behaviour

1. Update `spec/v1.md`.
2. Add or update a case under `conformance/cases/`.
3. Make every existing implementation pass, or explicitly record why one cannot.

A change that alters the wire format is a breaking change once any client is in the field. See the
compatibility section of the spec before proposing one.

## Adding a language

A new implementation is expected to provide, at minimum:

- the three primitives — counters, statuses, events
- handle-based counters (resolve the name once, keep the handle)
- a snapshot exporter that emits absolute values on an interval
- a passing run of the conformance suite
- a `README.md` documenting anything the language cannot express faithfully

Lifetime-bound aggregates are strongly encouraged where the language has deterministic destruction,
and should be omitted rather than faked where it does not.
