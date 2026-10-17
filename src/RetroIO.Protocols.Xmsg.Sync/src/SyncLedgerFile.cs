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

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// Writes a <see cref="SyncLedger"/> to a file and reads it back, so what has already been
    /// carried survives the daemon restarting.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the daemon needs this to be usable at all</b></para>
    /// <para>
    /// The ledger answers "have we already carried this exact content". Held only in memory, that
    /// answer is NO for everything after every restart, so the daemon offers the whole folder
    /// again. Against a machine that already has those files each one is a CREATE of a name that
    /// exists, which SINTRAN refuses - so the first pass after every restart is a burst of
    /// failures that mean nothing.
    /// </para>
    /// <para>Kept out of <see cref="SyncLedger"/> on purpose</para>
    /// <para>
    /// The ledger itself opens no file and needs no disk, which is what makes its rules cheap to
    /// test. Reading and writing are a separate concern and live here.
    /// </para>
    /// <para><b>Format</b></para>
    /// <para>
    /// One line per file: <c>direction|hashHex|path</c>. The path goes LAST because it is the only
    /// field that can contain almost anything, so nothing after it needs escaping. A line that
    /// cannot be read is skipped rather than fatal - a half-written ledger must not stop the daemon
    /// starting, and the cost of skipping one is that its file is offered again.
    /// </para>
    /// </remarks>
    public static class SyncLedgerFile
    {
        /// <summary>
        /// First field of a line that records only that a file is on the far machine.
        /// </summary>
        /// <remarks>
        /// Deliberately not a number. <see cref="Load"/> skips any line whose first field will not
        /// parse as a direction, so an older reader ignores these harmlessly.
        /// </remarks>
        private const string RemoteOnlyMarker = "X";

        /// <summary>
        /// Writes the ledger out.
        /// </summary>
        /// <param name="ledger">
        /// The ledger to save.
        /// </param>
        /// <param name="path">
        /// Where to write it.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="ledger"/> or <paramref name="path"/> is null.
        /// </exception>
        public static void Save(SyncLedger ledger, string path)
        {
            if (ledger == null) { throw new ArgumentNullException(nameof(ledger)); }
            if (path == null) { throw new ArgumentNullException(nameof(path)); }

            string[] paths = ledger.CopyPaths();
            List<string> lines = new List<string>(paths.Length);

            for (int i = 0; i < paths.Length; i++)
            {
                byte[] hash;
                SyncDirection direction;
                if (!ledger.TryGetEntry(paths[i], out hash, out direction))
                {
                    continue;
                }

                lines.Add(((int)direction).ToString(CultureInfo.InvariantCulture)
                    + "|" + Convert.ToHexString(hash)
                    + "|" + paths[i]);
            }

            // THE FILES THE MACHINE HAS TOLD US ABOUT, which we never carried ourselves.
            //
            // Written with a NON-NUMERIC first field on purpose. Load parses that field as the
            // direction and skips the line when it will not parse, so a reader that predates this
            // ignores these lines instead of choking - and the cost of ignoring one is a single
            // refused create, which is exactly what it was before they were written at all.
            //
            // The hash field is empty because there is none: we know the file is over there and
            // nothing whatever about its content.
            string[] remoteOnly = ledger.CopyRemoteOnlyPaths();

            for (int i = 0; i < remoteOnly.Length; i++)
            {
                lines.Add(RemoteOnlyMarker + "||" + remoteOnly[i]);
            }

            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllLines(path, lines);
            }
            catch (IOException)
            {
                // Best-effort. Losing the ledger costs a repeated offer, never a wrong transfer.
            }
        }

        /// <summary>
        /// Reads a ledger back, or returns an empty one when there is nothing to read.
        /// </summary>
        /// <param name="path">
        /// The file to read.
        /// </param>
        /// <returns>
        /// The ledger.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="path"/> is null.
        /// </exception>
        /// <remarks>
        /// A missing file is the normal first run and yields an empty ledger, not an error.
        /// </remarks>
        public static SyncLedger Load(string path)
        {
            if (path == null) { throw new ArgumentNullException(nameof(path)); }

            SyncLedger ledger = new SyncLedger();
            if (!File.Exists(path))
            {
                return ledger;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                return ledger;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    continue;
                }

                int firstBar = line.IndexOf('|');
                if (firstBar <= 0)
                {
                    continue;
                }

                int secondBar = line.IndexOf('|', firstBar + 1);
                if (secondBar <= firstBar || secondBar >= line.Length - 1)
                {
                    continue;
                }

                string firstField = line.Substring(0, firstBar);

                // "It is over there, and we did not put it there." No hash, no direction - see the
                // note where these are written.
                if (firstField == RemoteOnlyMarker)
                {
                    ledger.RecordRemoteExistence(line.Substring(secondBar + 1));
                    continue;
                }

                int directionValue;
                if (!int.TryParse(firstField, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out directionValue))
                {
                    continue;
                }

                string hashText = line.Substring(firstBar + 1, secondBar - firstBar - 1);
                string filePath = line.Substring(secondBar + 1);

                byte[] hash;
                try
                {
                    hash = Convert.FromHexString(hashText);
                }
                catch (FormatException)
                {
                    // A garbled line means that one file gets offered again. Skipping is right;
                    // guessing at a hash would be recording a transfer that may not have happened.
                    continue;
                }

                ledger.RecordTransfer(filePath, hash, (SyncDirection)directionValue);
            }

            return ledger;
        }
    }
}
