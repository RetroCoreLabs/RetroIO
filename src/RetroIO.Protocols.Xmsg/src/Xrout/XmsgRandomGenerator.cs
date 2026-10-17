//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// The XMSG kernel's magic-number randomiser (routine ZRAND), reproduced exactly.
    /// </summary>
    /// <remarks>
    /// <para><b>It is not random</b></para>
    /// Despite what the manual implies by calling a reopened port's identifier "extremely
    /// unlikely" to repeat, ZRAND is a plain 16-bit linear congruential generator carved from the
    /// XMSG L03 kernel at 131152 octal:
    /// <code>
    /// seed := seed * 5429 + 13849   (mod 65536)      ; octal 012465 and 033031
    /// draw := seed AND 127                            ; SAT 127 + RAND ST DA
    /// redraw while draw = 0 or draw = 127             ; JAZ + SKP IF DA UEQ ST
    /// </code>
    /// The increment is odd and the multiplier minus one is divisible by four, so by the
    /// Hull-Dobell theorem the generator has full period 65536 - and the low seven bits are
    /// themselves a full-period generator modulo 128, <c>r' = (53 * r + 25) mod 128</c>, which
    /// cycles through every one of the 128 values before repeating.
    /// <para><b>What that buys a decoder</b></para>
    /// Because the low seven bits of a magic number are these draws, and the wire port fields are
    /// the magic number's low word, successive port allocations on a node walk a known 128-long
    /// cycle. One observed port word therefore predicts every later one, and a value's position in
    /// the cycle is that node's allocation ordinal since XMSG started. This was confirmed against
    /// the capture corpus: 24 distinct draws taken from 753 wire endpoint fields all lie on the
    /// cycle, in runs of up to ten consecutive outputs, with no value ever 0 or 127.
    /// See DOC/XMSG-MAGIC-NUMBER-LAYOUT-CARVED-2026-07-26.md.
    /// Lives in the protocol assembly rather than the API one because the node layer,
    /// which may only reference RetroIO.Protocols.Xmsg, needs it to mint wire-faithful ports.
    /// <para><b>Seeding</b></para>
    /// The kernel seeds itself once, on the first call after a cold start, from a SINTRAN resident
    /// cell at 004137 octal - which lies below the XMSG load base and so is not in the kernel
    /// image. What that cell holds is therefore UNVERIFIED, and this class cannot reproduce it:
    /// the caller supplies the seed.
    /// </remarks>
    public sealed class XmsgRandomGenerator
    {
        /// <summary>
        /// The generator's multiplier (octal 012465).
        /// </summary>
        public const int Multiplier = 5429;

        /// <summary>
        /// The generator's increment (octal 033031).
        /// </summary>
        public const int Increment = 13849;

        /// <summary>
        /// The multiplier reduced to the low seven bits.
        /// </summary>
        public const int Low7Multiplier = Multiplier & 0x7F;

        /// <summary>
        /// The increment reduced to the low seven bits.
        /// </summary>
        public const int Low7Increment = Increment & 0x7F;

        /// <summary>
        /// The number of distinct values the low-seven-bit generator visits before repeating.
        /// </summary>
        public const int CycleLength = 128;

        /// <summary>
        /// The smallest value the kernel will accept as a random part.
        /// </summary>
        /// <remarks>
        /// Zero is redrawn, so a minted magic number never carries it.
        /// </remarks>
        public const int MinimumDraw = 1;

        /// <summary>
        /// The largest value the kernel will accept as a random part.
        /// </summary>
        /// <remarks>
        /// The mask allows 127, but the kernel redraws that value too, so 126 is the ceiling.
        /// </remarks>
        public const int MaximumDraw = 126;

        private ushort _seed;

        /// <summary>
        /// Initialises the generator with an explicit seed.
        /// </summary>
        /// <param name="seed">
        /// The 16-bit seed. The kernel takes its first seed from a resident SINTRAN cell whose
        /// content is not known, so there is no "correct" starting value to default to.
        /// </param>
        public XmsgRandomGenerator(ushort seed)
        {
            _seed = seed;
        }

        /// <summary>
        /// Gets the current 16-bit seed.
        /// </summary>
        public ushort Seed
        {
            get { return _seed; }
        }

        /// <summary>
        /// Draws the next random part, redrawing the two values the kernel rejects.
        /// </summary>
        /// <returns>
        /// A value in 1..126.
        /// </returns>
        /// <remarks>
        /// Advances the seed once per attempt, exactly as the kernel does - a rejected draw is not
        /// free, it consumes a step of the sequence.
        /// </remarks>
        public int Next()
        {
            while (true)
            {
                _seed = (ushort)((_seed * Multiplier) + Increment);

                int draw = _seed & 0x7F;
                if (draw != 0 && draw != 0x7F)
                {
                    return draw;
                }
            }
        }

        /// <summary>
        /// Advances the low-seven-bit sequence by one step, without needing the full seed.
        /// </summary>
        /// <param name="draw">
        /// The current draw.
        /// </param>
        /// <returns>
        /// The next value the low-seven-bit generator produces, including the values the kernel
        /// would reject; use <see cref="NextAccepted"/> to skip those.
        /// </returns>
        /// <remarks>
        /// This works because the low bits of a power-of-two-modulus generator are self-contained:
        /// the top nine bits of the seed never influence them.
        /// </remarks>
        public static int Step(int draw)
        {
            return ((draw * Low7Multiplier) + Low7Increment) & 0x7F;
        }

        /// <summary>
        /// Advances the low-seven-bit sequence to the next value the kernel would accept.
        /// </summary>
        /// <param name="draw">
        /// The current draw.
        /// </param>
        /// <returns>
        /// The next value in 1..126, skipping 0 and 127.
        /// </returns>
        public static int NextAccepted(int draw)
        {
            int next = Step(draw);
            while (next == 0 || next == 0x7F)
            {
                next = Step(next);
            }

            return next;
        }

        /// <summary>
        /// Counts how many steps separate two draws in the cycle.
        /// </summary>
        /// <param name="from">
        /// The earlier draw.
        /// </param>
        /// <param name="to">
        /// The later draw.
        /// </param>
        /// <returns>
        /// The number of generator steps from <paramref name="from"/> to <paramref name="to"/>,
        /// in 1..128; equal values give 128, a full lap.
        /// </returns>
        /// <remarks>
        /// This is the forensic use: the distance between two observed port words is how many
        /// ports that node allocated in between, so a small distance means the allocations were
        /// adjacent even if the capture missed the frames between them.
        /// </remarks>
        public static int Distance(int from, int to)
        {
            int current = from & 0x7F;
            int target = to & 0x7F;

            for (int steps = 1; steps <= CycleLength; steps++)
            {
                current = Step(current);
                if (current == target)
                {
                    return steps;
                }
            }

            // Unreachable: the low-seven-bit generator has full period, so every value is visited.
            throw new InvalidOperationException("The low-seven-bit generator failed to reach the target value.");
        }

        /// <summary>
        /// Determines whether a value could have been produced by the kernel as a random part.
        /// </summary>
        /// <param name="draw">
        /// The value to test, normally the low seven bits of an observed wire port field.
        /// </param>
        /// <returns>
        /// True when the value is in 1..126.
        /// </returns>
        /// <remarks>
        /// A value of 0 or 127 in a port field means the field is NOT a minted magic number - the
        /// clearest example being port 0, the XROUT protocol sink, which is a reserved address
        /// rather than an allocated port.
        /// </remarks>
        public static bool IsMintable(int draw)
        {
            return draw >= MinimumDraw && draw <= MaximumDraw;
        }
    }
}
