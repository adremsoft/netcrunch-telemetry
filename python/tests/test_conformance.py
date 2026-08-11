"""Runs the shared conformance suite against this implementation.

Fixtures live in ../../conformance/cases and are shared with every other
language, so "compatible with the spec" means the same thing in each.
"""

from __future__ import annotations

import json
import sys
import unittest
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from netcrunch_telemetry import Telemetry  # noqa: E402

CASES = sorted((Path(__file__).resolve().parents[2] / "conformance" / "cases").glob("*.json"))

# Placeholder only — never a real installation's endpoint. See CONTRIBUTING.md.
TEST_ENDPOINT = "https://netcrunch.example/api/rest/1/sensors/example@1/update"


def _connect(options: Dict[str, Any]) -> Telemetry:
    return Telemetry(
        TEST_ENDPOINT,
        retain_minutes=options.get("retainMinutes", 5),
        remove_minutes=options.get("removeMinutes", 1440),
        detect_leaks=False,
    )


def _stage_data_object(stats: Telemetry, entry: Dict[str, Any]) -> None:
    """Fixtures describe a data object as one flat record; the API splits it by type.

    The type is passed through untouched, so an unknown-type rejection fails for
    the reason the fixture states rather than incidentally.
    """
    members = {
        member: entry[member]
        for member in ("columns", "rows", "timestamps", "values", "categories")
        if member in entry
    }
    stats.data(
        entry.get("id"),
        entry.get("type"),
        members,
        name=entry.get("name"),
        series_name=entry.get("seriesName"),
        message=entry.get("message"),
        status=entry.get("status"),
    )


def _apply_snapshot(stats: Telemetry, snapshot: Dict[str, Any]) -> None:
    for entry in snapshot.get("counters", []):
        stats.counter(entry["object"], entry["counter"], entry.get("instance")).set(entry["value"])

    for entry in snapshot.get("statuses", []):
        stats.status(
            entry["key"],
            entry["value"],
            message=entry.get("message"),
            critical=bool(entry.get("critical")),
            data=entry.get("data"),
        )

    for entry in snapshot.get("events", []):
        stats.event(entry["message"], severity=entry.get("severity"))

    for entry in snapshot.get("data", []):
        _stage_data_object(stats, entry)

    for entry in snapshot.get("timestamps", []):
        stats.timestamp(
            entry["object"],
            entry["counter"],
            entry["statusKey"],
            observed_at=datetime.fromisoformat(entry["observedAt"].replace("Z", "+00:00")),
        )


def _run_operations(case: unittest.TestCase, stats: Telemetry, operations: List[Dict[str, Any]]) -> None:
    """Assertions are interleaved with the operations because the intermediate
    states are the point: an aggregate that ends up correct having passed through
    a wrong value is still broken."""
    aggregates: Dict[str, Any] = {}

    for step in operations:
        op = step["op"]
        if op == "counter":
            handle = stats.counter(step["object"], step["counter"], step.get("instance"))
            if "set" in step:
                handle.set(step["set"])
        elif op == "selfCount":
            aggregates[step["id"]] = stats.self_count(step["object"], step["counter"], step.get("instance"))
        elif op == "partCount":
            aggregates[step["id"]] = stats.part_count(step["object"], step["counter"], step.get("instance"))
        elif op == "category":
            aggregates[step["id"]] = stats.category(step["object"], step["counter"])
        elif op == "set":
            aggregates[step["id"]].set(step["value"])
        elif op == "dispose":
            aggregates[step["id"]].close()
        elif op == "assert":
            label = f"{step['object']}/{step['counter']}"
            if step.get("instance"):
                label += f".{step['instance']}"
            actual = stats.counter(step["object"], step["counter"], step.get("instance")).value
            case.assertEqual(step["value"], actual, label)
        else:
            case.fail(f'unknown operation "{op}"')


def _sort_counters(payload: Dict[str, Any]) -> Dict[str, Any]:
    """Counter order is not significant; identity is the path."""
    counters = payload.get("counters")
    if not counters:
        return payload
    key = lambda entry: (  # noqa: E731
        entry["path"]["object"],
        entry["path"]["counter"],
        entry["path"].get("instance") or "",
    )
    return {**payload, "counters": sorted(counters, key=key)}


class ConformanceTests(unittest.TestCase):
    maxDiff = None

    def test_cases(self) -> None:
        self.assertTrue(CASES, "no conformance cases found")

        for path in CASES:
            case = json.loads(path.read_text(encoding="utf-8"))

            if "rejects" in case:
                for rejection in case["rejects"]:
                    with self.subTest(case=case["name"], reason=rejection["reason"]):
                        self._run_rejection(case, rejection)
                continue

            with self.subTest(case=case["name"]):
                self._run_case(case)

    def _run_case(self, case: Dict[str, Any]) -> None:
        options = case.get("options", {})
        stats = _connect(options)

        if "operations" in case:
            _run_operations(self, stats, case["operations"])
        else:
            _apply_snapshot(stats, case.get("snapshot", {}))

        if "expect" not in case:
            return

        snapshot_at = None
        if "snapshotAt" in options:
            snapshot_at = datetime.fromisoformat(options["snapshotAt"].replace("Z", "+00:00"))

        # Round-tripped so the comparison happens in the shape that goes over the
        # wire, not as live Python objects.
        actual = json.loads(json.dumps(stats.build_payload(snapshot_at)))
        self.assertEqual(_sort_counters(case["expect"]), _sort_counters(actual))

    def _run_rejection(self, case: Dict[str, Any], rejection: Dict[str, Any]) -> None:
        stats = _connect(case.get("options", {}))
        payload = rejection["input"]
        kind = rejection["kind"]

        with self.assertRaises((TypeError, ValueError)) as caught:
            if kind == "counter":
                stats.counter(payload.get("object"), payload.get("counter"), payload.get("instance")).set(
                    payload.get("value")
                )
            elif kind == "status":
                stats.status(payload.get("key"), payload.get("value"))
            elif kind == "event":
                stats.event(payload.get("message"))
            elif kind == "data":
                _stage_data_object(stats, payload)
            else:
                self.fail(f'unknown rejection kind "{kind}"')

        self.assertTrue(str(caught.exception), "rejection must carry a message")


if __name__ == "__main__":
    unittest.main()
