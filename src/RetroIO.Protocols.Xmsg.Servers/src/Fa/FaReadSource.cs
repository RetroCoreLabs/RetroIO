//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Servers.Fa
{
    /// <summary>
    /// Everything a pull needs to know about where a file is coming from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror of <see cref="FaWriteTarget"/>, sharing its addressing through
    /// <see cref="FaFileEndpoint"/> and differing in exactly two ways: there is no access letter,
    /// and the name is not quoted, which buys two more characters.
    /// </para>
    /// <para><b>The read budget is THIRTEEN characters, not eleven</b></para>
    /// <para>
    /// Both directions carry the specification in a compact QFORM byte string that holds at most
    /// fifteen bytes, and both spend two of them on an apostrophe and a suffix character. The write
    /// then spends two MORE on the quotes it needs to create a file, leaving eleven; a read opens a
    /// file that already exists and sends the name bare, so it keeps thirteen.
    /// </para>
    /// <para>
    /// That asymmetry is worth stating plainly because it is a trap for anything that pushes a file
    /// and later pulls it back - the sync daemon, for one. A name that a read accepts may be two
    /// characters too long to have been written by us in the first place. Eleven is the number
    /// that matters when choosing a name; thirteen is only what a read will tolerate.
    /// </para>
    /// </remarks>
    public sealed class FaReadSource : FaFileEndpoint
    {
        /// <summary>
        /// How many bytes the open request's compact QFORM string can carry.
        /// </summary>
        /// <remarks>
        /// The captured field is <c>BD</c> then <c>BIGPSH3:TXT'.</c> - thirteen used of fifteen.
        /// The compact form's length lives in a nibble, so fifteen is its ceiling, not a choice.
        /// </remarks>
        public const int MaxOpenFieldLength = 127;

        /// <summary>
        /// The most characters a filespec being READ may have.
        /// </summary>
        /// <remarks>
        /// Thirteen: fifteen bytes less the apostrophe and the suffix character. See the class
        /// remarks for why the WRITE limit is eleven.
        /// </remarks>
        public const int MaxFileSpecLength = MaxOpenFieldLength - 2;

        /// <summary>
        /// Creates a source.
        /// </summary>
        /// <param name="serverNode">
        /// The node running <c>*FA-SERVER</c>, for example 100.
        /// </param>
        /// <param name="serverSystemName">
        /// The name of the machine the server runs on - <c>D100</c> for node 100.
        /// </param>
        /// <param name="fileSpec">
        /// The file specification AS THE SERVER'S OWN MACHINE SEES IT, UNQUOTED -
        /// <c>BIGPSH3:TXT</c>, not <c>D100(SYSTEM)."BIGPSH3:TXT"</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serverSystemName"/> or <paramref name="fileSpec"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when either string is empty, or the filespec will not fit the open request.
        /// </exception>
        public FaReadSource(ushort serverNode, string serverSystemName, string fileSpec)
            : base(serverNode, serverSystemName, fileSpec)
        {
            // Caught here rather than deep in the QFORM writer, where the message is about
            // encodings and says nothing about what the caller did wrong.
            if (fileSpec.Length > MaxFileSpecLength)
            {
                throw new ArgumentException(
                    "'" + fileSpec + "' is " + fileSpec.Length + " characters. The open request "
                        + "carries the specification, an apostrophe and one suffix character in a "
                        + "QFORM byte string of at most " + MaxOpenFieldLength + " bytes, so "
                        + MaxFileSpecLength + " is the most this can be. If it looks like "
                        + "D100(SYSTEM).\"NAME:TYPE\", drop the machine, the user and the quotes - "
                        + "the FA wire carries the name as the server's own machine sees it.",
                    nameof(fileSpec));
            }

            // The captured READ came from BAK05, where the captured write came from BAK04. The
            // field names the client's background program, so neither is more correct than the
            // other; this follows the capture it was taken from.
            BackgroundProgram = "BAK05";
        }
    }
}
