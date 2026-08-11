/**
 * Push metrics, states and events from a Node.js application into NetCrunch.
 *
 * See spec/v1.md for the wire format these types describe.
 */

export interface TelemetryOptions {
  /** URL from the Telemetry sensor form. Treat it as a secret — it is never logged. */
  endpoint: string;
  /** Auto-flush interval in seconds. 0 (the default) flushes only when asked. */
  flushSeconds?: number;
  /** Must exceed the flush interval, or values expire between sends. Default 5. */
  retainMinutes?: number;
  /** How long an object survives with no data before removal. Default 1440. */
  removeMinutes?: number;
  /** Default 30000. */
  timeoutMs?: number;
  /** Default 3. Retries are safe because payloads carry absolute values. */
  maxRetries?: number;
  /** Receives failures from automatic flushes, which have nowhere else to go. */
  onError?: (error: Error) => void;
  /** Warn when an aggregate is collected undisposed. Defaults on outside production. */
  detectLeaks?: boolean;
  onLeak?: (message: string) => void;
}

export interface StatusOptions {
  message?: string;
  critical?: boolean;
  data?: unknown;
}

export interface EventOptions {
  severity?: "info" | "warning" | "error";
}

export interface TimestampOptions {
  /** Defaults to now. Age is computed at flush time, not here. */
  observedAt?: Date;
  /** Value of the companion status. Default "OK". */
  statusValue?: string;
}

export interface CounterPath {
  object: string;
  counter: string;
  instance?: string;
}

export interface CounterPayloadEntry {
  path: CounterPath;
  value: number;
}

export interface StatusPayloadEntry {
  value: string;
  message?: string;
  critical?: boolean;
  data?: unknown;
}

export interface EventPayloadEntry {
  message: string;
  severity?: string;
}

export type DataObjectType = "table" | "time-series" | "category";

interface DataObjectCommon {
  /** Display title. */
  name?: string;
  /** A line of explanation shown with the object. */
  message?: string;
  /** The object's own state — "OK", "Warning", "Error". Not an alert; send a status for that. */
  status?: string;
}

export interface TableOptions extends DataObjectCommon {
  columns: unknown[];
  /** One array per row, each the same length as `columns`. */
  rows: unknown[][];
}

export interface TimeSeriesOptions extends DataObjectCommon {
  /** Label for the plotted series. */
  seriesName?: string;
  /** Epoch milliseconds. Same length as `values`. */
  timestamps: number[];
  values: number[];
}

export interface CategoryChartOptions extends DataObjectCommon {
  seriesName?: string;
  /** Same length as `values`. */
  categories: string[];
  values: number[];
}

export type DataPayloadEntry = { type: DataObjectType } & Record<string, unknown>;

export interface Payload {
  retain: number;
  remove: number;
  counters?: CounterPayloadEntry[];
  statuses?: Record<string, StatusPayloadEntry>;
  events?: EventPayloadEntry[];
  data?: Record<string, DataPayloadEntry>;
}

/** A resolved counter. Resolve once, keep the handle, mutate it on the hot path. */
export declare class CounterHandle {
  readonly object: string;
  readonly counter: string;
  readonly instance?: string;
  readonly value: number;
  set(value: number): this;
  add(delta: number): this;
  inc(by?: number): this;
  dec(by?: number): this;
  /** Raises to `value` if higher. */
  max(value: number): this;
  /** Lowers to `value` if lower. */
  min(value: number): this;
  reset(): this;
}

/** Base of the lifetime-bound aggregates. Disposal is idempotent. */
export declare class Aggregate implements Disposable {
  readonly disposed: boolean;
  dispose(): void;
  [Symbol.dispose](): void;
}

/** Holds +1 for as long as it is alive. */
export declare class SelfCount extends Aggregate {
  readonly handle: CounterHandle;
}

/** Contributes a movable amount, withdrawn in full on dispose. */
export declare class PartCount extends Aggregate {
  readonly handle: CounterHandle;
  readonly contribution: number;
  set(value: number): this;
}

/** Holds +1 against one instance at a time, moving it as the value changes. */
export declare class CategoryCount extends Aggregate {
  readonly object: string;
  readonly counter: string;
  readonly current: string | null;
  set(instance: string | null): this;
}

/** A send failure. Never carries the endpoint — see spec/v1.md section 1. */
export declare class TelemetryError extends Error {
  readonly name: "TelemetryError";
  /** HTTP status, or 0 for a transport-level failure. */
  readonly statusCode: number;
}

export declare class Telemetry {
  constructor(options: TelemetryOptions);

  readonly endpoint: string;
  readonly retainMinutes: number;
  readonly removeMinutes: number;
  readonly flushSeconds: number;

  /** Resolves a handle. The same path always returns the same handle. */
  counter(object: string, counter: string, instance?: string): CounterHandle;

  /** Stages a state. This — not a counter — is what NetCrunch alerting acts on. */
  status(key: string, value: string, options?: StatusOptions): this;

  /** Stages a discrete occurrence. Cleared once sent. */
  event(message: string, options?: EventOptions): this;

  /** Records when something last happened, as an age counter plus a readable status. */
  timestamp(object: string, counter: string, statusKey: string, options?: TimestampOptions): this;

  /** Stages a table rendered on the sensor's page. Re-using an id replaces it. */
  table(id: string, options: TableOptions): this;
  /** Stages a time series chart. Timestamps are epoch milliseconds. */
  timeSeries(id: string, options: TimeSeriesOptions): this;
  /** Stages a labelled bar chart. Named to avoid colliding with `category()`, the aggregate. */
  categoryChart(id: string, options: CategoryChartOptions): this;
  /** Generic form behind the three above. Rejects any type outside `DataObjectType`. */
  data(id: string, type: DataObjectType, options: Record<string, unknown>): this;

  selfCount(object: string, counter: string, instance?: string): SelfCount;
  partCount(object: string, counter: string, instance?: string): PartCount;
  category(object: string, counter: string): CategoryCount;

  /** Builds the payload a flush would post, without sending it. */
  buildPayload(options?: { snapshotAt?: Date }): Payload;

  /** Posts everything staged as one request. Concurrent calls share one flight. */
  flush(options?: { snapshotAt?: Date; signal?: AbortSignal }): Promise<void>;

  start(): this;
  stop(): this;
  /** Stops the timer and flushes once more. */
  close(): Promise<void>;
  /** Discards everything staged. */
  clear(): this;
}
