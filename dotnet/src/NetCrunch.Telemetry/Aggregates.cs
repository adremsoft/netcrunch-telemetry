namespace NetCrunch.Telemetry;

/// <summary>
/// Holds one against a counter for as long as it is undisposed.
/// </summary>
/// <remarks>
/// <code>
/// using var lease = stats.SelfCount("Pool", "Leases Active");
/// </code>
/// <para>
/// Disposal is idempotent. That matters more than it looks: <c>using</c> and an explicit
/// <see cref="Dispose"/> can both fire on the same instance, and a double decrement drives the value
/// negative — which drifts away from every threshold rather than towards one, so nothing ever
/// reports it.
/// </para>
/// </remarks>
public sealed class SelfCount : IDisposable
{
    private int _disposed;

    internal SelfCount(Counter counter)
    {
        Counter = counter;
        counter.Increment();
    }

    /// <summary>The counter this instance contributes to.</summary>
    public Counter Counter { get; }

    /// <summary>Releases the held count. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Counter.Decrement();
        }
    }
}

/// <summary>
/// Contributes a movable amount to a counter and withdraws exactly that amount on disposal, however
/// many times it changed in between.
/// </summary>
public sealed class PartCount : IDisposable
{
    // A plain object rather than System.Threading.Lock, which is .NET 9 only.
    private readonly object _gate = new();
    private double _contribution;
    private bool _disposed;

    internal PartCount(Counter counter) => Counter = counter;

    /// <summary>The counter this instance contributes to.</summary>
    public Counter Counter { get; }

    /// <summary>The amount currently contributed.</summary>
    public double Contribution
    {
        get
        {
            lock (_gate)
            {
                return _contribution;
            }
        }
    }

    /// <summary>
    /// Moves this instance's contribution to <paramref name="value"/>, adjusting the counter by the
    /// difference. Repeated calls do not accumulate.
    /// </summary>
    public void Set(double value)
    {
        Validate.CounterValue(value, nameof(value));

        lock (_gate)
        {
            // Not ObjectDisposedException.ThrowIf: that is .NET 7+, and this file
            // also compiles for netstandard2.0.
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            if (value == _contribution)
            {
                return;
            }

            Counter.Add(value - _contribution);
            _contribution = value;
        }
    }

    /// <summary>Withdraws the contribution. Safe to call more than once.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_contribution != 0)
            {
                Counter.Add(-_contribution);
                _contribution = 0;
            }
        }
    }
}

/// <summary>
/// Holds one against a single instance of a counter at a time, moving it as the value changes.
/// </summary>
/// <remarks>
/// "How many workers are in each phase" stays consistent without anyone remembering to decrement the
/// phase being left. Buckets are <em>instances</em> of one counter, so <c>Workers/By Phase.parsing</c>
/// and <c>Workers/By Phase.writing</c> are siblings rather than unrelated counters.
/// </remarks>
public sealed class CategoryCount : IDisposable
{
    private readonly Func<string, Counter> _resolve;
    // A plain object rather than System.Threading.Lock, which is .NET 9 only.
    private readonly object _gate = new();
    private string? _current;
    private bool _disposed;

    internal CategoryCount(Func<string, Counter> resolve) => _resolve = resolve;

    /// <summary>The instance currently held, or <see langword="null"/> if none.</summary>
    public string? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Moves the held count to <paramref name="instance"/>, decrementing whichever instance is being
    /// left. Passing <see langword="null"/> releases the count without holding a new one.
    /// </summary>
    public void Set(string? instance)
    {
        if (instance is not null && instance.Length == 0)
        {
            instance = null;
        }

        lock (_gate)
        {
            // Not ObjectDisposedException.ThrowIf: that is .NET 7+, and this file
            // also compiles for netstandard2.0.
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            if (instance == _current)
            {
                return;
            }

            Release();
            if (instance is not null)
            {
                _resolve(instance).Increment();
                _current = instance;
            }
        }
    }

    /// <summary>Releases the held instance. Safe to call more than once.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release();
        }
    }

    /// <summary>Must be called with <see cref="_gate"/> held.</summary>
    private void Release()
    {
        if (_current is null)
        {
            return;
        }

        _resolve(_current).Decrement();
        _current = null;
    }
}
