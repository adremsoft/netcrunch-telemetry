/**
 * The registry: staging values in memory, and turning a snapshot of them into a
 * payload.
 *
 * Instrumentation only mutates memory. Nothing in the request path touches the
 * network — a separate flush takes a snapshot and sends it. That keeps the cost of
 * an observation to a numeric mutation, and it means one request carries every
 * value, which matters because the receiver caps pending payloads per sensor and
 * discards the overflow without reporting it.
 */

import { CategoryCount, CounterHandle, LeakDetector, PartCount, SelfCount } from "./handles.js";
import { postPayload } from "./transport.js";
import {
  assertCounterInstance,
  assertCounterPath,
  assertCounterValue,
  assertDataObject,
  assertEventMessage,
  DATA_TYPE_MEMBERS,
  assertStatusKey,
  assertStatusValue,
} from "./validate.js";

// NUL, so a counter named "A B"/"C" cannot collide with "A"/"B C".
const KEY_SEPARATOR = "\u0000";

/** ISO 8601 without the milliseconds the wire format has no use for. */
function toIsoSeconds(date) {
  return date.toISOString().replace(/\.\d{3}Z$/, "Z");
}

export class Telemetry {
  #counters = new Map();
  #statuses = new Map();
  #timestamps = new Map();
  #dataObjects = new Map();
  #events = [];
  #timer = null;
  #inFlight = null;

  /**
   * @param {object} options
   * @param {string} options.endpoint         URL from the Telemetry sensor form. Treat as a secret.
   * @param {number} [options.flushSeconds=0] Auto-flush interval; 0 means flush only when asked.
   * @param {number} [options.retainMinutes=5]  Must exceed the flush interval, or values expire between sends.
   * @param {number} [options.removeMinutes=1440]
   * @param {number} [options.timeoutMs=30000]
   * @param {number} [options.maxRetries=3]
   * @param {(error: Error) => void} [options.onError]  Receives failures from automatic flushes.
   * @param {boolean} [options.detectLeaks]   Warn on undisposed aggregates. Defaults on outside production.
   * @param {(message: string) => void} [options.onLeak]
   */
  constructor(options = {}) {
    const {
      endpoint,
      token,
      flushSeconds = 0,
      retainMinutes = 5,
      removeMinutes = 1440,
      timeoutMs = 30_000,
      maxRetries = 3,
      onError,
      detectLeaks = process.env.NODE_ENV !== "production",
      onLeak = (message) => console.warn(message),
    } = options;

    if (typeof endpoint !== "string" || endpoint === "") {
      throw new TypeError("endpoint is required — copy it from the Telemetry sensor form.");
    }
    let parsed;
    try {
      parsed = new URL(endpoint);
    } catch {
      throw new TypeError("endpoint must be an absolute http or https URL.");
    }
    if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
      throw new TypeError("endpoint must be an absolute http or https URL.");
    }

    if (flushSeconds > 0 && retainMinutes * 60 <= flushSeconds) {
      throw new RangeError(
        `retainMinutes (${retainMinutes}) must exceed flushSeconds (${flushSeconds}), ` +
          "or values expire between sends."
      );
    }

    if (token !== undefined && (typeof token !== "string" || token === "")) {
      throw new TypeError("token must be a non-empty string when provided.");
    }

    this.endpoint = endpoint;
    // Not enumerable: a bare console.log(stats) is exactly how a credential ends
    // up in a log, and there is no reason for the token to appear there.
    Object.defineProperty(this, "token", { value: token, enumerable: false, writable: false });
    this.retainMinutes = retainMinutes;
    this.removeMinutes = removeMinutes;
    this.flushSeconds = flushSeconds;
    this.timeoutMs = timeoutMs;
    this.maxRetries = maxRetries;
    this.onError = onError;
    this.leakDetector = detectLeaks ? new LeakDetector(onLeak) : null;

