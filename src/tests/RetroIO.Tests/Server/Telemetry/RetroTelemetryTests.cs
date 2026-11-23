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
using NUnit.Framework;
using RetroIO.Telemetry;

namespace RetroIO.Tests.Server.Telemetry;

/// <summary>
/// The shared metrics-and-log set every RetroServer part uses: the readings for the management API,
/// the same numbers through a .NET <see cref="MeterListener"/> (the path OpenTelemetry reads), and
/// the kept log.
/// </summary>
[TestFixture]
public sealed class RetroTelemetryTests
{
    [Test]
    public void OneMetricWithTwoLabelledSeries_IsListedAsTwoReadings()
    {
        long sent = 5;
        long received = 7;
        using RetroTelemetry telemetry = new RetroTelemetry("RetroIO.Test." + Guid.NewGuid().ToString("N"));
        telemetry.AddCounter("retroio.test.io", "By", "Bytes.", () => received, new KeyValuePair<string, string>("direction", "in"));
        telemetry.AddCounter("retroio.test.io", "By", "Bytes.", () => sent, new KeyValuePair<string, string>("direction", "out"));
        telemetry.AddUpDownCounter("retroio.test.active", "{call}", "Up now.", () => 2);

        List<MetricReading> readings = new List<MetricReading>();
        telemetry.CollectMetrics(readings);

        Assert.That(readings, Has.Count.EqualTo(3));
        Assert.That(readings[0].Name, Is.EqualTo("retroio.test.io"));
        Assert.That(readings[0].Tags[0].Value, Is.EqualTo("in"));
        Assert.That(readings[0].Value, Is.EqualTo(7));
        Assert.That(readings[1].Tags[0].Value, Is.EqualTo("out"));
        Assert.That(readings[1].Value, Is.EqualTo(5));
        Assert.That(readings[2].Kind, Is.EqualTo(MetricKind.UpDownCounter));
    }

    [Test]
    public void TheSameNumbers_ReachAMeterListener_TheWayOpenTelemetryReadsThem()
    {
        string name = "RetroIO.Test." + Guid.NewGuid().ToString("N");
        using RetroTelemetry telemetry = new RetroTelemetry(name);
        telemetry.AddCounter("retroio.test.io", "By", "Bytes.", () => 7, new KeyValuePair<string, string>("direction", "in"));
        telemetry.AddCounter("retroio.test.io", "By", "Bytes.", () => 5, new KeyValuePair<string, string>("direction", "out"));

        Dictionary<string, long> seen = new Dictionary<string, long>();
        string unit = string.Empty;
        using MeterListener listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == name)
            {
                unit = instrument.Unit ?? string.Empty;
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            string direction = string.Empty;
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == "direction")
                {
                    direction = (string)tags[i].Value!;
                }
            }

            seen[instrument.Name + "/" + direction] = value;
        });
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.That(unit, Is.EqualTo("By"));
        Assert.That(seen["retroio.test.io/in"], Is.EqualTo(7));
        Assert.That(seen["retroio.test.io/out"], Is.EqualTo(5));
    }

    [Test]
    public void AMetricNameReusedWithAnotherKind_IsRefused()
    {
        using RetroTelemetry telemetry = new RetroTelemetry("RetroIO.Test." + Guid.NewGuid().ToString("N"));
        telemetry.AddCounter("retroio.test.x", "By", "Bytes.", () => 1);

        Assert.Throws<ArgumentException>(() => telemetry.AddGauge("retroio.test.x", "By", "Bytes.", () => 1));
    }

    [Test]
    public void TheLog_KeepsTheNewestRecords_AndGivesThoseAfterASequenceNumber()
    {
        using RetroTelemetry telemetry = new RetroTelemetry("RetroIO.Test.Log", logger: null, logCapacity: 3);
        for (int i = 1; i <= 5; i++)
        {
            telemetry.Log(LogLevel.Information, "line " + i, new KeyValuePair<string, string>("n", i.ToString()));
        }

        List<TelemetryLogRecord> all = new List<TelemetryLogRecord>();
        telemetry.CollectLog(0, all);
        Assert.That(all, Has.Count.EqualTo(3), "only the newest three are kept");
        Assert.That(all[0].Message, Is.EqualTo("line 3"));
        Assert.That(all[2].Sequence, Is.EqualTo(5));
        Assert.That(all[2].Source, Is.EqualTo("RetroIO.Test.Log"));
        Assert.That(all[2].Attributes[0].Value, Is.EqualTo("5"));

        List<TelemetryLogRecord> newer = new List<TelemetryLogRecord>();
        telemetry.CollectLog(4, newer);
        Assert.That(newer, Has.Count.EqualTo(1));
        Assert.That(newer[0].Message, Is.EqualTo("line 5"));
    }

    [Test]
    public void ARecordWithAnException_CarriesItsTypeAsTheOpenTelemetryAttribute()
    {
        using RetroTelemetry telemetry = new RetroTelemetry("RetroIO.Test.Error");
        telemetry.Log(LogLevel.Error, new InvalidOperationException("x"), "failed", new KeyValuePair<string, string>("port", "5010"));

        List<TelemetryLogRecord> records = new List<TelemetryLogRecord>();
        telemetry.CollectLog(0, records);

        Assert.That(records[0].Attributes, Has.Length.EqualTo(2));
        Assert.That(records[0].Attributes[0].Key, Is.EqualTo("port"));
        Assert.That(records[0].Attributes[1].Key, Is.EqualTo("exception.type"));
        Assert.That(records[0].Attributes[1].Value, Is.EqualTo("System.InvalidOperationException"));
    }

    [Test]
    public void TheXmsgHub_HasItsMetricsInTheSharedShape_ReadingZeroWhileStopped()
    {
        RetroIO.Protocols.Xmsg.Hub.Server.XmsgHubService hub =
            new RetroIO.Protocols.Xmsg.Hub.Server.XmsgHubService(new RetroIO.Protocols.Xmsg.Hub.Server.XmsgHubOptions());

        List<MetricReading> readings = new List<MetricReading>();
        hub.Telemetry.CollectMetrics(readings);

        Assert.That(hub.Telemetry.Name, Is.EqualTo("RetroIO.XmsgHub"));
        int dropped = 0;
        for (int i = 0; i < readings.Count; i++)
        {
            Assert.That(readings[i].Value, Is.EqualTo(0), readings[i].Name);
            if (readings[i].Name == "retroio.xmsghub.frames.dropped")
            {
                dropped++;
            }
        }

        Assert.That(dropped, Is.EqualTo(3), "slow member, loop and hop limit are three labelled series of one metric");
        hub.Dispose();
    }
}
