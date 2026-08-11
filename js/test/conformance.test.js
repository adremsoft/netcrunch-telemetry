/**
 * Runs the shared conformance suite against this implementation.
 *
 * Fixtures live in ../../conformance/cases and are shared with every other
 * language, so "compatible with the spec" means the same thing in each.
 */

import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import test from "node:test";

import { Telemetry } from "../src/index.js";

const casesDir = new URL("../../conformance/cases/", import.meta.url);

// Placeholder only — never a real installation's endpoint. See CONTRIBUTING.md.
const TEST_ENDPOINT = "https://netcrunch.example/api/rest/1/sensors/example@1/update";

function loadCases() {
  return readdirSync(casesDir)
    .filter((name) => name.endsWith(".json"))
    .sort()
    .map((name) => JSON.parse(readFileSync(new URL(name, casesDir), "utf8")));
}

function connect(options = {}) {
  return new Telemetry({
    endpoint: TEST_ENDPOINT,
    retainMinutes: options.retainMinutes ?? 5,
    removeMinutes: options.removeMinutes ?? 1440,
    detectLeaks: false,
  });
}

/**
 * Applies a fixture snapshot through the public API.
 *
 * Fixtures describe registry state, not calls, so this is the adapter between
 * the two. Anything it has to work around is a sign the API is awkward.
 */
function applySnapshot(stats, snapshot = {}) {
  for (const entry of snapshot.counters ?? []) {
    stats.counter(entry.object, entry.counter, entry.instance).set(entry.value);
  }

  for (const entry of snapshot.statuses ?? []) {
    stats.status(entry.key, entry.value, {
      message: entry.message,
      critical: entry.critical,
      data: entry.data,
    });
  }

  for (const entry of snapshot.events ?? []) {
    stats.event(entry.message, { severity: entry.severity });
  }

  for (const entry of snapshot.data ?? []) {
    stageDataObject(stats, entry);
  }

  for (const entry of snapshot.timestamps ?? []) {
    stats.timestamp(entry.object, entry.counter, entry.statusKey, {
      observedAt: new Date(entry.observedAt),
    });
  }
}

/** Counter order is not significant; identity is the path. */
function sortCounters(payload) {
  if (!payload.counters) return payload;
  const key = (entry) => `${entry.path.object}|${entry.path.counter}|${entry.path.instance ?? ""}`;
  return { ...payload, counters: [...payload.counters].sort((a, b) => key(a).localeCompare(key(b))) };
}

/**
 * Replays an aggregate case. Assertions are interleaved with the operations
 * because the intermediate states are the point — an aggregate that ends up
 * correct having passed through a wrong value is still broken.
 */
function runOperations(stats, operations) {
  const aggregates = new Map();

  const bind = (id, aggregate) => {
    assert.ok(!aggregates.has(id), `id "${id}" is bound twice`);
    aggregates.set(id, aggregate);
  };
  const lookup = (id) => {
    const aggregate = aggregates.get(id);
    assert.ok(aggregate, `no aggregate bound to id "${id}"`);
    return aggregate;
  };

  for (const step of operations) {
    switch (step.op) {
      case "counter": {
        const handle = stats.counter(step.object, step.counter, step.instance);
        if (step.set !== undefined) handle.set(step.set);
        break;
      }
      case "selfCount":
        bind(step.id, stats.selfCount(step.object, step.counter, step.instance));
        break;
      case "partCount":
        bind(step.id, stats.partCount(step.object, step.counter, step.instance));
        break;
      case "category":
        bind(step.id, stats.category(step.object, step.counter));
        break;
      case "set":
        lookup(step.id).set(step.value);
        break;
      case "dispose":
        lookup(step.id).dispose();
        break;
      case "assert": {
        const label = `${step.object}/${step.counter}${step.instance ? `.${step.instance}` : ""}`;
        assert.equal(stats.counter(step.object, step.counter, step.instance).value, step.value, label);
        break;
      }
      default:
        throw new Error(`Unknown operation "${step.op}".`);
    }
  }
}

/**
 * Fixtures describe a data object as one flat record; the API splits it by type.
 * An unknown type reaches `default` and is passed through to the library, which
 * is exactly what the rejection cases need to exercise.
 */
function stageDataObject(stats, entry) {
  const { id, type, ...rest } = entry;
  switch (type) {
    case "table":
      return stats.table(id, rest);
    case "time-series":
      return stats.timeSeries(id, rest);
    case "category":
      return stats.categoryChart(id, rest);
    default:
      // Passed through with the type intact, so an unknown-type rejection fails
      // for the reason the fixture states rather than incidentally.
      return stats.data(id, type, rest);
  }
}

function reject(stats, kind, input) {
  switch (kind) {
    case "counter":
      return () => stats.counter(input.object, input.counter, input.instance).set(input.value);
    case "status":
      return () => stats.status(input.key, input.value);
    case "event":
      return () => stats.event(input.message);
    case "data":
      return () => stageDataObject(stats, input);
    default:
      throw new Error(`Unknown rejection kind "${kind}".`);
  }
}

for (const testCase of loadCases()) {
  if (testCase.rejects) {
    for (const rejection of testCase.rejects) {
      test(`${testCase.name}: ${rejection.reason}`, () => {
        const stats = connect(testCase.options);
        assert.throws(reject(stats, rejection.kind, rejection.input), {
          message: /.+/,
        });
      });
    }
    continue;
  }

  if (testCase.operations) {
    test(testCase.name, () => {
      const stats = connect(testCase.options);
      runOperations(stats, testCase.operations);

      // An aggregate case may also pin the payload, tying in-memory behaviour
      // back to what actually goes over the wire.
      if (testCase.expect) {
        const actual = JSON.parse(JSON.stringify(stats.buildPayload()));
        assert.deepStrictEqual(sortCounters(actual), sortCounters(testCase.expect));
      }
    });
    continue;
  }

  test(testCase.name, () => {
    const stats = connect(testCase.options);
    applySnapshot(stats, testCase.snapshot);

    const built = stats.buildPayload(
      testCase.options?.snapshotAt ? { snapshotAt: new Date(testCase.options.snapshotAt) } : {}
    );

    // Round-trip so the comparison happens in the shape that goes over the wire,
    // not as live JavaScript objects.
    const actual = JSON.parse(JSON.stringify(built));

    assert.deepStrictEqual(sortCounters(actual), sortCounters(testCase.expect));
  });
}
