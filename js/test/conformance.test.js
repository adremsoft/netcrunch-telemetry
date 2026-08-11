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

function reject(stats, kind, input) {
  switch (kind) {
    case "counter":
      return () => stats.counter(input.object, input.counter, input.instance).set(input.value);
    case "status":
      return () => stats.status(input.key, input.value);
    case "event":
      return () => stats.event(input.message);
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
