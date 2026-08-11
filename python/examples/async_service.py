"""Instrumenting an asyncio service.

Staging is the same as in the blocking example — the registry is shared, and none
of it does I/O. What changes is that the flush loop is a task rather than a
thread, and it has to be started from inside a running loop, which `async with`
takes care of.

Note that the aggregates are still used with a plain `with`. Closing one only
touches memory, so there is nothing to await.

Run with NC_TELEMETRY_URL set to the URL from the Telemetry sensor form, and
NC_TELEMETRY_TOKEN to its token.
"""

from __future__ import annotations

import asyncio
import logging
import os
from datetime import datetime, timezone

from netcrunch_telemetry import AsyncTelemetry

log = logging.getLogger(__name__)


async def main() -> None:
    async with AsyncTelemetry(
        os.environ["NC_TELEMETRY_URL"],
        token=os.environ.get("NC_TELEMETRY_TOKEN"),
        flush_seconds=60,
        retain_minutes=5,
        on_error=lambda error: log.warning("telemetry: %s", error),
    ) as stats:
        # Resolved once. These are the objects the hot path touches.
        requests_total = stats.counter("HTTP", "Requests")
        requests_failed = stats.counter("HTTP", "Failed")
        slowest_ms = stats.counter("HTTP", "Slowest ms")

        stats.status("Service", "Starting")

        async def handle(request) -> object:
            # Held for the duration of the request, so the gauge is right even
            # when the handler raises — and a plain `with`, since closing it does
            # no I/O.
            with stats.self_count("HTTP", "In Flight"):
                started = asyncio.get_running_loop().time()
                requests_total.inc()
                try:
                    return await route(request)
                except Exception as error:
                    requests_failed.inc()
                    stats.event(f"{request.path} failed: {error}", severity="error")
                    raise
                finally:
                    elapsed = asyncio.get_running_loop().time() - started
                    slowest_ms.max(round(elapsed * 1000))

        await serve(handle)

        stats.status("Service", "OK", message=f"{requests_total.value:.0f} requests served")
        stats.timestamp(
            "Service", "Last Healthy Age s", "Last Healthy", observed_at=datetime.now(timezone.utc)
        )
    # aclose on the way out: the flush task is stopped and whatever is staged is
    # sent, rather than waiting for a tick that will not come.


async def route(request) -> object:
    raise NotImplementedError(f"no route for {request.path}")


async def serve(handle) -> None:
    """Stand-in for a real server loop."""
    await asyncio.sleep(0)


if __name__ == "__main__":
    asyncio.run(main())
