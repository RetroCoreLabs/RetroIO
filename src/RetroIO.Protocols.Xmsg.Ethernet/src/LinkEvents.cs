//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Ethernet
{
    /// <summary>
    /// Raised when a frame arrives whose ND link-layer kind byte is not one we handle.
    /// </summary>
    /// <param name="kind">
    /// The kind byte as it arrived.
    /// </param>
    /// <remarks>
    /// An unknown kind is worth surfacing rather than dropping quietly: the Ethernet path to D19999
    /// dies on a <c>0x60</c> nobody has decoded, and a silent drop is how that stayed invisible.
    /// </remarks>
    public delegate void UnknownFrameKindReceived(byte kind);

    /// <summary>
    /// Raised when the peer asks for the link to be taken down.
    /// </summary>
    /// <param name="kind">
    /// The kind byte that carried the request.
    /// </param>
    public delegate void DisconnectRequested(byte kind);

    /// <summary>
    /// Raised when the peer's identity has been learned from an inbound frame.
    /// </summary>
    public delegate void PeerLearned();

    /// <summary>
    /// Raised when the peer re-sends a data frame it has already sent us.
    /// </summary>
    /// <param name="sequence">
    /// The sequence the repeated frame carried.
    /// </param>
    /// <param name="expected">
    /// The sequence we were waiting for.
    /// </param>
    /// <remarks>
    /// <para><b>This is never normal, and it is the first thing to check</b></para>
    /// <para>
    /// A peer repeats a frame when it has not seen our acknowledgement. On 2026-08-10 D100 did
    /// this for seconds on end because we were sending far ahead of our window; every repeat
    /// reached the file server as a fresh request and it answered each one with a new session
    /// counter and a new connection number, ending in SINTRAN error 267 octal. Nothing in the
    /// file-access layer was wrong.
    /// </para>
    /// <para>
    /// It took two nights to find, because the only evidence was byte-identical frames buried in
    /// a log nobody was counting. Surfacing it means the next time this happens it is a warning
    /// line, not an investigation.
    /// </para>
    /// </remarks>
    public delegate void DuplicateDataFrameReceived(byte sequence, byte expected);

    /// <summary>
    /// Raised after a frame has been handed to the transport.
    /// </summary>
    /// <param name="frame">
    /// The buffer the frame was built in. It is REUSED after this call returns, so a handler that
    /// needs to keep the bytes must copy them.
    /// </param>
    /// <param name="length">
    /// How many bytes of <paramref name="frame"/> make up the frame.
    /// </param>
    public delegate void FrameTransmitted(byte[] frame, int length);
}
