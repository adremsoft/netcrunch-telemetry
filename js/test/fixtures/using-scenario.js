/**
 * Real `using` syntax, kept in its own file.
 *
 * Explicit resource management is a syntax feature, so a runtime without it fails
 * at parse time, not at call time. Isolating it here means the rest of the suite
 * still runs on Node 20 — the module is only imported once support is confirmed.
 */

export function runUsingScenario(stats) {
  const handle = stats.counter("Pool", "Leases Active");
  const observed = {};

  {
    using outer = stats.selfCount("Pool", "Leases Active");
    observed.insideOuter = handle.value;
    {
      using inner = stats.selfCount("Pool", "Leases Active");
      observed.insideInner = handle.value;
    }
    observed.afterInner = handle.value;
  }
  observed.afterOuter = handle.value;

  return observed;
}

export function runUsingWithThrow(stats) {
  const handle = stats.counter("Pool", "Leases Active");
  try {
    using lease = stats.selfCount("Pool", "Leases Active");
    throw new Error("boom");
  } catch {
    // Swallowed — the point is whether the decrement still happened.
  }
  return handle.value;
}