    if (flushSeconds > 0) this.start();
  }

  // -- staging ------------------------------------------------------------

  /**
   * Resolves a counter handle. Calling this again with the same path returns the
   * same handle, so it is safe (and intended) to resolve once and keep it.
   */
  counter(object, counter, instance) {
    assertCounterPath(object, counter);
    assertCounterInstance(instance);

    const key = object + KEY_SEPARATOR + counter + KEY_SEPARATOR + (instance ?? "");
    let handle = this.#counters.get(key);
    if (handle === undefined) {
      handle = new CounterHandle(object, counter, instance ?? undefined);
      this.#counters.set(key, handle);
    }
    return handle;
  }

  /**
   * Stages a state with an optional message. Statuses are what NetCrunch alerting
   * acts on — a counter on its own will not raise anything.
   */
  status(key, value, options = {}) {
    assertStatusKey(key);
    assertStatusValue(value);

    const status = { value };
    if (options.message !== undefined && options.message !== "") status.message = options.message;
    if (options.critical) status.critical = true;
    if (options.data !== undefined && options.data !== null) status.data = options.data;

    this.#statuses.set(key, status);
    return this;
  }

  /** Stages a discrete occurrence. Events accumulate and are cleared once sent. */
  event(message, options = {}) {
    assertEventMessage(message);

    const event = { message };
    if (options.severity !== undefined) event.severity = options.severity;

    this.#events.push(event);
    return this;
  }

  /**
   * Records when something last happened.
   *
   * The wire format has no timestamp type, and a raw clock value means nothing
   * outside the process that produced it. So this becomes two things: an age in
   * seconds, which an alert threshold can be set on, and a status message carrying
   * the absolute time, for a person to read. Age is computed at flush time.
   */
  timestamp(object, counter, statusKey, options = {}) {
    assertCounterPath(object, counter);
    assertStatusKey(statusKey);

    const { observedAt = new Date(), statusValue = "OK" } = options;
    if (!(observedAt instanceof Date) || Number.isNaN(observedAt.getTime())) {
      throw new TypeError("observedAt must be a valid Date.");
    }
    assertStatusValue(statusValue);

    this.#timestamps.set(statusKey, { object, counter, statusKey, observedAt, statusValue });
    return this;
  }

  // -- data objects -------------------------------------------------------

  /**
   * Stages a table, chart or series rendered on the sensor's page.
   *
   * `id` is the object's identity across payloads — staging the same id again
   * replaces it. Unlike a counter, a data object is a whole view each time; there
   * is no incremental form.
   *
   * A data object's own `status` is part of what is displayed. Alerting acts on
   * statuses, not on this — a red table is not an alert.
   */
  data(id, type, options = {}) {
    assertDataObject(id, type, options);

    const object = { type };
    for (const member of DATA_TYPE_MEMBERS[type]) object[member] = options[member];

    if (options.name !== undefined) object.name = options.name;
    // seriesName labels a plotted series; a table has no series to label.
    if (options.seriesName !== undefined && type !== "table") object.seriesName = options.seriesName;
    if (options.message !== undefined) object.message = options.message;
    if (options.status !== undefined) object.status = options.status;

    this.#dataObjects.set(id, object);
    return this;
  }

  /** @param {{name?: string, columns: unknown[], rows: unknown[][], message?: string, status?: string}} options */
  table(id, options = {}) {
    return this.data(id, "table", options);
  }

  /** @param {{name?: string, seriesName?: string, timestamps: number[], values: number[], message?: string, status?: string}} options */
  timeSeries(id, options = {}) {
    return this.data(id, "time-series", options);
  }

  /**
   * A labelled bar chart. Named `categoryChart` rather than `category` because
   * `category()` is the lifetime-bound aggregate — different thing entirely.
   *
   * @param {{name?: string, seriesName?: string, categories: string[], values: number[], message?: string, status?: string}} options
   */
  categoryChart(id, options = {}) {
    return this.data(id, "category", options);
  }

  // -- lifetime-bound aggregates -----------------------------------------

  /** Holds +1 until disposed. `using lease = stats.selfCount("Pool", "Leases Active")`. */
  selfCount(object, counter, instance) {
    return new SelfCount(this.counter(object, counter, instance), this.leakDetector);
  }

  /** Contributes a movable amount, withdrawn in full on dispose. */
  partCount(object, counter, instance) {
    return new PartCount(this.counter(object, counter, instance), this.leakDetector);
  }

  /** Holds +1 against one instance at a time, moving it as the value changes. */
  category(object, counter) {
    assertCounterPath(object, counter);
    return new CategoryCount(
      (instance) => this.counter(object, counter, instance),
      object,
      counter,
      this.leakDetector
    );
  }

  // -- payload ------------------------------------------------------------

  /**
   * Builds the payload a flush would post, without sending it. Members with
   * nothing in them are omitted rather than sent empty.
   */
  buildPayload({ snapshotAt = new Date() } = {}) {
    const payload = { retain: this.retainMinutes, remove: this.removeMinutes };

    const counters = [];
    for (const handle of this.#counters.values()) {
      const path = { object: handle.object, counter: handle.counter };
      if (handle.instance !== undefined && handle.instance !== "") path.instance = handle.instance;
      counters.push({ path, value: handle.value });
    }

    // A timestamp contributes to both collections, so it is expanded here rather
    // than at the call site — the age is only meaningful against this snapshot.
    const statuses = new Map(this.#statuses);
    for (const stamp of this.#timestamps.values()) {
      counters.push({
        path: { object: stamp.object, counter: stamp.counter },
        value: Math.round((snapshotAt.getTime() - stamp.observedAt.getTime()) / 1000),
      });
      statuses.set(stamp.statusKey, {
        value: stamp.statusValue,
        message: toIsoSeconds(stamp.observedAt),
      });
    }

    if (counters.length > 0) payload.counters = counters;
    if (statuses.size > 0) payload.statuses = Object.fromEntries(statuses);
    if (this.#events.length > 0) payload.events = [...this.#events];
    if (this.#dataObjects.size > 0) payload.data = Object.fromEntries(this.#dataObjects);

    return payload;
  }

  // -- sending ------------------------------------------------------------

  /**
   * Posts everything staged as a single request.
   *
   * Concurrent calls share one in-flight request rather than queueing a second —
   * two snapshots of the same absolute values racing each other would achieve
   * nothing except doubling the load.
   *
   * Events are cleared on success; counters and statuses are kept, so a
   * long-running process keeps reporting current values without restating them.
   */
  async flush({ snapshotAt = new Date(), signal } = {}) {
    if (this.#inFlight) return this.#inFlight;

    const payload = this.buildPayload({ snapshotAt });
    if (
      payload.counters === undefined &&
      payload.statuses === undefined &&
      payload.events === undefined &&
      payload.data === undefined
    ) {
      return;
    }

    const sentEventCount = this.#events.length;

    this.#inFlight = (async () => {
      try {
        await postPayload(this.endpoint, payload, {
          timeoutMs: this.timeoutMs,
          maxRetries: this.maxRetries,
          signal,
          token: this.token,
        });
        // Splice rather than clear: events staged while this was in flight have
        // not been sent, and dropping them would lose them silently.
        this.#events.splice(0, sentEventCount);
      } finally {
        this.#inFlight = null;
      }
    })();

    return this.#inFlight;
  }

  /** Starts the auto-flush timer. Unreferenced, so it never holds the process open. */
  start() {
    if (this.#timer !== null || this.flushSeconds <= 0) return this;
    this.#timer = setInterval(() => {
      this.flush().catch((error) => {
        if (this.onError) this.onError(error);
        else console.warn(error.message);
      });
    }, this.flushSeconds * 1000);
    this.#timer.unref?.();
    return this;
  }

  stop() {
    if (this.#timer !== null) {
      clearInterval(this.#timer);
      this.#timer = null;
    }
    return this;
  }

  /** Stops the timer and flushes one last time. */
  async close() {
    this.stop();
    await this.flush();
  }

  /** Discards everything staged. */
  clear() {
    this.#counters.clear();
    this.#statuses.clear();
    this.#timestamps.clear();
    this.#dataObjects.clear();
    this.#events.length = 0;
    return this;
  }
}
