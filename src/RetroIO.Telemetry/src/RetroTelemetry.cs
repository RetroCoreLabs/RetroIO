//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace RetroIO.Telemetry;

/// <summary>
/// What kind of number a metric is, using the OpenTelemetry names.
/// </summary>
public enum MetricKind
{
    /// <summary>Only goes up: calls answered, bytes sent. Rates are worked out from it.</summary>
    Counter,

    /// <summary>Goes up and down: calls up now, members joined now.</summary>
    UpDownCounter,

    /// <summary>A value read as it is now, not added up: a queue length, a temperature.</summary>
    Gauge,
}

/// <summary>
/// One reading of one series: a metric name plus one set of labels, and its value now.
/// </summary>
/// <param name="Name">The metric name, dotted lower case, for example <c>retroio.dialup.calls.answered</c>.</param>
/// <param name="Kind">Counter, up-down counter or gauge.</param>
/// <param name="Unit">The unit in OpenTelemetry (UCUM) spelling: <c>By</c> for bytes, <c>s</c>, or a <c>{call}</c> style count.</param>
/// <param name="Description">One plain sentence saying what is counted.</param>
/// <param name="Tags">The labels that tell this series apart from the other series of the metric; empty when there is one.</param>
/// <param name="Value">The value now.</param>
public readonly record struct MetricReading(
    string Name,
    MetricKind Kind,
    string Unit,
    string Description,
    KeyValuePair<string, string>[] Tags,
    long Value);

/// <summary>
/// One log record, in the shape of an OpenTelemetry log record: when, how serious, which part, the
/// text, and key-value attributes.
/// </summary>
/// <param name="Sequence">Its number in the log of the part, from 1; a reader asks for the records after the last one it saw.</param>
/// <param name="TimestampUtc">When it was written.</param>
/// <param name="Level">How serious it is.</param>
/// <param name="Source">The part that wrote it, for example <c>RetroIO.DialUp</c>.</param>
/// <param name="Message">The text.</param>
/// <param name="Attributes">Key-value attributes, for example <c>caller=127.0.0.1:30715</c>; empty when there are none.</param>
public readonly record struct TelemetryLogRecord(
    long Sequence,
    DateTime TimestampUtc,
    LogLevel Level,
    string Source,
    string Message,
    KeyValuePair<string, string>[] Attributes);

/// <summary>
/// A part of RetroServer whose metrics and log the management API lists, side by side.
/// </summary>
public interface ITelemetrySource
{
    /// <summary>Gets the name of the part, for example <c>RetroIO.DialUp</c>: the meter name and the log source.</summary>
    string Name { get; }

    /// <summary>Adds a reading of every metric series to <paramref name="into"/>.</summary>
    /// <param name="into">The list to add to.</param>
    void CollectMetrics(List<MetricReading> into);

    /// <summary>Adds the kept log records numbered after <paramref name="after"/>, oldest first.</summary>
    /// <param name="after">The last sequence number the reader already has; 0 for all that are kept.</param>
    /// <param name="into">The list to add to.</param>
    void CollectLog(long after, List<TelemetryLogRecord> into);
}

/// <summary>
/// The metrics and the log of one RetroServer part (one protocol, one service), kept together. This
/// is the rule for every part of RetroServer (Ronny, 2026-11-23): named counters with labels, like a
/// Windows perfmon counter list and like OpenTelemetry metrics, and the log next to them.
/// </summary>
/// <remarks>
/// <para>
/// Every metric is made as an observable instrument on a .NET <see cref="Meter"/>. That is the type
/// the OpenTelemetry .NET SDK reads, so exporting later is one line in the host
/// (<c>AddMeter("RetroIO.*")</c>) and nothing changes here. The same series are listed by
/// <see cref="CollectMetrics"/> for the management API and RetroAdmin.
/// </para>
/// <para>
/// The numbers stay where they are counted (an <c>Interlocked</c> field in the server); a series only
/// holds a function that reads it, so counting costs nothing extra. One metric name can have several
/// series that differ by labels, for example <c>retroio.dialup.io</c> with <c>direction=in</c> and
/// <c>direction=out</c>; OpenTelemetry wants that rather than two names.
/// </para>
/// <para>
/// Logs: <see cref="Log(LogLevel, Exception?, string, KeyValuePair{string, string}[])"/> writes each record to the <see cref="ILogger"/> of the part, which is
/// where OpenTelemetry takes logs from, with the attributes as structured fields (a logger scope). It
/// also keeps the newest records in a fixed ring, so the management API can show the log beside the
/// metrics.
/// </para>
/// </remarks>
public sealed class RetroTelemetry : ITelemetrySource, IDisposable
{
    /// <summary>
    /// The default number of log records kept for the management API.
    /// </summary>
    public const int DefaultLogCapacity = 1000;

