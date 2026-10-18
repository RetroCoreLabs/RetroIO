//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Node.Services
{
    /// <summary>
    /// The sentinel a send site passes when it is ORIGINATING a frame rather than answering one.
    /// </summary>
    /// <remarks>
    /// Its own type so the value is defined exactly once. See
    /// <c>IXmsgServerTransport.BuildDatagram</c> for the rule it selects.
    /// </remarks>
    public static class XmsgAnsweredFlags1
    {
        /// <summary>
        /// "Nothing is being answered - allocate the next number." Negative so it cannot collide
        /// with any real 16-bit Flags 1.
        /// </summary>
        public const int None = -1;
    }

    /// <summary>
    /// The narrow transport surface a high-level XMSG server (for example the TAD terminal server or,
    /// later, the XM-FIDO file server) is allowed to use. The node - NOT the server - owns all the
    /// low-level sequencing: the per-link outgoing datagram sequence (Flags 1), the sub-header Counter
    /// and the sub-protocol channel are all filled in by <see cref="BuildDatagram"/> via the envelope
    /// model, and the secure-ACK of incoming frames is done by the node too.
    /// </summary>
    /// <remarks>
    /// This is the deliberate firewall between the replaceable server layer and the XMSG transport:
    /// a server supplies only addressing, frame class (XMCSM), role and payload, and never sees a
    /// seed, a counter or a channel. That keeps the whole servers layer swappable without touching
    /// the protocol code. It is distinct from the seam's byte-level <c>Codec.IXmsgTransport</c>: this
    /// one builds whole XMSG datagrams; that one moves raw bytes.
    /// </remarks>
    public interface IXmsgServerTransport
    {
        /// <summary>
        /// Gets this node's number (for example 102), so a server can stamp its own identity.
        /// </summary>
        ushort NodeNumber { get; }

        /// <summary>
        /// Allocates a globally-unique session (subprocess) port for a new session, so ports never
        /// collide across servers or concurrent sessions. The layout is the verified
        /// <c>(logical shifted left 7) | incarnation</c> form.
        /// </summary>
        /// <returns>
        /// A fresh session port.
        /// </returns>
        ushort AllocateSessionPort();

        /// <summary>
        /// Allocates a monotonic session number (the operator-visible <c>ttyN</c> / TAD number).
        /// </summary>
        /// <returns>
        /// A fresh session number (1-based).
        /// </returns>
        int AllocateSessionNumber();

        /// <summary>
        /// Decides whether a datagram can be built for a node yet.
        /// </summary>
        /// <param name="remoteNode">
        /// The node we want to send to.
        /// </param>
        /// <returns>
        /// <c>true</c> when a link to that node has been seen and its seed learned.
        /// </returns>
        /// <remarks>
        /// <para><b>For code that ORIGINATES, which is the case that had no way to ask</b></para>
        /// <para>
        /// A server answering a request never needs this: the request itself proves the link is
        /// there. Something that starts a conversation has no such proof, and until now its only
        /// way to find out was to call <see cref="BuildDatagram"/> and catch the exception - which
        /// is not a test, it is an accident waiting for a stack trace.
        /// </para>
        /// <para>
        /// The link layer knowing the peer is NOT the same thing and must not be used instead. On
        /// the Ethernet path the peer's link id arrives with the first frame, while the XMSG seed
        /// is learned when that frame is DISPATCHED, a tick later. A file push wired to the link
        /// signal fired in the gap between the two and took the whole runner down with it.
        /// </para>
        /// </remarks>
        bool CanReach(ushort remoteNode);

        /// <summary>
        /// Builds one outgoing datagram to a client endpoint. The node assigns the per-link Flags 1
        /// (the single continuous outgoing sequence to <paramref name="remoteNode"/>, shared by every
        /// server and session on that link), and derives the Counter and channel from the envelope
        /// model, using the seed learned when the link was first seen.
        /// </summary>
        /// <param name="remoteNode">
        /// The client's node number (header destination, for example 100).
        /// </param>
        /// <param name="clientSystem">
        /// The client's system number (sub-header destination system).
        /// </param>
        /// <param name="clientPort">
        /// The client's port (sub-header destination port).
        /// </param>
        /// <param name="sourcePort">
        /// Our source port (the server's well-known port or a session port).
        /// </param>
        /// <param name="controlService">
        /// The XMCSM control/service word (its high half is the frame class).
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte.
        /// </param>
        /// <param name="payload">
        /// The trailer payload bytes (a TAD chain, a letter body, and so on).
        /// </param>
        /// <param name="answeredFlags1">
        /// The Flags 1 of the request this frame ANSWERS, to be echoed; or
        /// <see cref="XmsgAnsweredFlags1.None"/> when this frame is ORIGINATED and needs a number
        /// of its own.
        /// </param>
        /// <returns>
        /// The assembled datagram, ready to transmit.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no link to <paramref name="remoteNode"/> has been established (no seed learned).
        /// </exception>
        /// <remarks>
        /// <para>
        /// CARVED 2026-08-04 from DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt: on a
        /// real link Flags 1 is ONE value per EXCHANGE, shared by both directions, and the answer
        /// carries the request's value rather than a number of the answerer's own:
        /// </para>
        /// <code>
        /// L 9  0x0E 100->102 F1=0x01F9    the *FA-SERVER letter
        /// L11  0x03 102->100 F1=0x01F9    the ACK of it
        /// L13  0x0E 102->100 F1=0x01F9    the ConnectionConfirm - the SAME number
        /// L15  0x03 100->102 F1=0x01F9    its ACK
        /// L17  0x0E 100->102 F1=0x01FA    the next exchange, one higher
        /// L21  0x0E 102->100 F1=0x01FA    its answer - the same number again
        /// </code>
        /// <para>
        /// Unbroken through <c>0x01FF</c>. UNVERIFIED whether echoing is REQUIRED by the
        /// peer or merely what a kernel holding one counter per link naturally produces - this
        /// matches observed behaviour, it is not a documented rule.
        /// </para>
        /// <para>
        /// Which sites echo is a per-site judgement and is deliberately NOT guessed at centrally;
        /// each caller says what it is doing. Terminal output is the case that does not fit: one
        /// keystroke produces a multi-frame segmented burst released across several ACKs, and the
        /// captured TAD frames show the responder's own numbers ADVANCING there (the conn-to-d102
        /// DUMM is <c>0x0131</c> and its MOTD <c>0x0135</c>, four apart, not one echoed value), so
        /// that path originates.
        /// </para>
        /// </remarks>
        XmsgFrame BuildDatagram(
            ushort remoteNode,
            ushort clientSystem,
            ushort clientPort,
            ushort sourcePort,
            uint controlService,
            byte frameFlags,
            byte role,
            byte[] payload,
            int answeredFlags1);

        /// <summary>
        /// Builds a data datagram whose MESSAGE BODY is written verbatim at wire offset 28, with no
        /// XROUT serial / service / length header in front of it.
        /// </summary>
        /// <param name="remoteNode">
        /// The node to send to.
        /// </param>
        /// <param name="clientSystem">
        /// The peer's XMSG system number.
        /// </param>
        /// <param name="clientPort">
        /// The peer's port.
        /// </param>
        /// <param name="sourcePort">
        /// Our source port.
        /// </param>
        /// <param name="xmcsm">
        /// XMCSM, the one word at wire 26-27. Flags 2 is set to the same value.
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte.
        /// </param>
        /// <param name="body">
        /// The message body, starting at wire offset 28.
        /// </param>
        /// <param name="answeredFlags1">
        /// The Flags 1 of the request this frame ANSWERS, or
        /// <see cref="XmsgAnsweredFlags1.None"/> when it is originated. See
        /// <see cref="BuildDatagram"/> for the evidence.
        /// </param>
        /// <returns>
        /// The assembled datagram, ready to transmit.
        /// </returns>
        /// <remarks>
        /// File-access messages need this: their body opens with the 16-bit
        /// <c>FaMessageType</c> (<c>0x07D2</c>, <c>0x07F0</c>, ...) and carries no XROUT header.
        /// VERIFIED on D102's reply in
        /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt</c>, whose body at absolute
        /// 28 is <c>07D2 0002 0042 6400</c>.
        /// </remarks>
        XmsgFrame BuildBodyDatagram(
            ushort remoteNode,
            ushort clientSystem,
            ushort clientPort,
            ushort sourcePort,
            ushort xmcsm,
            byte frameFlags,
            byte role,
            byte[] body,
            int answeredFlags1);

        /// <summary>
        /// Builds the datagram (or the fragment pair) that carries one message body, splitting it
        /// across two frames when it is too long for one.
        /// </summary>
        /// <param name="remoteNode">
        /// The node to send to.
        /// </param>
        /// <param name="clientSystem">
        /// The peer's XMSG system number.
        /// </param>
        /// <param name="clientPort">
        /// The peer's port.
        /// </param>
        /// <param name="sourcePort">
        /// Our source port.
        /// </param>
        /// <param name="frameFlags">
        /// The sub-header frame-flags byte.
        /// </param>
        /// <param name="role">
        /// The sub-header role byte.
        /// </param>
        /// <param name="body">
        /// The whole message body, starting at wire offset 28.
        /// </param>
        /// <param name="answeredFlags1">
        /// The Flags 1 of the request this message ANSWERS, or
        /// <see cref="XmsgAnsweredFlags1.None"/> when it is originated. A fragment PAIR shares one
        /// Flags 1 between both frames - that is what ties them together.
        /// </param>
        /// <returns>
        /// One frame when the body fits, or the first fragment followed by its continuation.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no link to <paramref name="remoteNode"/> has been established.
        /// </exception>
        /// <remarks>
        /// <para>
        /// There is no <c>xmcsm</c> parameter, unlike <see cref="BuildBodyDatagram"/>. On a message
        /// long enough to fragment, Flags 2 is not a free choice: the first fragment declares the
        /// TOTAL body length and the continuation declares the offset it resumes at. Taking an
        /// XMCSM here would let a caller contradict the frame it is asking for.
        /// </para>
        /// <para>
        /// A body short enough to travel whole is sent exactly as
        /// <see cref="BuildBodyDatagram"/> would send it, with its own length as XMCSM - the rule
        /// verified over every file-access data frame in the 2026-08-04 captures.
        /// </para>
        /// </remarks>
        IReadOnlyList<XmsgFrame> BuildFragmentedBodyDatagram(
            ushort remoteNode,
            ushort clientSystem,
            ushort clientPort,
            ushort sourcePort,
            byte frameFlags,
            byte role,
            byte[] body,
            int answeredFlags1);
    }
}
