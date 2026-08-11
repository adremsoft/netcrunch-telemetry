from __future__ import annotations

import gc
import json
import sys
import threading
import unittest
import warnings
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable, Dict, List

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from netcrunch_telemetry import Telemetry, TelemetryError  # noqa: E402

ENDPOINT = "https://netcrunch.example/api/rest/1/sensors/example@1/update"


class _Handler(BaseHTTPRequestHandler):
    def do_POST(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length)
        server: Any = self.server
        with server.gate:
            server.received.append(json.loads(body or b"{}"))
            count = len(server.received)
        self.send_response(server.respond(count))
        self.end_headers()

    def log_message(self, *args: Any) -> None:
        pass


class _Server:
    """A throwaway receiver whose URL embeds a recognisable secret, so a leak into
    an error message is visible."""

    def __init__(self, respond: Callable[[int], int]) -> None:
        self._httpd: Any = ThreadingHTTPServer(("127.0.0.1", 0), _Handler)
        self._httpd.respond = respond
        self._httpd.received: List[Dict[str, Any]] = []
        self._httpd.gate = threading.Lock()
        self._thread = threading.Thread(target=self._httpd.serve_forever, daemon=True)
        self._thread.start()

    @property
    def url(self) -> str:
        port = self._httpd.server_address[1]
        return f"http://127.0.0.1:{port}/api/rest/1/sensors/SENSORSECRET@1/update"

    @property
    def received(self) -> List[Dict[str, Any]]:
        with self._httpd.gate:
            return list(self._httpd.received)

    def close(self) -> None:
        self._httpd.shutdown()
        self._httpd.server_close()


class ServerTestCase(unittest.TestCase):
    def serve(self, respond: Callable[[int], int]) -> _Server:
        server = _Server(respond)
        self.addCleanup(server.close)
        return server


class ConstructionTests(unittest.TestCase):
    def test_endpoint_must_be_absolute_http(self) -> None:
        for bad in ("", "   ", "not-a-url", "ftp://host/x"):
            with self.subTest(endpoint=bad), self.assertRaises(ValueError):
                Telemetry(bad)

    def test_retain_must_outlast_flush_interval(self) -> None:
        # A 60s flush against a 1 minute retain would let values expire between sends.
        with self.assertRaises(ValueError):
            Telemetry(ENDPOINT, flush_seconds=60, retain_minutes=1)


class CounterTests(unittest.TestCase):
    def setUp(self) -> None:
        self.stats = Telemetry(ENDPOINT, detect_leaks=False)

    def test_same_path_resolves_to_same_handle(self) -> None:
        self.assertIs(
            self.stats.counter("Queue", "Depth", "inbound"),
            self.stats.counter("Queue", "Depth", "inbound"),
        )
        self.assertIsNot(
            self.stats.counter("Queue", "Depth", "inbound"),
            self.stats.counter("Queue", "Depth", "outbound"),
        )

    def test_max_and_min_move_one_way(self) -> None:
        peak = self.stats.counter("SNMP", "Peak ms")
        peak.max(120)
        peak.max(90)
        peak.max(200)
        self.assertEqual(200, peak.value)

        floor = self.stats.counter("SNMP", "Floor ms").set(100)
        floor.min(150)
        floor.min(40)
        floor.min(70)
        self.assertEqual(40, floor.value)

    def test_booleans_are_not_numbers(self) -> None:
        # bool subclasses int; True silently becoming 1 is never what was meant.
        with self.assertRaises(TypeError):
            self.stats.counter("Queue", "Depth").set(True)


class AggregateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.stats = Telemetry(ENDPOINT, detect_leaks=False)

    def test_self_count_closes_idempotently(self) -> None:
        # A `with` block and an explicit close can both fire on the same object. A
        # double decrement drives the value negative, which drifts away from every
        # threshold rather than towards one, so nothing would ever report it.
        handle = self.stats.counter("Pool", "Leases Active")
        lease = self.stats.self_count("Pool", "Leases Active")
        self.assertEqual(1, handle.value)

        lease.close()
        lease.close()
        lease.close()
        self.assertEqual(0, handle.value)
        self.assertTrue(lease.closed)

    def test_self_count_as_context_manager(self) -> None:
        handle = self.stats.counter("Pool", "Leases Active")

        with self.stats.self_count("Pool", "Leases Active"):
            self.assertEqual(1, handle.value)
        self.assertEqual(0, handle.value)

        with self.assertRaises(RuntimeError):
            with self.stats.self_count("Pool", "Leases Active"):
                raise RuntimeError("boom")
        self.assertEqual(0, handle.value)

    def test_part_count_withdraws_its_contribution(self) -> None:
        handle = self.stats.counter("Cache", "Entries").set(1000)
        shard = self.stats.part_count("Cache", "Entries")

        shard.set(5)
        shard.set(9)
        shard.set(3)
        self.assertEqual(1003, handle.value)
        self.assertEqual(3, shard.contribution)

        shard.close()
        self.assertEqual(1000, handle.value)
        with self.assertRaises(ValueError):
            shard.set(1)

    def test_category_moves_between_instances(self) -> None:
        phase = self.stats.category("Workers", "By Phase")

        phase.set("parsing")
        self.assertEqual(1, self.stats.counter("Workers", "By Phase", "parsing").value)

        phase.set("writing")
        self.assertEqual(0, self.stats.counter("Workers", "By Phase", "parsing").value)
        self.assertEqual(1, self.stats.counter("Workers", "By Phase", "writing").value)

        phase.set("writing")
        self.assertEqual(1, self.stats.counter("Workers", "By Phase", "writing").value)

        phase.set(None)
        self.assertEqual(0, self.stats.counter("Workers", "By Phase", "writing").value)

        phase.close()

    def test_unclosed_aggregate_warns(self) -> None:
        # The warning is a diagnostic, never the decrement: it fires from __del__,
        # which is a CPython implementation detail rather than a guarantee.
        stats = Telemetry(ENDPOINT, detect_leaks=True)

        with warnings.catch_warnings(record=True) as caught:
            warnings.simplefilter("always")
            stats.self_count("Pool", "Leases Active")
            gc.collect()

        messages = [str(w.message) for w in caught if issubclass(w.category, ResourceWarning)]
        self.assertTrue(messages, "expected a ResourceWarning for the unclosed aggregate")
        self.assertIn("Pool/Leases Active", messages[0])


