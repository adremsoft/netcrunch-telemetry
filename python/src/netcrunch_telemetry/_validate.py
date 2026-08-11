"""Local validation.

Every rule here mirrors something the NetCrunch receiver discards *silently* — an
empty status value, a key it reserves, an event with no message. A library that
forwarded those would lose data with nothing raised at either end, so each is
rejected at the call site, where the traceback points at the code that got it
wrong.
"""

from __future__ import annotations

import math
from typing import Any, Mapping, Sequence

MAX_STATUS_KEY_LENGTH = 500

#: Beyond this the receiver slices arrays without telling anyone.
MAX_DATA_ENTRIES = 1024

#: Data object type to the members carrying its payload, and the accepted type set.
DATA_TYPE_MEMBERS = {
    "table": ("columns", "rows"),
    "time-series": ("timestamps", "values"),
    "category": ("categories", "values"),
}


def _describe(value: Any) -> str:
    if value is None:
        return "None"
    if value == "":
        return "an empty string"
    if isinstance(value, str) and value.strip() == "":
        return "a blank string"
    return type(value).__name__


def _is_number(value: Any) -> bool:
    # bool is a subclass of int, and True silently becoming 1 is never what anyone
    # meant to report.
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def counter_path(obj: Any, counter: Any) -> None:
    if not isinstance(obj, str) or obj.strip() == "":
        raise TypeError(f"Counter object is required and must be a non-empty string (got {_describe(obj)}).")
    if not isinstance(counter, str) or counter.strip() == "":
        raise TypeError(f"Counter name is required and must be a non-empty string (got {_describe(counter)}).")


def counter_instance(instance: Any) -> None:
    if instance is None:
        return
    if not isinstance(instance, str):
        raise TypeError(f"Counter instance must be a string (got {_describe(instance)}).")


def counter_value(value: Any) -> None:
    if not _is_number(value):
        raise TypeError(f"Counter value must be a number (got {_describe(value)}). Use status() for text.")
    # NaN and infinity have no JSON representation; json.dumps writes them as
    # literals no other parser will accept.
    if not math.isfinite(value):
        raise ValueError(f"Counter value must be finite (got {value}).")


def status_key(key: Any) -> None:
    if not isinstance(key, str) or key.strip() == "":
        raise TypeError(f"Status key is required and must be a non-empty string (got {_describe(key)}).")
    if key.startswith("@"):
        raise ValueError(f'Status key "{key}" is reserved — NetCrunch uses the "@" prefix internally.')
    if len(key) > MAX_STATUS_KEY_LENGTH:
        raise ValueError(
            f"Status key is {len(key)} characters; NetCrunch truncates at {MAX_STATUS_KEY_LENGTH}."
        )


def status_value(value: Any) -> None:
    if not isinstance(value, str):
        raise TypeError(f"Status value must be a string (got {_describe(value)}). Use counter() for numbers.")
    if value == "":
        raise ValueError(
            "Status value must not be empty — NetCrunch discards empty statuses without reporting it."
        )


def event_message(message: Any) -> None:
    if not isinstance(message, str):
        raise TypeError(f"Event message must be a string (got {_describe(message)}).")
    if message.strip() == "":
        raise ValueError(
            "Event message must not be empty — NetCrunch discards such events without reporting it."
        )


def _sequence_length(value: Any) -> "int | None":
    # str and bytes are sequences too, and silently sending a string as a column
    # list would produce a table of single characters.
    if isinstance(value, (str, bytes)) or not isinstance(value, Sequence):
        return None
    return len(value)


def data_object(object_id: Any, object_type: Any, members: Mapping[str, Any]) -> None:
    """Checks a data object against spec/v1.md section 6."""
    if not isinstance(object_id, str) or object_id.strip() == "":
        raise TypeError(
            f"Data object id is required and must be a non-empty string (got {_describe(object_id)})."
        )

    if object_type == "internal":
        raise ValueError('The "internal" data object type is reserved for NetCrunch\'s own sensors.')

    required = DATA_TYPE_MEMBERS.get(object_type)
    if required is None:
        known = ", ".join(DATA_TYPE_MEMBERS)
        raise ValueError(
            f'Unknown data object type "{object_type}" — NetCrunch discards these with only a '
            f"server-side warning. Use one of: {known}."
        )

    lengths = {}
    for member in required:
        length = _sequence_length(members.get(member))
        if length is None:
            raise TypeError(f'A {object_type} data object requires "{member}" to be a sequence.')
        if length > MAX_DATA_ENTRIES:
            raise ValueError(
                f'"{member}" has {length} entries; NetCrunch truncates at '
                f"{MAX_DATA_ENTRIES} without reporting it."
            )
        lengths[member] = length

    # Ragged parallel arrays are the dangerous case: nothing errors anywhere and
    # the chart quietly plots the wrong thing.
    if object_type == "table":
        width = lengths["columns"]
        for index, row in enumerate(members["rows"]):
            cells = _sequence_length(row)
            if cells is None:
                raise TypeError(f"Table row {index} must be a sequence of cells.")
            if cells != width:
                raise ValueError(f"Table row {index} has {cells} cells but there are {width} columns.")
        return

    left, right = required
    if lengths[left] != lengths[right]:
        raise ValueError(
            f'"{left}" has {lengths[left]} entries but "{right}" has {lengths[right]}; they must match.'
        )
