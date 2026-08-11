using System.Collections;

namespace NetCrunch.Telemetry;

/// <summary>
/// Local validation.
/// </summary>
/// <remarks>
/// Every rule here mirrors something the NetCrunch receiver discards <em>silently</em> — an empty
/// status value, a key it reserves, an event with no message. A library that forwarded those would
/// lose data with nothing raised at either end, so each throws at the call site, where the stack
/// trace points at the code that got it wrong.
/// <para>
/// The type system removes several checks the dynamically typed implementations need: a counter
/// value cannot be a string, and a status value cannot be a number, because the signatures do not
/// allow it.
/// </para>
/// </remarks>
internal static class Validate
{
    internal const int MaxStatusKeyLength = 500;

    /// <summary>Beyond this the receiver slices arrays without telling anyone.</summary>
    internal const int MaxDataEntries = 1024;

    /// <summary>Data object type to the members carrying its payload, and the accepted type set.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> DataTypeMembers =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["table"] = ["columns", "rows"],
            ["time-series"] = ["timestamps", "values"],
            ["category"] = ["categories", "values"],
        };

    internal static void CounterPath(string? @object, string? counter)
    {
        if (string.IsNullOrWhiteSpace(@object))
        {
            throw new ArgumentException("Counter object is required and must not be empty.", nameof(@object));
        }

        if (string.IsNullOrWhiteSpace(counter))
        {
            throw new ArgumentException("Counter name is required and must not be empty.", nameof(counter));
        }
    }

    internal static void CounterValue(double value, string parameterName)
    {
        // NaN and infinity have no JSON representation; System.Text.Json writes them as
        // literals no parser will accept, and a silent zero would be worse than the throw.
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Counter value must be finite.");
        }
    }

    internal static void StatusKey(string? key)
    {
        // Spelled out rather than string.IsNullOrWhiteSpace, which is annotated
        // with [NotNullWhen(false)] only on modern targets. On netstandard2.0 the
        // compiler would not learn that key is non-null here, and the dereference
        // below would warn.
        if (key is null || key.Trim().Length == 0)
        {
            throw new ArgumentException("Status key is required and must not be empty.", nameof(key));
        }

        // The char overload is .NET Core only; this file also targets netstandard2.0.
        if (key.StartsWith("@", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Status key \"{key}\" is reserved — NetCrunch uses the \"@\" prefix internally.",
                nameof(key));
        }

        if (key.Length > MaxStatusKeyLength)
        {
            throw new ArgumentException(
                $"Status key is {key.Length} characters; NetCrunch truncates at {MaxStatusKeyLength}.",
                nameof(key));
        }
    }

    internal static void StatusValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException(
                "Status value must not be empty — NetCrunch discards empty statuses without reporting it.",
                nameof(value));
        }
    }

    internal static void EventMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException(
                "Event message must not be empty — NetCrunch discards such events without reporting it.",
                nameof(message));
        }
    }

    /// <summary>Checks a data object against spec/v1.md section 6.</summary>
    internal static void DataObject(string? id, string? type, IReadOnlyDictionary<string, object?> members)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Data object id is required and must not be empty.", nameof(id));
        }

        if (type == "internal")
        {
            throw new ArgumentException(
                "The \"internal\" data object type is reserved for NetCrunch's own sensors.",
                nameof(type));
        }

        if (type is null || !DataTypeMembers.TryGetValue(type, out var required))
        {
            throw new ArgumentException(
                $"Unknown data object type \"{type}\" — NetCrunch discards these with only a server-side " +
                "warning; use table, time-series or category.",
                nameof(type));
        }

        var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in required)
        {
            members.TryGetValue(member, out var value);
            if (!TryCount(value, out var length))
            {
                throw new ArgumentException($"A {type} data object requires \"{member}\" to be an array.", nameof(members));
            }

            if (length > MaxDataEntries)
            {
                throw new ArgumentException(
                    $"\"{member}\" has {length} entries; NetCrunch truncates at {MaxDataEntries} without reporting it.",
                    nameof(members));
            }

            lengths[member] = length;
        }

        // Ragged parallel arrays are the dangerous case: nothing errors anywhere and the chart
        // quietly plots the wrong thing.
        if (type == "table")
        {
            var width = lengths["columns"];
            var index = 0;
            foreach (var row in (IEnumerable)members["rows"]!)
            {
                if (!TryCount(row, out var cells))
                {
                    throw new ArgumentException($"Table row {index} must be an array of cells.", nameof(members));
                }

                if (cells != width)
                {
                    throw new ArgumentException(
                        $"Table row {index} has {cells} cells but there are {width} columns.",
                        nameof(members));
                }

                index++;
            }

            return;
        }

        var left = required[0];
        var right = required[1];
        if (lengths[left] != lengths[right])
        {
            throw new ArgumentException(
                $"\"{left}\" has {lengths[left]} entries but \"{right}\" has {lengths[right]}; they must match.",
                nameof(members));
        }
    }

    private static bool TryCount(object? value, out int count)
    {
        if (value is ICollection collection)
        {
            count = collection.Count;
            return true;
        }

        count = 0;
        return false;
    }
}
