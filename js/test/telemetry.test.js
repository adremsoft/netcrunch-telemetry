import assert from "node:assert/strict";
import http from "node:http";
import test from "node:test";

import { Telemetry, TelemetryError } from "../src/index.js";

const ENDPOINT = "https://netcrunch.example/api/rest/1/sensors/example@1/update";

const connect = (options = {}) =>
  new Telemetry({ endpoint: ENDPOINT, detectLeaks: false, ...options });

/** Starts a throwaway server whose URL embeds a recognisable secret. */
function startServer(handler) {
  return new Promise((resolve) => {
    const requests = [];
    const server = http.createServer((req, res) => {
      const chunks = [];
      req.on("data", (chunk) => chunks.push(chunk));
      req.on("end", () => {
        requests.push(JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}"));
        handler(res, requests.length);
      });
    });
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address();
      resolve({
        server,
        requests,
        url: `http://127.0.0.1:${port}/api/rest/1/sensors/SENSORSECRET@1/update`,
        close: () => new Promise((done) => server.close(done)),
      });
    });
  });
}

// -- construction -----------------------------------------------------------

test("endpoint must be an absolute http or https URL", () => {
  assert.throws(() => new Telemetry({}), /endpoint is required/);
  assert.throws(() => new Telemetry({ endpoint: "not-a-url" }), /absolute http or https/);
  assert.throws(() => new Telemetry({ endpoint: "ftp://host/x" }), /absolute http or https/);
});

test("retain must outlast the flush interval", () => {
  // 60s flush against a 1 minute retain would let values expire between sends.
  assert.throws(
    () => new Telemetry({ endpoint: ENDPOINT, flushSeconds: 60, retainMinutes: 1 }),
    /must exceed flushSeconds/
  );
});

// -- counter handles --------------------------------------------------------

test("the same path always resolves to the same handle", () => {
  const stats = connect();
  const a = stats.counter("Queue", "Depth", "inbound");
  const b = stats.counter("Queue", "Depth", "inbound");
  assert.equal(a, b);
  assert.notEqual(a, stats.counter("Queue", "Depth", "outbound"));
});

test("max and min only move the value one way", () => {
  const stats = connect();
  const peak = stats.counter("SNMP", "Peak ms");
  peak.max(120).max(90).max(200);
  assert.equal(peak.value, 200);

  const floor = stats.counter("SNMP", "Floor ms").set(100);
  floor.min(150).min(40).min(70);
  assert.equal(floor.value, 40);
});

// -- lifetime-bound aggregates ---------------------------------------------

test("selfCount holds +1 until disposed", () => {
  const stats = connect();
  const handle = stats.counter("Pool", "Leases Active");

  const first = stats.selfCount("Pool", "Leases Active");
  const second = stats.selfCount("Pool", "Leases Active");
  assert.equal(handle.value, 2);

  first.dispose();
  assert.equal(handle.value, 1);
  second.dispose();
  assert.equal(handle.value, 0);
});

test("disposing twice does not decrement twice", () => {
  // `using` and an explicit dispose() can both fire on the same object. A double
  // decrement would corrupt the count far more quietly than a missing one.
  const stats = connect();
  const handle = stats.counter("Pool", "Leases Active");
  const lease = stats.selfCount("Pool", "Leases Active");

  lease.dispose();
  lease.dispose();
  lease[Symbol.dispose]();

  assert.equal(handle.value, 0);
  assert.equal(lease.disposed, true);
});

test("partCount withdraws exactly what it contributed", () => {
  const stats = connect();
  const handle = stats.counter("Cache", "Entries").set(1000);
  const part = stats.partCount("Cache", "Entries");

  part.set(5);
  part.set(9);
  part.set(3);
  assert.equal(handle.value, 1003);
  assert.equal(part.contribution, 3);

  part.dispose();
  assert.equal(handle.value, 1000);
});

test("category moves its +1 between instances", () => {
  const stats = connect();
  const phase = stats.category("Workers", "By Phase");

  phase.set("parsing");
  assert.equal(stats.counter("Workers", "By Phase", "parsing").value, 1);

  phase.set("writing");
  assert.equal(stats.counter("Workers", "By Phase", "parsing").value, 0);
  assert.equal(stats.counter("Workers", "By Phase", "writing").value, 1);

  phase.set("writing"); // no-op
  assert.equal(stats.counter("Workers", "By Phase", "writing").value, 1);

  phase.dispose();
  assert.equal(stats.counter("Workers", "By Phase", "writing").value, 0);
});

