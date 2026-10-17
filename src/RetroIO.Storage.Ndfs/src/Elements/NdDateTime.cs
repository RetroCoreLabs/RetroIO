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
        /// Encodes a DateTime into the raw packed NDFS date word, or 0 ("not set") if the year
        /// falls outside the representable range (1950-2013) — notably, the current real-world
        /// date, which is out of range for every year from 2014 onward. Use this (not
        /// <see cref="ToNdDate"/>) when stamping "now" onto a newly written/modified file: it's
        /// more honest to record no timestamp than to fabricate or silently clamp one the format
        /// was never able to hold.
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
