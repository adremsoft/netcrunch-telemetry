using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetCrunch.Telemetry;

/// <summary>
/// Stages metrics, states and events in memory and flushes them to NetCrunch as a single payload.
/// </summary>
/// <remarks>
/// Instrumentation only mutates memory. A separate flush snapshots the registry and sends absolute
/// current values, so nothing in a request path touches the network, and one request carries every
/// value — which matters because the receiver caps pending payloads per sensor and discards the
/// overflow without reporting it.
/// <para>Instances are safe for concurrent use.</para>
/// <para>See spec/v1.md for the wire format and spec/client-model.md for the behaviour above it.</para>
/// </remarks>
public sealed class Telemetry : IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly List<Counter> _counterOrder = [];
    private readonly Dictionary<string, StatusEntryDto> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stamp> _timestamps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, object?>> _dataObjects = new(StringComparer.Ordinal);
    private readonly List<EventEntryDto> _events = [];

    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TelemetryOptions _options;

    private int _disposed;

    /// <summary>Creates a registry and, if configured, starts the background flush loop.</summary>
    /// <exception cref="ArgumentException">The endpoint is missing or not an absolute http/https URL.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Retain does not exceed the flush interval.</exception>
    public Telemetry(TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new ArgumentException(
                "Endpoint is required — copy it from the Telemetry sensor form.",
                nameof(options));
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Endpoint must be an absolute http or https URL.", nameof(options));
        }

        if (options.FlushInterval > TimeSpan.Zero && options.Retain <= options.FlushInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Retain ({options.Retain}) must exceed FlushInterval ({options.FlushInterval}), " +
                "or values expire between sends.");
        }

        _options = options;
        _ownsClient = options.HttpClient is null;
        _client = options.HttpClient ?? new HttpClient();

        _loop = options.FlushInterval > TimeSpan.Zero
            ? Task.Run(() => LoopAsync(options.FlushInterval))
            : Task.CompletedTask;
    }

    private readonly record struct Stamp(
        string Object,
        string CounterName,
        string StatusKey,
        DateTimeOffset ObservedAt,
        string StatusValue);

    private async Task LoopAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    await FlushAsync(_stopping.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _options.OnError?.Invoke(error);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
    }

    // -- staging -----------------------------------------------------------

    /// <summary>
    /// Resolves a counter handle. The same object, counter and instance always return the same
    /// handle, so separate parts of a program instrumenting the same thing converge on one value.
    /// </summary>
    public Counter Counter(string @object, string counterName, string? instance = null)
    {
        Validate.CounterPath(@object, counterName);

        var key = string.Concat(@object, "\0", counterName, "\0", instance ?? string.Empty);

        lock (_gate)
        {
            if (_counters.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = new Counter(@object, counterName, instance);
            _counters[key] = created;
            _counterOrder.Add(created);
            return created;
        }
    }

    /// <summary>
    /// Stages a state with an optional explanation. Statuses are what NetCrunch alerting acts on — a
    /// counter on its own raises nothing.
    /// </summary>
    public void Status(string key, string value, string? message = null, bool critical = false, object? data = null)
    {
        Validate.StatusKey(key);
        Validate.StatusValue(value);

        var entry = new StatusEntryDto
        {
            Value = value,
            Message = string.IsNullOrEmpty(message) ? null : message,
            Critical = critical ? true : null,
            Data = data,
        };

        lock (_gate)
        {
            _statuses[key] = entry;
        }
    }

    /// <summary>
    /// Stages a discrete occurrence. Events accumulate and are cleared once sent. Use a status for a
    /// condition that begins and later ends.
    /// </summary>
    public void Event(string message, string? severity = null)
    {
        Validate.EventMessage(message);

        var entry = new EventEntryDto { Message = message, Severity = severity };

        lock (_gate)
        {
            _events.Add(entry);
        }
    }

    /// <summary>Records when something last happened.</summary>
    /// <remarks>
    /// The wire format has no timestamp type, and a raw clock value means nothing outside the process
    /// that produced it. So this becomes two things: an age in seconds, which an alert threshold can
    /// be set on, and a status message carrying the absolute time, for a person to read. The age is
    /// computed at flush time.
    /// </remarks>
    public void Timestamp(
        string @object,
        string counterName,
        string statusKey,
        DateTimeOffset? observedAt = null,
        string statusValue = "OK")
    {
        Validate.CounterPath(@object, counterName);
        Validate.StatusKey(statusKey);
        Validate.StatusValue(statusValue);

        var stamp = new Stamp(@object, counterName, statusKey, observedAt ?? DateTimeOffset.UtcNow, statusValue);

        lock (_gate)
        {
            _timestamps[statusKey] = stamp;
        }
    }

    // -- data objects ------------------------------------------------------

    /// <summary>Stages a data object rendered on the sensor's page.</summary>
    /// <remarks>
    /// The id is the object's identity across payloads: staging the same id again replaces it. There
    /// is no incremental form — a data object is a whole view each time.
    /// </remarks>
    public void Data(string id, DataObject dataObject)
    {
        ArgumentNullException.ThrowIfNull(dataObject);
        Validate.DataObject(id, dataObject.Type, dataObject.Members);

        var encoded = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = dataObject.Type };
        foreach (var member in Validate.DataTypeMembers[dataObject.Type])
        {
            encoded[member] = dataObject.Members[member];
        }

        if (!string.IsNullOrEmpty(dataObject.Name))
        {
            encoded["name"] = dataObject.Name;
        }

        // seriesName labels a plotted series; a table has no series to label.
        if (!string.IsNullOrEmpty(dataObject.SeriesName) && dataObject.Type != "table")
        {
            encoded["seriesName"] = dataObject.SeriesName;
        }

        if (!string.IsNullOrEmpty(dataObject.Message))
        {
            encoded["message"] = dataObject.Message;
        }

        if (!string.IsNullOrEmpty(dataObject.Status))
        {
            encoded["status"] = dataObject.Status;
        }

        lock (_gate)
        {
            _dataObjects[id] = encoded;
        }
    }

    /// <summary>Stages a table.</summary>
    public void Table(string id, TableData table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Data(id, new DataObject
        {
            Type = "table",
            Name = table.Name,
            Message = table.Message,
            Status = table.Status,
            Members = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["columns"] = table.Columns,
                ["rows"] = table.Rows,
            },
        });
    }

    /// <summary>Stages a time chart. Timestamps are epoch milliseconds.</summary>
    public void TimeSeries(string id, TimeSeriesData series)
    {
        ArgumentNullException.ThrowIfNull(series);
        Data(id, new DataObject
        {
            Type = "time-series",
            Name = series.Name,
            SeriesName = series.SeriesName,
            Message = series.Message,
            Status = series.Status,
            Members = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["timestamps"] = series.Timestamps,
                ["values"] = series.Values,
            },
        });
    }

    /// <summary>
    /// Stages a labelled bar chart. Named apart from <see cref="Category"/>, which is the
    /// lifetime-bound aggregate — same word in NetCrunch, unrelated meanings.
    /// </summary>
    public void CategoryChart(string id, CategoryChartData chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        Data(id, new DataObject
        {
            Type = "category",
            Name = chart.Name,
            SeriesName = chart.SeriesName,
            Message = chart.Message,
            Status = chart.Status,
            Members = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["categories"] = chart.Categories,
                ["values"] = chart.Values,
            },
        });
    }

    // -- lifetime-bound aggregates -----------------------------------------

    /// <summary>Holds one against a counter until disposed.</summary>
    public SelfCount SelfCount(string @object, string counterName, string? instance = null)
        => new(Counter(@object, counterName, instance));

    /// <summary>Contributes a movable amount, withdrawn in full on disposal.</summary>
    public PartCount PartCount(string @object, string counterName, string? instance = null)
        => new(Counter(@object, counterName, instance));

    /// <summary>
    /// Holds one against a single instance at a time, moving it as the value changes. For the chart
    /// of the same name see <see cref="CategoryChart"/>.
    /// </summary>
    public CategoryCount Category(string @object, string counterName)
    {
        Validate.CounterPath(@object, counterName);
        return new CategoryCount(instance => Counter(@object, counterName, instance));
    }

    // -- payload -----------------------------------------------------------

    /// <summary>
    /// Builds the JSON a flush would post, without sending it. Members with nothing in them are
    /// omitted rather than sent empty.
    /// </summary>
    public string BuildPayload(DateTimeOffset? snapshotAt = null)
        => JsonSerializer.Serialize(Snapshot(snapshotAt ?? DateTimeOffset.UtcNow), SerializerOptions);

    private PayloadDto Snapshot(DateTimeOffset snapshotAt)
    {
        lock (_gate)
        {
            var payload = new PayloadDto
            {
                Retain = (int)_options.Retain.TotalMinutes,
                Remove = (int)_options.Remove.TotalMinutes,
            };

            if (_counterOrder.Count > 0 || _timestamps.Count > 0)
            {
                payload.Counters = [];
                foreach (var handle in _counterOrder)
                {
                    payload.Counters.Add(new CounterEntryDto
                    {
                        Path = new CounterPathDto
                        {
                            Object = handle.Object,
                            Counter = handle.CounterName,
                            Instance = handle.Instance,
                        },
                        Value = handle.Value,
                    });
                }
            }

            if (_statuses.Count > 0 || _timestamps.Count > 0)
            {
                payload.Statuses = new Dictionary<string, StatusEntryDto>(_statuses, StringComparer.Ordinal);
            }

            // A timestamp contributes to both collections, so it is expanded here rather than at the
            // call site — the age is only meaningful against this snapshot.
            foreach (var stamp in _timestamps.Values)
            {
                payload.Counters!.Add(new CounterEntryDto
                {
                    Path = new CounterPathDto { Object = stamp.Object, Counter = stamp.CounterName },
                    // Away from zero, so a half second rounds the way every other implementation
                    // rounds it; the framework default is banker's rounding.
                    Value = Math.Round((snapshotAt - stamp.ObservedAt).TotalSeconds, MidpointRounding.AwayFromZero),
                });

                payload.Statuses![stamp.StatusKey] = new StatusEntryDto
                {
                    Value = stamp.StatusValue,
                    Message = stamp.ObservedAt.ToUniversalTime()
                        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                };
            }

            if (_events.Count > 0)
            {
                payload.Events = [.. _events];
            }

            if (_dataObjects.Count > 0)
            {
                payload.Data = new Dictionary<string, Dictionary<string, object?>>(_dataObjects, StringComparer.Ordinal);
            }

            return payload;
        }
    }

    // -- sending -----------------------------------------------------------

    /// <summary>Posts everything staged as a single request.</summary>
    /// <remarks>
    /// Concurrent calls serialise rather than run together; each sends the absolute state at the
    /// moment it runs. Events are cleared on success. Counters and statuses are kept, so a
    /// long-running process keeps reporting current values without restating them.
    /// </remarks>
    /// <exception cref="TelemetryException">The send failed. The endpoint is never included.</exception>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = Snapshot(DateTimeOffset.UtcNow);
            if (payload.IsEmpty)
            {
                return;
            }

            var sentEvents = payload.Events?.Count ?? 0;
            var body = JsonSerializer.Serialize(payload, SerializerOptions);

            await PostAsync(body, cancellationToken).ConfigureAwait(false);

            // Trimmed rather than emptied: events staged while the request was in flight have not
            // been sent, and dropping them would lose them silently.
            lock (_gate)
            {
                _events.RemoveRange(0, Math.Min(sentEvents, _events.Count));
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task PostAsync(string body, CancellationToken cancellationToken)
    {
        TelemetryException? last = null;

        for (var attempt = 1; attempt <= _options.MaxRetries + 1; attempt++)
        {
            var failure = await PostOnceAsync(body, cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                return;
            }

            var retryable = failure.StatusCode == 0
                || failure.StatusCode == 429
                || failure.StatusCode >= 500;

            if (!retryable)
            {
                throw failure;
            }

            last = failure;
            if (attempt > _options.MaxRetries)
            {
                break;
            }

            var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1)));
            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
        }

        throw last!;
    }

    private async Task<TelemetryException?> PostOnceAsync(string body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _client
                .PostAsync(_options.Endpoint, content, timeout.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            return new TelemetryException(
                $"NetCrunch telemetry send failed with HTTP {(int)response.StatusCode}.",
                (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Rebuilt, never wrapped: the original names the endpoint, and that URL is currently the
            // credential.
            var reason = timeout.IsCancellationRequested
                ? $"timed out after {_options.Timeout.TotalSeconds:0.#}s"
                : "the endpoint was unreachable";
            return new TelemetryException($"NetCrunch telemetry send failed: {reason}.");
        }
    }

    /// <summary>Discards everything staged.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _counters.Clear();
            _counterOrder.Clear();
            _statuses.Clear();
            _timestamps.Clear();
            _dataObjects.Clear();
            _events.Clear();
        }
    }

    /// <summary>Stops the flush loop and flushes once more, swallowing any failure.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (TelemetryException error)
        {
            _options.OnError?.Invoke(error);
        }

        Cleanup();
    }

    /// <summary>
    /// Stops the flush loop without a final send. Prefer <see cref="DisposeAsync"/>, which flushes
    /// what is still staged.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        Cleanup();
    }

    private void Cleanup()
    {
        _stopping.Dispose();
        _flushGate.Dispose();
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
