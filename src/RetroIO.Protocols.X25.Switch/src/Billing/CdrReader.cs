//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RetroIO.Protocols.X25.Switch.Model;

namespace RetroIO.Protocols.X25.Switch.Billing
{
    /// <summary>
    /// Reads the call record file back: the lines <see cref="CdrFileWriter"/> and x25emu's switch write, in the same
    /// columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A line has 24 columns. Lines written before 2026-11-17 have 23 (no called account); they are read with that column
    /// empty. Anything else is refused with the line number, never skipped: a bill that quietly leaves out a call is
    /// wrong in a way nobody would see.
    /// </para>
    /// </remarks>
    public static class CdrReader
    {
        /// <summary>
        /// Reads every line of a record file.
        /// </summary>
        /// <param name="path">
        /// The record file.
        /// </param>
        /// <returns>
        /// The records, in file order.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// A line is not a record line.
        /// </exception>
        public static List<Cdr> ReadFile(string path)
        {
            return Read(File.ReadAllLines(path));
        }

        /// <summary>
        /// Reads record lines.
        /// </summary>
        /// <param name="lines">
        /// The lines; empty ones are passed over.
        /// </param>
        /// <returns>
        /// The records, in order.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// A line is not a record line.
        /// </exception>
        public static List<Cdr> Read(IReadOnlyList<string> lines)
        {
            List<Cdr> records = new List<Cdr>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                records.Add(Parse(line, i + 1));
            }

            return records;
        }

        /// <summary>
        /// Reads one record line.
        /// </summary>
        /// <param name="line">
        /// The line, without its line end.
        /// </param>
        /// <param name="lineNumber">
        /// Its line number, for the error message.
        /// </param>
        /// <returns>
        /// The record.
        /// </returns>
        /// <exception cref="InvalidDataException">
        /// The line is not a record line.
        /// </exception>
        public static Cdr Parse(string line, int lineNumber)
        {
            string[] c = line.Split(',');
            if (c.Length != 23 && c.Length != 24)
            {
                throw new InvalidDataException("record line " + lineNumber + ": " + c.Length + " columns, expected 24 (or 23)");
            }

            long start = Long(c, 0, lineNumber);
            long duration = Long(c, 1, lineNumber);
            Cdr cdr = new Cdr
            {
                StartTimeMs = start,
                EndTimeMs = start + duration,
                Calling = c[2],
                Called = c[3],
                InPort = (int)Long(c, 4, lineNumber),
                OutPort = (int)Long(c, 5, lineNumber),
                PacketsForward = (uint)Long(c, 6, lineNumber),
                PacketsReverse = (uint)Long(c, 7, lineNumber),
                BytesForward = (ulong)Long(c, 8, lineNumber),
                BytesReverse = (ulong)Long(c, 9, lineNumber),
                ClearCause = (byte)Long(c, 10, lineNumber),
                ClearDiagnostic = (byte)Long(c, 11, lineNumber),
                StartWallSeconds = Long(c, 12, lineNumber),
                CallIdentifier = c[13],
                ConnectedTimeMs = Long(c, 14, lineNumber),
                SegmentsForward = (ulong)Long(c, 16, lineNumber),
                SegmentsReverse = (ulong)Long(c, 17, lineNumber),
                Account = c[18],
                Nui = c[19],
                ReverseCharging = c[20] == "1",
                ChargingRequested = c[22] == "1",
                CalledAccount = c.Length == 24 ? c[23] : string.Empty,
            };

            // Column 16, the connected duration, is not kept: both writers make it from the others (end minus connect
            // time, 0 when never connected), and a bill uses ConnectedDurationMs(cdr), which does the same. A line where
            // it does not agree was not written by either switch, so it is refused rather than billed one way or other.
            long written = Long(c, 15, lineNumber);
            if (written != ConnectedDurationMs(cdr))
            {
                throw new InvalidDataException("record line " + lineNumber + ": connected duration " + written
                    + " ms does not match connect time and duration (" + ConnectedDurationMs(cdr) + " ms)");
            }

            return cdr;
        }

        /// <summary>
        /// The time a call was connected: from the Call Connected to the clear, the time ITU-T D.11 charges; 0 for a
        /// call that never connected.
        /// </summary>
        /// <param name="cdr">
        /// The record.
        /// </param>
        /// <returns>
        /// Milliseconds.
        /// </returns>
        public static long ConnectedDurationMs(Cdr cdr)
        {
            return cdr.ConnectedTimeMs == 0 ? 0 : Math.Max(0, cdr.EndTimeMs - cdr.ConnectedTimeMs);
        }

        private static long Long(string[] c, int column, int lineNumber)
        {
            if (!long.TryParse(c[column], NumberStyles.None, CultureInfo.InvariantCulture, out long v))
            {
                throw new InvalidDataException("record line " + lineNumber + ", column " + (column + 1) + ": '" + c[column]
                    + "' is not a whole number");
            }

            return v;
        }
    }
}
