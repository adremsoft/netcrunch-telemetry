/**
 * Counter handles and lifetime-bound aggregates.
 *
 * A counter is resolved once and kept, so the hot path is a numeric mutation with
 * no name lookup and no allocation. This is the difference between instrumentation
 * you can afford inside a loop and instrumentation you take back out later.
 *
 * The aggregates make "how many X are currently in state Y" correct by
 * construction: the decrement is tied to the object's lifetime instead of being
 * written by hand in a finally block that someone will eventually forget.
 */

import { assertCounterValue } from "./validate.js";

const hasDisposeSymbol = typeof Symbol.dispose === "symbol";

/** A single counter. Cheap to mutate, resolved once by the registry. */
export class CounterHandle {
  #value = 0;

  constructor(object, counter, instance) {
    this.object = object;
    this.counter = counter;
    this.instance = instance;
  }

  get value() {
    return this.#value;
  }

  set(value) {
    assertCounterValue(value);
    this.#value = value;
    return this;
  }

  add(delta) {
    assertCounterValue(delta);
    this.#value += delta;
    return this;
  }

  inc(by = 1) {
    return this.add(by);
  }

  dec(by = 1) {
    return this.add(-by);
  }

  /** Raises the counter to `value` if that is higher. Leaves it alone otherwise. */
  max(value) {
    assertCounterValue(value);
    if (value > this.#value) this.#value = value;
    return this;
  }

  /** Lowers the counter to `value` if that is lower. */
  min(value) {
    assertCounterValue(value);
    if (value < this.#value) this.#value = value;
    return this;
  }

  reset() {
    this.#value = 0;
    return this;
  }
}

/**
 * Shared plumbing for the aggregates.
 *
 * Disposal is idempotent, because the two ways of triggering it — an explicit
 * `dispose()` and a `using` block going out of scope — can both happen to the
 * same object, and a double decrement would corrupt the count far more quietly
 * than a missing one.
 *
 * Leak watching is started by the subclass rather than here: `handle` is assigned
 * after `super()` returns, and the watcher needs it to say which counter is stuck.
 */
class Aggregate {
  #disposed = false;

  constructor(onDispose, leakDetector) {
    this._onDispose = onDispose;
    this._leakDetector = leakDetector;
  }

  get disposed() {
    return this.#disposed;
  }

  dispose() {
    if (this.#disposed) return;
    this.#disposed = true;
    this._leakDetector?.release(this);
    this._onDispose();
  }

  _watch(label) {
    this._leakDetector?.watch(this, label);
  }
}

if (hasDisposeSymbol) {
  Aggregate.prototype[Symbol.dispose] = function () {
    this.dispose();
  };
}

/** Holds +1 for as long as it is alive. `using lease = stats.selfCount(...)`. */
export class SelfCount extends Aggregate {
  constructor(handle, leakDetector) {
    handle.inc();
    super(() => handle.dec(), leakDetector);
    this.handle = handle;
    this._watch(`SelfCount on ${handle.object}/${handle.counter}`);
  }
}

/**
 * Contributes a movable amount to a counter and takes exactly that amount back
 * when disposed, however many times the value changed in between.
 */
export class PartCount extends Aggregate {
  #contribution = 0;

  constructor(handle, leakDetector) {
    super(() => this.#withdraw(), leakDetector);
    this.handle = handle;
    this._watch(`PartCount on ${handle.object}/${handle.counter}`);
  }

  set(value) {
    assertCounterValue(value);
    if (value === this.#contribution) return this;
    this.handle.add(value - this.#contribution);
    this.#contribution = value;
    return this;
  }

  get contribution() {
    return this.#contribution;
  }

  #withdraw() {
    if (this.#contribution === 0) return;
    this.handle.add(-this.#contribution);
    this.#contribution = 0;
  }
}

/**
 * Holds +1 against one instance of a counter at a time, moving it as the value
 * changes. "How many workers are in each phase" stays consistent without anyone
 * having to remember to decrement the phase being left.
 *
 * The instance is the natural home for this in NetCrunch's counter model, so
 * `Workers/By Phase.parsing` and `Workers/By Phase.writing` are siblings rather
 * than unrelated names.
 */
export class CategoryCount extends Aggregate {
  #current = null;

  constructor(resolve, object, counter, leakDetector) {
    super(() => this.#clear(), leakDetector);
    this._resolve = resolve;
    this.object = object;
    this.counter = counter;
    this._watch(`CategoryCount on ${object}/${counter}`);
  }

  set(instance) {
    if (instance !== null && instance !== undefined && typeof instance !== "string") {
      throw new TypeError(`Category instance must be a string or null (got ${typeof instance}).`);
    }
    const next = instance === undefined ? null : instance;
    if (next === this.#current) return this;

    this.#clear();
    if (next !== null) {
      this._resolve(next).inc();
      this.#current = next;
    }
    return this;
  }

  get current() {
    return this.#current;
  }

  #clear() {
    if (this.#current === null) return;
    this._resolve(this.#current).dec();
    this.#current = null;
  }
}

/**
 * Warns when an aggregate is garbage collected without having been disposed.
 *
 * This is not the decrement — FinalizationRegistry is explicitly non-deterministic
 * and not guaranteed to run, so it cannot be the source of truth for a live count.
 * It is a development aid: without it, a forgotten dispose shows up as a gauge that
 * drifts upward over days, with nothing pointing at the code responsible.
 */
export class LeakDetector {
  #registry;

  constructor(report) {
    this.#registry = new FinalizationRegistry((label) => {
      report(
        `NetCrunch telemetry: ${label} was garbage collected without being disposed, ` +
          "so its contribution is stuck. Use `using`, or call dispose() explicitly."
      );
    });
  }

  watch(aggregate, label) {
    this.#registry.register(aggregate, label, aggregate);
  }

  release(aggregate) {
    this.#registry.unregister(aggregate);
  }
}
