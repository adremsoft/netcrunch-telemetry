namespace NetCrunch.Telemetry;

/// <summary>
/// The generic form of a data object, behind <see cref="TableData"/>, <see cref="TimeSeriesData"/>
/// and <see cref="CategoryChartData"/>.
/// </summary>
public sealed class DataObject
{
    /// <summary>One of <c>table</c>, <c>time-series</c> or <c>category</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Display title.</summary>
    public string? Name { get; init; }

    /// <summary>Label for the plotted series. Ignored for tables, which have no series.</summary>
    public string? SeriesName { get; init; }

    /// <summary>A line of explanation shown with the object.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// The object's own state — <c>"OK"</c>, <c>"Warning"</c>, <c>"Error"</c>. Part of what is
    /// displayed; alerting acts on statuses, so a red table is not an alert.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>The type-specific arrays, keyed by member name.</summary>
    public required IReadOnlyDictionary<string, object?> Members { get; init; }
}

/// <summary>A table rendered on the sensor's page.</summary>
public sealed class TableData
{
    /// <summary>Column headings.</summary>
    public required IReadOnlyList<object?> Columns { get; init; }

    /// <summary>One entry per row, each the same length as <see cref="Columns"/>.</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    /// <summary>Display title.</summary>
    public string? Name { get; init; }

    /// <summary>A line of explanation shown with the table.</summary>
    public string? Message { get; init; }

    /// <summary>The table's own state. Not an alert.</summary>
    public string? Status { get; init; }
}

/// <summary>A time chart rendered on the sensor's page.</summary>
public sealed class TimeSeriesData
{
    /// <summary>Epoch milliseconds. Must be the same length as <see cref="Values"/>.</summary>
    public required IReadOnlyList<long> Timestamps { get; init; }

    /// <summary>The plotted values.</summary>
    public required IReadOnlyList<double> Values { get; init; }

    /// <summary>Display title.</summary>
    public string? Name { get; init; }

    /// <summary>Label for the plotted series.</summary>
    public string? SeriesName { get; init; }

    /// <summary>A line of explanation shown with the chart.</summary>
    public string? Message { get; init; }

    /// <summary>The chart's own state. Not an alert.</summary>
    public string? Status { get; init; }
}

/// <summary>A labelled bar chart rendered on the sensor's page.</summary>
public sealed class CategoryChartData
{
    /// <summary>Bucket labels. Must be the same length as <see cref="Values"/>.</summary>
    public required IReadOnlyList<string> Categories { get; init; }

    /// <summary>The plotted values.</summary>
    public required IReadOnlyList<double> Values { get; init; }

    /// <summary>Display title.</summary>
    public string? Name { get; init; }

    /// <summary>Label for the plotted series.</summary>
    public string? SeriesName { get; init; }

    /// <summary>A line of explanation shown with the chart.</summary>
    public string? Message { get; init; }

    /// <summary>The chart's own state. Not an alert.</summary>
    public string? Status { get; init; }
}