// -- payload ----------------------------------------------------------------

test("empty collections are omitted rather than sent empty", () => {
  const stats = connect();
  assert.deepStrictEqual(stats.buildPayload(), { retain: 5, remove: 1440 });

  stats.status("Phase", "running");
  const payload = stats.buildPayload();
  assert.ok(payload.statuses);
  assert.equal(payload.counters, undefined);
  assert.equal(payload.events, undefined);
});

test("timestamp messages carry no milliseconds", () => {
  const stats = connect();
  stats.timestamp("Sync", "Age s", "Last Sync", { observedAt: new Date("2026-08-10T09:14:22.512Z") });

  const payload = stats.buildPayload({ snapshotAt: new Date("2026-08-10T09:15:54.000Z") });
  // Milliseconds are dropped from the message but still count towards the age:
  // 91.488s rounds to 91, not to the 92 a whole-second observation would give.
  assert.equal(payload.statuses["Last Sync"].message, "2026-08-10T09:14:22Z");
  assert.equal(payload.counters[0].value, 91);
});

// -- sending ----------------------------------------------------------------

test("a successful flush clears events but keeps counters and statuses", async (t) => {
  const target = await startServer((res) => res.writeHead(200).end("{}"));
  t.after(target.close);

  const stats = connect({ endpoint: target.url });
  stats.counter("Job", "Items").set(7);
  stats.status("Job", "OK");
  stats.event("started");

  await stats.flush();
  await stats.flush();

  assert.equal(target.requests.length, 2);
  assert.deepStrictEqual(target.requests[0].events, [{ message: "started" }]);
  assert.equal(target.requests[1].events, undefined);
  assert.equal(target.requests[1].counters[0].value, 7);
  assert.deepStrictEqual(target.requests[1].statuses, { Job: { value: "OK" } });
});

test("errors never carry the endpoint", async (t) => {
  const target = await startServer((res) => res.writeHead(401).end("nope"));
  t.after(target.close);

  const stats = connect({ endpoint: target.url });
  stats.status("Job", "OK");

  await assert.rejects(stats.flush(), (error) => {
    assert.ok(error instanceof TelemetryError);
    assert.equal(error.statusCode, 401);
    // The URL is currently the credential — it must not reach a log or a stack.
    assert.doesNotMatch(error.message, /SENSORSECRET|127\.0\.0\.1/);
    assert.doesNotMatch(error.stack ?? "", /SENSORSECRET/);
    return true;
  });
});

test("4xx is not retried; 5xx is", async (t) => {
  const rejected = await startServer((res) => res.writeHead(400).end());
  t.after(rejected.close);

  const stats = connect({ endpoint: rejected.url, maxRetries: 3 });
  stats.status("Job", "OK");
  await assert.rejects(stats.flush(), /HTTP 400/);
  assert.equal(rejected.requests.length, 1, "a rejected request must not be repeated");

  const flaky = await startServer((res, count) =>
    count === 1 ? res.writeHead(503).end() : res.writeHead(200).end("{}")
  );
  t.after(flaky.close);

  const retrying = connect({ endpoint: flaky.url, maxRetries: 1 });
  retrying.status("Job", "OK");
  await retrying.flush();
  assert.equal(flaky.requests.length, 2, "a 503 must be retried");
});

test("concurrent flushes share one request", async (t) => {
  const target = await startServer((res) => setTimeout(() => res.writeHead(200).end("{}"), 50));
  t.after(target.close);

  const stats = connect({ endpoint: target.url });
  stats.counter("Job", "Items").set(1);

  await Promise.all([stats.flush(), stats.flush(), stats.flush()]);
  assert.equal(target.requests.length, 1);
});

test("nothing staged means nothing sent", async (t) => {
  const target = await startServer((res) => res.writeHead(200).end("{}"));
  t.after(target.close);

  await connect({ endpoint: target.url }).flush();
  assert.equal(target.requests.length, 0);
});
