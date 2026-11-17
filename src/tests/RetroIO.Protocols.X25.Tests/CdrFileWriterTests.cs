//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using NUnit.Framework;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Model;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The call record line: x25emu's fourteen columns in its order, then segments forward, segments back and the paying
    /// account (PLAN B1c).
    /// </summary>
    [TestFixture]
    public class CdrFileWriterTests
    {
        private static Cdr Sample()
        {
            return new Cdr
            {
                StartTimeMs = 1_000_000,
                EndTimeMs = 6_025_000,
                Calling = "23400001",
                Called = "23420001",
                InPort = 0,
                OutPort = 1,
                PacketsForward = 3,
                PacketsReverse = 1,
                BytesForward = 193,
                BytesReverse = 65,
                ClearCause = 0,
                ClearDiagnostic = 0,
                StartWallSeconds = 1_794_873_600,
                ConnectedTimeMs = 1_000_500,
                SegmentsForward = 4,
                SegmentsReverse = 2,
                Account = "card7",
                Nui = "ACCT-7",
                ReverseCharging = false,
                ChargingRequested = true,
                CalledAccount = "hostB",
            };
        }

        [Test]
        public void Format_IsTheCEmulatorsColumns_ThenTheVersionTwoColumns()
        {
            // Connected 500 ms after routing, so the connected duration is 5 025 000 - 500 = 5 024 500 ms.
            Assert.That(CdrFileWriter.Format(Sample()),
                Is.EqualTo("1000000,5025000,23400001,23420001,0,1,3,1,193,65,0,0,1794873600,000000,"
                    + "1000500,5024500,4,2,card7,ACCT-7,0,caller,1,hostB"));
        }

        [Test]
        public void Format_NeverConnected_ConnectTimeAndConnectedDurationAreZero()
        {
            Cdr cdr = Sample();
            cdr.ConnectedTimeMs = 0;
            Assert.That(CdrFileWriter.Format(cdr).Split(',')[14], Is.EqualTo("0"));
            Assert.That(CdrFileWriter.Format(cdr).Split(',')[15], Is.EqualTo("0"));
        }

        [Test]
        public void Format_ReverseCharging_TheCalledEndIsCharged()
        {
            Cdr cdr = Sample();
            cdr.ReverseCharging = true;
            Assert.That(CdrFileWriter.Format(cdr), Does.EndWith(",1,called,1,hostB"));
        }

        [Test]
        public void Format_NoAccountNoNui_LeavesThoseColumnsEmpty()
        {
            Cdr cdr = Sample();
            cdr.Account = string.Empty;
            cdr.Nui = string.Empty;
            Assert.That(CdrFileWriter.Format(cdr), Does.EndWith(",4,2,,,0,caller,1,hostB"));
        }

        [Test]
        public void Format_ACommaInTheAccountOrNui_DoesNotAddAColumn()
        {
            Cdr cdr = Sample();
            cdr.Account = "a,b";
            cdr.Nui = "c,d";
            cdr.CalledAccount = "e,f";
            Assert.That(CdrFileWriter.Format(cdr).Split(',').Length, Is.EqualTo(24));
        }

        [Test]
        public void Write_AppendsOneLinePerCall()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cdr-" + System.Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                using (CdrFileWriter w = new CdrFileWriter(path))
                {
                    w.Write(Sample());
                    w.Write(Sample());
                }

                string[] lines = File.ReadAllLines(path);
                Assert.That(lines.Length, Is.EqualTo(2));
                Assert.That(lines[1], Is.EqualTo(CdrFileWriter.Format(Sample())));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void ARestartedSwitch_AddsToTheRecordFile_AndKeepsWhatWasThere()
        {
            // The record file is the bill: a switch that starts again must never empty it. Opened for appending, as
            // x25emu does (fopen "a"), so the lines from before the restart stay and the new ones follow them.
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cdr-" + System.Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                using (CdrFileWriter before = new CdrFileWriter(path))
                {
                    before.Write(Sample());
                }

                using (CdrFileWriter after = new CdrFileWriter(path))
                {
                    after.Write(Sample());
                    after.Write(Sample());
                }

                string[] lines = File.ReadAllLines(path);
                Assert.That(lines.Length, Is.EqualTo(3), "the line from before the restart is kept");
                for (int i = 0; i < lines.Length; i++)
                {
                    Assert.That(lines[i], Is.EqualTo(CdrFileWriter.Format(Sample())));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
