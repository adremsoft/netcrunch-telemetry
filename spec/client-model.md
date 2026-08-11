# Client Model — v1

**Status:** Draft.
**Companion to:** [`v1.md`](v1.md), which specifies the wire format.

This document specifies behaviour *above* the wire — what a client library does in memory before a
payload exists. Nothing here is visible to the NetCrunch receiver; two libraries could satisfy
`v1.md` perfectly and still disagree about everything below. That is what this pins down.

The key words MUST, MUST NOT, SHOULD, SHOULD NOT and MAY are to be interpreted as described in
RFC 2119.

---

## 1. Counter handles

Resolving a counter MUST return a **handle** the caller keeps, not perform a measurement.

Resolving the same object / counter / instance again MUST return the same handle, so that separate
parts of a program instrumenting the same thing converge on one value instead of competing.

The handle carries the current value and is mutated in place. This is the whole point: the cost of an
observation is a numeric mutation, with no name lookup and no allocation on the hot path.
Instrumentation that costs more than that gets removed again.

A handle SHOULD offer at least: set, add, increment, decrement, and one-way `max` / `min` (raise-only
and lower-only respectively).

---

## 2. Lifetime-bound aggregates

Three aggregates make "how many X are currently in state Y" correct by construction, by tying the
decrement to an object's lifetime rather than to a line of code someone has to remember to write.

| Aggregate | Contract |
| --- | --- |
| **SelfCount** | Adds 1 on creation. Subtracts 1 on disposal. |
| **PartCount** | Contributes a movable amount. `set(n)` adjusts the underlying counter by the difference from its previous contribution. Disposal withdraws exactly its current contribution, whatever the counter has done in the meantime. |
| **CategoryCount** | Holds 1 against one **instance** of a counter at a time. `set(x)` decrements the instance being left and increments the one being entered. `set` with the current value is a no-op. `set(null)` and disposal leave the count at zero everywhere. |

CategoryCount MUST use the counter's **instance** to distinguish buckets, so `Workers/By Phase.parsing`
and `Workers/By Phase.writing` are siblings under one counter. Encoding the bucket into the counter
*name* would scatter what is one measurement across unrelated counters.

An aggregate operates through an ordinary handle. Values it manages are indistinguishable, on the
wire, from values set by hand.

---

## 3. Disposal

Disposal MUST be **idempotent**. Disposing twice MUST have the same effect as disposing once.

This is not defensive tidiness. The two ways of triggering disposal — an explicit call and a scope
guard — can both fire on the same object, and a double decrement corrupts the count far more quietly
than a missed one: the value drifts *negative* over time rather than upward, so it never trips a
threshold.

Disposal SHOULD be wired into whatever scope guard the language offers, so that it survives an early
return or a thrown exception: `defer` in Go, `Drop` in Rust, `IDisposable` in C#, a context manager in
Python, `using` in JavaScript and C++ RAII.

A library MAY omit the aggregates where the language cannot support them. It MUST NOT provide them
with semantics weaker than the above — an aggregate whose decrement is best-effort is worse than no
aggregate, because it silently produces numbers that look authoritative.

Where the language cannot enforce the ownership chain — JavaScript most of all, where `using` only
covers block scope — a library SHOULD detect and report undisposed aggregates rather than let a gauge
drift unattributed. Such detection MUST NOT be the decrement itself when the mechanism is not
guaranteed to run.

---

## 4. Participation in the payload

A counter that reaches zero is still a counter. Once resolved, a handle MUST continue to appear in
every payload, including at value zero, until the client is told to discard it.

**Zero and absent mean different things.** "Zero leases are active" is a measurement. "No longer
reporting" is what `v1.md` §3.2 uses to expire a counter — an absent counter disappears from
NetCrunch after the retain period. A library that omitted zeroes would make an idle pool
indistinguishable from a crashed one.

Disposing an aggregate therefore leaves its counter reporting zero. It does not remove it.

---

## 5. Conformance

Cases under [`../conformance/cases/`](../conformance/cases/) carrying an `operations` array test this
document; those carrying a `snapshot` test `v1.md`. See the
[conformance README](../conformance/README.md) for the format.

An implementation that omits the aggregates under §3 skips those cases and MUST report them as
skipped rather than passed.
