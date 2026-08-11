"""Push metrics, states and events from a Python application into NetCrunch.

See spec/v1.md for the wire format and spec/client-model.md for the behaviour
above it.
"""

from ._counters import CategoryCount, Counter, PartCount, SelfCount
from ._telemetry import Telemetry
from ._transport import TelemetryError

__all__ = [
    "CategoryCount",
    "Counter",
    "PartCount",
    "SelfCount",
    "Telemetry",
    "TelemetryError",
]

__version__ = "0.1.0a1"