    private readonly Meter _meter;
    private readonly List<Metric> _metrics = new List<Metric>();
    private readonly object _gate = new object();
    private readonly ILogger? _logger;
    private readonly TelemetryLogRecord[] _ring;
    private long _lastSequence;

    /// <summary>
    /// Initialises a set with its own meter and log.
    /// </summary>
    /// <param name="name">The name of the part: <c>RetroIO.</c> and the part, for example <c>RetroIO.DialUp</c>.</param>
    /// <param name="logger">Where each log record also goes (OpenTelemetry reads it there); null keeps them only in the ring.</param>
    /// <param name="logCapacity">How many log records are kept for the management API.</param>
    public RetroTelemetry(string name, ILogger? logger = null, int logCapacity = DefaultLogCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(logCapacity, 1);
        _meter = new Meter(name);
        _logger = logger;
        _ring = new TelemetryLogRecord[logCapacity];
    }

    /// <inheritdoc />
    public string Name => _meter.Name;

    /// <summary>
    /// Gets the sequence number of the newest log record, 0 before the first.
    /// </summary>
    public long LastLogSequence
    {
        get
        {
            lock (_gate)
            {
                return _lastSequence;
            }
        }
    }

    /// <summary>
    /// Adds a series of a counter (only goes up).
    /// </summary>
    /// <param name="name">The metric name.</param>
    /// <param name="unit">The unit.</param>
    /// <param name="description">What is counted.</param>
    /// <param name="read">Reads the value now; called from any thread.</param>
    /// <param name="tags">The labels of this series.</param>
    public void AddCounter(string name, string unit, string description, Func<long> read, params KeyValuePair<string, string>[] tags)
    {
        Add(name, MetricKind.Counter, unit, description, read, tags);
    }

    /// <summary>
    /// Adds a series of an up-down counter (goes up and down).
    /// </summary>
    /// <param name="name">The metric name.</param>
    /// <param name="unit">The unit.</param>
    /// <param name="description">What is counted.</param>
    /// <param name="read">Reads the value now; called from any thread.</param>
    /// <param name="tags">The labels of this series.</param>
    public void AddUpDownCounter(string name, string unit, string description, Func<long> read, params KeyValuePair<string, string>[] tags)
    {
        Add(name, MetricKind.UpDownCounter, unit, description, read, tags);
    }

    /// <summary>
    /// Adds a series of a gauge (a value as it is now).
    /// </summary>
    /// <param name="name">The metric name.</param>
    /// <param name="unit">The unit.</param>
    /// <param name="description">What is measured.</param>
    /// <param name="read">Reads the value now; called from any thread.</param>
    /// <param name="tags">The labels of this series.</param>
    public void AddGauge(string name, string unit, string description, Func<long> read, params KeyValuePair<string, string>[] tags)
    {
        Add(name, MetricKind.Gauge, unit, description, read, tags);
    }

    /// <summary>
    /// Writes one log record: to the logger, and into the ring the management API reads.
    /// </summary>
    /// <param name="level">How serious it is.</param>
    /// <param name="message">The text, in plain words.</param>
    /// <param name="attributes">Key-value attributes.</param>
    public void Log(LogLevel level, string message, params KeyValuePair<string, string>[] attributes)
    {
        Log(level, null, message, attributes);
    }

    /// <summary>
    /// Writes one log record with the exception behind it: to the logger (with the exception, so its
    /// stack goes with it), and into the ring with the exception type as the <c>exception.type</c>
    /// attribute (the OpenTelemetry attribute name).
    /// </summary>
    /// <param name="level">How serious it is.</param>
    /// <param name="exception">The exception, or null.</param>
    /// <param name="message">The text, in plain words.</param>
    /// <param name="attributes">Key-value attributes.</param>
    public void Log(LogLevel level, Exception? exception, string message, params KeyValuePair<string, string>[] attributes)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (exception != null)
        {
            KeyValuePair<string, string>[] given = attributes ?? Array.Empty<KeyValuePair<string, string>>();
            KeyValuePair<string, string>[] with = new KeyValuePair<string, string>[given.Length + 1];
            Array.Copy(given, with, given.Length);
            with[given.Length] = new KeyValuePair<string, string>("exception.type", exception.GetType().FullName ?? exception.GetType().Name);
            attributes = with;
        }

        KeyValuePair<string, string>[] tags = attributes ?? Array.Empty<KeyValuePair<string, string>>();
        Keep(level, message, tags);

        if (_logger == null || !_logger.IsEnabled(level))
        {
            return;
        }

