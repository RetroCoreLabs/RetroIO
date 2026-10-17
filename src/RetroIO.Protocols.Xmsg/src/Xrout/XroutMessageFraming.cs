//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Whether an XROUT message carries the four-byte serial/service/length header.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a choice at all</b></para>
    /// The COSMOS Programmer Guide (appendix B section 2) defines an XROUT message as a four-byte
    /// header - serial, service, remainder length - followed by even-aligned parameter blocks.
    /// That is the shape a task builds in its MESSAGE BUFFER before handing it to the kernel, and
    /// it is VERIFIED: a MON 200 trace of a running SINTRAN shows the buffer a server writes when
    /// it registers its name, header and all - <c>53 42 000A FF 08 "*XM-FIDO"</c>, serial 0x53,
    /// service 0x42 = XSNAM. See DOC/XMSG-XROUT-BUFFER-FORM-CAPTURED-2026-07-26.md.
    /// <para><b>The wire disagrees</b></para>
    /// VERIFIED against the capture corpus: the XROUT messages carried in XMSG data frames have NO
    /// such header. Their parameter blocks start at the first trailer byte, and the service that
    /// the header's byte 1 would have carried is in the XMCSM control word instead (low byte
    /// <c>0x41</c> for XSLET, and the XSGSY request/reply words for routing).
    /// The connect letter proves it: trailer <c>FF 07 2A "TADADM" 00 FE 04 "D102"</c> parses
    /// header-free as string parameter 1 = the server name and string parameter 2 = the system
    /// name, exactly the two IN parameters appendix B section 3.4 specifies for XSLET. Read with a
    /// header assumed, the leading <c>FF 07</c> is mistaken for serial 255 plus service 7 and every
    /// parameter is lost. The XSGSY reply confirms it independently: header-free it yields
    /// parameters 1 to 4 = system 100, connection type 4 (Local), extra info 100, network info 0 -
    /// the manual's four OUT parameters - whereas assuming a header swallows parameter 1 and shifts
    /// the rest.
    /// </remarks>
    public enum XroutMessageFraming
    {
        /// <summary>
        /// The four-byte header is present, as the manual describes a message buffer.
        /// </summary>
        WithHeader = 0,

        /// <summary>
        /// Parameter blocks only, with no header - the form observed in XMSG data frames.
        /// </summary>
        BodyOnly = 1,
    }
}
