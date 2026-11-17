//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Switch.Model
{
    /// <summary>
    /// Counts charging segments for one direction of a circuit, by the rule of ITU-T D.12.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D.12 (1): a segment is 64 octets ("This number is subject to further study"). D.12 (2): "the quantity of
    /// chargeable data contained in each message is expressed as a number of segments and is rounded off to the next
    /// higher unit." A message is a complete packet sequence (D.12 reference [5], X.25 4.3.5): "a single category B
    /// packet and all contiguous preceding category A packets (if any). Category A packets have the exact maximum user
    /// data field length with the M bit set to 1 and the D bit set to 0. All other data packets are category B packets."
    /// </para>
    /// <para>
    /// So the octets of a message are added up across its packets and rounded up once, at its last packet: a message of
    /// two full 128-octet packets and a 1-octet one is ceil(257 / 64) = 5 segments, not 2 + 2 + 1. X.25 7.3.8.3 makes the
    /// segment size and which packets count "a matter of the Administration" for national calls; this switch uses the
    /// D.12 values and counts data packets only.
    /// </para>
    /// <para>
    /// Two readings, decided with Ronny 2026-11-16 to follow the wording, the same in x25emu: a message of zero octets
    /// counts 0 segments (zero "rounded off to the next higher unit" is still zero), and a message still open when the call
    /// ends (its last packet had M = 1) is not a complete packet sequence, so <see cref="Close"/> drops it.
    /// </para>
    /// </remarks>
    public sealed class SegmentCounter
    {
        /// <summary>
        /// The D.12 (1) segment length, in octets: the default.
        /// </summary>
        public const int SegmentOctets = 64;

        /// <summary>
        /// The largest segment length a switch accepts (<c>[switch] segment_size</c>, the same limit as x25emu).
        /// </summary>
        public const int MaxSegmentOctets = 4096;

        private readonly int _segmentOctets;
        private long _openOctets;

        /// <summary>
        /// A counter with D.12's 64-octet segments.
        /// </summary>
        public SegmentCounter()
            : this(SegmentOctets)
        {
        }

        /// <summary>
        /// A counter with another segment length. X.25 7.3.8.3 leaves the segment size of national calls to the
        /// Administration, so the operator may set it (<c>[switch] segment_size</c>, the same key as x25emu).
        /// </summary>
        /// <param name="segmentOctets">
        /// Octets in one segment, 1 to <see cref="MaxSegmentOctets"/>.
        /// </param>
        public SegmentCounter(int segmentOctets)
        {
            if (segmentOctets < 1 || segmentOctets > MaxSegmentOctets)
            {
                throw new System.ArgumentOutOfRangeException(nameof(segmentOctets), segmentOctets, "1 to " + MaxSegmentOctets);
            }

            _segmentOctets = segmentOctets;
        }
        private ulong _credited;

        /// <summary>
        /// The segments counted since the last call, for crediting an account as the traffic passes: each segment is
        /// handed out once, whether its message ended in <see cref="Add"/> or in <see cref="Close"/>.
        /// </summary>
        /// <returns>
        /// The segments not handed out before.
        /// </returns>
        public ulong TakeNew()
        {
            ulong fresh = Segments - _credited;
            _credited = Segments;
            return fresh;
        }

        /// <summary>
        /// Segments counted so far, for the messages that have ended.
        /// </summary>
        public ulong Segments { get; private set; }

        /// <summary>
        /// Adds one data packet.
        /// </summary>
        /// <param name="length">
        /// The octets of user data in the packet.
        /// </param>
        /// <param name="mBit">
        /// The more-data bit.
        /// </param>
        /// <param name="dBit">
        /// The delivery-confirmation bit.
        /// </param>
        /// <param name="fullSize">
        /// The maximum user data field length agreed for this direction (X.25 6.12): a packet is full when it carries
        /// exactly this many octets.
        /// </param>
        public void Add(int length, bool mBit, bool dBit, int fullSize)
        {
            _openOctets += length;

            bool categoryA = mBit && !dBit && length == fullSize;
            if (!categoryA)
            {
                // The category B packet ends the message: count it, rounded up once (D.12 (2)).
                Segments += (ulong)((_openOctets + _segmentOctets - 1) / _segmentOctets);
                _openOctets = 0;
            }
        }

        /// <summary>
        /// The call has ended: a message still open (its last packet had M = 1) is dropped, not counted.
        /// </summary>
        /// <remarks>
        /// D.12 charges "each message", and a message is a complete packet sequence, which only a category B packet
        /// completes (X.25 4.3.5). A sequence the call ended in the middle of was never completed, so it is not a message
        /// and is not charged. Decided with Ronny 2026-11-16 ("follow the spec"); until then it was counted. x25emu does
        /// the same. Safe to call more than once.
        /// </remarks>
        public void Close()
        {
            _openOctets = 0;
        }

        /// <summary>
        /// A PVC billing period has ended and its record was written: the next period counts from zero. A message still
        /// open is kept - the PVC goes on, so it is charged in the period in which it completes (D.12 charges per message).
        /// The same in x25emu (<c>sw_pvc_period_end</c>).
        /// </summary>
        public void StartPeriod()
        {
            Segments = 0;
            _credited = 0;
        }
    }
}