        if (tags.Length == 0)
        {
            _logger.Log(level, exception, "{Message}", message);
            return;
        }

        // A scope carries the attributes as structured fields; OpenTelemetry exports them as log attributes.
        Dictionary<string, object?> scope = new Dictionary<string, object?>(tags.Length);
        for (int i = 0; i < tags.Length; i++)
        {
            scope[tags[i].Key] = tags[i].Value;
        }

        using (_logger.BeginScope(scope))
        {
            _logger.Log(level, exception, "{Message}", message);
        }
    }

    /// <summary>
    /// Puts one record into the ring only; the logger is not written. Used for lines that already
    /// went through a logger (<see cref="TelemetryLoggerProvider"/>).
    /// </summary>
    /// <param name="level">How serious it is.</param>
    /// <param name="message">The text.</param>
    /// <param name="attributes">Key-value attributes.</param>
    internal void Keep(LogLevel level, string message, KeyValuePair<string, string>[] attributes)
    {
        lock (_gate)
        {
            _lastSequence++;
            _ring[(int)((_lastSequence - 1) % _ring.Length)] =
                new TelemetryLogRecord(_lastSequence, DateTime.UtcNow, level, _meter.Name, message, attributes);
        }
    }

    /// <inheritdoc />
    public void CollectMetrics(List<MetricReading> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_gate)
        {
            for (int m = 0; m < _metrics.Count; m++)
            {
                Metric metric = _metrics[m];
                for (int s = 0; s < metric.Series.Count; s++)
                {
                    Series series = metric.Series[s];
                    into.Add(new MetricReading(metric.Name, metric.Kind, metric.Unit, metric.Description, series.Tags, series.Read()));
                }
            }
        }
    }

    /// <inheritdoc />
    public void CollectLog(long after, List<TelemetryLogRecord> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_gate)
        {
            long oldest = Math.Max(1, _lastSequence - _ring.Length + 1);
            long first = Math.Max(oldest, after + 1);
            for (long n = first; n <= _lastSequence; n++)
            {
                into.Add(_ring[(int)((n - 1) % _ring.Length)]);
            }
        }
    }

    /// <summary>
    /// Ends the meter; OpenTelemetry stops reading it.
    /// </summary>
    public void Dispose()
    {
        _meter.Dispose();
    }

    private void Add(string name, MetricKind kind, string unit, string description, Func<long> read, KeyValuePair<string, string>[] tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            Metric? metric = null;
            for (int i = 0; i < _metrics.Count; i++)
            {
                if (string.Equals(_metrics[i].Name, name, StringComparison.Ordinal))
                {
                    metric = _metrics[i];
                    break;
                }
            }

            if (metric == null)
            {
                metric = new Metric(name, kind, unit, description);
                _metrics.Add(metric);
                Metric captured = metric;
                switch (kind)
                {
                    case MetricKind.Counter:
                        _meter.CreateObservableCounter(name, () => Observe(captured), unit, description);
                        break;
                    case MetricKind.UpDownCounter:
                        _meter.CreateObservableUpDownCounter(name, () => Observe(captured), unit, description);
                        break;
                    default:
                        _meter.CreateObservableGauge(name, () => Observe(captured), unit, description);
                        break;
                }
            }
            else if (metric.Kind != kind || !string.Equals(metric.Unit, unit, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Metric {name} already exists as a {metric.Kind} in {metric.Unit}.", nameof(name));
            }

            metric.Series.Add(new Series(tags ?? Array.Empty<KeyValuePair<string, string>>(), read));
        }
    }

    private Measurement<long>[] Observe(Metric metric)
    {
        lock (_gate)
        {
            Measurement<long>[] all = new Measurement<long>[metric.Series.Count];
            for (int s = 0; s < all.Length; s++)
            {
                Series series = metric.Series[s];
                KeyValuePair<string, object?>[] tags = new KeyValuePair<string, object?>[series.Tags.Length];
                for (int t = 0; t < tags.Length; t++)
                {
                    tags[t] = new KeyValuePair<string, object?>(series.Tags[t].Key, series.Tags[t].Value);
                }

                all[s] = new Measurement<long>(series.Read(), tags);
            }

            return all;
        }
    }

    private sealed class Metric
    {
        public Metric(string name, MetricKind kind, string unit, string description)
        {
            Name = name;
            Kind = kind;
            Unit = unit;
            Description = description;
        }

        public string Name { get; }

        public MetricKind Kind { get; }

        public string Unit { get; }

        public string Description { get; }

        public List<Series> Series { get; } = new List<Series>();
    }

    private readonly record struct Series(KeyValuePair<string, string>[] Tags, Func<long> Read);
}
