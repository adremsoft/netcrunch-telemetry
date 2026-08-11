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

export function assertEventMessage(message) {
  if (typeof message !== "string") {
    throw new TypeError(`Event message must be a string (got ${describe(message)}).`);
  }
  if (message.trim() === "") {
    throw new TypeError("Event message must not be empty — NetCrunch discards such events without reporting it.");
  }
}
