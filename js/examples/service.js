/**
 * Instrumenting a long-running service.
 *
 * The shape worth copying: resolve handles once at module scope, mutate them on
 * the hot path, and let the timer do the sending. Nothing in the request path
 * touches the network.
 *
 * Run with NC_TELEMETRY_URL set to the URL from the Telemetry sensor form. The
 * URL is effectively a credential — keep it in the environment, not in the source.
 */

import { Telemetry } from "@netcrunch/telemetry";

const stats = new Telemetry({
  endpoint: process.env.NC_TELEMETRY_URL,
  flushSeconds: 60,
  // Comfortably longer than the flush interval, so a single missed send does not
  // expire the values — but short enough that a dead process is noticed quickly.
  retainMinutes: 5,
  onError: (error) => console.warn(`telemetry: ${error.message}`),
});

// Resolved once. These are the objects the hot path touches.
const requestsTotal = stats.counter("HTTP", "Requests");
const requestsFailed = stats.counter("HTTP", "Failed");
const slowestMs = stats.counter("HTTP", "Slowest ms");

stats.status("Service", "Starting");

export async function handleRequest(request) {
  // Held for the duration of the request, so the gauge is right even if the
  // handler throws — no finally block, no chance to forget.
  using inFlight = stats.selfCount("HTTP", "In Flight");

  const startedAt = performance.now();
  requestsTotal.inc();

  try {
    return await route(request);
  } catch (error) {
    requestsFailed.inc();
    // An event per failure: a discrete thing that happened, not a state.
    stats.event(`${request.method} ${request.path} failed: ${error.message}`, {
      severity: "error",
    });
    throw error;
  } finally {
    slowestMs.max(Math.round(performance.now() - startedAt));
  }
}

/** Called after each successful batch — drives the "has it stalled?" alert. */
export function recordHealthy() {
  stats.status("Service", "OK", { message: `${requestsTotal.value} requests served` });
  stats.timestamp("Service", "Last Healthy Age s", "Last Healthy");
}

/**
 * Reset the peak after each flush, so "slowest request" means "since the last
 * report" rather than "since the process started" — otherwise one bad request
 * pins the chart forever.
 */
setInterval(() => slowestMs.reset(), 60_000).unref();

async function shutdown() {
  stats.status("Service", "Stopping");
  // Flush what is staged before the process goes away; the timer would not fire
  // again in time.
  await stats.close();
  process.exit(0);
}

process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);

async function route(request) {
  throw new Error(`no route for ${request.path}`);
}
