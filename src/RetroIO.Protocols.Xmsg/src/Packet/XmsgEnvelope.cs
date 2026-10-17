//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Packet
{
    /// <summary>
    /// The complete XMSG envelope arithmetic. Per link (node pair) there is one constant byte - the
    /// seed - and per direction one variable - Flags 1 (the datagram sequence, +1 per
    /// data frame, starting at 0x0000). Everything else (the sub-header Counter and the sub-protocol
    /// Channel / Protocol ID) is derived arithmetically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SUPERSEDED 2026-07-31 - use <see cref="ComputeHeaderChecksum(SintranHeader)"/>. Everything below this
    /// notice describes a model that was FITTED to observations and is now known to be the wrong
    /// explanation. The SINTRAN header is SEVEN WORDS and word 6 is a ones-complement checksum over
    /// the other six - <c>w6 == ~ones_complement_sum(w0..w5, 0)</c> - carved from the kernel at
    /// <c>137314</c> and verified on 3595 of 3595 frames, every subtype, no special cases. The wire
    /// bytes at offsets 12 and 13 are that checksum's high and low halves; there is no channel, no
    /// epoch and no per-link seed. Doc:
    /// <c>SINTRAN/XMSG/DOC/XMSG-HEADER-WORD6-IS-A-CHECKSUM-2026-07-31.md</c>.
    /// </para>
    /// <para>
    /// The old model survived because the checksum is a deterministic function of the same header
    /// fields it used as inputs. Its "per-link seed" was the contribution of the fields nobody
    /// varied; its "epoch" was carry propagation out of the low byte; the masked-versus-signed
    /// <c>baseLow</c> question was that same carry seen as a borrow; and a peer crashes on a "wrong
    /// channel" simply because a bad word 6 is a corrupt header checksum.
    /// </para>
    /// <para>
    /// The members below are kept so existing call sites compile. They reproduce the wire on the
    /// traffic they were fitted to and NOT in general. Prefer the checksum for anything new.
    /// </para>
    /// <para><b>HISTORICAL, from here down.</b></para>
    /// <para>The rule (VERIFIED against all 601 captured Data frames + 602 ACKs, zero exceptions -
    /// see <c>SINTRAN/XMSG/DOC/XMSG-CHANNEL-SEQUENCE-ANALYSIS-2026-07-03.md</c>):</para>
    /// <code>
    /// baseLow = (seed - (Flags2 AND 0xFF)) AND 0xFF        // Flags2 == XMCSM >> 16, always
    /// Counter = (baseLow - Flags1) AND 0xFF
    /// epoch   = (Flags1 - baseLow + 0xFF) >> 8          // cumulative counter-wrap count; 0 when fresh
    /// Channel = 0xDE - (XMCSM >> 24) - epoch
    /// Base    = Flags1 + Counter = baseLow + 0x100*epoch  // the old "envelope Base", now derived
    /// </code>
    /// <para>Fresh-responder consequence: at epoch 0 a terminal-data frame (XMCSM 0x01080000)
    /// derives to channel 0xDD, not DB. DB only appears at epoch 2 (a node with a high running
    /// sequence, e.g. the conn-to-d102 capture). The same XROUT letter class rides DD (epoch 0),
    /// DC (epoch 1) and DB (epoch 2) purely by epoch. This is why the fresh live node's terminal data
    /// belongs on DD with <c>Counter = (seed - 8 - Flags1) AND 0xFF</c>.</para>
    /// <para>SCOPE LIMIT, found 2026-07-30. The Channel line above holds only where the LOW BYTE
    /// of Flags 2 is a message-class marker - <c>0x00</c> control, <c>0x08</c> terminal data. That was
    /// the whole corpus when the rule was derived. The COSMOS file-server captures broke the
    /// assumption: there Flags 2 is the message BODY LENGTH, so <c>baseLow = seed - (Flags2 AND 0xFF)</c>
    /// becomes a function of how long the message happened to be, and the epoch derived from it counts
    /// wraps of nothing. Measured over the current corpus:</para>
    /// <code>
    /// Flags2 low byte IS a class marker:  800 frames, 0 channel mismatches
    /// Flags2 low byte is a length:        170 frames, 70 channel mismatches
    /// </code>
    /// <para>So the rule is exactly right on 800 frames and inapplicable on the rest, rather than
    /// approximately right everywhere. The Counter line is unaffected. What the channel does on
    /// length-valued Flags 2 is UNKNOWN - observed wire offsets there are 0, 1, 2 and 3, and no
    /// replacement formula has been fitted on purpose. Track the channel per direction on that traffic,
    /// or capture enough of it to derive the rule properly. See
    /// <c>ChannelOffsetDiagnosticTests</c> and <c>EnvelopeConformanceTests</c>.</para>
    /// <para>Seed: learn it from any received Data frame via <see cref="LearnSeed"/> rather than
    /// hardcoding. Observed seeds: 100-102 = 0x14, 100-103 = 0x13, 102-103 = 0x11 (direct) / 0x12
    /// (relayed). What the seed byte encodes is UNKNOWN; learn-from-peer sidesteps it.</para>
    /// </remarks>
    public static class XmsgEnvelope
    {
        /// <summary>
        /// The constant the channel derivation subtracts from (the top of the Protocol-ID range).
        /// </summary>
        /// <remarks>
        /// SUPERSEDED - see <see cref="ComputeHeaderChecksum(SintranHeader)"/>. There is no "channel range" and
        /// nothing anchors at <c>0xDE</c>; that value is simply the most common high byte of the
        /// header checksum for typical headers. Kept only so existing call sites still compile.
        /// </remarks>
        public const byte ChannelAnchor = 0xDE;

        /// <summary>
        /// Computes the SINTRAN header checksum - the REAL definition of what the wire carries at
        /// offsets 12 and 13.
        /// </summary>
        /// <remarks>
        /// <para>
        /// CARVED 2026-07-31 from the XMSG kernel routine at <c>137314</c>, reached from
        /// <c>XSDGM</c> through the pointer at <c>137744</c>, whose result the caller stores at
        /// <c>137675 STA ,X 32</c> - header word 6 itself:
        /// </para>
        /// <code>
        /// T := X + 0o32                  ; limit
        /// A := 0                         ; accumulator
        /// X += 0o24                      ; start   -> 0o24..0o32 is SEVEN words, the header
        /// while X at most T:
        ///     A += mem[X]                ; ADD
        ///     A += carry                 ; RADD ADC  -> END-AROUND CARRY
        ///     X += 1
        /// if A != 0: A := ~A             ; ones complement
        /// </code>
        /// <para>
        /// The SINTRAN header is SEVEN WORDS, and word 6 is this checksum over the other six with
        /// the checksum field itself counted as zero. On the wire that word reads as two bytes:
        /// offset 12 (long mislabelled "Protocol ID" or "channel") is the HIGH byte, and offset 13
        /// (long mislabelled "Counter") is the LOW byte. There is no channel, no epoch and no
        /// per-link seed - those were artifacts of curve-fitting an expression to checksum
        /// arithmetic.
        /// </para>
        /// <para>
        /// Verified on 3595 of 3595 frames across the whole capture corpus - every subtype
        /// (Ack 1671, Data 1449, transfer <c>0x0A</c> 226 and <c>0x0C</c> 226, ReachRequest 10,
        /// ReachReply 6, NetworkError 3, and the <c>0xFD</c>/<c>0xFE</c> family 4), both
        /// directions, every link, with no per-subtype special cases. The model this replaces
        /// needed one formula for Data frames, a separate closed form for ACKs, and had nothing to
        /// say about the remaining 475.
        /// </para>
        /// <para>
        /// Doc: <c>SINTRAN/XMSG/DOC/XMSG-HEADER-WORD6-IS-A-CHECKSUM-2026-07-31.md</c>.
        /// </para>
        /// </remarks>
        /// <param name="markers">
        /// Word 0 - Marker 1 in the high byte, Marker 2 in the low byte (normally <c>0x2113</c>).
        /// </param>
        /// <param name="typeAndSubtype">
        /// Word 1 - the packet type in the high byte, the subtype in the low byte.
        /// </param>
        /// <param name="destinationNode">
        /// Word 2 - the destination node number.
        /// </param>
        /// <param name="sourceNode">
        /// Word 3 - the source node number.
        /// </param>
        /// <param name="flags1">
        /// Word 4 - the datagram sequence (XMSEQ).
        /// </param>
        /// <param name="flags2">
        /// Word 5 - equal to the 16-bit XMCSM.
        /// </param>
        /// <returns>
        /// Header word 6: the checksum high byte is the wire's offset-12 byte, its low byte is the
        /// wire's offset-13 byte.
        /// </returns>
        public static ushort ComputeHeaderChecksum(
            ushort markers,
            ushort typeAndSubtype,
            ushort destinationNode,
            ushort sourceNode,
            ushort flags1,
            ushort flags2)
        {
            // Ones-complement sum with end-around carry, exactly as the kernel loop does it. The
            // checksum word itself contributes zero, so it is simply not added here.
            int sum = 0;
            sum = AddOnesComplement(sum, markers);
            sum = AddOnesComplement(sum, typeAndSubtype);
            sum = AddOnesComplement(sum, destinationNode);
            sum = AddOnesComplement(sum, sourceNode);
            sum = AddOnesComplement(sum, flags1);
            sum = AddOnesComplement(sum, flags2);

            // The kernel skips the complement when the sum is zero (JAZ over the RADD CM1). No
            // frame in the corpus hits that case, but mirror the code rather than the corpus.
            if (sum == 0)
            {
                return 0;
            }

            return (ushort)(~sum & 0xFFFF);
        }

        /// <summary>
        /// Computes header word 6 from a header, reading all six inputs off it.
        /// </summary>
        /// <param name="header">
        /// The header to read. Its own <see cref="SintranHeader.Checksum"/> is not an input - the
        /// checksum word contributes zero to its own sum.
        /// </param>
        /// <returns>
        /// Header word 6.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="header"/> is null.
        /// </exception>
        /// <remarks>
        /// <para><b>Prefer this over the six-argument form for anything holding a header</b></para>
        /// <para>
        /// Nine call sites packed these same six words by hand, and two of them wrote the type and
        /// subtype as a LITERAL subtype instead of reading <see cref="SintranHeader.PacketType"/>,
        /// silently assuming it is zero. That assumption holds today and held on the wire, but it
        /// meant the day a packet type other than <c>0x00</c> appears, only those two sites would be
        /// wrong - the exact split-brain this class exists to prevent. Reading every input off the
        /// header removes the chance to disagree.
        /// </para>
        /// </remarks>
        public static ushort ComputeHeaderChecksum(SintranHeader header)
        {
            if (header == null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            return ComputeHeaderChecksum(
                (ushort)((header.Marker1 << 8) | header.Marker2),
                (ushort)((header.PacketType << 8) | (byte)header.Subtype),
                header.DestinationNode,
                header.SourceNode,
                header.Flags1,
                header.Flags2);
        }

        /// <summary>
        /// Computes header word 6 and writes it into the header.
        /// </summary>
        /// <param name="header">
        /// The header to stamp. Every other field must already be final - the checksum covers
        /// them, so stamping before they are set produces a wrong word.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="header"/> is null.
        /// </exception>
        /// <remarks>
        /// A fabricated word 6 is not a cosmetic fault: on 2026-08-04 every link-up killed D100 with
        /// <c>XMSG ERROR CODE 24</c>. Stamp, never invent.
        /// </remarks>
        public static void StampChecksum(SintranHeader header)
        {
            // The null check lives in the overload this calls.
            ushort checksum = ComputeHeaderChecksum(header);
            header.Checksum = checksum;
        }


        /// <summary>
        /// Adds one word into a ones-complement accumulator, folding the carry back in.
        /// </summary>
        /// <param name="accumulator">
        /// The running 16-bit accumulator.
        /// </param>
        /// <param name="word">
        /// The word to add.
        /// </param>
        /// <returns>
        /// The updated 16-bit accumulator.
        /// </returns>
        private static int AddOnesComplement(int accumulator, ushort word)
        {
            int sum = accumulator + word;
            if (sum > 0xFFFF)
            {
                sum = (sum & 0xFFFF) + 1;      // end-around carry (RADD ADC in the kernel)
            }

            return sum;
        }

        /// <summary>
        /// Learns the per-link seed byte from a received frame:
        /// <c>seed = (Counter + Flags1 + (Flags2 AND 0xFF)) AND 0xFF</c>.
        /// </summary>
        /// <param name="flags1">
        /// The frame's Flags 1 (datagram sequence).
        /// </param>
        /// <param name="counter">
        /// The frame's sub-header Counter.
        /// </param>
        /// <param name="flags2">
        /// The frame's Flags 2 (equals XMCSM >> 16).
        /// </param>
        /// <returns>
        /// The link seed byte.
        /// </returns>
        public static byte LearnSeed(ushort flags1, byte counter, ushort flags2)
        {
            return (byte)(counter + flags1 + (flags2 & 0xFF));
        }

        /// <summary>
        /// The per-class base low byte: <c>(seed - (Flags2 AND 0xFF)) AND 0xFF</c>. Control frames
        /// (Flags2 low 0x00) use <c>seed</c>; terminal-data frames (Flags2 low 0x08) use <c>seed - 8</c>.
        /// </summary>
        /// <param name="seed">
        /// The link seed.
        /// </param>
        /// <param name="flags2">
        /// The frame's Flags 2.
        /// </param>
        /// <returns>
        /// The base low byte.
        /// </returns>
        public static byte BaseLow(byte seed, ushort flags2)
        {
            // Truncation to a byte is correct HERE because the only consumer of this form is
            // the Counter, which is itself a byte and so is exact modulo 256 either way.
            return (byte)BaseLowSigned(seed, flags2);
        }

        /// <summary>
        /// The base low value WITHOUT truncation to a byte, for the epoch calculation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When the low byte of <paramref name="flags2"/> exceeds <paramref name="seed"/> this is
        /// NEGATIVE, and it has to stay negative. <see cref="ComputeEpoch"/> divides
        /// <c>Flags1 - baseLow</c> by 256, so truncating a negative base into 0..255 moves the
        /// boundary and loses a borrow: the epoch comes out one too low and the derived channel
        /// one too high.
        /// </para>
        /// <para>
        /// CORRECTED 2026-07-31. The masked form was verified only against TAD and routing
        /// captures, where every observed Flags 2 low byte is at or below the seed, so the case
        /// never arose. File-server traffic carries 11-15 distinct class words per session, many
        /// above the seed, and mismatched constantly. With the truncation removed the model is
        /// exact on the whole capture corpus - 1449/1449 data frames across every capture and
        /// every link - against 1267/1449 with it.
        /// See <c>SINTRAN/XMSG/DOC/XMSG-CHANNEL-FORMULA-DIVERGES-ON-FILE-SERVER-TRAFFIC-2026-07-31.md</c>.
        /// </para>
        /// </remarks>
        /// <param name="seed">
        /// The link seed.
        /// </param>
        /// <param name="flags2">
        /// The frame's Flags 2.
        /// </param>
        /// <returns>
        /// The signed base low value; negative when the Flags 2 low byte exceeds the seed.
        /// </returns>
        public static int BaseLowSigned(byte seed, ushort flags2)
        {
            // CARVED CONTEXT, 2026-07-31: flags2 IS the kernel's 16-bit XMCSM (proven equal on
            // 1449/1449 frames - see XmsgDataFields.Flags2), and the kernel comments XMCSM as
            // "datagram checksum; if not checksum, then message size". So the identity
            //     (Counter + Flags1 + XMCSMlow) & 0xFF == seed
            // that the whole envelope rests on is a CHECKSUM relation, and this subtraction is one
            // term of it. Read that way, dropping the 8-bit truncation above is not a fudge: it is
            // keeping the BORROW that an ordinary subtract produces.
            // NOT yet confirmed against the kernel CODE - the symbol file gives the field, not the
            // routine that computes it. Finding what writes SINTRAN header offset 12 is the open
            // item. Until then treat "epoch"/"class" in this file as MODEL VOCABULARY, not carved.
            return seed - (flags2 & 0xFF);
        }

        /// <summary>
        /// Computes the sub-header Counter: <c>(baseLow - Flags1) AND 0xFF</c>.
        /// </summary>
        /// <param name="seed">
        /// The link seed.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence for this frame.
        /// </param>
        /// <param name="flags2">
        /// The frame's Flags 2.
        /// </param>
        /// <returns>
        /// The sub-header Counter byte.
        /// </returns>
        public static byte ComputeCounter(byte seed, ushort flags1, ushort flags2)
        {
            return (byte)(BaseLow(seed, flags2) - flags1);
        }

        /// <summary>
        /// Computes the epoch (cumulative counter-wrap count) for a frame:
        /// <c>(Flags1 - baseLow + 0xFF) >> 8</c>. Zero while Flags1 is at or below baseLow (a fresh direction).
        /// </summary>
        /// <param name="seed">
        /// The link seed.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence for this frame.
        /// </param>
        /// <param name="flags2">
        /// The frame's Flags 2.
        /// </param>
        /// <returns>
        /// The epoch (0, 1, 2, ...).
        /// </returns>
        public static int ComputeEpoch(byte seed, ushort flags1, ushort flags2)
        {
            // int arithmetic; the +0xFF keeps small (Flags1 - baseLow) negatives at epoch 0.
            // Uses the SIGNED base - see BaseLowSigned for why truncating here is a defect.
            return (flags1 - BaseLowSigned(seed, flags2) + 0xFF) >> 8;
        }

        /// <summary>
        /// Derives the sub-protocol channel from the full seed model:
        /// <c>Channel = 0xDE - (XMCSM >> 24) - epoch</c>.
        /// </summary>
        /// <param name="seed">
        /// The link seed.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence for this frame.
        /// </param>
        /// <param name="flags2">
        /// The frame's Flags 2.
        /// </param>
        /// <param name="controlService">
        /// The XMCSM control/service word.
        /// </param>
        /// <returns>
        /// The derived Protocol ID.
        /// </returns>
        public static SintranProtocolId DeriveChannel(byte seed, ushort flags1, ushort flags2, uint controlService)
        {
            int epoch = ComputeEpoch(seed, flags1, flags2);
            byte channel = (byte)(ChannelAnchor - (byte)(controlService >> 24) - epoch);
            return (SintranProtocolId)channel;
        }

        /// <summary>
        /// Computes the envelope base <c>Flags1 + Counter</c> (16-bit, wrapping) - the legacy view,
        /// equal to <c>baseLow + 0x100*epoch</c>.
        /// </summary>
        /// <param name="flags1">
        /// The datagram sequence.
        /// </param>
        /// <param name="counter">
        /// The sub-header counter.
        /// </param>
        /// <returns>
        /// The 16-bit base.
        /// </returns>
        public static ushort ComputeBase(ushort flags1, byte counter)
        {
            return (ushort)(flags1 + counter);
        }

        /// <summary>
        /// Legacy channel derivation from an already-known Counter:
        /// <c>Channel = 0xDE - (XMCSM >> 24) - (Base >> 8)</c>, where <c>Base = Flags1 + Counter</c>.
        /// Equivalent to the seed model when the Counter is the model-correct one (Base>>8 == epoch).
        /// </summary>
        /// <param name="flags1">
        /// The datagram sequence.
        /// </param>
        /// <param name="counter">
        /// The per-direction counter.
        /// </param>
        /// <param name="controlService">
        /// The XMCSM control/service word.
        /// </param>
        /// <returns>
        /// The derived Protocol ID.
        /// </returns>
        public static SintranProtocolId DeriveChannel(ushort flags1, byte counter, uint controlService)
        {
            ushort baseValue = ComputeBase(flags1, counter);
            byte channel = (byte)(ChannelAnchor - (byte)(controlService >> 24) - (byte)(baseValue >> 8));
            return (SintranProtocolId)channel;
        }
    }
}
