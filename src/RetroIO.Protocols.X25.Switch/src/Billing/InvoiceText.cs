//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RetroIO.Protocols.X25.Switch.Billing
{
    /// <summary>
    /// Prints a bill as plain text, 78 columns wide, laid out like a printed statement from a packet network of the
    /// 1980s: a heading, the monthly fee, one line per call in time order, calls to other networks in a part of their
    /// own, and the total. Used by the switch console and by the RetroX25 bill window.
    /// </summary>
    public static class InvoiceText
    {
        private const int Width = 78;

        /// <summary>
        /// The bill as text, lines separated by <c>\n</c>.
        /// </summary>
        /// <param name="inv">
        /// The bill.
        /// </param>
        /// <param name="networkName">
        /// The network's name for the heading; the DNIC is used when empty.
        /// </param>
        /// <returns>
        /// The text.
        /// </returns>
        public static string Format(Invoice inv, string networkName)
        {
            Tariff t = inv.Tariff;
            StringBuilder sb = new StringBuilder(4096);
            string rule = new string('=', Width);
            string thin = new string('-', Width);
            string name = networkName.Length > 0 ? networkName : "Packet network " + t.HomeDnic;

            sb.Append(rule).Append('\n');
            Centre(sb, name.ToUpperInvariant());
            Centre(sb, "STATEMENT OF ACCOUNT");
            sb.Append(rule).Append('\n');
            sb.Append("Account : ").Append(inv.Account).Append('\n');
            sb.Append("Period  : ").Append(inv.Year.ToString("D4", CultureInfo.InvariantCulture)).Append('-')
                .Append(inv.Month.ToString("D2", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("Currency: ").Append(t.Currency).Append("    Time unit: ").Append(t.TimeUnitSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(" s    Segment: 64 octets\n");
            sb.Append(thin).Append('\n');
            Amount(sb, "Monthly subscription", t.MonthlyFee, t);
            sb.Append('\n');

            Section(sb, "CALLS WITHIN THE NETWORK", inv.HomeCalls, false, t);
            Amount(sb, "Subtotal, calls within the network", inv.HomeTotal, t);
            sb.Append('\n');
            Section(sb, "CALLS TO AND FROM OTHER NETWORKS (X.75)", inv.OtherNetworkCalls, true, t);
            Amount(sb, "Subtotal, other networks", inv.OtherNetworkTotal, t);

            sb.Append(rule).Append('\n');
            Amount(sb, "TOTAL DUE", inv.Total, t);
            sb.Append(rule).Append('\n');
            sb.Append("R = reverse charged: you were called and paid for the call.\n");
            sb.Append("* = off-peak: ").Append(t.OffPeakDiscountPercent.ToString(CultureInfo.InvariantCulture))
                .Append("% off the time and data charges.\n");
            return sb.ToString();
        }

        /// <summary>
        /// An amount in minor units as major units, for example 12345 with 100 minor units as <c>123.45</c>.
        /// </summary>
        /// <param name="minor">
        /// The amount.
        /// </param>
        /// <param name="t">
        /// The prices (for the minor units).
        /// </param>
        /// <returns>
        /// The text.
        /// </returns>
        public static string Money(long minor, Tariff t)
        {
            int digits = 0;
            int m = t.MinorUnits;
            while (m > 1 && m % 10 == 0)
            {
                m /= 10;
                digits++;
            }

            if (m != 1 || digits == 0)
            {
                // Not a power of ten (or no minor unit): print the minor units as they are.
                return minor.ToString(CultureInfo.InvariantCulture);
            }

            long major = minor / t.MinorUnits;
            long rest = minor % t.MinorUnits;
            string sign = minor < 0 ? "-" : string.Empty;
            return sign + System.Math.Abs(major).ToString(CultureInfo.InvariantCulture) + "."
                + System.Math.Abs(rest).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
        }

        private static void Section(StringBuilder sb, string title, List<InvoiceLine> lines, bool withCallId, Tariff t)
        {
            sb.Append(title).Append('\n');

            // Columns: date and time 17, address 20 (13 and a 6-digit call id on the other-network part), then units 5,
            // segments 7, call fee 8, time and data after any discount 11, amount 10, and the off-peak mark: 79 wide
            // with the mark, which is blank on most lines, so the text itself stays within 78.
            StringBuilder head = new StringBuilder(Width);
            head.Append("Date       Time  ");
            head.Append(withCallId ? Fit("Address", 13) + " " + "Callid" : Fit("Address", 20));
            head.Append("Units".PadLeft(5)).Append("Segs".PadLeft(7)).Append("Fee".PadLeft(8))
                .Append("Time+data".PadLeft(11)).Append("Amount".PadLeft(10));
            sb.Append(head).Append('\n');
            if (lines.Count == 0)
            {
                sb.Append("  (none)\n");
            }

            for (int i = 0; i < lines.Count; i++)
            {
                InvoiceLine l = lines[i];
                StringBuilder row = new StringBuilder(Width);
                row.Append(l.LocalConnected.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(' ');
                string who = l.IsPvc ? "PVC" : (l.ReverseCharged ? "R " : "  ") + l.OtherParty;
                if (withCallId)
                {
                    row.Append(Fit(who, 13)).Append(' ').Append(l.CallIdentifier.PadRight(6));
                }
                else
                {
                    row.Append(Fit(who, 20));
                }

                row.Append(l.TimeUnits.ToString(CultureInfo.InvariantCulture).PadLeft(5));
                row.Append(l.Segments.ToString(CultureInfo.InvariantCulture).PadLeft(7));
                row.Append(Money(l.CallFee, t).PadLeft(8));
                row.Append(Money(l.TimeCharge + l.DataCharge - l.Discount, t).PadLeft(11));
                row.Append(Money(l.Total, t).PadLeft(10));
                row.Append(l.OffPeak ? '*' : ' ');
                sb.Append(row.ToString().TrimEnd()).Append('\n');
            }
        }

        private static string Fit(string s, int width)
        {
            return s.Length >= width ? s.Substring(0, width - 1) + " " : s.PadRight(width);
        }

        private static void Amount(StringBuilder sb, string label, long minor, Tariff t)
        {
            string money = Money(minor, t);
            sb.Append(label).Append(' ', System.Math.Max(1, Width - label.Length - money.Length - 1)).Append(money).Append('\n');
        }

        private static void Centre(StringBuilder sb, string s)
        {
            sb.Append(' ', System.Math.Max(0, (Width - s.Length) / 2)).Append(s).Append('\n');
        }
    }
}
