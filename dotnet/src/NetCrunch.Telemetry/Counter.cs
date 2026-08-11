namespace NetCrunch.Telemetry;

/// <summary>
/// A resolved counter. Resolve it once, keep it, and mutate it on the hot path.
/// </summary>
/// <remarks>
/// The cost of an observation is one interlocked operation, with no name lookup and no allocation.
/// That is the difference between instrumentation you can afford inside a loop and instrumentation
/// somebody takes back out later.
/// <para>Instances are safe for concurrent use.</para>
/// </remarks>
public sealed class Counter
{
    private double _value;

    internal Counter(string @object, string counterName, string? instance)
    {
        Object = @object;
        CounterName = counterName;
        Instance = string.IsNullOrEmpty(instance) ? null : instance;
    }

    /// <summary>The object this counter belongs to.</summary>
    public string Object { get; }

    /// <summary>The measurement name.</summary>
    public string CounterName { get; }

    /// <summary>The instance, or <see langword="null"/> when the counter has none.</summary>
    public string? Instance { get; }

    /// <summary>The current value.</summary>
    public double Value => Volatile.Read(ref _value);

    /// <summary>Replaces the value.</summary>
    public void Set(double value)
    {
        Validate.CounterValue(value, nameof(value));
        Interlocked.Exchange(ref _value, value);
    }

    /// <summary>Adds <paramref name="delta"/>, which may be negative.</summary>
    public void Add(double delta)
    {
        Validate.CounterValue(delta, nameof(delta));

        double initial, computed;
        do
        {
            initial = Volatile.Read(ref _value);
            computed = initial + delta;
        }
        while (Interlocked.CompareExchange(ref _value, computed, initial) != initial);
    }

    /// <summary>Adds one.</summary>
    public void Increment() => Add(1);

    /// <summary>Subtracts one.</summary>
    public void Decrement() => Add(-1);

    /// <summary>Raises the value to <paramref name="value"/> if that is higher, and leaves it otherwise.</summary>
    public void Max(double value)
    {
        Validate.CounterValue(value, nameof(value));

        double initial;
        do
        {
            initial = Volatile.Read(ref _value);
            if (initial >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _value, value, initial) != initial);
    }

    /// <summary>Lowers the value to <paramref name="value"/> if that is lower.</summary>
    public void Min(double value)
    {
        Validate.CounterValue(value, nameof(value));

        double initial;
        do
        {
            initial = Volatile.Read(ref _value);
            if (initial <= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _value, value, initial) != initial);
    }

    /// <summary>
    /// Sets the value back to zero. The counter keeps being reported — see spec/client-model.md
    /// section 4 on why zero and absent differ.
    /// </summary>
    public void Reset() => Set(0);
}
