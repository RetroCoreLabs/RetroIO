//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Storage.Ndfs.Elements
{
    /// <summary>
    /// Decodes/encodes the packed 32-bit SINTRAN/ND-100 calendar date word used throughout NDFS
    /// on-disk structures (ObjectEntry.DateCreated/LastDateOpenedForRead/LastDateOpenedForWrite,
    /// UserEntry.DateCreated/LastDateEntered, ...).
    ///
    /// RetroIO.Storage.Ndfs never implemented this decode step before — the field was read/written as a
    /// raw, opaque uint32 with only a "(ND date format)" label comment. Ported from the verified
    /// reference implementation in the legacy RetroCommander codebase
    /// (Emulated.Lib/DateHelper.cs FromNDDate/ToNDDate), cross-corroborated there by an
    /// independent ND-500/Sintran filesystem driver and a real disk-dump sample.
    ///
    /// Bit layout (32-bit word, bit 31 = MSB):
    ///   bits 31-26 (6 bits): year, offset from 1950 (0-63 -> 1950-2013)
    ///   bits 25-22 (4 bits): month (1-12)
    ///   bits 21-17 (5 bits): day of month (1-31)
    ///   bits 16-12 (5 bits): hour (0-23; the source system encodes 24-31 which are clamped to 0)
    ///   bits 11-6  (6 bits): minute (0-59)
    ///   bits 5-0   (6 bits): second (0-59)
    /// </summary>
    public static class NdDateTime
    {
        private const int MinYear = 1950;
        private const int MaxYear = 2013;

        /// <summary>
        /// Decodes a raw packed NDFS date word into a DateTime. Returns null for the raw value 0
        /// — unlike the legacy reference (which maps 0 to a literal 1950-01-01 sentinel date, since
        /// it only ever reads real vintage disk images), RetroIO.Storage.Ndfs's own file-creation path does
        /// not yet always set this field, so 0 here means "no date recorded" rather than a
        /// deliberate SINTRAN sentinel — surfacing that as null is more correct for RetroFS's own
        /// writers. Also returns null if the packed fields fail range/calendar validation (e.g.
        /// day 31 in a 30-day month), which indicates corrupt or unset data rather than a real date.
        /// </summary>
        public static DateTime? FromNdDate(uint raw)
        {
            if (raw == 0)
                return null;

            int year = (int)((raw >> 26) & 0x3F) + MinYear;
            int month = (int)((raw >> 22) & 0x0F);
            int day = (int)((raw >> 17) & 0x1F);
            int hour = (int)((raw >> 12) & 0x1F);
            int minute = (int)((raw >> 6) & 0x3F);
            int second = (int)(raw & 0x3F);

            if (hour > 23)
                hour = 0;

            if (year < MinYear || year > MaxYear ||
                month < 1 || month > 12 ||
                day < 1 || day > 31 ||
                minute > 59 || second > 59)
            {
                return null;
            }

            try
            {
                return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            }
            catch (ArgumentOutOfRangeException)
            {
                // The packed word doesn't itself validate day-per-month (e.g. day 31 in April).
                return null;
            }
        }

        /// <summary>
        /// Moves a date into the years the ND date word can hold (1950-2013).
        /// </summary>
        /// <param name="dateTime">
        /// Any date.
        /// </param>
        /// <returns>
        /// The date itself when it is in range; otherwise the same day, month and time in a year
        /// that fits.
        /// </returns>
        /// <remarks>
        /// The year field has 6 bits, so today's date does not fit. Rule chosen by Ronny Hansen on
        /// 2026-11-07: a date before 1950 becomes the same day in 1950; a date after 2013 moves back
        /// 30 years, again until it fits (2026-11-07 becomes 1996-11-07, 2050 becomes 1990).
        /// <see cref="DateTime.AddYears"/> turns 29 February into 28 February when the new year has
        /// no 29 February.
        /// </remarks>
        public static DateTime FitNdRange(DateTime dateTime)
        {
            int year = dateTime.Year;
            if (year < MinYear)
                year = MinYear;
            while (year > MaxYear)
                year -= 30;

            // One AddYears to the final year, not one per 30-year step: stepping through a year
            // without 29 February (2044 -> 2014 -> 1984) would lose the 29th that 1984 has.
            return dateTime.AddYears(year - dateTime.Year);
        }

        /// <summary>
        /// The local time now, moved into range with <see cref="FitNdRange"/> and packed. This is
        /// what the writers stamp on a file they create or write.
        /// </summary>
        /// <returns>
        /// The packed ND date word; never 0.
        /// </returns>
        /// <remarks>
        /// Replaces <c>ToNdDateOrUnset(DateTime.Now)</c> at the write sites, which always gave 0
        /// from 2014 on, so SINTRAN showed every file RetroIO wrote as created "?0, 1950".
        /// </remarks>
        public static uint Now()
        {
            return ToNdDate(FitNdRange(DateTime.Now));
        }

        /// <summary>
        /// Encodes a DateTime into the raw packed NDFS date word, or 0 ("not set") if the year
        /// falls outside the representable range (1950-2013) — notably, the current real-world
        /// date, which is out of range for every year from 2014 onward. To stamp "now" on a file
        /// use <see cref="Now"/>, which moves the date into range instead (Ronny's 2026-11-07
        /// rule); a 0 here made SINTRAN show the file as created "?0, 1950".
        /// </summary>
        public static uint ToNdDateOrUnset(DateTime dateTime)
        {
            if (dateTime.Year < MinYear || dateTime.Year > MaxYear)
                return 0;
            return ToNdDate(dateTime);
        }

        /// <summary>
        /// Encodes a DateTime into the raw packed NDFS date word. Throws if the year is outside
        /// the representable range (1950-2013) — for "record the current timestamp" call sites,
        /// prefer <see cref="ToNdDateOrUnset"/> instead, since the real-world "now" is always out
        /// of range from 2014 onward.
        /// </summary>
        public static uint ToNdDate(DateTime dateTime)
        {
            if (dateTime.Year < MinYear || dateTime.Year > MaxYear)
            {
                throw new ArgumentOutOfRangeException(nameof(dateTime),
                    $"Year {dateTime.Year} is outside the representable NDFS date range ({MinYear}-{MaxYear}).");
            }

            uint raw = 0;
            raw |= (uint)(dateTime.Year - MinYear) << 26;
            raw |= (uint)dateTime.Month << 22;
            raw |= (uint)dateTime.Day << 17;
            raw |= (uint)dateTime.Hour << 12;
            raw |= (uint)dateTime.Minute << 6;
            raw |= (uint)dateTime.Second;
            return raw;
        }
    }
}
