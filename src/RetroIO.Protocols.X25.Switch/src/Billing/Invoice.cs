//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Protocols.X25.Switch.Model;

namespace RetroIO.Protocols.X25.Switch.Billing
{
    /// <summary>
    /// One charged call (or one PVC period) on a bill.
    /// </summary>
    public sealed class InvoiceLine
    {
        /// <summary>
        /// When the call connected, local time (the tariff's UTC offset applied).
        /// </summary>
        public DateTime LocalConnected { get; set; }

        /// <summary>
        /// The other end's address: the called one, or the calling one when this account paid for a reverse-charged
        /// call it received. Empty for a PVC.
        /// </summary>
        public string OtherParty { get; set; } = string.Empty;

        /// <summary>
        /// True when this account received the call and paid for it (reverse charging, X.25 6.18).
        /// </summary>
        public bool ReverseCharged { get; set; }

        /// <summary>
        /// True for a PVC period line (no addresses, no call fee).
        /// </summary>
        public bool IsPvc { get; set; }

        /// <summary>
        /// The X.75 call identifier from the record, six hex digits; <c>000000</c> when the call had none.
        /// </summary>
        public string CallIdentifier { get; set; } = "000000";

        /// <summary>
        /// Connected time, milliseconds (ITU-T D.11).
        /// </summary>
        public long ConnectedMs { get; set; }

        /// <summary>
        /// Time units charged: the connected time rounded up to whole units.
        /// </summary>
        public long TimeUnits { get; set; }

        /// <summary>
        /// Segments both ways (ITU-T D.12): the paying side pays for what it sent and what it received.
        /// </summary>
        public ulong Segments { get; set; }

        /// <summary>
        /// True when the call connected in off-peak hours.
        /// </summary>
        public bool OffPeak { get; set; }

        /// <summary>
        /// The call fee, minor units.
        /// </summary>
        public long CallFee { get; set; }

        /// <summary>
        /// The time charge, minor units.
        /// </summary>
        public long TimeCharge { get; set; }

        /// <summary>
        /// The data charge, minor units.
        /// </summary>
        public long DataCharge { get; set; }

        /// <summary>
        /// The off-peak discount, minor units (taken off).
        /// </summary>
        public long Discount { get; set; }

        /// <summary>
        /// What this line costs: call fee, time and data, less the discount.
        /// </summary>
        public long Total => CallFee + TimeCharge + DataCharge - Discount;
    }

    /// <summary>
    /// A bill: one account, one month.
    /// </summary>
    public sealed class Invoice
    {
        /// <summary>
        /// The account billed.
        /// </summary>
        public string Account { get; set; } = string.Empty;

        /// <summary>
        /// The year of the month billed.
        /// </summary>
        public int Year { get; set; }

        /// <summary>
        /// The month billed, 1 to 12.
        /// </summary>
        public int Month { get; set; }

        /// <summary>
        /// The prices the bill was worked out with.
        /// </summary>
        public Tariff Tariff { get; set; } = new Tariff();

        /// <summary>
        /// Calls within our network, and PVC periods, in time order.
        /// </summary>
        public List<InvoiceLine> HomeCalls { get; } = new List<InvoiceLine>();

        /// <summary>
        /// Calls to or from another network, in time order.
        /// </summary>
        public List<InvoiceLine> OtherNetworkCalls { get; } = new List<InvoiceLine>();

        /// <summary>
        /// The sum of <see cref="HomeCalls"/>.
        /// </summary>
        public long HomeTotal => Sum(HomeCalls);

        /// <summary>
        /// The sum of <see cref="OtherNetworkCalls"/>.
        /// </summary>
        public long OtherNetworkTotal => Sum(OtherNetworkCalls);

        /// <summary>
        /// The whole bill: the monthly fee and every call.
        /// </summary>
        public long Total => Tariff.MonthlyFee + HomeTotal + OtherNetworkTotal;

        private static long Sum(List<InvoiceLine> lines)
        {
            long s = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                s += lines[i].Total;
            }

