using System.Text.Json.Serialization;

namespace NetCrunch.Telemetry;

// Wire shapes. Optional members are nullable so that the serializer's
// WhenWritingNull condition omits them rather than sending null — the receiver
// treats an absent member and a null one differently in places, and "critical":
// false is noise on every status that is fine.

internal sealed class PayloadDto
{
    [JsonPropertyName("retain")]
    public int Retain { get; set; }

    [JsonPropertyName("remove")]
    public int Remove { get; set; }

    [JsonPropertyName("counters")]
    public List<CounterEntryDto>? Counters { get; set; }

    [JsonPropertyName("statuses")]
    public Dictionary<string, StatusEntryDto>? Statuses { get; set; }

    [JsonPropertyName("events")]
    public List<EventEntryDto>? Events { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, Dictionary<string, object?>>? Data { get; set; }

    internal bool IsEmpty => Counters is null && Statuses is null && Events is null && Data is null;
}

internal sealed class CounterPathDto
{
    [JsonPropertyName("object")]
    public required string Object { get; set; }

    [JsonPropertyName("counter")]
    public required string Counter { get; set; }

    [JsonPropertyName("instance")]
    public string? Instance { get; set; }
}

internal sealed class CounterEntryDto
{
    [JsonPropertyName("path")]
    public required CounterPathDto Path { get; set; }

    [JsonPropertyName("value")]
    public double Value { get; set; }
}

internal sealed class StatusEntryDto
{
    [JsonPropertyName("value")]
    public required string Value { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    // Nullable so that a status which is not critical omits the member entirely.
    [JsonPropertyName("critical")]
    public bool? Critical { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

internal sealed class EventEntryDto
{
    [JsonPropertyName("message")]
    public required string Message { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }
}
