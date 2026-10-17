//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Servers.Fa
{
    /// <summary>
    /// The outcome this server reports back in a reply when it cannot do what was asked.
    /// </summary>
    /// <remarks>
    /// <para><b>These are SINTRAN III file-system error numbers, not protocol codes</b></para>
    /// A refusal is carried as selector 1 in the reply body - <c>F2 0001</c> then <c>A2 code</c> -
    /// and a SUCCESS omits selector 1 entirely. The client prints the number through SINTRAN's own
    /// error table, so sending an invented value makes a real machine print the wrong message.
    /// <para><b>CORRECTED 2026-08-05, and the values used to be wrong</b></para>
    /// This enum used to carry made-up codes 0 to 5, with a remark saying no capture of a refusal
    /// existed and that the values must be replaced when one turned up. The captures in
    /// <c>DOC\captures\FA-READ-WRITE-2026-08-04\</c> hold three, so they have been.
    /// <para><b>The three that are verified</b></para>
    /// Each was read off the wire and matched against what the terminal printed:
    ///  - 46 from an <c>OpenFile</c> of a file that does not exist, in <c>capture-open-error.txt</c>.
    ///  - 197 from the reply that ends a directory walk, in <c>capture-list-files.txt</c>.
    ///  - 48 from a wrong password, in the 2026-07-29 access capture.
    /// <para><b>The rest are still ours - and all three were re-pointed on 2026-08-07</b></para>
    /// <see cref="NotSupported"/>, <see cref="BadRequest"/> and <see cref="StoreError"/> have never
    /// been captured. They are mapped onto plausible SINTRAN numbers so a client prints SOMETHING
    /// sane rather than a number that means something else. Replace them when a real capture turns
    /// up; do not "confirm" them from our own traffic.
    /// <para>
    /// The point of the exercise: the numbers previously chosen were checked against ND's own error
    /// table (<c>systemerrors.h</c>, NDIX release 3, stamped <c>3.1 88/08/12</c>) and ALL THREE named
    /// something else. 45 was "RESERVED SPACE ALREADY USED", 2 was "BAD FILE NUMBER", and 3 - the
    /// damaging one - was "END OF FILE", which tells a client its read finished normally when in
    /// fact we failed to parse the request. "Plausible" had been judged from the English word we had
    /// in mind, never against the table the client actually prints from.
    /// </para>
    /// <para>
    /// They now carry 129 "ILLEGAL FUNCTION CODE", 211 "FILE-ACCESS INTERNAL ERROR; INVALID
    /// PARAMETER VALUE" and 97 "TRANSFER ERROR". Still not measured - but a client shown one of
    /// these is told approximately the truth instead of something false.
    /// </para>
    /// <para><b>Where the text comes from</b></para>
    /// Every quoted string above is ND's own, from <c>systemerrors.h</c>. The same file gives 180 as
    /// "NO ANSWER FROM REMOTE SYSTEM; FILE-ACCESS CONNECTION ABORTED" - word for word what a real
    /// terminal printed at us when this server refused a <c>Set-block-size</c> it had not yet
    /// implemented. That is a useful cross-check that these are the codes a client really surfaces.
    /// </remarks>
    public enum FaServerStatus : ushort
    {
        /// <summary>
        /// The request was carried out.
        /// </summary>
        /// <remarks>
        /// Never written to the wire. A successful reply omits selector 1 altogether rather than
        /// carrying a zero, so this value exists only to name the case in code.
        /// </remarks>
        Ok = 0,

        /// <summary>
        /// No file of that name is in the served folder - SINTRAN error 46, <c>0x002E</c>.
        /// </summary>
        /// <remarks>
        /// VERIFIED in <c>capture-open-error.txt</c>: an <c>OpenFile</c> of <c>NO-SUCH-FILE:OUT</c>
        /// was answered <c>F2 0001 A2 002E</c>, and the terminal printed "NO SUCH FILE NAME".
        /// </remarks>
        NoSuchFile = 46,

        /// <summary>
        /// The directory walk has run off the end - SINTRAN error 197, <c>0x00C5</c>.
        /// </summary>
        /// <remarks>
        /// VERIFIED in <c>capture-list-files.txt</c>: the reply that ends the walk drops the entry
        /// block and carries <c>F2 0001 A2 00C5</c>.
        /// <para><b>This one was live and wrong</b></para>
        /// Our listing sends this on every completed walk, so until 2026-08-05 every listing we
        /// served ended with the number 2 where a real server sends 197.
        /// </remarks>
        EndOfDirectory = 197,

        /// <summary>
        /// A password was given and it did not match - SINTRAN error 48, <c>0x0030</c>.
        /// </summary>
        /// <remarks>
        /// VERIFIED in the 2026-07-29 access capture, written up in
        /// <c>DOC\XMSG-FA-ACCESS-PASSWORD-ON-THE-WIRE-2026-07-29.md</c>. We do not check passwords
        /// yet, so nothing sends this - it is here so the number is not lost.
        /// </remarks>
        WrongPassword = 48,

        /// <summary>
        /// The operation is one this server does not implement - SINTRAN error 129,
        /// <c>0x0081</c>, "ILLEGAL FUNCTION CODE".
        /// </summary>
        /// <remarks>
        /// NOT CAPTURED, but the NUMBER is no longer arbitrary.
        /// <para><b>CORRECTED 2026-08-07. This used to be 45, which means something else.</b></para>
        /// ND's own error table gives 45 as "RESERVED SPACE ALREADY USED". Refusing an unimplemented
        /// operation with it would have made a real client print that, which is not merely unhelpful
        /// but misleading - it names a disk-space condition that did not happen. 129 is ND's
        /// "ILLEGAL FUNCTION CODE", which is exactly what a refused operation is.
        /// <para>
        /// Still not captured: what a real COSMOS server answers to an operation it cannot do is
        /// UNKNOWN. This is a correctly-MEANING code rather than a measured one. Only the three
        /// operations that have never been captured can reach it, so nothing a real client sends
        /// today produces it.
        /// </para>
        /// </remarks>
        NotSupported = 129,

        /// <summary>
        /// The message did not parse as an FA exchange at all - SINTRAN error 211, <c>0x00D3</c>,
        /// "FILE-ACCESS INTERNAL ERROR; INVALID PARAMETER VALUE".
        /// </summary>
        /// <remarks>
        /// NOT CAPTURED, but the number now comes from ND's FILE-ACCESS range.
        /// <para><b>CORRECTED 2026-08-07. This used to be 3, which means END OF FILE.</b></para>
        /// That was the worst of the three: a client refused with 3 is told the file ENDED
        /// NORMALLY. A read loop would stop and report success on what was actually a parse failure
        /// at our end. 211 is ND's own file-access "invalid parameter value", which is what a body
        /// we cannot parse amounts to.
        /// </remarks>
        BadRequest = 211,

        /// <summary>
        /// The Windows folder behind the server refused the operation - SINTRAN error 97,
        /// <c>0x0061</c>, "TRANSFER ERROR".
        /// </summary>
        /// <remarks>
        /// NOT CAPTURED, and it never can be: this condition cannot arise on a real ND
        /// machine, so it stands for a host-side failure SINTRAN has no name for.
        /// <para><b>CORRECTED 2026-08-07. This used to be 2, "BAD FILE NUMBER".</b></para>
        /// That pointed the blame at the client's own file number when the fault was entirely on
        /// our side of the wire. 97 is ND's plain "TRANSFER ERROR", the closest thing their table
        /// has to "the storage under me failed", and it does not accuse the caller of anything.
        /// </remarks>
        StoreError = 97,
    }
}
