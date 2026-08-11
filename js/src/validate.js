/**
 * Local validation.
 *
 * Every rule here mirrors something the NetCrunch receiver discards *silently* —
 * an empty status value, a status key it reserves, an event with no message. A
 * library that forwarded those would lose data with nothing raised at either end,
 * so each is rejected at the call site instead, where the stack trace points at
 * the code that got it wrong.
 *
 * See spec/v1.md sections 3 to 5.
 */

export const MAX_STATUS_KEY_LENGTH = 500;

function describe(value) {
  if (value === null) return "null";
  if (value === undefined) return "undefined";
  if (value === "") return "an empty string";
  if (typeof value === "string" && value.trim() === "") return "a blank string";
  return typeof value;
}

export function assertCounterPath(object, counter) {
  if (typeof object !== "string" || object.trim() === "") {
    throw new TypeError(`Counter object is required and must be a non-empty string (got ${describe(object)}).`);
  }
  if (typeof counter !== "string" || counter.trim() === "") {
    throw new TypeError(`Counter name is required and must be a non-empty string (got ${describe(counter)}).`);
  }
}

export function assertCounterInstance(instance) {
  if (instance === undefined || instance === null) return;
  if (typeof instance !== "string") {
    throw new TypeError(`Counter instance must be a string (got ${describe(instance)}).`);
  }
}

export function assertCounterValue(value) {
  if (typeof value !== "number") {
    throw new TypeError(`Counter value must be a number (got ${describe(value)}). Use status() for text.`);
  }
  // NaN and Infinity have no JSON representation — JSON.stringify turns them into
  // null, which the receiver would take as a legitimate value.
  if (!Number.isFinite(value)) {
    throw new RangeError(`Counter value must be finite (got ${value}).`);
  }
}

export function assertStatusKey(key) {
  if (typeof key !== "string" || key.trim() === "") {
    throw new TypeError(`Status key is required and must be a non-empty string (got ${describe(key)}).`);
  }
  if (key.startsWith("@")) {
    throw new TypeError(`Status key "${key}" is reserved — NetCrunch uses the "@" prefix internally.`);
  }
  if (key.length > MAX_STATUS_KEY_LENGTH) {
    throw new RangeError(`Status key is ${key.length} characters; NetCrunch truncates at ${MAX_STATUS_KEY_LENGTH}.`);
  }
}

export function assertStatusValue(value) {
  if (typeof value !== "string") {
    throw new TypeError(`Status value must be a string (got ${describe(value)}). Use counter() for numbers.`);
  }
  if (value === "") {
    throw new TypeError("Status value must not be empty — NetCrunch discards empty statuses without reporting it.");
  }
}

/** Beyond this the receiver slices arrays without telling anyone. */
export const MAX_DATA_ENTRIES = 1024;

/** Type to the members that carry its payload. Also the set of accepted types. */
export const DATA_TYPE_MEMBERS = {
  table: ["columns", "rows"],
  "time-series": ["timestamps", "values"],
  category: ["categories", "values"],
};

const DATA_TYPES = DATA_TYPE_MEMBERS;

export function assertDataObject(id, type, details) {
  if (typeof id !== "string" || id.trim() === "") {
    throw new TypeError(`Data object id is required and must be a non-empty string (got ${describe(id)}).`);
  }

  if (type === "internal") {
    throw new TypeError('The "internal" data object type is reserved for NetCrunch\'s own sensors.');
  }

  const required = DATA_TYPES[type];
  if (required === undefined) {
    const known = Object.keys(DATA_TYPES).join(", ");
    throw new TypeError(
      `Unknown data object type "${type}". NetCrunch discards these with only a server-side warning. Use one of: ${known}.`
    );
  }

  for (const member of required) {
    if (!Array.isArray(details[member])) {
      throw new TypeError(`A ${type} data object requires "${member}" to be an array.`);
    }
    if (details[member].length > MAX_DATA_ENTRIES) {
      throw new RangeError(
        `"${member}" has ${details[member].length} entries; NetCrunch truncates at ${MAX_DATA_ENTRIES} without reporting it.`
      );
    }
  }

  // Ragged parallel arrays are the dangerous case: nothing errors anywhere, and
  // the chart quietly plots the wrong thing.
  if (type === "table") {
    const width = details.columns.length;
    details.rows.forEach((row, index) => {
      if (!Array.isArray(row)) {
        throw new TypeError(`Table row ${index} must be an array of cells.`);
      }
      if (row.length !== width) {
        throw new RangeError(
          `Table row ${index} has ${row.length} cells but there are ${width} columns.`
        );
      }
    });
  } else {
    const [left, right] = required;
    if (details[left].length !== details[right].length) {
      throw new RangeError(
        `"${left}" has ${details[left].length} entries but "${right}" has ${details[right].length}; they must match.`
      );
    }
  }
}

export function assertEventMessage(message) {
  if (typeof message !== "string") {
    throw new TypeError(`Event message must be a string (got ${describe(message)}).`);
  }
  if (message.trim() === "") {
    throw new TypeError("Event message must not be empty — NetCrunch discards such events without reporting it.");
  }
}
