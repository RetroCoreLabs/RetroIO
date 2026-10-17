//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Packet;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Receive-side of the XFSEC secure-datagram model: accepts a data frame,
    /// delivers it, and produces the subtype-<c>0x03</c> delivery ACK that echoes
    /// the data frame's Flags 1 (datagram sequence).
    /// </summary>
    /// <remarks>
    /// <para>
    /// There are two ACK models. The LEGACY model (reachability / list-route) echoes the data
    /// frame's own channel and carries a simple per-direction counter that decrements per ACK.
    /// </para>
    /// <para>
    /// The TAD-SESSION model (enabled by <see cref="UseSessionAckModel"/>) is the capture-verified
    /// closed form: the ACK Counter and channel are a STATELESS pure function of the acknowledged
    /// datagram's Flags 1, computed via the envelope arithmetic (<see cref="XmsgEnvelope"/>) with an
    /// ACK seed of <c>link-seed + 0x0B</c>. It is one continuous sequence across every connect (never
    /// re-seeded per connect), stepping DE -> DD -> DC as Flags 1 climbs past the ACK baseLow.
    /// See XMSG-PROTOCOL.md section 6.
    /// </para>
    /// </remarks>
    public sealed class SecureDatagramReceiver
    {
        // LEGACY (reachability / list-route) per-direction counter: decrements per ACK, echoes the
        // data channel. Untouched by the TAD-session model below.
        private byte _counter;

        // TAD-session closed-form ACK model. When _sessionAckModel is set, the ACK Counter and channel
        // are derived per-frame from the acknowledged Flags 1 and this ACK seed (link-seed + 0x0B) via
        // the envelope arithmetic - a SINGLE continuous, STATELESS sequence across every connect.
        // CAPTURE-VERIFIED against 904/904 subtype-0x03 frames (multiple-connect + the whole corpus,
        // GOD-LLM sweep): ctr = ComputeCounter(seed, F1, F2) and channel = DeriveChannel(...) reproduce
        // every ACK, including the DE->DD wrap and the reboot reset. The earlier per-connect re-seed
        // reset the channel to DE where the real 102 rode DD (F1 past the baseLow), and 100 PERF_CONNCT/
        // 24B-crashed on the third connect.
        private byte _sessionAckSeed;
        private bool _sessionAckModel;

        // The ACK datagram class (Flags 2) and its XMCSM. We always emit class 0x0001: the corpus proves
        // the 0x0002 "batch" variant is situational (the real 102 does not apply it consistently), and a
        // sum-checking receiver cannot distinguish which class we chose (trail + F1 + F2low == seed for
        // both). XMCSM 0x00010000 -> (XMCSM >> 24) == 0x00, so the channel is 0xDE - epoch.
        private const ushort AckFlags2 = 0x0001;
        private const uint AckControlService = 0x00010000;

        /// <summary>
        /// Raised when a data frame is delivered to the local application.
        /// </summary>
        /// <param name="datagramSequence">
        /// The datagram sequence (Flags 1) of the delivered frame.
        /// </param>
        public delegate void DatagramReceived(ushort datagramSequence);

        /// <summary>
        /// Occurs when a data frame is delivered.
        /// </summary>
        public event DatagramReceived? OnReceived;

        /// <summary>
        /// Initialises a new receiver.
        /// </summary>
        /// <param name="initialCounter">
        /// The starting value of the legacy per-direction counter placed in each reachability /
        /// list-route ACK.
        /// </param>
        public SecureDatagramReceiver(byte initialCounter)
        {
            _counter = initialCounter;
        }

        /// <summary>
        /// Gets the current legacy per-direction counter value (the byte placed in the next
        /// reachability / list-route ACK).
        /// </summary>
        public byte Counter
        {
            get { return _counter; }
        }

        /// <summary>
        /// Re-seeds the LEGACY per-direction ACK counter (reachability / list-route path only).
        /// </summary>
        /// <remarks>
        /// The TAD-session path does NOT use this - it uses the stateless closed form via
        /// <see cref="UseSessionAckModel"/>. This remains for the reachability / list-route ACKs.
        /// </remarks>
        /// <param name="value">
        /// The value the next legacy ACK will carry as its trailing byte.
        /// </param>
        public void SeedCounter(byte value)
        {
            _counter = value;
        }

        /// <summary>
        /// Switches this receiver to the capture-verified TAD-session ACK model: the ACK Counter and
        /// channel become a stateless pure function of the acknowledged datagram's Flags 1, via the
        /// envelope arithmetic with an ACK seed of <paramref name="linkSeed"/> + <c>0x0B</c>.
        /// </summary>
        /// <remarks>
        /// One continuous sequence across every connect - NEVER re-seeded per connect (the F1-space,
        /// and therefore the ACK, resets only on a peer reachability restart, exactly like the data
        /// sequences). Calling this repeatedly with the same learned seed is idempotent because the
        /// model holds no per-ACK state.
        /// </remarks>
        /// <param name="linkSeed">
        /// The learned per-link seed (for example 0x14 for 100 to 102); the ACK seed is this + 0x0B.
        /// </param>
        public void UseSessionAckModel(byte linkSeed)
        {
            _sessionAckSeed = (byte)(linkSeed + 0x0B);
            _sessionAckModel = true;
        }

        /// <summary>
        /// Accepts a data frame, delivers it, and builds the delivery ACK for it.
        /// </summary>
        /// <param name="dataFrame">
        /// The received data frame (subtype <see cref="SintranPacketSubtype.Data"/>).
        /// </param>
        /// <param name="ackChannel">
        /// The session ACK routing signal. When non-null AND the TAD-session model is enabled
        /// (<see cref="UseSessionAckModel"/>), the ACK uses the stateless closed form (its channel is
        /// DERIVED, so the value passed here is only the "this is a session frame" marker). When null
        /// (reachability / list-route) the legacy model runs: the ACK echoes the data frame's own
        /// channel and carries the decrementing <see cref="Counter"/>.
        /// </param>
        /// <returns>
        /// The subtype-<c>0x03</c> ACK frame echoing the data frame's Flags 1.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="dataFrame"/> or its header is null.
        /// </exception>
        public XmsgFrame ReceiveDataFrame(XmsgFrame dataFrame, SintranProtocolId? ackChannel = null)
        {
            if (dataFrame == null)
            {
                throw new ArgumentNullException(nameof(dataFrame));
            }

            if (dataFrame.Header == null)
            {
                throw new ArgumentNullException(nameof(dataFrame), "Frame header is null.");
            }

            SintranHeader dataHeader = dataFrame.Header;

            // Deliver the message to the local application.
            OnReceived?.Invoke(dataHeader.Flags1);

            // Build the ACK: opposite direction, echo Flags 1, Flags 2 = 0x0001.
            XmsgFrame ack = new XmsgFrame();
            ack.Header.Marker1 = SintranHeader.Marker1Value;
            ack.Header.Marker2 = SintranHeader.Marker2Normal;
            ack.Header.Subtype = SintranPacketSubtype.Ack;
            ack.Header.DestinationNode = dataHeader.SourceNode;   // reply to the sender
            ack.Header.SourceNode = dataHeader.DestinationNode;
            ack.Header.Flags1 = dataHeader.Flags1;                // echo the datagram sequence
            ack.Header.Flags2 = AckFlags2;

            if (_sessionAckModel && ackChannel != null)
            {
                // The ACK's "channel" and its "trailing byte" are the two halves of header word 6,
                // which is a ones-complement checksum over the other six words - carved from the
                // XMSG kernel at 137314 and verified on all 1671 captured ACKs (3595/3595 frames
                // overall). See XmsgEnvelope.ComputeHeaderChecksum.
                //
                // This REPLACES the S_ack = link-seed + 0x0B closed form that used to live here.
                // That form reproduced every observed ACK, so this is not a bug fix - but it was a
                // curve fit to the checksum and it needed a LEARNED per-link seed to work at all.
                // The checksum needs no learned state: every input is already in the header we just
                // built, so an ACK is correct BY CONSTRUCTION rather than correct because the seed
                // happened to be learned first. _sessionAckSeed is consequently unused here now.
                // CORRECTED 2026-08-04: those two halves are ONE header field at offsets 12-13,
                // so an ACK is fourteen bytes with NO trailing byte. The emitted bytes are
                // unchanged - only the model is.
                //
                // The type/subtype word used to be written here as the LITERAL SintranPacketSubtype
                // .Ack, which silently assumed PacketType is zero. Subtype is already set on this
                // header above, so reading it off the header emits the same bytes and drops the
                // assumption.
                XmsgEnvelope.StampChecksum(ack.Header);
                return ack;
            }

            // LEGACY (reachability / list-route): echo the data channel, carry the decrementing
            // counter. Both are halves of header word 6 - see above; still emitted as chosen
            // values here because this path exists to reproduce traffic we have not derived.
            ack.Header.Checksum = (ushort)(((ushort)(ackChannel ?? (SintranProtocolId)(byte)(dataHeader.Checksum >> 8)) << 8) | _counter);
            _counter = (byte)(_counter - 1);
            return ack;
        }
    }
}