class PayloadTests(unittest.TestCase):
    def setUp(self) -> None:
        self.stats = Telemetry(ENDPOINT, detect_leaks=False)

    def test_empty_collections_are_omitted(self) -> None:
        self.assertEqual({"retain": 5, "remove": 1440}, self.stats.build_payload())

        self.stats.status("Phase", "running")
        payload = self.stats.build_payload()
        self.assertIn("statuses", payload)
        self.assertNotIn("counters", payload)
        self.assertNotIn("events", payload)

    def test_timestamp_message_carries_no_subsecond_part(self) -> None:
        observed = datetime(2026, 8, 10, 9, 14, 22, 512000, tzinfo=timezone.utc)
        self.stats.timestamp("Sync", "Age s", "Last Sync", observed_at=observed)

        payload = self.stats.build_payload(datetime(2026, 8, 10, 9, 15, 54, tzinfo=timezone.utc))

        self.assertEqual("2026-08-10T09:14:22Z", payload["statuses"]["Last Sync"]["message"])
        # Sub-second precision is dropped from the message but still counts towards
        # the age: 91.488s rounds to 91, not the 92 a whole-second observation gives.
        self.assertEqual(91, payload["counters"][0]["value"])


class SendingTests(ServerTestCase):
    def test_flush_clears_events_but_keeps_counters_and_statuses(self) -> None:
        server = self.serve(lambda _: 200)
        stats = Telemetry(server.url, detect_leaks=False)

        stats.counter("Job", "Items").set(7)
        stats.status("Job", "OK")
        stats.event("started")

        stats.flush()
        stats.flush()

        received = server.received
        self.assertEqual(2, len(received))
        self.assertEqual([{"message": "started"}], received[0]["events"])
        self.assertNotIn("events", received[1])
        self.assertEqual(7, received[1]["counters"][0]["value"])
        self.assertEqual({"Job": {"value": "OK"}}, received[1]["statuses"])

    def test_errors_never_carry_the_endpoint(self) -> None:
        server = self.serve(lambda _: 401)
        stats = Telemetry(server.url, detect_leaks=False)
        stats.status("Job", "OK")

        with self.assertRaises(TelemetryError) as caught:
            stats.flush()

        error = caught.exception
        self.assertEqual(401, error.status_code)
        # The URL is currently the credential; it must not reach a log.
        self.assertNotIn("SENSORSECRET", str(error))
        self.assertNotIn("127.0.0.1", str(error))
        # A chained cause would print the URL when the traceback is formatted.
        self.assertIsNone(error.__cause__)
        self.assertIsNone(error.__context__)

    def test_rejected_requests_are_not_retried(self) -> None:
        server = self.serve(lambda _: 400)
        stats = Telemetry(server.url, max_retries=3, detect_leaks=False)
        stats.status("Job", "OK")

        with self.assertRaises(TelemetryError):
            stats.flush()

        self.assertEqual(1, len(server.received), "a rejected request must not be repeated")

    def test_server_errors_are_retried(self) -> None:
        server = self.serve(lambda count: 503 if count == 1 else 200)
        stats = Telemetry(server.url, max_retries=1, detect_leaks=False)
        stats.status("Job", "OK")

        stats.flush()

        self.assertEqual(2, len(server.received))

    def test_nothing_staged_means_nothing_sent(self) -> None:
        server = self.serve(lambda _: 200)
        Telemetry(server.url, detect_leaks=False).flush()
        self.assertEqual(0, len(server.received))

    def test_concurrent_use_is_safe(self) -> None:
        server = self.serve(lambda _: 200)
        stats = Telemetry(server.url, detect_leaks=False)
        handle = stats.counter("Load", "Operations")

        def worker() -> None:
            for _ in range(200):
                handle.inc()
                with stats.self_count("Load", "In Flight"):
                    stats.status("Worker", "OK")
            stats.flush()

        threads = [threading.Thread(target=worker) for _ in range(8)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()

        self.assertEqual(1600, handle.value)
        self.assertEqual(0, stats.counter("Load", "In Flight").value)


if __name__ == "__main__":
    unittest.main()
