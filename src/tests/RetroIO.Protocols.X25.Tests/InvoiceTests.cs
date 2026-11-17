//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Billing;
using RetroIO.Protocols.X25.Switch.Model;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A bill worked out by hand from a price list and a few record lines, and the program's bill compared with it figure
    /// by figure. The record lines go through the writer and back through the reader first, so the file format is part
    /// of what is tested.
    /// </summary>
    [TestFixture]
    public sealed class InvoiceTests
    {
        // Prices (all in øre): 250.00 a month; within the network 0.50 a call, 0.30 a minute, 2.00 per 1000 segments; to
        // another network 3.00 a call, 1.50 a minute, 10.00 per 1000 segments. Local time is UTC+1; off-peak 18:00 to
        // 08:00 local, 50% off time and data.
        private static readonly string[] TariffFile =
        {
            "[tariff]",
            "currency = NOK",
            "minor_units = 100",
            "home_dnic = 2342",
            "monthly_fee = 25000",
            "time_unit = 60",
            "utc_offset = 60",
            "off_peak_start = 18",
            "off_peak_end = 8",
            "off_peak_discount = 50",
            "[tariff.home]",
            "call = 50",
            "per_unit = 30",
            "per_kilosegment = 200",
            "[tariff.other]",
            "call = 300",
            "per_unit = 150",
            "per_kilosegment = 1000",
        };

        private static Tariff Prices()
        {
            return Tariff.Load(IniConfig.Parse(TariffFile));
        }

        // A record: routed at the given UTC time, connected 500 ms later, connected for connectedMs.
        private static Cdr Call(int month, int day, int hourUtc, string calling, string called, long connectedMs,
            ulong segsForward, ulong segsReverse, string account, bool reverse = false, string calledAccount = "",
            string callId = "000000")
        {
            long wall = new DateTimeOffset(2026, month, day, hourUtc, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            return new Cdr
            {
                StartTimeMs = 1_000_000,
                ConnectedTimeMs = connectedMs < 0 ? 0 : 1_000_500,
                EndTimeMs = 1_000_500 + Math.Max(0, connectedMs),
                StartWallSeconds = wall,
                Calling = calling,
                Called = called,
                SegmentsForward = segsForward,
                SegmentsReverse = segsReverse,
                Account = account,
                ReverseCharging = reverse,
                CalledAccount = calledAccount,
                CallIdentifier = callId,
            };
        }

        private static List<Cdr> Records()
        {
            Cdr[] written =
            {
                // A: 10:00 local (peak), within the network, 150 s = 3 minutes started, 6 segments.
                //    0.50 + 3 x 0.30 + ceil(6 x 200 / 1000) = 50 + 90 + 2 = 142.
                Call(11, 2, 9, "23420001", "23420100", 150_000, 4, 2, "lineA"),
                // B: 21:00 local (off-peak), to DNIC 3134, exactly 60 s = 1 minute, 1500 segments.
                //    300 + 150 + 1500, less 50% of (150 + 1500) = 825: 1125.
                Call(11, 3, 20, "23420001", "31340012", 60_000, 1000, 500, "lineA", callId: "00A3F1"),
                // C: lineA received a reverse-charged call from another account: lineA pays. 1 ms = 1 minute started.
                //    50 + 30 + 0 = 80.
                Call(11, 4, 9, "23420007", "23420001", 1, 0, 0, "otherA", reverse: true, calledAccount: "lineA"),
                // D: never connected: not charged.
                Call(11, 5, 9, "23420001", "23420100", -1, 0, 0, "lineA"),
                // E: October: another month.
                Call(10, 30, 9, "23420001", "23420100", 60_000, 1, 1, "lineA"),
                // F: lineA called hostB reverse charged: hostB pays, not lineA.
                Call(11, 6, 9, "23420001", "23420100", 60_000, 1, 1, "lineA", reverse: true, calledAccount: "hostB"),
                // G: a PVC period of lineA, 13:00 local (peak), 15 minutes, 10 segments: no call fee.
                //    15 x 30 + ceil(10 x 200 / 1000) = 450 + 2 = 452.
                Call(11, 7, 12, string.Empty, string.Empty, 900_000, 6, 4, "lineA"),
            };

            string[] lines = new string[written.Length];
            for (int i = 0; i < written.Length; i++)
            {
                lines[i] = CdrFileWriter.Format(written[i]);
            }

            return CdrReader.Read(lines);
        }

        [Test]
        public void TheBill_IsTheOneWorkedOutByHand()
        {
            Invoice inv = InvoiceBuilder.Build(Prices(), Records(), "lineA", 2026, 11);

            Assert.That(inv.HomeCalls.Count, Is.EqualTo(3), "A, C and the PVC period");
            Assert.That(inv.HomeCalls[0].Total, Is.EqualTo(142));
            Assert.That(inv.HomeCalls[0].TimeUnits, Is.EqualTo(3));
            Assert.That(inv.HomeCalls[1].Total, Is.EqualTo(80));
            Assert.That(inv.HomeCalls[1].ReverseCharged, Is.True);
            Assert.That(inv.HomeCalls[1].OtherParty, Is.EqualTo("23420007"), "the caller, for a call lineA received");
            Assert.That(inv.HomeCalls[2].IsPvc, Is.True);
            Assert.That(inv.HomeCalls[2].CallFee, Is.EqualTo(0));
            Assert.That(inv.HomeCalls[2].Total, Is.EqualTo(452));

            Assert.That(inv.OtherNetworkCalls.Count, Is.EqualTo(1));
            InvoiceLine b = inv.OtherNetworkCalls[0];
            Assert.That(b.OffPeak, Is.True, "21:00 local");
            Assert.That(b.TimeUnits, Is.EqualTo(1), "exactly one minute is one unit");
            Assert.That(b.DataCharge, Is.EqualTo(1500));
            Assert.That(b.Discount, Is.EqualTo(825));
            Assert.That(b.Total, Is.EqualTo(1125));
            Assert.That(b.CallIdentifier, Is.EqualTo("00A3F1"));

            Assert.That(inv.HomeTotal, Is.EqualTo(674));
            Assert.That(inv.OtherNetworkTotal, Is.EqualTo(1125));
            Assert.That(inv.Total, Is.EqualTo(26_799), "250.00 + 6.74 + 11.25");
        }

        [Test]
        public void ThePrintedBill_ShowsTheTotal_AndStaysWithin78Columns()
        {
            Invoice inv = InvoiceBuilder.Build(Prices(), Records(), "lineA", 2026, 11);
            string text = InvoiceText.Format(inv, "Test network");
            TestContext.Progress.WriteLine(text);

            Assert.That(text, Does.Contain("TOTAL DUE"));
            Assert.That(text, Does.Contain("267.99"));
            Assert.That(text, Does.Contain("00A3F1"));
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Assert.That(lines[i].TrimEnd('*').Length, Is.LessThanOrEqualTo(78), "line " + (i + 1) + ": " + lines[i]);
            }
        }

        /// <summary>
        /// The switch console's <c>show x25 invoice</c> reads <c>cdr_file</c> and <c>tariff_file</c> and prints the same
        /// bill; with no month given it takes this month, local time.
        /// </summary>
        [Test]
        public void TheConsoleCommand_PrintsTheBill()
        {
            string dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "invoice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string cdr = Path.Combine(dir, "calls.cdr");
                string tariff = Path.Combine(dir, "tariff.conf");
                List<Cdr> records = Records();
                string[] lines = new string[records.Count];
                for (int i = 0; i < records.Count; i++)
                {
                    lines[i] = CdrFileWriter.Format(records[i]);
                }

                File.WriteAllLines(cdr, lines);
                File.WriteAllLines(tariff, TariffFile);

                SwitchNode node = new SwitchNode(new SwitchConfig { CdrFile = cdr, TariffFile = tariff, Name = "Test network" });
                RetroIO.Protocols.X25.Switch.Management.SwitchCli cli = new RetroIO.Protocols.X25.Switch.Management.SwitchCli(node)
                {
                    WallClock = () => new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
                };

                string named = cli.Process("show x25 invoice lineA 2026-11");
                Assert.That(named, Does.Contain("267.99"));
                Assert.That(named, Does.Contain("TEST NETWORK"));
                Assert.That(cli.Process("show x25 invoice lineA"), Is.EqualTo(named), "no month: this month");
                Assert.That(cli.Process("show x25 invoice lineA 2026-10"), Does.Contain("250.00"), "October: the monthly fee and nothing else");
                Assert.That(cli.Process("show x25 invoice lineA 2026-13"), Does.StartWith("Usage"));

                File.AppendAllText(cdr, "not,a,record\n");
                Assert.That(cli.Process("show x25 invoice lineA 2026-11"), Does.StartWith("Error: record line 8"), "a bad line is reported, not billed around");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>
        /// Four lines exactly as x25emu's switch wrote them (its call-through-switch test, 2026-11-17), 24 columns. All
        /// on 2026-11-17 08:00 local (1794898846 = 07:00:46 UTC), peak. Worked by hand: the first two never connected,
        /// nothing; the third connected for 0 ms (its far end cleared at once), the call fee only, 50; the fourth
        /// connected 245 ms = 1 minute started, 50 + 30 + ceil(2 x 200 / 1000) = 81. With 250.00 a month: 251.31.
        /// </summary>
        [Test]
        public void ABill_FromLinesX25emuWrote()
        {
            string[] lines =
            {
                "1873290920,20,23420001,23420100,0,1,0,0,0,0,0,0,1794898846,000000,0,0,0,0,lineA,,0,caller,1,",
                "1873290940,20,23420001,23420100,0,1,0,0,0,0,3,84,1794898846,000000,0,0,0,0,lineA,,0,caller,0,",
                "1873291162,20,23420001,23420100,0,1,0,0,0,0,0,0,1794898846,000000,1873291182,0,0,0,lineA,,0,caller,0,card7",
                "1873291384,255,23420001,23420100,0,1,1,1,13,13,9,0,1794898846,000000,1873291394,245,1,1,lineA,,0,caller,0,",
            };

            Invoice inv = InvoiceBuilder.Build(Prices(), CdrReader.Read(lines), "lineA", 2026, 11);

            Assert.That(inv.HomeCalls.Count, Is.EqualTo(2));
            // In time order: both lines carry the same wall-clock second (x25emu's test sets it once), and the fourth
            // connected 10 ms after it was routed, the third 20 ms after, so the fourth comes first.
            Assert.That(inv.HomeCalls[0].Total, Is.EqualTo(81));
            Assert.That(inv.HomeCalls[1].Total, Is.EqualTo(50));
            Assert.That(inv.HomeCalls[0].OffPeak, Is.False, "08:00 local is the first peak hour");
            Assert.That(inv.Total, Is.EqualTo(25_131));
            Assert.That(InvoiceBuilder.PayingAccounts(CdrReader.Read(lines)), Is.EqualTo(new[] { "lineA" }), "card7 received a call it did not pay for");
        }

        [Test]
        public void TheAccountsThatPay_AreFoundInTheRecords()
        {
            List<string> payers = InvoiceBuilder.PayingAccounts(Records());
            Assert.That(payers, Is.EqualTo(new[] { "lineA", "hostB" }));
        }

        [Test]
        public void Money_IsPrintedInMajorUnits()
        {
            Tariff t = Prices();
            Assert.That(InvoiceText.Money(26_799, t), Is.EqualTo("267.99"));
            Assert.That(InvoiceText.Money(5, t), Is.EqualTo("0.05"));
            Assert.That(InvoiceText.Money(0, t), Is.EqualTo("0.00"));
        }

        [Test]
        public void ARecordLineWithTheWrongNumberOfColumns_IsRefused()
        {
            Assert.Throws<InvalidDataException>(() => CdrReader.Parse("1,2,3", 7));
        }

        [Test]
        public void AnOldLineOf23Columns_IsReadWithNoCalledAccount()
        {
            string line = CdrFileWriter.Format(Call(11, 2, 9, "23420001", "23420100", 60_000, 1, 1, "lineA", calledAccount: "x"));
            string old = line.Substring(0, line.LastIndexOf(','));
            Cdr c = CdrReader.Parse(old, 1);
            Assert.That(c.CalledAccount, Is.Empty);
            Assert.That(c.Account, Is.EqualTo("lineA"));
        }

        [TestCase("home_dnic = 23", "home_dnic")]
        [TestCase("off_peak_discount = 150", "off_peak_discount")]
        [TestCase("monthly_fee = 12.50", "monthly_fee")]
        public void ABadPriceSetting_IsRefused(string bad, string key)
        {
            string[] lines = (string[])TariffFile.Clone();
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(key + " ", StringComparison.Ordinal))
                {
                    lines[i] = bad;
                }
            }

            InvalidDataException e = Assert.Throws<InvalidDataException>(() => Tariff.Load(IniConfig.Parse(lines)))!;
            Assert.That(e.Message, Does.Contain(key));
        }

        [Test]
        public void AMissingPriceSection_IsRefused()
        {
            string[] lines = new string[11];
            Array.Copy(TariffFile, lines, 11);
            Assert.Throws<InvalidDataException>(() => Tariff.Load(IniConfig.Parse(lines)));
        }
    }
}
