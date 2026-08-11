from __future__ import annotations

import asyncio
import sys
import time
import unittest
from pathlib import Path
from typing import Any, Dict, List

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from netcrunch_telemetry import AsyncTelemetry, TelemetryError  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))

from test_telemetry import ENDPOINT, ServerTestCase  # noqa: E402


class AsyncTelemetryTests(ServerTestCase):
    """AsyncTelemetry shares its registry with Telemetry, so the staging surface is
    already covered by test_telemetry. What is worth testing here is the part that
    differs: flushing, the task lifecycle, and not blocking the loop."""

    def test_flush_sends_and_clears_events(self) -> None:
        server = self.serve(lambda _: 200)

        async def scenario() -> None:
            async with AsyncTelemetry(server.url, detect_leaks=False) as stats:
                stats.counter("Job", "Items").set(7)
                stats.status("Job", "OK")
                stats.event("started")
                await stats.flush()
                await stats.flush()

        asyncio.run(scenario())

        received = server.received
        self.assertGreaterEqual(len(received), 2)
        self.assertEqual([{"message": "started"}], received[0]["events"])
        self.assertNotIn("events", received[1])
        self.assertEqual(7, received[1]["counters"][0]["value"])

    def test_token_is_sent_as_bearer(self) -> None:
        server = self.serve(lambda _: 200)

        async def scenario() -> None:
            stats = AsyncTelemetry(server.url, token="TOKENSECRET", detect_leaks=False)
            stats.status("Job", "OK")
            await stats.flush()

        asyncio.run(scenario())
        self.assertEqual(["Bearer TOKENSECRET"], server.authorizations)

    def test_errors_never_carry_the_endpoint(self) -> None:
        server = self.serve(lambda _: 401)

        async def scenario() -> TelemetryError:
            stats = AsyncTelemetry(server.url, detect_leaks=False)
            stats.status("Job", "OK")
            with self.assertRaises(TelemetryError) as caught:
                await stats.flush()
            return caught.exception

        error = asyncio.run(scenario())
        self.assertEqual(401, error.status_code)
        self.assertNotIn("SENSORSECRET", str(error))
        self.assertIsNone(error.__context__)

    def test_rejected_requests_are_not_retried(self) -> None:
        server = self.serve(lambda _: 400)

        async def scenario() -> None:
            stats = AsyncTelemetry(server.url, max_retries=3, detect_leaks=False)
            stats.status("Job", "OK")
            with self.assertRaises(TelemetryError):
                await stats.flush()

        asyncio.run(scenario())
        self.assertEqual(1, len(server.received))

    def test_server_errors_are_retried(self) -> None:
        server = self.serve(lambda count: 503 if count == 1 else 200)

        async def scenario() -> None:
            stats = AsyncTelemetry(server.url, max_retries=1, detect_leaks=False)
            stats.status("Job", "OK")
            await stats.flush()

        asyncio.run(scenario())
        self.assertEqual(2, len(server.received))

    def test_backoff_does_not_block_the_event_loop(self) -> None:
        """The retry wait is a real asyncio.sleep, so the loop keeps running.

        This is the whole reason the async front end exists: with the blocking
        client, a one-second backoff is a second the loop cannot do anything.
        """
        server = self.serve(lambda count: 503 if count == 1 else 200)
        ticks = 0

        async def ticker() -> None:
            nonlocal ticks
            while True:
                await asyncio.sleep(0.05)
                ticks += 1

        async def scenario() -> None:
            stats = AsyncTelemetry(server.url, max_retries=1, detect_leaks=False)
            stats.status("Job", "OK")
            beat = asyncio.create_task(ticker())
            try:
                await stats.flush()
            finally:
                beat.cancel()

        asyncio.run(scenario())

        # One retry means a full second of backoff; a blocked loop would tick
        # nowhere near this many times.
        self.assertGreater(ticks, 10, f"loop appears to have been blocked (only {ticks} ticks)")

    def test_flush_loop_runs_and_stops(self) -> None:
        server = self.serve(lambda _: 200)

        async def scenario() -> None:
            async with AsyncTelemetry(
                server.url, flush_seconds=0.1, retain_minutes=5, detect_leaks=False
            ) as stats:
                stats.status("Job", "OK")
                await asyncio.sleep(0.35)

        asyncio.run(scenario())

        # Several timer flushes plus the one aclose performs.
        self.assertGreaterEqual(len(server.received), 2)

    def test_a_custom_sender_replaces_the_thread_hop(self) -> None:
        """Applications with an async HTTP client already in hand can supply it."""
        sent: List[Dict[str, Any]] = []

        async def sender(endpoint: str, body: bytes, **options: Any) -> None:
            sent.append({"endpoint": endpoint, "body": body, "options": options})

        async def scenario() -> None:
            stats = AsyncTelemetry(ENDPOINT, token="T", send=sender, detect_leaks=False)
            stats.status("Job", "OK")
            await stats.flush()

        asyncio.run(scenario())

        self.assertEqual(1, len(sent))
        self.assertEqual(ENDPOINT, sent[0]["endpoint"])
        self.assertEqual("T", sent[0]["options"]["token"])
        self.assertIn(b'"Job"', sent[0]["body"])

    def test_staging_is_shared_with_the_blocking_front_end(self) -> None:
        """Aggregates stay synchronous: closing one only touches memory."""

        async def scenario() -> None:
            stats = AsyncTelemetry(ENDPOINT, detect_leaks=False)
            handle = stats.counter("Pool", "Leases Active")

            with stats.self_count("Pool", "Leases Active"):
                self.assertEqual(1, handle.value)
            self.assertEqual(0, handle.value)

            phase = stats.category("Workers", "By Phase")
            phase.set("parsing")
            phase.set("writing")
            self.assertEqual(0, stats.counter("Workers", "By Phase", "parsing").value)
            self.assertEqual(1, stats.counter("Workers", "By Phase", "writing").value)

        asyncio.run(scenario())

    def test_nothing_staged_means_nothing_sent(self) -> None:
        server = self.serve(lambda _: 200)

        async def scenario() -> None:
            await AsyncTelemetry(server.url, detect_leaks=False).flush()

        asyncio.run(scenario())
        self.assertEqual(0, len(server.received))


if __name__ == "__main__":
    unittest.main()
