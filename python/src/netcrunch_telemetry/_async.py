"""The asyncio front end."""

from __future__ import annotations

import asyncio
import json
from datetime import datetime
from typing import Awaitable, Callable, Optional

from . import _transport
from ._registry import Registry
from ._transport import TelemetryError

#: A replacement sender: ``await send(endpoint, body, timeout_seconds=..., max_retries=..., token=...)``.
Sender = Callable[..., Awaitable[None]]


class AsyncTelemetry(Registry):
    """The asyncio counterpart of :class:`Telemetry`.

    Staging is identical and shared — :meth:`counter`, :meth:`status`,
    :meth:`event`, the data objects and the lifetime-bound aggregates all behave
    exactly as they do in the blocking front end, because none of them does I/O.
    Only flushing differs.

    ::

        async with AsyncTelemetry(url, flush_seconds=60) as stats:
            stats.counter("HTTP", "Requests").inc()

    Two differences from :class:`Telemetry` are worth knowing:

    * **The flush loop is not started by the constructor.** Creating a task needs
      a running event loop, and there may not be one where the object is built.
      ``async with`` starts it; otherwise call :meth:`start` from inside the loop.
    * **Aggregates stay synchronous.** ``with stats.self_count(...)`` — not
      ``async with`` — since closing one only touches memory.
    """

    def __init__(self, endpoint: str, *, send: Optional[Sender] = None, **options) -> None:
        """
        :param send: Replaces the built-in sender. Supply one to route sends
            through ``aiohttp``, ``httpx`` or anything else already in the
            application, instead of the thread hop the default uses.
        """
        super().__init__(endpoint, **options)
        self._send: Sender = send or _transport.post_async
        self._flush_lock = asyncio.Lock()
        self._stopping: Optional[asyncio.Event] = None
        self._task: Optional[asyncio.Task] = None

    async def flush(self, snapshot_at: Optional[datetime] = None) -> None:
        """Posts everything staged as a single request.

        Concurrent calls serialise rather than run together; each sends the
        absolute state at the moment it runs. Events are cleared on success.

        :raises TelemetryError: the send failed. The endpoint is never included.
        """
        async with self._flush_lock:
            payload = self.build_payload(snapshot_at)
            if len(payload) <= 2:
                return

            sent_events = len(payload.get("events", ()))
            body = json.dumps(payload, separators=(",", ":")).encode("utf-8")

            await self._send(
                self.endpoint,
                body,
                timeout_seconds=self.timeout_seconds,
                max_retries=self.max_retries,
                token=self.token,
            )

            self._trim_sent_events(sent_events)

    async def start(self) -> "AsyncTelemetry":
        """Starts the background flush task. Must be called from inside a running loop."""
        if self._task is not None or self.flush_seconds <= 0:
            return self
        self._stopping = asyncio.Event()
        self._task = asyncio.create_task(self._loop(), name="netcrunch-telemetry")
        return self

    async def stop(self) -> "AsyncTelemetry":
        """Stops the background flush task and waits for it to finish."""
        task = self._task
        if task is None:
            return self
        if self._stopping is not None:
            self._stopping.set()
        try:
            await task
        except asyncio.CancelledError:  # pragma: no cover - cancelled from outside
            pass
        self._task = None
        return self

    async def _loop(self) -> None:
        assert self._stopping is not None
        while True:
            try:
                await asyncio.wait_for(self._stopping.wait(), timeout=self.flush_seconds)
                return
            except asyncio.TimeoutError:
                pass

            try:
                await self.flush()
            except asyncio.CancelledError:
                raise
            except BaseException as error:  # noqa: BLE001 - the loop must not die
                if self.on_error is not None:
                    self.on_error(error)

    async def aclose(self) -> None:
        """Stops the flush task and flushes once more."""
        await self.stop()
        await self.flush()

    async def __aenter__(self) -> "AsyncTelemetry":
        await self.start()
        return self

    async def __aexit__(self, exc_type, exc, traceback) -> bool:
        try:
            await self.aclose()
        except TelemetryError as error:
            if self.on_error is not None:
                self.on_error(error)
            else:
                raise
        return False
