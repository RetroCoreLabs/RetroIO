//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Server;

namespace RetroIO.Tests.TNFS
{
    /// <summary>
    /// Regression tests for TnfsServerStatistics — the Management API's GetStatistics endpoint
    /// used to hardcode all of these fields to 0/"0s" (see Stub-Gap-Audit.md); this class is the
    /// real, live instrumentation that replaces that.
    /// </summary>
    [TestFixture]
    public class TnfsServerStatisticsTests
    {
        [Test]
        public void RecordRequest_AccumulatesTotalRequestsAndBytes()
        {
            var stats = new TnfsServerStatistics();

            stats.RecordRequest(100);
            stats.RecordRequest(250);

            Assert.That(stats.TotalRequests, Is.EqualTo(2));
            Assert.That(stats.BytesTransferred, Is.EqualTo(350));
        }

        [Test]
        public void RecordFileTransfer_IncrementsFilesTransferred_IndependentlyOfRequests()
        {
            var stats = new TnfsServerStatistics();

            stats.RecordRequest(10);
            stats.RecordRequest(10);
            stats.RecordFileTransfer();

            Assert.That(stats.TotalRequests, Is.EqualTo(2), "file transfers must not double-count as requests");
            Assert.That(stats.FilesTransferred, Is.EqualTo(1));
        }

        [Test]
        public void Uptime_IsPositive_ImmediatelyAfterConstruction()
        {
            var stats = new TnfsServerStatistics();
            Assert.That(stats.Uptime.TotalMilliseconds, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void RequestsPerSecond_And_MbPerSecond_AreZero_NotNaNOrInfinity_BeforeAnyTimeHasPassed()
        {
            // Regression guard: dividing by an uptime of ~0 seconds must not produce NaN/Infinity,
            // which would serialize to invalid JSON in the API response.
            var stats = new TnfsServerStatistics();
            stats.RecordRequest(1024);

            Assert.That(double.IsNaN(stats.RequestsPerSecond), Is.False);
            Assert.That(double.IsInfinity(stats.RequestsPerSecond), Is.False);
            Assert.That(double.IsNaN(stats.MbPerSecond), Is.False);
            Assert.That(double.IsInfinity(stats.MbPerSecond), Is.False);
        }

        [Test]
        public async Task RecordRequest_IsThreadSafe_UnderConcurrentCalls()
        {
            var stats = new TnfsServerStatistics();
            const int tasksCount = 20;
            const int callsPerTask = 100;

            var tasks = new Task[tasksCount];
            for (int i = 0; i < tasksCount; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < callsPerTask; j++)
                    {
                        stats.RecordRequest(1);
                    }
                });
            }

            await Task.WhenAll(tasks);

            Assert.That(stats.TotalRequests, Is.EqualTo(tasksCount * callsPerTask),
                "concurrent RecordRequest calls must not lose increments to race conditions");
        }
    }
}
