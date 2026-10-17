//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The 32-bit magic number (MAGNO) by which a task identifies another task's port.
    /// </summary>
    /// <remarks>
    /// <para><b>Layout</b></para>
    /// Per the manual (section 1.2.3 and appendix A section 3.1) a magic number is composed of the
    /// port number, the system number and a random part; the random part makes it extremely
    /// unlikely that a port which is closed and reopened gets the same identifier. The manual
    /// never publishes HOW the three are packed - it exposes conversion functions instead - so the
    /// packing below was carved from the XMSG L03 kernel (routines ZCRMG, ZRAND and MFM2P) and is
    /// documented with the disassembly in DOC/XMSG-MAGIC-NUMBER-LAYOUT-CARVED-2026-07-26.md.
    /// <code>
    ///  bit  31              16 15                7 6           0
    ///      +------------------+-------------------+-------------+
    ///      |  system number   |    port number    |   random    |
    ///      +------------------+-------------------+-------------+
    ///        A register (16)     9 bits, 1-based     7 bits
    /// </code>
    /// The kernel builds the low word as <c>(port shifted left 7) | random</c> and puts the system
    /// number in the high word; the random part is masked to seven bits, so a reopened port
    /// collides with its former identifier once in 128 times.
    /// <para><b>Prefer the kernel functions anyway</b></para>
    /// Knowing the layout does not make hand-packing correct: the kernel validates that a port
    /// number lands on a real port block and refuses otherwise. Application code with a kernel
    /// available should still go through <see cref="IXmsgKernel.ConvertMagicToPort"/> and
    /// <see cref="IXmsgKernel.ConvertPortToMagic"/>. The accessors here exist for decoding,
    /// tooling and emulation.
    /// <para><b>The wire port fields are this low word</b></para>
    /// VERIFIED against the capture corpus: the XMSG sub-header fields XMDPT (offset 20-21) and
    /// XMSPT (offset 24-25) carry exactly <see cref="PortWord"/>, and XMSSY/XMDSY carry
    /// <see cref="SystemNumber"/>. The TAD port-assign message (7CORS) ships the two halves
    /// together as a whole magic number, and they then appear verbatim in the sub-header of every
    /// following frame. So <see cref="FromRegisterPair"/> reassembles a magic number straight from
    /// a decoded frame's system and port fields.
    /// The random part is drawn by <see cref="XmsgRandomGenerator"/>, which is a linear
    /// congruential generator rather than a random source - see that type before assuming these
    /// identifiers are unpredictable.
    /// The 16-bit "hashed magic number" (RPORT) is a separate, still opaque, almost-unique
    /// abbreviation; see <see cref="XmsgHashedMagicNumber"/>.
    /// </remarks>
    public readonly struct XmsgMagicNumber : IEquatable<XmsgMagicNumber>
    {
        /// <summary>
        /// The magic number meaning "no port".
        /// </summary>
        public static readonly XmsgMagicNumber None = new XmsgMagicNumber(0);

        private readonly uint _value;

        /// <summary>
        /// Initialises a magic number from its raw 32-bit value.
        /// </summary>
        /// <param name="value">
        /// The opaque 32-bit identifier as carried in the A and D registers.
        /// </param>
        public XmsgMagicNumber(uint value)
        {
            _value = value;
        }

        /// <summary>
        /// Gets the raw opaque 32-bit value.
        /// </summary>
        public uint Value
        {
            get { return _value; }
        }

        /// <summary>
        /// Gets a value indicating whether this identifies no port.
        /// </summary>
        public bool IsNone
        {
            get { return _value == 0; }
        }

        /// <summary>
        /// Gets the system number that owns the port.
        /// </summary>
        /// <remarks>
        /// The whole high word - the same 16 bits as <see cref="HighWord"/>, named for the meaning
        /// rather than the register. The kernel takes it from its base field when it builds the
        /// magic number, so every port of one system shares this value.
        /// </remarks>
        public ushort SystemNumber
        {
            get { return HighWord; }
        }

        /// <summary>
        /// Gets the local port number.
        /// </summary>
        /// <remarks>
        /// The nine bits above the random part. Port numbers are one-based; zero is the reserved
        /// "default port" marker and never appears in a real magic number.
        /// </remarks>
        public int PortNumber
        {
            get { return (int)((_value >> 7) & 0x1FF); }
        }

        /// <summary>
        /// Gets the random part that makes a reopened port unlikely to reuse its identifier.
        /// </summary>
        /// <remarks>
        /// Seven bits, so the guarantee is one collision in 128 reopens - which is what the manual
        /// means by "extremely unlikely", and why the 16-bit hashed form is only "almost unique".
        /// </remarks>
        public int Random
        {
            get { return (int)(_value & 0x7F); }
        }

        /// <summary>
        /// Gets the low-order 16 bits: the port number and random part combined.
        /// </summary>
        /// <remarks>
        /// This is the value the kernel computes as <c>(port shifted left 7) | random</c>. It is the
        /// same 16 bits as <see cref="LowWord"/>, named for the meaning rather than the register.
        /// </remarks>
        public ushort PortWord
        {
            get { return LowWord; }
        }

        /// <summary>
        /// Gets a value indicating whether the random part is one the kernel could have minted.
        /// </summary>
        /// <remarks>
        /// False means this is not an allocated port's magic number: the kernel redraws the two
        /// values 0 and 127, so a port field carrying either is a reserved address instead - port
        /// 0, the XROUT protocol sink, being the case that actually occurs on the wire.
        /// </remarks>
        public bool HasMintableRandom
        {
            get { return XmsgRandomGenerator.IsMintable(Random); }
        }

        /// <summary>
        /// Gets the high-order 16 bits, as carried in the A register of the AD register pair.
        /// </summary>
        public ushort HighWord
        {
            get { return (ushort)(_value >> 16); }
        }

        /// <summary>
        /// Gets the low-order 16 bits, as carried in the D register of the AD register pair.
        /// </summary>
        public ushort LowWord
        {
            get { return (ushort)(_value & 0xFFFF); }
        }

        /// <summary>
        /// Composes a magic number from its three fields.
        /// </summary>
        /// <param name="systemNumber">
        /// The owning system number.
        /// </param>
        /// <param name="portNumber">
        /// The one-based local port number; must fit in nine bits.
        /// </param>
        /// <param name="random">
        /// The random part; must fit in seven bits.
        /// </param>
        /// <returns>
        /// The composed magic number.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="portNumber"/> or <paramref name="random"/> is outside the
        /// width the kernel packs it into.
        /// </exception>
        /// <remarks>
        /// This composes the bits only. It cannot check that the port number names a real port -
        /// the kernel does that when it builds a magic number, and rejects a port address that
        /// does not land on a port block.
        /// </remarks>
        public static XmsgMagicNumber Create(ushort systemNumber, int portNumber, int random)
        {
            if (portNumber < 0 || portNumber > 0x1FF)
            {
                throw new ArgumentOutOfRangeException(nameof(portNumber), "The port number field is nine bits.");
            }

            if (random < 0 || random > 0x7F)
            {
                throw new ArgumentOutOfRangeException(nameof(random), "The random field is seven bits.");
            }

            return new XmsgMagicNumber(((uint)systemNumber << 16) | (uint)(portNumber << 7) | (uint)random);
        }

        /// <summary>
        /// Builds a magic number from the register pair a task received it in.
        /// </summary>
        /// <param name="highWord">
        /// The A register content (most significant 16 bits).
        /// </param>
        /// <param name="lowWord">
        /// The D register content (least significant 16 bits).
        /// </param>
        /// <returns>
        /// The reassembled magic number.
        /// </returns>
        /// <remarks>
        /// Also the way to rebuild a magic number from a decoded frame, because the sub-header's
        /// system field is the high word and its port field is the low word.
        /// </remarks>
        public static XmsgMagicNumber FromRegisterPair(ushort highWord, ushort lowWord)
        {
            return new XmsgMagicNumber(((uint)highWord << 16) | lowWord);
        }

        /// <summary>
        /// Determines whether this magic number equals another.
        /// </summary>
        /// <param name="other">
        /// The magic number to compare with.
        /// </param>
        /// <returns>
        /// True when both carry the same raw value.
        /// </returns>
        public bool Equals(XmsgMagicNumber other)
        {
            return _value == other._value;
        }

        /// <summary>
        /// Determines whether this magic number equals another object.
        /// </summary>
        /// <param name="obj">
        /// The object to compare with.
        /// </param>
        /// <returns>
        /// True when the object is a magic number carrying the same raw value.
        /// </returns>
        public override bool Equals(object? obj)
        {
            return obj is XmsgMagicNumber other && Equals(other);
        }

        /// <summary>
        /// Gets a hash code for this magic number.
        /// </summary>
        /// <returns>
        /// A hash of the raw value.
        /// </returns>
        public override int GetHashCode()
        {
            return (int)_value;
        }

        /// <summary>
        /// Formats the magic number as eight hexadecimal digits.
        /// </summary>
        /// <returns>
        /// The raw value in hexadecimal.
        /// </returns>
        public override string ToString()
        {
            return "0x" + _value.ToString("X8")
                + " (system " + SystemNumber.ToString()
                + ", port " + PortNumber.ToString()
                + ", random " + Random.ToString() + ")";
        }

        /// <summary>
        /// Determines whether two magic numbers are equal.
        /// </summary>
        /// <param name="left">
        /// The first magic number.
        /// </param>
        /// <param name="right">
        /// The second magic number.
        /// </param>
        /// <returns>
        /// True when both carry the same raw value.
        /// </returns>
        public static bool operator ==(XmsgMagicNumber left, XmsgMagicNumber right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Determines whether two magic numbers differ.
        /// </summary>
        /// <param name="left">
        /// The first magic number.
        /// </param>
        /// <param name="right">
        /// The second magic number.
        /// </param>
        /// <returns>
        /// True when the raw values differ.
        /// </returns>
        public static bool operator !=(XmsgMagicNumber left, XmsgMagicNumber right)
        {
            return !left.Equals(right);
        }
    }
}
