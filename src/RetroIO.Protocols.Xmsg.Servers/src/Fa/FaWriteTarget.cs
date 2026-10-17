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
    /// Everything a push needs to know about where a file is going.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the driver so the addressing can be built and checked without starting a
    /// conversation, and so a test can vary one field without a constructor of a dozen arguments.
    /// </para>
    /// <para><b>The filespec is LOCAL to the server, not machine-qualified</b></para>
    /// <para>
    /// The captured open sends <c>"WRTEST1:OUT"</c> - no machine, no user. It does not need them:
    /// the conversation is already addressed to that machine, and the USER is carried separately
    /// by the <c>ReserveFileEntry</c> request - which means <see cref="FaFileEndpoint.User"/> must
    /// be SET, or the file lands in whatever user the session already is.
    /// </para>
    /// <para>
    /// This used to end "a D102(SYSTEM). prefix is command-line syntax and has no place on the FA
    /// wire". Drop that claim: it is not measured. What was measured is that the addressed form
    /// did not FIT the compact string this class emits. Whether a real client can put a
    /// <c>(USER)</c> prefix in the open specification is UNKNOWN - no capture shows one either
    /// way - and "we never sent one" is not evidence that the machine would refuse it.
    /// </para>
    /// <para>
    /// Passing the full form is the mistake this class is shaped to prevent, and it was made once:
    /// it produced a 28-byte field, which the compact QFORM string cannot carry at all, so the
    /// push threw before it reached the wire.
    /// </para>
    /// <para><b>Fifteen bytes is a real ceiling, not a formality</b></para>
    /// <para>
    /// The open field is the QUOTED specification, an apostrophe and the access letter, in a
    /// compact QFORM byte string that holds at most fifteen bytes. So a quoted name and type can
    /// use twelve: <c>"PUSHED:DATA"</c> fits exactly, and a full 16-character name would not.
    /// What a real client does with a longer name is UNKNOWN - no capture shows one - so this
    /// refuses early with a clear message rather than guessing an encoding.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// The addressing every direction shares now lives on <see cref="FaFileEndpoint"/>. Nothing
    /// about this class's behaviour changed when it moved: same constructor, same properties, same
    /// exception messages, same defaults.
    /// </remarks>
    public sealed class FaWriteTarget : FaFileEndpoint
    {
        /// <summary>
        /// How many bytes the open request's compact QFORM string can carry.
        /// </summary>
        /// <remarks>
        /// The captured field is <c>BF</c> then <c>"WRTEST1:OUT"'W</c> - exactly fifteen. The
        /// compact form's length lives in a nibble, so fifteen is THAT FORM's ceiling.
        /// <para>
        /// IT IS NOT THE PROTOCOL'S CEILING, and reading it as one sends you hunting the wrong
        /// thing. The same protocol has a long string form, <c>B0</c> followed by a whole length
        /// byte, and our own captures use it: the open request in FaOpenFileCodec is
        /// <c>B0 10 "PATCH-FILE:OUT"27 54</c> - a sixteen-byte field holding a FOURTEEN-character
        /// name, longer than anything this constant allows. ReserveFileEntry uses <c>B0 10</c>
        /// too. So a longer specification is expressible; this class simply does not emit it yet.
        /// </para>
        /// </remarks>
        public const int MaxOpenFieldLength = 127;

        /// <summary>
        /// Creates a target.
        /// </summary>
        /// <param name="serverNode">
        /// The node running <c>*FA-SERVER</c>, for example 102.
        /// </param>
        /// <param name="serverSystemName">
        /// The name of the machine the server runs on - <c>D100</c> for node 100. This is what
        /// goes in the connect letter, because XROUT looks the server up by the system it is
        /// asked for. It is NOT our own name; see
        /// <see cref="RetroIO.Protocols.Xmsg.Fa.FaConnectLetter"/>.
        /// </param>
        /// <param name="fileSpec">
        /// The file specification AS THE SERVER'S OWN MACHINE SEES IT - <c>"PUSHED:DATA"</c>, not
        /// <c>D102(SYSTEM)."PUSHED:DATA"</c>. Quoted only when the file is being created.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serverSystemName"/> or <paramref name="fileSpec"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when either string is empty.
        /// </exception>
        public FaWriteTarget(ushort serverNode, string serverSystemName, string fileSpec)
            : base(serverNode, serverSystemName, fileSpec)
        {
            // Caught here rather than deep in the QFORM writer, where the message is about
            // encodings and says nothing about what the caller did wrong. The open field is the
            // spec, an apostrophe and the access letter, and the compact byte string holds 15.
            if (fileSpec.Length + 2 > MaxOpenFieldLength)
            {
                throw new ArgumentException(
                    "'" + fileSpec + "' is " + fileSpec.Length + " characters. The open request "
                        + "carries the specification, an apostrophe and the access letter in a "
                        + "QFORM byte string of at most " + MaxOpenFieldLength + " bytes, so "
                        + (MaxOpenFieldLength - 2) + " is the most this can be. If it looks like "
                        + "D102(SYSTEM).\"NAME:TYPE\", drop the machine and user - the FA wire "
                        + "carries the name as the server's own machine sees it.",
                    nameof(fileSpec));
            }

            // The one default that is write-only. Everything else is set by the base.
            Access = 'W';
        }

        /// <summary>
        /// Gets or sets the access letter the open request asks for.
        /// </summary>
        /// <remarks>
        /// <c>W</c> for a write. The captured session opened <c>"WRTEST1:OUT"</c> with it.
        /// </remarks>
        public char Access { get; set; }
    }
}
