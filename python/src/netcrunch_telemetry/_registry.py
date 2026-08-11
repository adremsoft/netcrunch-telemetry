"""Staging values in memory, and turning a snapshot into a payload.

Everything here is synchronous and touches nothing but memory, which is what lets
the blocking and the asyncio front ends share it unchanged. Only flushing differs
between them.
"""

from __future__ import annotations

import math
import threading
from datetime import datetime, timezone
from typing import Any, Callable, Dict, List, Mapping, Optional, Sequence
from urllib.parse import urlparse

from . import _validate
from ._counters import CategoryCount, Counter, PartCount, SelfCount

#: NUL, so a counter named "A B"/"C" cannot collide with "A"/"B C".
_KEY_SEPARATOR = "\x00"


def _round_half_away(value: float) -> int:
    """Rounds half away from zero.

    Python's built-in ``round`` uses banker's rounding, which would disagree with
    every other implementation on exactly the halves.
    """
    return int(math.floor(value + 0.5)) if value >= 0 else -int(math.floor(-value + 0.5))


def _iso_seconds(moment: datetime) -> str:
    """ISO 8601 without the microseconds the wire format has no use for."""
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


class Registry:
    """Shared base of :class:`Telemetry` and :class:`AsyncTelemetry`.

    Not exported: construct one of the two front ends instead.
    """

    def __init__(
        self,
        endpoint: str,
        *,
        token: Optional[str] = None,
        flush_seconds: float = 0,
        retain_minutes: int = 5,
        remove_minutes: int = 1440,
        timeout_seconds: float = 30,
        max_retries: int = 3,
        on_error: Optional[Callable[[BaseException], None]] = None,
        detect_leaks: bool = True,
    ) -> None:
        if not isinstance(endpoint, str) or endpoint.strip() == "":
            raise ValueError("endpoint is required — copy it from the Telemetry sensor form.")
        parsed = urlparse(endpoint)
        if parsed.scheme not in ("http", "https") or not parsed.netloc:
            raise ValueError("endpoint must be an absolute http or https URL.")

        if token is not None and (not isinstance(token, str) or token == ""):
            raise ValueError("token must be a non-empty string when provided.")

        if flush_seconds > 0 and retain_minutes * 60 <= flush_seconds:
            raise ValueError(
                f"retain_minutes ({retain_minutes}) must exceed flush_seconds ({flush_seconds}), "
                "or values expire between sends."
            )

        self.endpoint = endpoint
        self.token = token
        self.retain_minutes = retain_minutes
        self.remove_minutes = remove_minutes
        self.flush_seconds = flush_seconds
        self.timeout_seconds = timeout_seconds
        self.max_retries = max_retries
        self.on_error = on_error
        self.detect_leaks = detect_leaks

        # A threading lock rather than an asyncio one: staging never awaits, so it
        # is never held across a suspension point, and this way a counter can be
        # mutated from a worker thread and the event loop alike.
        self._lock = threading.Lock()
        self._counters: Dict[str, Counter] = {}
        self._counter_order: List[Counter] = []
        self._statuses: Dict[str, Dict[str, Any]] = {}
        self._timestamps: Dict[str, Dict[str, Any]] = {}
        self._data_objects: Dict[str, Dict[str, Any]] = {}
        self._events: List[Dict[str, Any]] = []

    # -- staging -----------------------------------------------------------

    def counter(self, obj: str, counter: str, instance: Optional[str] = None) -> Counter:
        """Resolves a counter handle.

        The same object, counter and instance always return the same handle, so
        separate parts of a program instrumenting the same thing converge on one
        value. Resolve once and keep it.
        """
        _validate.counter_path(obj, counter)
        _validate.counter_instance(instance)

        key = obj + _KEY_SEPARATOR + counter + _KEY_SEPARATOR + (instance or "")
        with self._lock:
            existing = self._counters.get(key)
            if existing is not None:
                return existing
            created = Counter(obj, counter, instance)
            self._counters[key] = created
            self._counter_order.append(created)
            return created

    def status(
        self,
        key: str,
        value: str,
        *,
        message: Optional[str] = None,
        critical: bool = False,
        data: Any = None,
    ):
        """Stages a state with an optional explanation.

        Statuses are what NetCrunch alerting acts on — a counter on its own raises
        nothing.
        """
        _validate.status_key(key)
        _validate.status_value(value)

        entry: Dict[str, Any] = {"value": value}
        if message:
            entry["message"] = message
        if critical:
            entry["critical"] = True
        if data is not None:
            entry["data"] = data

        with self._lock:
            self._statuses[key] = entry
        return self

    def event(self, message: str, *, severity: Optional[str] = None):
        """Stages a discrete occurrence.

        Events accumulate and are cleared once sent. Use a status for a condition
        that begins and later ends.
        """
        _validate.event_message(message)

        entry: Dict[str, Any] = {"message": message}
        if severity is not None:
            entry["severity"] = severity

        with self._lock:
            self._events.append(entry)
        return self

    def timestamp(
        self,
        obj: str,
        counter: str,
        status_key: str,
        *,
        observed_at: Optional[datetime] = None,
        status_value: str = "OK",
    ):
        """Records when something last happened.

        The wire format has no timestamp type, and a raw clock value means nothing
        outside the process that produced it. So this becomes two things: an age in
        seconds, which an alert threshold can be set on, and a status message
        carrying the absolute time, for a person to read. The age is computed at
        flush time.
        """
        _validate.counter_path(obj, counter)
        _validate.status_key(status_key)
        _validate.status_value(status_value)

        moment = observed_at or datetime.now(timezone.utc)
        if not isinstance(moment, datetime):
            raise TypeError("observed_at must be a datetime.")

        with self._lock:
            self._timestamps[status_key] = {
                "object": obj,
                "counter": counter,
                "status_key": status_key,
                "observed_at": moment,
                "status_value": status_value,
            }
        return self

    # -- data objects ------------------------------------------------------

    def data(
        self,
        object_id: str,
        object_type: str,
        members: Mapping[str, Any],
        *,
        name: Optional[str] = None,
        series_name: Optional[str] = None,
        message: Optional[str] = None,
        status: Optional[str] = None,
    ):
        """Stages a data object rendered on the sensor's page.

        The id is the object's identity across payloads: staging the same id again
        replaces it. There is no incremental form — a data object is a whole view
        each time.

        A data object's ``status`` is part of what is displayed. Alerting acts on
        statuses; a red table is not an alert.
        """
        _validate.data_object(object_id, object_type, members)

        encoded: Dict[str, Any] = {"type": object_type}
        for member in _validate.DATA_TYPE_MEMBERS[object_type]:
            encoded[member] = members[member]
        if name is not None:
            encoded["name"] = name
        # series_name labels a plotted series; a table has no series to label.
        if series_name is not None and object_type != "table":
            encoded["seriesName"] = series_name
        if message is not None:
            encoded["message"] = message
        if status is not None:
            encoded["status"] = status

        with self._lock:
            self._data_objects[object_id] = encoded
        return self

    def table(
        self,
        object_id: str,
        *,
        columns: Sequence[Any],
        rows: Sequence[Sequence[Any]],
        name: Optional[str] = None,
        message: Optional[str] = None,
        status: Optional[str] = None,
    ):
        """Stages a table. Every row must have as many cells as there are columns."""
        return self.data(
            object_id,
            "table",
            {"columns": columns, "rows": rows},
            name=name,
            message=message,
            status=status,
        )

    def time_series(
        self,
        object_id: str,
        *,
        timestamps: Sequence[int],
        values: Sequence[float],
        name: Optional[str] = None,
        series_name: Optional[str] = None,
        message: Optional[str] = None,
        status: Optional[str] = None,
    ):
        """Stages a time chart. Timestamps are epoch milliseconds."""
        return self.data(
            object_id,
            "time-series",
            {"timestamps": timestamps, "values": values},
            name=name,
            series_name=series_name,
            message=message,
            status=status,
        )

    def category_chart(
        self,
        object_id: str,
        *,
        categories: Sequence[str],
        values: Sequence[float],
        name: Optional[str] = None,
        series_name: Optional[str] = None,
        message: Optional[str] = None,
        status: Optional[str] = None,
    ):
        """Stages a labelled bar chart.

        Named apart from :meth:`category`, which is the lifetime-bound aggregate —
        same word in NetCrunch, unrelated meanings.
        """
        return self.data(
            object_id,
            "category",
            {"categories": categories, "values": values},
            name=name,
            series_name=series_name,
            message=message,
            status=status,
        )

    # -- lifetime-bound aggregates -----------------------------------------

    def self_count(self, obj: str, counter: str, instance: Optional[str] = None) -> SelfCount:
        """Holds one against a counter until closed."""
        return SelfCount(self.counter(obj, counter, instance), self.detect_leaks)

    def part_count(self, obj: str, counter: str, instance: Optional[str] = None) -> PartCount:
        """Contributes a movable amount, withdrawn in full on close."""
        return PartCount(self.counter(obj, counter, instance), self.detect_leaks)

    def category(self, obj: str, counter: str) -> CategoryCount:
        """Holds one against a single instance at a time, moving it as the value changes."""
        _validate.counter_path(obj, counter)
        return CategoryCount(
            lambda instance: self.counter(obj, counter, instance),
            obj,
            counter,
            self.detect_leaks,
        )

    # -- payload -----------------------------------------------------------

    def build_payload(self, snapshot_at: Optional[datetime] = None) -> Dict[str, Any]:
        """Builds the payload a flush would post, without sending it.

        Members with nothing in them are omitted rather than sent empty.
        """
        moment = snapshot_at or datetime.now(timezone.utc)
        if moment.tzinfo is None:
            moment = moment.replace(tzinfo=timezone.utc)

        with self._lock:
            payload: Dict[str, Any] = {"retain": self.retain_minutes, "remove": self.remove_minutes}

            counters: List[Dict[str, Any]] = []
            for handle in self._counter_order:
                path: Dict[str, Any] = {"object": handle.object, "counter": handle.counter}
                if handle.instance:
                    path["instance"] = handle.instance
                counters.append({"path": path, "value": handle.value})

            # A timestamp contributes to both collections, so it is expanded here
            # rather than at the call site — the age is only meaningful against
            # this snapshot.
            statuses = dict(self._statuses)
            for stamp in self._timestamps.values():
                observed = stamp["observed_at"]
                if observed.tzinfo is None:
                    observed = observed.replace(tzinfo=timezone.utc)
                counters.append(
                    {
                        "path": {"object": stamp["object"], "counter": stamp["counter"]},
                        "value": _round_half_away((moment - observed).total_seconds()),
                    }
                )
                statuses[stamp["status_key"]] = {
                    "value": stamp["status_value"],
                    "message": _iso_seconds(observed),
                }

            if counters:
                payload["counters"] = counters
            if statuses:
                payload["statuses"] = statuses
            if self._events:
                payload["events"] = list(self._events)
            if self._data_objects:
                payload["data"] = dict(self._data_objects)

            return payload

    def _trim_sent_events(self, count: int) -> None:
        """Trimmed rather than emptied: events staged while the request was in
        flight have not been sent, and dropping them would lose them silently."""
        with self._lock:
            del self._events[:count]

    def clear(self):
        """Discards everything staged."""
        with self._lock:
            self._counters.clear()
            self._counter_order.clear()
            self._statuses.clear()
            self._timestamps.clear()
            self._data_objects.clear()
            self._events.clear()
        return self
