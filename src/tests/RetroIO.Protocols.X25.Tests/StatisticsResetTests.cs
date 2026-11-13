//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Link;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Transport;
using RetroIO.Protocols.X25.Runtime;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Setting the statistics back to zero: the counter classes, and the runtime that owns them.
    /// </summary>
    [TestFixture]
    public class StatisticsResetTests
    {
        /// <summary>
        /// A link's counters go to zero; its name, type, status and start time stay.
        /// </summary>
        [Test]
        public void LinkStatistics_ResetCounters_KeepsWhatDescribesTheLink()
        {
            DateTime started = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
            LinkStatistics stats = new LinkStatistics
            {
                LinkName = "L1",
                LinkType = LinkType.Serial,
                CurrentStatus = LinkStatus.Active,
                StartTime = started,
                BytesTransmitted = 1,
                BytesReceived = 2,
                FramesTransmitted = 3,
                FramesReceived = 4,
                Errors = 5
            };

            stats.ResetCounters();

            Assert.That(stats.BytesTransmitted + stats.BytesReceived + stats.FramesTransmitted + stats.FramesReceived + stats.Errors, Is.EqualTo(0));
            Assert.That(stats.LinkName, Is.EqualTo("L1"));
            Assert.That(stats.LinkType, Is.EqualTo(LinkType.Serial));
            Assert.That(stats.CurrentStatus, Is.EqualTo(LinkStatus.Active));
            Assert.That(stats.StartTime, Is.EqualTo(started));
        }

        /// <summary>
        /// A transport's counters go to zero; its name, status, start time and open connections stay.
        /// </summary>
        [Test]
        public void TransportStatistics_ResetCounters_KeepsWhatDescribesTheTransport()
        {
            TransportStatistics stats = new TransportStatistics
            {
                TransportName = "xot1",
                CurrentStatus = TransportStatus.Active,
                ActiveConnections = 2,
                OutboundCallsAttempted = 1,
                OutboundCallsSucceeded = 2,
                InboundCallsReceived = 3,
                InboundCallsAccepted = 4,
                InboundCallsRejected = 5,
                BytesTransmitted = 6,
                BytesReceived = 7,
                ConnectionErrors = 8,
                TimeoutErrors = 9,
                ProtocolErrors = 10
            };

            stats.ResetCounters();

            Assert.That(stats.OutboundCallsAttempted + stats.OutboundCallsSucceeded + stats.InboundCallsReceived
                + stats.InboundCallsAccepted + stats.InboundCallsRejected + stats.ConnectionErrors
                + stats.TimeoutErrors + stats.ProtocolErrors, Is.EqualTo(0));
            Assert.That(stats.BytesTransmitted + stats.BytesReceived, Is.EqualTo(0));
            Assert.That(stats.TransportName, Is.EqualTo("xot1"));
            Assert.That(stats.CurrentStatus, Is.EqualTo(TransportStatus.Active));
            Assert.That(stats.ActiveConnections, Is.EqualTo(2));
        }

        /// <summary>
        /// The runtime's reset gives zero totals, raises the statistics event once, and keeps the state
        /// words that are not counters.
        /// </summary>
        [Test]
        public void Runtime_ResetStatistics_RaisesOneEvent_AndTheTotalsAreZero()
        {
            RuntimeConfig config = new RuntimeConfig { HistoryCapacity = 100 };
            using ProtocolRuntime runtime = new ProtocolRuntime(NullLogger.Instance, config);
            int raised = 0;
            StatisticsSnapshot? last = null;
            runtime.StatisticsUpdated += (s, snapshot) => { raised++; last = snapshot; };

            runtime.ResetStatistics();

            Assert.That(raised, Is.EqualTo(1), "one statistics event per reset");
            Assert.That(last, Is.Not.Null);
            StatisticsSnapshot now = runtime.GetStatistics();
            Assert.That(now.HDLC.FramesReceived + now.HDLC.FramesTransmitted + now.HDLC.BytesReceived + now.HDLC.BytesTransmitted, Is.EqualTo(0));
            Assert.That(now.LAPB.FramesReceived + now.LAPB.FramesTransmitted + now.LAPB.Retransmissions, Is.EqualTo(0));
            Assert.That(now.X25.PacketsReceived + now.X25.PacketsTransmitted + now.X25.CallRequestsTransmitted, Is.EqualTo(0));
            Assert.That(now.XOT.PacketsReceived + now.XOT.PacketsTransmitted, Is.EqualTo(0));
        }
        /// <summary>
        /// A runtime that has just been built reports an uptime of seconds, not millions of hours. The
        /// start time used to be set after the first statistics update, so that update measured from
        /// the year 1 (about 17.7 million hours) until the next update five seconds later.
        /// </summary>
        [Test]
        public void Runtime_JustStarted_UptimeIsSmall()
        {
            RuntimeConfig config = new RuntimeConfig { HistoryCapacity = 100 };
            using ProtocolRuntime runtime = new ProtocolRuntime(NullLogger.Instance, config);

            TimeSpan uptime = runtime.GetStatistics().Uptime;

            Assert.That(uptime, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
            Assert.That(uptime, Is.LessThan(TimeSpan.FromMinutes(1)));
        }
    }
}
