"""Sending.

A payload carries absolute current values rather than deltas, which makes the
request idempotent: a retry after a timeout cannot double-count. That is what
licenses the retry loop. 4xx responses are not retried, since repeating a
rejected request will not change the answer.
"""

from __future__ import annotations

import asyncio
import time
import urllib.error
import urllib.request
from typing import Optional


class TelemetryError(Exception):
    """A send failure, with the endpoint deliberately absent.

    The endpoint URL currently carries the sensor identity and is effectively the
    credential (spec/v1.md section 1). ``urllib`` puts the URL on the exceptions
    it raises, so failures here are rebuilt and raised ``from None`` — a chained
    cause would print the credential into any log that formats the traceback.
    """

    def __init__(self, message: str, status_code: int = 0) -> None:
        super().__init__(message)
        #: The HTTP status, or ``0`` for a transport-level failure.
        self.status_code = status_code


def _retryable(status_code: int) -> bool:
    return status_code == 0 or status_code == 429 or status_code >= 500


def _backoff_seconds(attempt: int) -> float:
    return min(30.0, float(2 ** (attempt - 1)))


def post(
    endpoint: str,
    body: bytes,
    *,
    timeout_seconds: float,
    max_retries: int,
    token: Optional[str] = None,
    sleep=time.sleep,
) -> None:
    """Posts one payload, retrying transport failures and 5xx responses."""
    last: Optional[TelemetryError] = None

    for attempt in range(1, max_retries + 2):
        failure = _post_once(endpoint, body, timeout_seconds, token)
        if failure is None:
            return

        if not _retryable(failure.status_code):
            raise failure

        last = failure
        if attempt > max_retries:
            break
        sleep(_backoff_seconds(attempt))

    assert last is not None
    raise last


async def post_async(
    endpoint: str,
    body: bytes,
    *,
    timeout_seconds: float,
    max_retries: int,
    token: Optional[str] = None,
) -> None:
    """The same policy, without blocking the event loop.

    Only the socket work goes to a worker thread; the backoff between attempts is
    a real ``asyncio.sleep``, so a retrying send does not hold a thread for the
    seconds it spends waiting.

    ``urllib`` is blocking and the standard library has no async HTTP client, so a
    thread hop is unavoidable without taking a dependency. It costs one hop per
    flush interval, not per observation.
    """
    last: Optional[TelemetryError] = None

    for attempt in range(1, max_retries + 2):
        failure = await asyncio.to_thread(_post_once, endpoint, body, timeout_seconds, token)
        if failure is None:
            return

        if not _retryable(failure.status_code):
            raise failure

        last = failure
        if attempt > max_retries:
            break
        await asyncio.sleep(_backoff_seconds(attempt))

    assert last is not None
    raise last


def _post_once(
    endpoint: str,
    body: bytes,
    timeout_seconds: float,
    token: Optional[str] = None,
) -> Optional[TelemetryError]:
    headers = {"Content-Type": "application/json; charset=utf-8"}
    if token:
        headers["Authorization"] = f"Bearer {token}"

    request = urllib.request.Request(endpoint, data=body, method="POST", headers=headers)

    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            response.read()  # Drain so the connection can be reused.
        return None
    except urllib.error.HTTPError as error:
        # Rebuilt, never chained: the original names the endpoint.
        status = int(error.code)
        try:
            error.read()
        except Exception:
            pass
        return TelemetryError(f"NetCrunch telemetry send failed with HTTP {status}.", status)
    except (urllib.error.URLError, OSError):
        return TelemetryError(
            "NetCrunch telemetry send failed: the endpoint was unreachable or the request timed out."
        )
