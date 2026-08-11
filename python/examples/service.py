"""Instrumenting a long-running Python service.

The shape worth copying: resolve handles once at module scope, mutate them on the
hot path, and let the flush thread do the sending. Nothing in the request path
touches the network.

Run with NC_TELEMETRY_URL set to the URL from the Telemetry sensor form. That URL
is effectively a credential — keep it in the environment, not in the source.
"""

from __future__ import annotations

import logging
import os
import signal
import time
from datetime import datetime, timezone

from netcrunch_telemetry import Telemetry

log = logging.getLogger(__name__)

stats = Telemetry(
    os.environ["NC_TELEMETRY_URL"],
    flush_seconds=60,
    # Comfortably longer than the flush interval, so one missed send does not
    # expire the values — but short enough that a dead process is noticed.
    retain_minutes=5,
    on_error=lambda error: log.warning("telemetry: %s", error),
)

# Resolved once. These are the objects the hot path touches.
requests_total = stats.counter("HTTP", "Requests")
requests_failed = stats.counter("HTTP", "Failed")
slowest_ms = stats.counter("HTTP", "Slowest ms")

stats.status("Service", "Starting")


def handle_request(request):
    """Held for the duration of the request, so the gauge is right even when the
    handler raises — no try/finally, no chance to forget."""
    with stats.self_count("HTTP", "In Flight"):
        started = time.perf_counter()
        requests_total.inc()
        try:
            return route(request)
        except Exception as error:
            requests_failed.inc()
            # An event per failure: a discrete thing that happened, not a state.
            stats.event(f"{request.method} {request.path} failed: {error}", severity="error")
            raise
        finally:
            slowest_ms.max(round((time.perf_counter() - started) * 1000))


def record_healthy() -> None:
    """Called after each successful batch — drives the "has it stalled?" alert."""
    stats.status("Service", "OK", message=f"{requests_total.value:.0f} requests served")
    stats.timestamp("Service", "Last Healthy Age s", "Last Healthy", observed_at=datetime.now(timezone.utc))


def shutdown(_signum, _frame) -> None:
    stats.status("Service", "Stopping")
    # Send what is staged before the process goes away; the timer would not fire
    # again in time.
    stats.close()
    raise SystemExit(0)


signal.signal(signal.SIGTERM, shutdown)
signal.signal(signal.SIGINT, shutdown)


def route(request):
    raise NotImplementedError(f"no route for {request.path}")
