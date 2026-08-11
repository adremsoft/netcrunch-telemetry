"""The blocking front end."""

from __future__ import annotations

import json
import threading
from datetime import datetime
from typing import Optional

from . import _transport
from ._registry import Registry
from ._transport import TelemetryError


class Telemetry(Registry):
    """Stages metrics, states and events, and flushes them as a single payload.

    Instrumentation only mutates memory. A separate flush snapshots the registry
    and sends absolute current values, so nothing in a request path touches the
    network, and one request carries every value — which matters because the
    receiver caps pending payloads per sensor and discards the overflow without
    reporting it.

    Instances are safe for concurrent use, and usable as a context manager::

        with Telemetry(endpoint) as stats:
            stats.status("Job", "OK")

    In an asyncio application use :class:`AsyncTelemetry` instead: :meth:`flush`
    here blocks, and blocking an event loop for the length of an HTTP round trip
    is not something a telemetry library should ask for.

    See spec/v1.md for the wire format and spec/client-model.md for the behaviour
    above it.

    :param endpoint: URL from the Telemetry sensor form. Treat it as a secret;
        this library never writes it to an exception or a log.
    :param token: Bearer token from the Telemetry sensor, sent as an
        ``Authorization`` header. Optional only because the receiver does not yet
        require one; see spec/v1.md section 1.1.
    :param flush_seconds: Starts a background flush thread when above zero. Zero —
        the default — flushes only when asked.
    :param retain_minutes: Must exceed the flush interval, or values expire
        between sends.
    :param on_error: Receives failures from background flushes, which have nowhere
        else to go. Explicit :meth:`flush` calls raise instead.
    :param detect_leaks: Warn when an aggregate is collected unclosed. The warning
        is a ``ResourceWarning``, so it is quiet unless warnings are enabled —
        under ``python -X dev`` or in a test run.
    """

    def __init__(self, endpoint: str, **options) -> None:
        super().__init__(endpoint, **options)
        self._flush_lock = threading.Lock()
        self._stopping = threading.Event()
        self._thread: Optional[threading.Thread] = None
        if self.flush_seconds > 0:
            self.start()

    def flush(self, snapshot_at: Optional[datetime] = None) -> None:
        """Posts everything staged as a single request.

        Concurrent calls serialise rather than run together; each sends the
        absolute state at the moment it runs. Events are cleared on success.
        Counters and statuses are kept, so a long-running process keeps reporting
        current values without restating them.

        :raises TelemetryError: the send failed. The endpoint is never included.
        """
        with self._flush_lock:
            payload = self.build_payload(snapshot_at)
            if len(payload) <= 2:
                return

            sent_events = len(payload.get("events", ()))
            body = json.dumps(payload, separators=(",", ":")).encode("utf-8")

            _transport.post(
                self.endpoint,
                body,
                timeout_seconds=self.timeout_seconds,
                max_retries=self.max_retries,
                token=self.token,
            )

            self._trim_sent_events(sent_events)

    def start(self) -> "Telemetry":
        """Starts the background flush thread. It is a daemon, so it never holds the process open."""
        if self._thread is not None or self.flush_seconds <= 0:
            return self
        self._stopping.clear()
        self._thread = threading.Thread(target=self._loop, name="netcrunch-telemetry", daemon=True)
        self._thread.start()
        return self

    def stop(self) -> "Telemetry":
        """Stops the background flush thread."""
        thread = self._thread
        if thread is None:
            return self
        self._stopping.set()
        thread.join(timeout=self.timeout_seconds + 5)
        self._thread = None
        return self

    def _loop(self) -> None:
        while not self._stopping.wait(self.flush_seconds):
            try:
                self.flush()
            except BaseException as error:  # noqa: BLE001 - the loop must not die
                if self.on_error is not None:
                    self.on_error(error)

    def close(self) -> None:
        """Stops the flush thread and flushes once more."""
        self.stop()
        self.flush()

    def __enter__(self) -> "Telemetry":
        return self

    def __exit__(self, exc_type, exc, traceback) -> bool:
        try:
            self.close()
        except TelemetryError as error:
            if self.on_error is not None:
                self.on_error(error)
            else:
                raise
        return False
