/**
 * Sending.
 *
 * A payload carries absolute current values rather than deltas, which makes the
 * request idempotent: a retry after a timeout cannot double-count. That is what
 * licenses the retry loop below. 4xx responses are not retried, since repeating a
 * rejected request will not change the answer.
 */

/**
 * A send failure, with the endpoint deliberately absent.
 *
 * The endpoint URL currently carries the sensor identity and is effectively the
 * credential (spec/v1.md section 1). `fetch` puts the request URL into the errors
 * it raises, so failures are rebuilt from scratch here rather than wrapped — a
 * wrapped cause would put the credential into every log that prints the stack.
 */
export class TelemetryError extends Error {
  constructor(message, statusCode = 0) {
    super(message);
    this.name = "TelemetryError";
    this.statusCode = statusCode;
  }
}

const RETRYABLE_STATUS = (status) => status === 429 || status >= 500;

function backoffMs(attempt) {
  return Math.min(30_000, 2 ** (attempt - 1) * 1000);
}

const delay = (ms, signal) =>
  new Promise((resolve, reject) => {
    const timer = setTimeout(resolve, ms);
    signal?.addEventListener(
      "abort",
      () => {
        clearTimeout(timer);
        reject(new TelemetryError("Send aborted."));
      },
      { once: true }
    );
  });

/** Frees the socket. The response body is never of interest. */
async function discardBody(response) {
  try {
    await response.body?.cancel();
  } catch {
    // Already consumed or unsupported — nothing to release.
  }
}

/**
 * Posts one payload, retrying transport failures and 5xx responses.
 *
 * @param {string} endpoint
 * @param {object} payload
 * @param {{timeoutMs?: number, maxRetries?: number, signal?: AbortSignal}} [options]
 */
export async function postPayload(endpoint, payload, options = {}) {
  const { timeoutMs = 30_000, maxRetries = 3, signal, token } = options;
  const body = JSON.stringify(payload);

  const headers = { "content-type": "application/json; charset=utf-8" };
  if (token) headers.authorization = `Bearer ${token}`;

  let attempt = 0;
  let lastFailure = null;

  while (attempt <= maxRetries) {
    attempt += 1;

    const timeoutController = new AbortController();
    const timer = setTimeout(() => timeoutController.abort(), timeoutMs);
    const abort = signal
      ? AbortSignal.any([signal, timeoutController.signal])
      : timeoutController.signal;

    try {
      const response = await fetch(endpoint, { method: "POST", headers, body, signal: abort });
      await discardBody(response);

      if (response.ok) return;

      if (!RETRYABLE_STATUS(response.status) || attempt > maxRetries) {
        throw new TelemetryError(
          `NetCrunch telemetry send failed with HTTP ${response.status}.`,
          response.status
        );
      }
      lastFailure = new TelemetryError(
        `NetCrunch telemetry send failed with HTTP ${response.status}.`,
        response.status
      );
    } catch (error) {
      if (error instanceof TelemetryError) throw error;

      if (signal?.aborted) throw new TelemetryError("Send aborted.");

      const reason = timeoutController.signal.aborted
        ? `timed out after ${timeoutMs} ms`
        : "the endpoint was unreachable";
      lastFailure = new TelemetryError(`NetCrunch telemetry send failed: ${reason}.`);

      if (attempt > maxRetries) throw lastFailure;
    } finally {
      clearTimeout(timer);
    }

    await delay(backoffMs(attempt), signal);
  }

  throw lastFailure ?? new TelemetryError("NetCrunch telemetry send failed.");
}