            return s;
        }
    }

    /// <summary>
    /// Works out a bill from the call record file and a price list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules, every one written in <c>docs/billing.md</c> of RetroX25:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Who pays: the caller's account (column 19), or the called side's (column 24) when the call was
    /// reverse charged (X.25 6.18, column 21).</description></item>
    /// <item><description>Only a call that connected is charged (D.11 charges connected time; nothing crosses before).
    /// Its month and off-peak hour are those of the moment it connected, local time.</description></item>
    /// <item><description>Another network: the other end's address starts with a DNIC that is not ours.</description></item>
    /// <item><description>Time: the connected time rounded up to whole time units, once per call. Data: the segments
    /// both ways, times the price of 1000, rounded up to a whole minor unit, once per call.</description></item>
    /// <item><description>Off-peak: a percentage off the time and data charges, rounded down; the call fee is never
    /// discounted.</description></item>
    /// <item><description>A PVC period line (no addresses) is charged time and data, no call fee.</description></item>
    /// </list>
    /// </remarks>
    public static class InvoiceBuilder
    {
        /// <summary>
        /// Works out one account's bill for one month.
        /// </summary>
        /// <param name="tariff">
        /// The prices.
        /// </param>
        /// <param name="records">
        /// The call records (<see cref="CdrReader"/>).
        /// </param>
        /// <param name="account">
        /// The account.
        /// </param>
        /// <param name="year">
        /// The year.
        /// </param>
        /// <param name="month">
        /// The month, 1 to 12.
        /// </param>
        /// <returns>
        /// The bill.
        /// </returns>
        public static Invoice Build(Tariff tariff, IReadOnlyList<Cdr> records, string account, int year, int month)
        {
            Invoice inv = new Invoice { Account = account, Year = year, Month = month, Tariff = tariff };
            for (int i = 0; i < records.Count; i++)
            {
                Cdr r = records[i];
                if (r.ConnectedTimeMs == 0 || Payer(r) != account)
                {
                    continue;
                }

                DateTime local = LocalConnected(tariff, r);
                if (local.Year != year || local.Month != month)
                {
                    continue;
                }

                InvoiceLine line = Price(tariff, r, local, out bool otherNetwork);
                (otherNetwork ? inv.OtherNetworkCalls : inv.HomeCalls).Add(line);
            }

            inv.HomeCalls.Sort(ByTime);
            inv.OtherNetworkCalls.Sort(ByTime);
            return inv;
        }

        /// <summary>
        /// The account that pays for a record: the caller's, or the called side's when it was reverse charged.
        /// </summary>
        /// <param name="r">
        /// The record.
        /// </param>
        /// <returns>
        /// The account name; empty when none.
        /// </returns>
        public static string Payer(Cdr r)
        {
            return r.ReverseCharging ? r.CalledAccount : r.Account;
        }

        /// <summary>
        /// Every account that pays for at least one connected call in the records, each once, in the order first seen.
        /// </summary>
        /// <param name="records">
        /// The records.
        /// </param>
        /// <returns>
        /// The account names.
        /// </returns>
        public static List<string> PayingAccounts(IReadOnlyList<Cdr> records)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < records.Count; i++)
            {
                string p = Payer(records[i]);
                if (records[i].ConnectedTimeMs != 0 && p.Length > 0 && !names.Contains(p))
                {
                    names.Add(p);
                }
            }

            return names;
        }

        /// <summary>
        /// When a record's call connected, local time. The record holds the wall-clock start (UTC seconds) and the
        /// switch-clock times; the connect is that start plus the time from routing to connect.
        /// </summary>
        /// <param name="tariff">
        /// The prices (for the UTC offset).
        /// </param>
        /// <param name="r">
        /// The record.
        /// </param>
        /// <returns>
        /// The local time.
        /// </returns>
        public static DateTime LocalConnected(Tariff tariff, Cdr r)
        {
            long ms = r.StartWallSeconds * 1000 + Math.Max(0, r.ConnectedTimeMs - r.StartTimeMs) + tariff.UtcOffsetMinutes * 60_000L;
            return DateTime.UnixEpoch.AddMilliseconds(ms);
        }

        private static InvoiceLine Price(Tariff tariff, Cdr r, DateTime local, out bool otherNetwork)
        {
            bool pvc = r.Calling.Length == 0 && r.Called.Length == 0;
            string other = r.ReverseCharging ? r.Calling : r.Called;
            otherNetwork = !pvc && other.Length >= 4 && !other.StartsWith(tariff.HomeDnic, StringComparison.Ordinal);
            TariffPrices prices = otherNetwork ? tariff.Other : tariff.Home;

            long connected = CdrReader.ConnectedDurationMs(r);
            long unitMs = tariff.TimeUnitSeconds * 1000L;
            long units = (connected + unitMs - 1) / unitMs;
            ulong segments = r.SegmentsForward + r.SegmentsReverse;

            InvoiceLine line = new InvoiceLine
            {
                LocalConnected = local,
                OtherParty = pvc ? string.Empty : other,
                ReverseCharged = r.ReverseCharging,
                IsPvc = pvc,
                CallIdentifier = r.CallIdentifier,
                ConnectedMs = connected,
                TimeUnits = units,
                Segments = segments,
                OffPeak = tariff.IsOffPeak(local.Hour),
                CallFee = pvc ? 0 : prices.CallFee,
                TimeCharge = units * prices.PerTimeUnit,
                DataCharge = ((long)segments * prices.PerKilosegment + 999) / 1000,
            };

            if (line.OffPeak)
            {
                line.Discount = (line.TimeCharge + line.DataCharge) * tariff.OffPeakDiscountPercent / 100;
            }

            return line;
        }

        private static int ByTime(InvoiceLine a, InvoiceLine b)
        {
            return a.LocalConnected.CompareTo(b.LocalConnected);
        }
    }
}
