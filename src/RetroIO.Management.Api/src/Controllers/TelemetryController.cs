//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Server.Core.Telemetry;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// The metrics and logs of every part of RetroServer, side by side, in one shape for all of them
/// (Ronny, 2026-11-23): named counters with labels, like Windows perfmon and like OpenTelemetry.
/// </summary>
/// <remarks>
/// Each running part registers an <see cref="ITelemetrySource"/>; a part that is switched off has
/// none, so it is simply not listed.
/// </remarks>
[ApiController]
[Route("api/v1/[controller]")]
public class TelemetryController : ControllerBase
{
    /// <summary>
    /// The most log records one request returns.
    /// </summary>
    public const int MaxLogPage = 5000;

    private readonly ITelemetrySource[] _sources;

    /// <summary>
    /// Initialises the controller.
    /// </summary>
    /// <param name="sources">
    /// The parts that are running.
    /// </param>
    public TelemetryController(IEnumerable<ITelemetrySource> sources)
    {
        List<ITelemetrySource> list = new List<ITelemetrySource>(sources);
        _sources = list.ToArray();
    }

    /// <summary>
    /// Lists the parts that have metrics and a log.
    /// </summary>
    /// <returns>
    /// The parts.
    /// </returns>
    [HttpGet("sources")]
    public ActionResult<ApiResponse<List<TelemetrySourceDto>>> GetSources()
    {
        List<TelemetrySourceDto> result = new List<TelemetrySourceDto>(_sources.Length);
        List<MetricReading> readings = new List<MetricReading>();
        for (int i = 0; i < _sources.Length; i++)
        {
            readings.Clear();
            _sources[i].CollectMetrics(readings);
            result.Add(new TelemetrySourceDto { Name = _sources[i].Name, Series = readings.Count });
        }

        return Ok(ApiResponse<List<TelemetrySourceDto>>.Ok(result));
    }

    /// <summary>
    /// Gets the metrics now.
    /// </summary>
    /// <param name="source">
    /// One part, for example <c>RetroIO.DialUp</c>; empty for all.
    /// </param>
    /// <returns>
    /// Every series of the parts asked for.
    /// </returns>
    [HttpGet("metrics")]
    public ActionResult<ApiResponse<MetricsResponse>> GetMetrics([FromQuery] string? source = null)
    {
        MetricsResponse response = new MetricsResponse { ReadAtUtc = DateTime.UtcNow };
        List<MetricReading> readings = new List<MetricReading>();
        for (int i = 0; i < _sources.Length; i++)
        {
            ITelemetrySource part = _sources[i];
            if (!Matches(part, source))
            {
                continue;
            }

            readings.Clear();
            part.CollectMetrics(readings);
            for (int r = 0; r < readings.Count; r++)
            {
                MetricReading reading = readings[r];
                response.Metrics.Add(new MetricDto
                {
                    Source = part.Name,
                    Name = reading.Name,
                    Kind = reading.Kind.ToString(),
                    Unit = reading.Unit,
                    Description = reading.Description,
                    Tags = ToDictionary(reading.Tags),
                    Value = reading.Value,
                });
            }
        }

        return Ok(ApiResponse<MetricsResponse>.Ok(response));
    }

    /// <summary>
    /// Gets the kept log records.
    /// </summary>
    /// <param name="source">
    /// One part; empty for all. The sequence numbers belong to each part, so <paramref name="after"/>
    /// is meant for one part at a time.
    /// </param>
    /// <param name="after">
    /// The last sequence number the reader has; 0 for all that are kept.
    /// </param>
    /// <param name="limit">
    /// The most records to return, newest kept; at most <see cref="MaxLogPage"/>.
    /// </param>
    /// <returns>
    /// The records, oldest first.
    /// </returns>
    [HttpGet("log")]
    public ActionResult<ApiResponse<TelemetryLogResponse>> GetLog(
        [FromQuery] string? source = null,
        [FromQuery] long after = 0,
        [FromQuery] int limit = 1000)
    {
        int max = Math.Clamp(limit, 1, MaxLogPage);
        List<TelemetryLogRecord> records = new List<TelemetryLogRecord>();
        for (int i = 0; i < _sources.Length; i++)
        {
            if (Matches(_sources[i], source))
            {
                _sources[i].CollectLog(after, records);
            }
        }

        if (_sources.Length > 1)
        {
            records.Sort(static (a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        }

        TelemetryLogResponse response = new TelemetryLogResponse();
        for (int r = Math.Max(0, records.Count - max); r < records.Count; r++)
        {
            TelemetryLogRecord record = records[r];
            response.Records.Add(new TelemetryLogRecordDto
            {
                Sequence = record.Sequence,
                TimestampUtc = record.TimestampUtc,
                Level = record.Level.ToString(),
                Source = record.Source,
                Message = record.Message,
                Attributes = ToDictionary(record.Attributes),
            });
        }

        return Ok(ApiResponse<TelemetryLogResponse>.Ok(response));
    }

    private static bool Matches(ITelemetrySource part, string? source)
    {
        return string.IsNullOrEmpty(source) || string.Equals(part.Name, source, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ToDictionary(KeyValuePair<string, string>[] pairs)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(pairs.Length);
        for (int i = 0; i < pairs.Length; i++)
        {
            result[pairs[i].Key] = pairs[i].Value;
        }

        return result;
    }
}
