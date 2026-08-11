import assert from "node:assert/strict";
import test from "node:test";

import { Telemetry } from "../src/index.js";

/**
 * `using` needs V8 13.4+, which lands in Node 24. On anything older the scenario
 * module cannot even be parsed, so support is probed before it is imported.
 */
const supportsUsing = (() => {
  try {
    new Function("using disposable = { [Symbol.dispose]() {} };");
    return true;
  } catch {
    return false;
  }
})();

const options = { skip: supportsUsing ? false : "requires explicit resource management (Node 24+)" };

test("using decrements at scope exit", options, async () => {
  const { runUsingScenario } = await import("./fixtures/using-scenario.js");
  const stats = new Telemetry({ endpoint: "https://netcrunch.example/x", detectLeaks: false });

  assert.deepStrictEqual(runUsingScenario(stats), {
    insideOuter: 1,
    insideInner: 2,
    afterInner: 1,
    afterOuter: 0,
  });
});

test("using decrements even when the block throws", options, async () => {
  const { runUsingWithThrow } = await import("./fixtures/using-scenario.js");
  const stats = new Telemetry({ endpoint: "https://netcrunch.example/x", detectLeaks: false });

  assert.equal(runUsingWithThrow(stats), 0);
});
