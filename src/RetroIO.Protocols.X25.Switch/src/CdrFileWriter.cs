//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Globalization;
using System.IO;
using System.Text;
using RetroIO.Protocols.X25.Switch.Model;

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// Writes call records to the file named by <c>cdr_file</c> in the switch config, one line per call, in the same
    /// column order as the C emulator's x25switch (PLAN B1c: one format, so a billing test can compare the two).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Columns, comma separated (record format v2, PLAN D2):
    /// </para>
    /// <list type="number">
    /// <item><description>start, ms on the switch clock (when the call was routed);</description></item>
    /// <item><description>duration, ms, start to clear;</description></item>
    /// <item><description>calling address;</description></item>
    /// <item><description>called address;</description></item>
    /// <item><description>in port;</description></item>
    /// <item><description>out port;</description></item>
    /// <item><description>packets forward;</description></item>
    /// <item><description>packets back;</description></item>
    /// <item><description>bytes forward;</description></item>
    /// <item><description>bytes back;</description></item>
    /// <item><description>clearing cause;</description></item>
    /// <item><description>diagnostic;</description></item>
    /// <item><description>start, wall clock, seconds since 1970;</description></item>
    /// <item><description>X.75 call identifier, six hex digits (000000 here: the C# switch has no X.75);</description></item>
    /// <item><description>connect time, ms on the switch clock (0: never connected);</description></item>
    /// <item><description>connected duration, ms, connect to clear - the duration ITU-T D.11 charges (0: never connected);</description></item>
    /// <item><description>segments forward (ITU-T D.12);</description></item>
    /// <item><description>segments back;</description></item>
    /// <item><description>paying account (empty: none);</description></item>
    /// <item><description>NUI billed by (the one named, or the port's default; empty: none);</description></item>
    /// <item><description>reverse charging asked, 1 or 0 (X.25 6.18);</description></item>
    /// <item><description>who is charged, <c>caller</c> or <c>called</c>;</description></item>
    /// <item><description>charging answer given, 1 or 0 (X.25 6.22).</description></item>
    /// </list>
    /// <para>
    /// The first fourteen are x25emu's old columns in their old places; columns 15 to 23 are new on 2026-11-16, at the end
    /// so a reader that counts columns keeps working. The rest of PLAN D2 (facilities, clearing network, transit list)
    /// will go after them. "Forward" is from the calling DTE toward the called one. A comma in an account name or NUI would
    /// split the column, so it is written as a space rather than producing a line with the wrong number of columns.
    /// </para>
    /// </remarks>
    public sealed class CdrFileWriter : IDisposable
    {
        private readonly object _sync = new object();
        private readonly StreamWriter _writer;

        /// <summary>
        /// Opens the file for appending, creating it when it is not there.
        /// </summary>
        /// <param name="path">
        /// The record file.
        /// </param>
        public CdrFileWriter(string path)
        {
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        }

        /// <summary>
        /// Appends one record and flushes it, so a reader sees each call as soon as it has ended.
        /// </summary>
        /// <param name="cdr">
        /// The record.
        /// </param>
        public void Write(Cdr cdr)
        {
            string line = Format(cdr);
            lock (_sync)
            {
                _writer.Write(line);
                _writer.Write('\n');
                _writer.Flush();
            }
        }

        /// <summary>
        /// One record as a line, without the line end.
        /// </summary>
        /// <param name="cdr">
        /// The record.
        /// </param>
        /// <returns>
        /// The line.
        /// </returns>
        public static string Format(Cdr cdr)
        {
            StringBuilder sb = new StringBuilder(160);
            CultureInfo inv = CultureInfo.InvariantCulture;
            sb.Append(cdr.StartTimeMs.ToString(inv)).Append(',');
            sb.Append(Math.Max(0, cdr.EndTimeMs - cdr.StartTimeMs).ToString(inv)).Append(',');
            sb.Append(cdr.Calling).Append(',');
            sb.Append(cdr.Called).Append(',');
            sb.Append(cdr.InPort.ToString(inv)).Append(',');
            sb.Append(cdr.OutPort.ToString(inv)).Append(',');
            sb.Append(cdr.PacketsForward.ToString(inv)).Append(',');
            sb.Append(cdr.PacketsReverse.ToString(inv)).Append(',');
            sb.Append(cdr.BytesForward.ToString(inv)).Append(',');
            sb.Append(cdr.BytesReverse.ToString(inv)).Append(',');
            sb.Append(cdr.ClearCause.ToString(inv)).Append(',');
            sb.Append(cdr.ClearDiagnostic.ToString(inv)).Append(',');
            sb.Append(cdr.StartWallSeconds.ToString(inv)).Append(',');
            sb.Append(cdr.CallIdentifier).Append(',');
            sb.Append(cdr.ConnectedTimeMs.ToString(inv)).Append(',');
            sb.Append((cdr.ConnectedTimeMs == 0 ? 0 : Math.Max(0, cdr.EndTimeMs - cdr.ConnectedTimeMs)).ToString(inv)).Append(',');
            sb.Append(cdr.SegmentsForward.ToString(inv)).Append(',');
            sb.Append(cdr.SegmentsReverse.ToString(inv)).Append(',');
            sb.Append(cdr.Account.Replace(',', ' ')).Append(',');
            sb.Append(cdr.Nui.Replace(',', ' ')).Append(',');
            sb.Append(cdr.ReverseCharging ? '1' : '0').Append(',');
            sb.Append(cdr.ReverseCharging ? "called" : "caller").Append(',');
            sb.Append(cdr.ChargingRequested ? '1' : '0').Append(',');
            sb.Append(cdr.CalledAccount.Replace(',', ' '));
            return sb.ToString();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            lock (_sync)
            {
                _writer.Dispose();
            }
        }
    }
}
