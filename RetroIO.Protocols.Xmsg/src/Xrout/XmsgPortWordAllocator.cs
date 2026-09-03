using System;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Mints wire port words the way the SINTRAN kernel does: a port number in the high nine bits
    /// and a generator draw in the low seven.
    /// </summary>
    /// <remarks>
    /// <para><b>What a wire port actually is</b></para>
    /// A port field in the XMSG sub-header is the low word of a 32-bit magic number:
    /// <c>(port number shifted left 7) | random</c>. The port number is the allocating node's own
    /// port-table index, one-based; the random part is a draw from <see cref="XmsgRandomGenerator"/>
    /// and is what stops a reopened port reusing its old identifier.
    /// <para><b>Why a peer must never compute one</b></para>
    /// This is deliberately an allocator for OUR OWN ports, never a way to predict a peer's. The
    /// spec's rule stands: every non-zero port is an opaque learned value - the asker port comes
    /// from the connect frame, the server port from the accept, the terminal port from the
    /// port-assign. A responder may answer on any well-formed port word it likes, which is exactly
    /// why deriving one from the other side's fields is both unnecessary and wrong.
    /// <para><b>Fidelity, not compatibility</b></para>
    /// Using this changes nothing a peer can object to - it only makes our port words look like
    /// the ones a real kernel mints, drawn from the real generator in the real order. A fixed
    /// port word works just as well on the wire and has been verified live.
    /// </remarks>
    public sealed class XmsgPortWordAllocator
    {
        private readonly XmsgRandomGenerator _random;

        /// <summary>
        /// Initialises an allocator with an explicit generator seed.
        /// </summary>
        /// <param name="seed">
        /// The 16-bit seed for the random part. The kernel takes its first seed from a resident
        /// SINTRAN cell whose content is unknown, so any value is as faithful as any other; what
        /// matters is that successive draws follow the generator.
        /// </param>
        public XmsgPortWordAllocator(ushort seed)
        {
            _random = new XmsgRandomGenerator(seed);
        }

        /// <summary>
        /// Gets the generator's current seed, so an allocator can be resumed across a restart.
        /// </summary>
        /// <remarks>
        /// Persisting this is what makes a restarted node continue the sequence instead of
        /// repeating it - the same reason the outgoing datagram sequence is persisted per link.
        /// </remarks>
        public ushort Seed
        {
            get { return _random.Seed; }
        }

        /// <summary>
        /// Mints the next wire port word for a given local port number.
        /// </summary>
        /// <param name="portNumber">
        /// The one-based local port number, which must fit in nine bits.
        /// </param>
        /// <returns>
        /// The port word to place in XMDPT or XMSPT.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="portNumber"/> is outside 1 to 511.
        /// </exception>
        public ushort Next(int portNumber)
        {
            if (portNumber < 1 || portNumber > 0x1FF)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(portNumber), "A port number is one-based and nine bits wide.");
            }

            return (ushort)((portNumber << 7) | _random.Next());
        }

        /// <summary>
        /// Splits a wire port word into its port number and random part.
        /// </summary>
        /// <param name="portWord">
        /// The port word as it appears on the wire.
        /// </param>
        /// <param name="portNumber">
        /// On return, the port number from the high nine bits.
        /// </param>
        /// <param name="random">
        /// On return, the random part from the low seven bits.
        /// </param>
        /// <returns>
        /// True when the random part is one the kernel could have minted, that is 1 to 126. False
        /// means the word is a reserved address rather than an allocated port - port 0, the XROUT
        /// sink, being the case that occurs in practice.
        /// </returns>
        public static bool TrySplit(ushort portWord, out int portNumber, out int random)
        {
            portNumber = portWord >> 7;
            random = portWord & 0x7F;
            return XmsgRandomGenerator.IsMintable(random);
        }
    }
}
