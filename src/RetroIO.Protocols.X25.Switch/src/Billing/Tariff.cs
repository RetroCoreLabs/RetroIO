//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Globalization;
using System.IO;
using RetroIO.Protocols.X25.Common;

namespace RetroIO.Protocols.X25.Switch.Billing
{
    /// <summary>
    /// The prices of one kind of call: within our network, or to another one.
    /// </summary>
    public sealed class TariffPrices
    {
        /// <summary>
        /// The fee for each call that connected, in minor units. A PVC period line has none: a PVC is never set up.
        /// </summary>
        public long CallFee { get; set; }

        /// <summary>
        /// The price of one time unit connected (ITU-T D.11 charges connected time), in minor units.
        /// </summary>
        public long PerTimeUnit { get; set; }

        /// <summary>
        /// The price of 1000 segments (ITU-T D.12), in minor units.
        /// </summary>
        public long PerKilosegment { get; set; }
    }

    /// <summary>
    /// A price list: what a call costs. Read from <c>tariff.conf</c> by <see cref="Load(string)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// X.25 and the D-series say what is counted - connected time (D.11) and 64-octet segments (D.12) - and leave the
    /// prices to the Administration. Every figure here is therefore the operator's choice. Money is in whole minor units
    /// (øre, pence, cents): no floating point, so a bill adds up to the last unit and the same file gives the same bill
    /// on every machine.
    /// </para>
    /// </remarks>
    public sealed class Tariff
    {
        /// <summary>
        /// The currency name printed on the bill, for example <c>NOK</c>.
        /// </summary>
        public string Currency { get; set; } = string.Empty;

        /// <summary>
        /// Minor units in one major unit (100 øre in a krone). Only used to print the amounts.
        /// </summary>
        public int MinorUnits { get; set; } = 100;

        /// <summary>
        /// Our network's DNIC (X.121: the first four digits of an address). A call to an address that starts with any
        /// other DNIC left our network, and is priced with <see cref="Other"/>.
        /// </summary>
        public string HomeDnic { get; set; } = string.Empty;

        /// <summary>
        /// The fee for each month, per account, in minor units.
        /// </summary>
        public long MonthlyFee { get; set; }

        /// <summary>
        /// The length of a time unit, in seconds. Each call's connected time is rounded up to whole units.
        /// </summary>
        public int TimeUnitSeconds { get; set; } = 60;

        /// <summary>
        /// The local time, as minutes from UTC. The record file holds UTC; the bill month and the off-peak hours are
        /// local time.
        /// </summary>
        public int UtcOffsetMinutes { get; set; }

        /// <summary>
        /// The local hour off-peak starts (0 to 23).
        /// </summary>
        public int OffPeakStartHour { get; set; }

        /// <summary>
        /// The local hour off-peak ends (0 to 23); equal to <see cref="OffPeakStartHour"/> means no off-peak.
        /// </summary>
        public int OffPeakEndHour { get; set; }

        /// <summary>
        /// The discount, in percent, on the time and data charges of a call connected in off-peak hours. Not on the
        /// call fee.
        /// </summary>
        public int OffPeakDiscountPercent { get; set; }

        /// <summary>
        /// The prices within our network.
        /// </summary>
        public TariffPrices Home { get; set; } = new TariffPrices();

        /// <summary>
        /// The prices of a call to another network (through an X.75 gateway).
        /// </summary>
        public TariffPrices Other { get; set; } = new TariffPrices();

        /// <summary>
        /// True when a local hour is off-peak. The period may run past midnight (18 to 8).
        /// </summary>
        /// <param name="hour">
        /// The local hour, 0 to 23.
        /// </param>
        /// <returns>
        /// True when off-peak.
        /// </returns>
        public bool IsOffPeak(int hour)
        {
            if (OffPeakStartHour == OffPeakEndHour)
            {
                return false;
            }

            return OffPeakStartHour < OffPeakEndHour
                ? hour >= OffPeakStartHour && hour < OffPeakEndHour
                : hour >= OffPeakStartHour || hour < OffPeakEndHour;
        }

        /// <summary>
        /// Reads a price file.
        /// </summary>
        /// <param name="path">
        /// The file.
        /// </param>
        /// <returns>
        /// The prices.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// A setting is missing or not valid.
        /// </exception>
        public static Tariff Load(string path)
        {
            return Load(IniConfig.Load(path));
        }

        /// <summary>
        /// Reads prices from an already-parsed file.
        /// </summary>
        /// <param name="ini">
        /// The parsed file.
        /// </param>
        /// <returns>
        /// The prices.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// A setting is missing or not valid.
        /// </exception>
        public static Tariff Load(IniConfig ini)
        {
            Tariff t = new Tariff();
            t.Currency = Required(ini, "tariff", "currency");
            t.HomeDnic = Required(ini, "tariff", "home_dnic");
            if (t.HomeDnic.Length != 4 || !IsDigits(t.HomeDnic))
            {
                throw new InvalidDataException("[tariff] home_dnic = " + t.HomeDnic + ": four digits (X.121)");
            }

            t.MinorUnits = (int)Number(ini, "tariff", "minor_units", 100, 1, 10000);
            t.MonthlyFee = Number(ini, "tariff", "monthly_fee", 0, 0, long.MaxValue / 4);
            t.TimeUnitSeconds = (int)Number(ini, "tariff", "time_unit", 60, 1, 86400);
            t.UtcOffsetMinutes = (int)Number(ini, "tariff", "utc_offset", 0, -14 * 60, 14 * 60);
            t.OffPeakStartHour = (int)Number(ini, "tariff", "off_peak_start", 0, 0, 23);
            t.OffPeakEndHour = (int)Number(ini, "tariff", "off_peak_end", 0, 0, 23);
            t.OffPeakDiscountPercent = (int)Number(ini, "tariff", "off_peak_discount", 0, 0, 100);
            t.Home = Prices(ini, "tariff.home");
            t.Other = Prices(ini, "tariff.other");
            return t;
        }

        private static TariffPrices Prices(IniConfig ini, string section)
        {
            if (!ini.HasSection(section))
            {
                throw new InvalidDataException("[" + section + "] is missing");
            }

            return new TariffPrices
            {
                CallFee = Number(ini, section, "call", -1, 0, long.MaxValue / 4),
                PerTimeUnit = Number(ini, section, "per_unit", -1, 0, long.MaxValue / 4),
                PerKilosegment = Number(ini, section, "per_kilosegment", -1, 0, long.MaxValue / 4),
            };
        }

        private static string Required(IniConfig ini, string section, string key)
        {
            string? v = ini.Get(section, key);
            if (string.IsNullOrEmpty(v))
            {
                throw new InvalidDataException("[" + section + "] " + key + " is missing");
            }

            return v;
        }

        // A whole number in range. A default of -1 means the setting must be there.
        private static long Number(IniConfig ini, string section, string key, long defaultValue, long min, long max)
        {
            string? v = ini.Get(section, key);
            if (v == null)
            {
                if (defaultValue < 0)
                {
                    throw new InvalidDataException("[" + section + "] " + key + " is missing");
                }

                return defaultValue;
            }

            if (!long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n) || n < min || n > max)
            {
                throw new InvalidDataException("[" + section + "] " + key + " = " + v + ": a whole number from " + min + " to " + max);
            }

            return n;
        }

        private static bool IsDigits(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
