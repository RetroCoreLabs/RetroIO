//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// Builds requests to <c>*XFTRA</c>, the COSMOS file-transfer server, which serves both
    /// <c>TRANSFER-FILE</c> and <c>APPEND-REMOTE-BATCH</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>One message shape, two operations</b></para>
    /// <para>
    /// There is no separate batch server. <c>APPEND-REMOTE-BATCH</c> sends ONE XSLET letter to
    /// <c>*XFTRA</c> with the same tagged-parameter vocabulary a transfer uses;
    /// <see cref="XftraOperation"/> in parameter 11 is what tells them apart.
    /// </para>
    /// <para><b>The parameters, measured</b></para>
    /// <para>
    /// From <c>DOC/XMSG-APPEND-REMOTE-BATCH-CAPTURED-2026-07-31.md</c>, node 102 to node 100,
    /// both SINTRAN K. The tag rule is the established one: an INTEGER parameter <c>n</c> is
    /// tagged <c>n</c>, a STRING parameter <c>n</c> is tagged <c>256 - n</c>.
    /// </para>
    /// <code>
    /// FF  1  string   "*XFTRA"        the server name
    /// FE  2  string   "D100"          the remote system
    /// F4 12  string   "SYSTEM"        the remote user
    /// 0D 13  integer  0               the password - see the remarks on the password parameter
    /// F8  8  string   "ARBTEST:SYMB"  THE FILE - role decided by the operation
    /// F7  9  string   "SYMB"          constant, meaning UNKNOWN
    /// 0A 10  integer  1024            constant, meaning UNKNOWN
    /// 0B 11  integer  3               the operation
    /// F0 16  string   "ARBOUT:SYMB"   the batch OUTPUT file
    /// </code>
    /// <para>
    /// Parameters 9 and 10 are reproduced because they are what the captures contain. The
    /// transfer work already proved parameter 9 is NOT the file type. Neither is understood, so
    /// neither is exposed as a caller option - inventing a meaning for them would be a guess.
    /// </para>
    /// <para>
    /// Parameter 16 appears only in the batch capture; the transfer capture had nothing above 13.
    /// </para>
    /// <para><b>The odd-length pad</b></para>
    /// <para>
    /// <c>"ARBOUT:SYMB"</c> is 11 bytes, odd, and last. It is followed by one <c>0x00</c> pad
    /// while its declared length stays 11. A caller computing a message length from the declared
    /// parameter lengths alone comes up two bytes short. The message builder already applies this
    /// rule; it is called out because this request is the capture where it first fired on a final
    /// odd string.
    /// </para>
    /// </remarks>
    public static class XftraRequests
    {
        /// <summary>
        /// The server name every request is addressed to.
        /// </summary>
        public const string ServerName = "*XFTRA";

        /// <summary>
        /// Parameter 9, a string of unknown meaning, reproduced from the captures.
        /// </summary>
        /// <remarks>
        /// Both the transfer and the batch capture carry <c>"SYMB"</c> here. It is NOT the file
        /// type - the transfer work disproved that. Kept as a constant rather than a caller
        /// option, because nothing is known about what varying it would do.
        /// </remarks>
        public const string UnknownParameter9 = "SYMB";

        /// <summary>
        /// Parameter 10, an integer of unknown meaning, reproduced from the captures.
        /// </summary>
        /// <remarks>
        /// Both captures carry 1024 (<c>0x0400</c>). Meaning UNKNOWN.
        /// </remarks>
        public const ushort UnknownParameter10 = 1024;

        /// <summary>
        /// Builds an <c>APPEND-REMOTE-BATCH</c> letter: run a batch job on a remote system.
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="remoteSystem">
        /// The remote system name, for example <c>D100</c>.
        /// </param>
        /// <param name="remoteUser">
        /// The user to run as on the remote system, for example <c>SYSTEM</c>.
        /// </param>
        /// <param name="batchInputFile">
        /// The batch job file on the remote system, for example <c>ARBTEST:SYMB</c>.
        /// </param>
        /// <param name="batchOutputFile">
        /// The file the batch listing is written to, for example <c>ARBOUT:SYMB</c>.
        /// </param>
        /// <param name="password">
        /// The remote user's password, or <c>null</c> when none is given.
        /// </param>
        /// <returns>
        /// The XSLET letter, ready to be sent to <c>*XFTRA</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="remoteSystem"/>, <paramref name="remoteUser"/>,
        /// <paramref name="batchInputFile"/> or <paramref name="batchOutputFile"/> is null.
        /// </exception>
        public static XroutMessage AppendRemoteBatch(
            byte serial,
            string remoteSystem,
            string remoteUser,
            string batchInputFile,
            string batchOutputFile,
            string? password)
        {
            if (batchOutputFile == null)
            {
                throw new ArgumentNullException(nameof(batchOutputFile));
            }

            return Build(
                serial, XftraOperation.AppendRemoteBatch, remoteSystem, remoteUser,
                batchInputFile, batchOutputFile, password);
        }

        /// <summary>
        /// Builds a <c>TRANSFER-FILE</c> letter.
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="remoteSystem">
        /// The remote system name.
        /// </param>
        /// <param name="remoteUser">
        /// The user on the remote system.
        /// </param>
        /// <param name="destinationFile">
        /// The destination file - parameter 8, whose role is set by the operation.
        /// </param>
        /// <param name="password">
        /// The remote user's password, or <c>null</c> when none is given.
        /// </param>
        /// <returns>
        /// The XSLET letter.
        /// </returns>
        /// <remarks>
        /// Sends no parameter 16: the transfer capture has nothing above parameter 13.
        /// </remarks>
        public static XroutMessage TransferFile(
            byte serial,
            string remoteSystem,
            string remoteUser,
            string destinationFile,
            string? password)
        {
            return Build(
                serial, XftraOperation.TransferFile, remoteSystem, remoteUser,
                destinationFile, null, password);
        }

        /// <summary>
        /// Builds an <c>*XFTRA</c> letter for either operation, in the parameter ORDER the
        /// captures use.
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="operation">
        /// The operation, which also decides what <paramref name="file"/> means.
        /// </param>
        /// <param name="remoteSystem">
        /// The remote system name.
        /// </param>
        /// <param name="remoteUser">
        /// The user on the remote system.
        /// </param>
        /// <param name="file">
        /// Parameter 8 - the destination file for a transfer, the job input file for a batch.
        /// </param>
        /// <param name="outputFile">
        /// Parameter 16, the batch output file, or <c>null</c> to omit it.
        /// </param>
        /// <param name="password">
        /// The password, or <c>null</c> for none.
        /// </param>
        /// <returns>
        /// The XSLET letter.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="remoteSystem"/>, <paramref name="remoteUser"/> or
        /// <paramref name="file"/> is null.
        /// </exception>
        /// <remarks>
        /// The order below is the captured order and is kept deliberately: 1, 2, 12, 13, 8, 9, 10,
        /// 11, 16. It is NOT ascending, and nothing establishes that the receiver tolerates a
        /// different order, so it is reproduced rather than tidied.
        /// </remarks>
        public static XroutMessage Build(
            byte serial,
            XftraOperation operation,
            string remoteSystem,
            string remoteUser,
            string file,
            string? outputFile,
            string? password)
        {
            if (remoteSystem == null)
            {
                throw new ArgumentNullException(nameof(remoteSystem));
            }

            if (remoteUser == null)
            {
                throw new ArgumentNullException(nameof(remoteUser));
            }

            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            // Parameters 1 and 2 are the letter's own addressing - the server name and the remote
            // system - and XroutRequests.SendLetter already writes them. Build the letter through
            // it rather than repeating those two AddString calls here: a second copy of the letter
            // framing is exactly where a subtle difference would appear later.
            //
            // Everything after them is the XFTRA payload, in the CAPTURED ORDER: 12, 13, 8, 9, 10,
            // 11, 16. That order is not ascending, and nothing establishes the receiver tolerates
            // another, so it is reproduced rather than tidied.
            List<XroutParameter> payload = new List<XroutParameter>(7);
            payload.Add(XroutParameter.Text(12, remoteUser));

            // Parameter 13 is the password, and its TYPE changes with whether one was given. The
            // batch capture omitted the password and carries an INTEGER 0 here, where the transfer
            // capture carries a STRING password. So an absent password is not an empty string.
            // INFERRED from one observation - a non-empty password has not been tested on the
            // batch path.
            payload.Add(password == null
                ? XroutParameter.Integer16(13, 0)
                : XroutParameter.Text(13, password));

            payload.Add(XroutParameter.Text(8, file));
            payload.Add(XroutParameter.Text(9, UnknownParameter9));
            payload.Add(XroutParameter.Integer16(10, UnknownParameter10));
            payload.Add(XroutParameter.Integer16(11, (ushort)operation));

            if (outputFile != null)
            {
                payload.Add(XroutParameter.Text(16, outputFile));
            }

            XroutMessage message = XroutRequests.SendLetter(
                serial, ServerName, remoteSystem, null, payload.ToArray());

            // The captured writer pads a final odd-length parameter and COUNTS the pad in XMLEN.
            // See XroutMessage.PadFinalParameter - it is the writer's choice, and this is the
            // writer we are reproducing.
            message.PadFinalParameter = true;

            // Re-serialise so the declared length picks up the pad.
            message.ToArray();
            return message;
        }
    }
}
