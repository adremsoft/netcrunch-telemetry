using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace NetCrunch.Telemetry.Tests;

/// <summary>
/// Runs the shared conformance suite. Fixtures live in conformance/cases at the repository root and
/// are shared with every other implementation, so "compatible with the spec" means the same thing in
/// each.
/// </summary>
public sealed class ConformanceTests(ITestOutputHelper output)
{
    // Placeholder only — never a real installation's endpoint. See CONTRIBUTING.md.
    private const string TestEndpoint = "https://netcrunch.example/api/rest/1/sensors/example@1/update";

    private static readonly string CasesDirectory = FindCases();

    private static string FindCases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "conformance", "cases");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("conformance/cases not found above the test assembly.");
    }

    public static TheoryData<string> CaseFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(CasesDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseFiles))]
    public void Case(string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(CasesDirectory, fileName)));
        var root = document.RootElement;

        if (root.TryGetProperty("rejects", out var rejects))
        {
            RunRejections(rejects);
            return;
        }

        using var stats = Connect(root);

        if (root.TryGetProperty("operations", out var operations))
        {
            RunOperations(stats, operations);
        }
        else if (root.TryGetProperty("snapshot", out var snapshot))
        {
            ApplySnapshot(stats, snapshot);
        }

        if (root.TryGetProperty("expect", out var expect))
        {
            ComparePayload(stats, root, expect);
        }
    }

    private static Telemetry Connect(JsonElement root)
    {
        var retain = TimeSpan.FromMinutes(5);
        var remove = TimeSpan.FromDays(1);

        if (root.TryGetProperty("options", out var options))
        {
            if (options.TryGetProperty("retainMinutes", out var retainMinutes))
            {
                retain = TimeSpan.FromMinutes(retainMinutes.GetInt32());
            }

            if (options.TryGetProperty("removeMinutes", out var removeMinutes))
            {
                remove = TimeSpan.FromMinutes(removeMinutes.GetInt32());
            }
        }

        return new Telemetry(new TelemetryOptions { Endpoint = TestEndpoint, Retain = retain, Remove = remove });
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Converts a fixture record into the generic form. The type is carried through untouched, so an
    /// unknown-type rejection fails for the reason the fixture states rather than incidentally.
    /// </summary>
    private static (string Id, DataObject Object) ReadDataObject(JsonElement element)
    {
        var members = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in new[] { "columns", "rows", "timestamps", "values", "categories" })
        {
            if (element.TryGetProperty(member, out var array))
            {
                members[member] = ReadArray(array);
            }
        }

        return (Text(element, "id") ?? string.Empty, new DataObject
        {
            Type = Text(element, "type") ?? string.Empty,
            Name = Text(element, "name"),
            SeriesName = Text(element, "seriesName"),
            Message = Text(element, "message"),
            Status = Text(element, "status"),
            Members = members,
        });
    }

    private static List<object?> ReadArray(JsonElement array)
    {
        var items = new List<object?>();
        foreach (var item in array.EnumerateArray())
        {
            items.Add(item.ValueKind switch
            {
                JsonValueKind.Array => ReadArray(item),
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Number => item.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            });
        }

        return items;
    }

    private static void ApplySnapshot(Telemetry stats, JsonElement snapshot)
    {
        if (snapshot.TryGetProperty("counters", out var counters))
        {
            foreach (var entry in counters.EnumerateArray())
            {
                stats.Counter(Text(entry, "object")!, Text(entry, "counter")!, Text(entry, "instance"))
                    .Set(entry.GetProperty("value").GetDouble());
            }
        }

        if (snapshot.TryGetProperty("statuses", out var statuses))
        {
            foreach (var entry in statuses.EnumerateArray())
            {
                object? data = null;
                if (entry.TryGetProperty("data", out var dataElement))
                {
                    data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dataElement.GetRawText());
                }

                stats.Status(
                    Text(entry, "key")!,
                    Text(entry, "value")!,
                    Text(entry, "message"),
                    entry.TryGetProperty("critical", out var critical) && critical.GetBoolean(),
                    data);
            }
        }

        if (snapshot.TryGetProperty("events", out var events))
        {
            foreach (var entry in events.EnumerateArray())
            {
                stats.Event(Text(entry, "message")!, Text(entry, "severity"));
            }
        }

        if (snapshot.TryGetProperty("data", out var data2))
        {
            foreach (var entry in data2.EnumerateArray())
            {
                var (id, dataObject) = ReadDataObject(entry);
                stats.Data(id, dataObject);
            }
        }

        if (snapshot.TryGetProperty("timestamps", out var timestamps))
        {
            foreach (var entry in timestamps.EnumerateArray())
            {
                stats.Timestamp(
                    Text(entry, "object")!,
                    Text(entry, "counter")!,
                    Text(entry, "statusKey")!,
                    DateTimeOffset.Parse(Text(entry, "observedAt")!, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    private static void RunOperations(Telemetry stats, JsonElement operations)
    {
        var aggregates = new Dictionary<string, IDisposable>(StringComparer.Ordinal);

        IDisposable Lookup(JsonElement op)
        {
            var id = Text(op, "id")!;
            Assert.True(aggregates.ContainsKey(id), $"no aggregate bound to id \"{id}\"");
            return aggregates[id];
        }

        foreach (var op in operations.EnumerateArray())
        {
            var name = Text(op, "op");
            var @object = Text(op, "object");
            var counter = Text(op, "counter");
            var instance = Text(op, "instance");

            switch (name)
            {
                case "counter":
                    var handle = stats.Counter(@object!, counter!, instance);
                    if (op.TryGetProperty("set", out var set))
                    {
                        handle.Set(set.GetDouble());
                    }

                    break;

                case "selfCount":
                    aggregates[Text(op, "id")!] = stats.SelfCount(@object!, counter!, instance);
                    break;

                case "partCount":
                    aggregates[Text(op, "id")!] = stats.PartCount(@object!, counter!, instance);
                    break;

                case "category":
                    aggregates[Text(op, "id")!] = stats.Category(@object!, counter!);
                    break;

                case "set":
                    var value = op.GetProperty("value");
                    switch (Lookup(op))
                    {
                        case PartCount part:
                            part.Set(value.GetDouble());
                            break;
                        case CategoryCount category:
                            // null clears the held instance.
                            category.Set(value.ValueKind == JsonValueKind.Null ? null : value.GetString());
                            break;
                        default:
                            Assert.Fail("set is not defined for this aggregate");
                            break;
                    }

                    break;

                case "dispose":
                    Lookup(op).Dispose();
                    break;

                case "assert":
                    var label = $"{@object}/{counter}" + (instance is null ? "" : $".{instance}");
                    Assert.Equal(
                        op.GetProperty("value").GetDouble(),
                        stats.Counter(@object!, counter!, instance).Value);
                    _ = label;
                    break;

                default:
                    Assert.Fail($"unknown operation \"{name}\"");
                    break;
            }
        }
    }

    private void ComparePayload(Telemetry stats, JsonElement root, JsonElement expect)
    {
        var snapshotAt = DateTimeOffset.UtcNow;
        if (root.TryGetProperty("options", out var options) &&
            options.TryGetProperty("snapshotAt", out var at))
        {
            snapshotAt = DateTimeOffset.Parse(at.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Compared in the shape that goes over the wire, not as live objects.
        var actual = Normalize(JsonDocument.Parse(stats.BuildPayload(snapshotAt)).RootElement);
        var wanted = Normalize(expect);

        if (actual != wanted)
        {
            output.WriteLine("--- got ---");
            output.WriteLine(actual);
            output.WriteLine("--- want ---");
            output.WriteLine(wanted);
        }

        Assert.Equal(wanted, actual);
    }

    /// <summary>
    /// Renders JSON with object members sorted, and the one array whose order is not significant —
    /// counters — sorted by path.
    /// </summary>
    private static string Normalize(JsonElement element)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            Write(writer, element, null);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());

        static void Write(Utf8JsonWriter writer, JsonElement value, string? propertyName)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(writer, property.Value, property.Name);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    var items = value.EnumerateArray().ToList();
                    if (propertyName == "counters")
                    {
                        items = [.. items.OrderBy(CounterKey, StringComparer.Ordinal)];
                    }

                    foreach (var item in items)
                    {
                        Write(writer, item, null);
                    }

                    writer.WriteEndArray();
                    break;

                default:
                    value.WriteTo(writer);
                    break;
            }
        }

        static string CounterKey(JsonElement entry)
        {
            var path = entry.GetProperty("path");
            return string.Join(
                "|",
                path.GetProperty("object").GetString(),
                path.GetProperty("counter").GetString(),
                path.TryGetProperty("instance", out var instance) ? instance.GetString() : string.Empty);
        }
    }

    /// <summary>
    /// Reports why the type system makes a fixture input impossible to express, if it does.
    /// Saying so is not the same as passing: it means the invalid state cannot be constructed, not
    /// that it was caught.
    /// </summary>
    private static string? Unrepresentable(JsonElement reject)
    {
        var kind = Text(reject, "kind");
        var input = reject.GetProperty("input");

        if (kind == "counter" && input.TryGetProperty("value", out var counterValue) &&
            counterValue.ValueKind != JsonValueKind.Number)
        {
            return "counter values are double; a non-numeric value cannot be passed";
        }

        if (kind == "status" && input.TryGetProperty("value", out var statusValue) &&
            statusValue.ValueKind != JsonValueKind.String)
        {
            return "status values are string; a non-string value cannot be passed";
        }

        return null;
    }

    private void RunRejections(JsonElement rejects)
    {
        foreach (var reject in rejects.EnumerateArray())
        {
            var reason = Text(reject, "reason");
            var why = Unrepresentable(reject);
            if (why is not null)
            {
                output.WriteLine($"UNREPRESENTABLE  {reason}\n                 {why}");
                continue;
            }

            using var stats = new Telemetry(new TelemetryOptions { Endpoint = TestEndpoint });
            var input = reject.GetProperty("input");

            var thrown = Record.Exception(() =>
            {
                switch (Text(reject, "kind"))
                {
                    case "counter":
                        stats.Counter(Text(input, "object") ?? string.Empty, Text(input, "counter") ?? string.Empty)
                            .Set(input.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number
                                ? v.GetDouble()
                                : 0);
                        break;

                    case "status":
                        stats.Status(Text(input, "key") ?? string.Empty, Text(input, "value") ?? string.Empty);
                        break;

                    case "event":
                        stats.Event(Text(input, "message") ?? string.Empty);
                        break;

                    case "data":
                        var (id, dataObject) = ReadDataObject(input);
                        stats.Data(id, dataObject);
                        break;
                }
            });

            Assert.True(thrown is not null, $"accepted an input the receiver would discard silently: {reason}");
            output.WriteLine($"rejected with: {thrown!.Message.Split('\n')[0]}");
        }
    }
}
