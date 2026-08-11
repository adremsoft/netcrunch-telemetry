"""Counter handles and lifetime-bound aggregates.

A counter is resolved once and kept, so the hot path is a numeric mutation with
no name lookup. The aggregates make "how many X are currently in state Y" correct
by construction: the decrement is tied to a scope rather than to a line of code
someone has to remember to write.
"""

from __future__ import annotations

import threading
import warnings
from typing import Callable, Optional

from . import _validate


class Counter:
    """A single counter. Resolve it once, keep it, and mutate it on the hot path.

    Instances are safe for concurrent use. A lock rather than a bare ``+=``,
    because read-modify-write is not atomic under the GIL and lost updates in a
    gauge are invisible once they happen.
    """

    __slots__ = ("object", "counter", "instance", "_value", "_lock")

    def __init__(self, obj: str, counter: str, instance: Optional[str] = None) -> None:
        self.object = obj
        self.counter = counter
        self.instance = instance or None
        self._value: float = 0.0
        self._lock = threading.Lock()

    @property
    def value(self) -> float:
        """The current value."""
        with self._lock:
            return self._value

    def set(self, value: float) -> "Counter":
        """Replaces the value."""
        _validate.counter_value(value)
        with self._lock:
            self._value = value
        return self

    def add(self, delta: float) -> "Counter":
        """Adds ``delta``, which may be negative."""
        _validate.counter_value(delta)
        with self._lock:
            self._value += delta
        return self

    def inc(self, by: float = 1) -> "Counter":
        """Adds ``by``, one by default."""
        return self.add(by)

    def dec(self, by: float = 1) -> "Counter":
        """Subtracts ``by``, one by default."""
        return self.add(-by)

    def max(self, value: float) -> "Counter":
        """Raises the value to ``value`` if that is higher, and leaves it otherwise."""
        _validate.counter_value(value)
        with self._lock:
            if value > self._value:
                self._value = value
        return self

    def min(self, value: float) -> "Counter":
        """Lowers the value to ``value`` if that is lower."""
        _validate.counter_value(value)
        with self._lock:
            if value < self._value:
                self._value = value
        return self

    def reset(self) -> "Counter":
        """Sets the value back to zero.

        The counter keeps being reported — see spec/client-model.md section 4 on
        why zero and absent differ.
        """
        return self.set(0)

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        path = f"{self.object}/{self.counter}"
        if self.instance:
            path += f".{self.instance}"
        return f"<Counter {path} = {self.value}>"


class _Aggregate:
    """Shared plumbing.

    Closing is idempotent: a ``with`` block and an explicit :meth:`close` can both
    fire on the same object, and a double decrement drives the value negative —
    which drifts away from every threshold rather than towards one, so nothing
    ever reports it.
    """

    __slots__ = ("_closed", "_lock", "_warn_on_leak", "_label", "__weakref__")

    def __init__(self, warn_on_leak: bool, label: str) -> None:
        self._closed = False
        self._lock = threading.Lock()
        self._warn_on_leak = warn_on_leak
        self._label = label

    @property
    def closed(self) -> bool:
        return self._closed

    def _release(self) -> None:  # pragma: no cover - overridden
        raise NotImplementedError

    def close(self) -> None:
        """Releases the held contribution. Safe to call more than once."""
        with self._lock:
            if self._closed:
                return
            self._closed = True
            self._release()

    def __enter__(self) -> "_Aggregate":
        return self

    def __exit__(self, *exc_info: object) -> bool:
        self.close()
        return False

    def __del__(self) -> None:
        # A warning, never the decrement. CPython's refcounting makes this fire
        # promptly in practice, but that is an implementation detail rather than a
        # language guarantee and it does not hold on PyPy, so it cannot be the
        # source of truth for a live count.
        #
        # ResourceWarning is the right category and is silenced by default,
        # surfacing under `python -X dev` and in test runs — on where it helps,
        # quiet where it would be noise.
        try:
            if not self._warn_on_leak or self._closed:
                return
            warnings.warn(
                f"{self._label} was collected without being closed, so its contribution is stuck. "
                "Use it as a context manager, or call close().",
                ResourceWarning,
                stacklevel=2,
            )
        except Exception:  # pragma: no cover - a failed __init__, or interpreter shutdown
            pass


class SelfCount(_Aggregate):
    """Holds one against a counter for as long as it is open.

    ::

        with stats.self_count("Pool", "Leases Active"):
            ...
    """

    __slots__ = ("counter",)

    def __init__(self, counter: Counter, warn_on_leak: bool) -> None:
        super().__init__(warn_on_leak, f"SelfCount on {counter.object}/{counter.counter}")
        self.counter = counter
        counter.inc()

    def _release(self) -> None:
        self.counter.dec()


class PartCount(_Aggregate):
    """Contributes a movable amount, withdrawn in full on close."""

    __slots__ = ("counter", "_contribution")

    def __init__(self, counter: Counter, warn_on_leak: bool) -> None:
        super().__init__(warn_on_leak, f"PartCount on {counter.object}/{counter.counter}")
        self.counter = counter
        self._contribution = 0.0

    @property
    def contribution(self) -> float:
        """The amount currently contributed."""
        return self._contribution

    def set(self, value: float) -> "PartCount":
        """Moves this instance's contribution, adjusting the counter by the difference."""
        _validate.counter_value(value)
        with self._lock:
            if self._closed:
                raise ValueError("This part count is closed.")
            if value == self._contribution:
                return self
            self.counter.add(value - self._contribution)
            self._contribution = value
        return self

    def _release(self) -> None:
        if self._contribution:
            self.counter.add(-self._contribution)
            self._contribution = 0.0


class CategoryCount(_Aggregate):
    """Holds one against a single instance of a counter at a time.

    "How many workers are in each phase" stays consistent without anyone
    remembering to decrement the phase being left. Buckets are *instances* of one
    counter, so ``Workers/By Phase.parsing`` and ``Workers/By Phase.writing`` are
    siblings rather than unrelated counters.
    """

    __slots__ = ("_resolve", "_current")

    def __init__(self, resolve: Callable[[str], Counter], obj: str, counter: str, warn_on_leak: bool) -> None:
        super().__init__(warn_on_leak, f"CategoryCount on {obj}/{counter}")
        self._resolve = resolve
        self._current: Optional[str] = None

    @property
    def current(self) -> Optional[str]:
        """The instance currently held, or ``None``."""
        return self._current

    def set(self, instance: Optional[str]) -> "CategoryCount":
        """Moves the held count, decrementing whichever instance is being left.

        ``None`` releases the count without holding a new one.
        """
        if instance is not None and not isinstance(instance, str):
            raise TypeError(f"Category instance must be a string or None (got {type(instance).__name__}).")
        if instance == "":
            instance = None

        with self._lock:
            if self._closed:
                raise ValueError("This category count is closed.")
            if instance == self._current:
                return self
            self._release()
            if instance is not None:
                self._resolve(instance).inc()
                self._current = instance
        return self

    def _release(self) -> None:
        if self._current is None:
            return
        self._resolve(self._current).dec()
        self._current = None
