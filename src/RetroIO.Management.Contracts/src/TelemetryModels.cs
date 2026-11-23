//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// One metric series as the management API shows it: like one counter line in Windows perfmon, and
/// named and labelled the way OpenTelemetry names a metric.
/// </summary>
public class MetricDto
{
    /// <summary>Gets or sets the part it belongs to, for example <c>RetroIO.DialUp</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the metric name, for example <c>retroio.dialup.calls.answered</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind: <c>Counter</c> (only goes up), <c>UpDownCounter</c> or <c>Gauge</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the unit, OpenTelemetry spelling: <c>By</c> bytes, <c>s</c> seconds, <c>{call}</c> a count of calls.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Gets or sets what is counted, in one sentence.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the labels that tell this series apart, for example <c>direction=in</c>.</summary>
    public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();

    /// <summary>Gets or sets the value now.</summary>
    public long Value { get; set; }
}

/// <summary>
/// The metrics of one or all parts.
/// </summary>
public class MetricsResponse
{
    /// <summary>Gets or sets when the values were read.</summary>
    public DateTime ReadAtUtc { get; set; }

    /// <summary>Gets or sets the series.</summary>
    public List<MetricDto> Metrics { get; set; } = new List<MetricDto>();
}

/// <summary>
/// One log record, in the shape of an OpenTelemetry log record.
/// </summary>
public class TelemetryLogRecordDto
{
    /// <summary>Gets or sets its number in the log of its part.</summary>
    public long Sequence { get; set; }

    /// <summary>Gets or sets when it was written.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Gets or sets how serious it is: <c>Information</c>, <c>Warning</c>, <c>Error</c> and so on.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Gets or sets the part that wrote it.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the text.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets its key-value attributes.</summary>
    public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
}

/// <summary>
/// Log records of one or all parts.
/// </summary>
public class TelemetryLogResponse
{
    /// <summary>Gets or sets the records, oldest first.</summary>
    public List<TelemetryLogRecordDto> Records { get; set; } = new List<TelemetryLogRecordDto>();
}

/// <summary>
/// One part of RetroServer that has metrics and a log.
/// </summary>
public class TelemetrySourceDto
{
    /// <summary>Gets or sets its name, for example <c>RetroIO.DialUp</c>; also its OpenTelemetry meter name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets how many metric series it has.</summary>
    public int Series { get; set; }
}
