//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Ethernet;

namespace RetroIO.Protocols.Xmsg.Hub
{
    /// <summary>
    /// What the hub did with one frame.
    /// </summary>
    public enum HubFrameOutcome
    {
        /// <summary>
        /// Repeated to the other members (possibly to none, when the sender is alone).
        /// </summary>
        Forwarded = 0,

        /// <summary>
        /// Dropped: it came back from another hub carrying this hub's own origin id, so it had
        /// been round a loop.
        /// </summary>
        DroppedLoop = 1,

        /// <summary>
        /// Dropped: its hub hop count had run out.
        /// </summary>
        DroppedTtl = 2,
    }

    /// <summary>
    /// One frame as it arrived at the hub, kept in the hub's buffer of recent frames.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A frame is recorded ONCE, in the direction from its sender to the hub. The hub is a
    /// broadcast hub, so the same frame then leaves on every other member; recording those copies
    /// as well would count every frame several times and make a two-machine conversation look
    /// like a crowd.
    /// </para>
    /// <para>
    /// The bytes are the raw Ethernet frame, exactly what the member sent, without the 2-byte
    /// length the TCP framing put in front of it and without the loop markers a hub link adds.
    /// The source and destination system numbers are decoded from the ND MAC addresses for
    /// listing only; when the frame is not a well-formed ND 802.3 frame they are zero and
    /// <see cref="IsNdFrame"/> is false - the bytes are still kept, because a malformed frame is
    /// often the one worth seeing.
    /// </para>
    /// </remarks>
    public sealed class HubFrameRecord
    {
        /// <summary>
        /// Initialises a record and decodes the addresses.
        /// </summary>
        /// <param name="sequence">
        /// The record's number. It starts at 1 and counts every frame the hub has seen, so a
        /// reader asking for "after N" never misses a frame silently: a gap in the numbers means
        /// the buffer wrapped.
        /// </param>
        /// <param name="timeUtc">
        /// When the frame arrived.
        /// </param>
        /// <param name="member">
        /// The member it came from.
        /// </param>
        /// <param name="frame">
        /// The frame bytes. A copy is kept.
        /// </param>
        /// <param name="outcome">
        /// What the hub did with it.
        /// </param>
        /// <param name="copiesSent">
        /// How many members it was queued to.
        /// </param>
        /// <param name="copiesDropped">
        /// How many members could not take it because they had fallen behind.
        /// </param>
        public HubFrameRecord(
            long sequence,
            DateTime timeUtc,
            string member,
            ReadOnlySpan<byte> frame,
            HubFrameOutcome outcome,
            int copiesSent,
            int copiesDropped)
        {
            Sequence = sequence;
            TimeUtc = timeUtc;
            Member = member ?? string.Empty;
            Frame = frame.ToArray();
            Outcome = outcome;
            CopiesSent = copiesSent;
            CopiesDropped = copiesDropped;

            if (Ieee8023Frame.TryParse(frame, out NdMacAddress destination, out NdMacAddress source, out _, out _))
            {
                IsNdFrame = true;
                SourceSystem = source.TryGetSystemNumber(out ushort s) ? s : (ushort)0;
                DestinationSystem = destination.TryGetSystemNumber(out ushort d) ? d : (ushort)0;
                SourceAddress = source.ToString();
                DestinationAddress = destination.ToString();
            }
            else
            {
                SourceAddress = string.Empty;
                DestinationAddress = string.Empty;
            }
        }

        /// <summary>
        /// Gets the record's number, counting from 1.
        /// </summary>
        public long Sequence { get; }

        /// <summary>
        /// Gets when the frame arrived, in UTC.
        /// </summary>
        public DateTime TimeUtc { get; }

        /// <summary>
        /// Gets the name of the member it came from (its remote address).
        /// </summary>
        public string Member { get; }

        /// <summary>
        /// Gets the raw Ethernet frame.
        /// </summary>
        public byte[] Frame { get; }

        /// <summary>
        /// Gets what the hub did with it.
        /// </summary>
        public HubFrameOutcome Outcome { get; }

        /// <summary>
        /// Gets how many members it was queued to.
        /// </summary>
        public int CopiesSent { get; }

        /// <summary>
        /// Gets how many members had fallen behind and lost it.
        /// </summary>
        public int CopiesDropped { get; }

        /// <summary>
        /// Gets whether the frame parsed as an ND/COSMOS 802.3 LLC frame.
        /// </summary>
        public bool IsNdFrame { get; }

        /// <summary>
        /// Gets the sending system number, or 0 when it could not be decoded.
        /// </summary>
        public ushort SourceSystem { get; }

        /// <summary>
        /// Gets the destination system number, or 0 when it could not be decoded.
        /// </summary>
        public ushort DestinationSystem { get; }

        /// <summary>
        /// Gets the source MAC address as text, or empty.
        /// </summary>
        public string SourceAddress { get; }

        /// <summary>
        /// Gets the destination MAC address as text, or empty.
        /// </summary>
        public string DestinationAddress { get; }
    }
}
