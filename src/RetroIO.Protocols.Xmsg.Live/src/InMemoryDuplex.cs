using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Protocols.Xmsg.Live
{
    /// <summary>
    /// An in-memory <see cref="IByteDuplex"/> for tests: reads are served from a
    /// pre-loaded inbound byte buffer and writes are captured into an outbound buffer.
    /// </summary>
    /// <remarks>
    /// Reads never block: once the pre-loaded inbound bytes are exhausted,
    /// <see cref="ReadAsync"/> returns 0 (end of stream), which lets a
    /// <see cref="LiveNode"/> receive loop terminate deterministically in a unit test.
    /// </remarks>
    public sealed class InMemoryDuplex : IByteDuplex
    {
        private readonly byte[] _inbound;
        private readonly List<byte> _outbound;
        private int _readPosition;

        /// <summary>
        /// Initialises the duplex with the bytes to feed to the receive loop.
        /// </summary>
        /// <param name="inbound">
        /// The bytes that <see cref="ReadAsync"/> hands out, in order.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="inbound"/> is null.
        /// </exception>
        public InMemoryDuplex(byte[] inbound)
        {
            if (inbound == null)
            {
                throw new ArgumentNullException(nameof(inbound));
            }

            _inbound = inbound;
            _outbound = new List<byte>();
        }

        /// <summary>
        /// Gets a copy of the bytes written to the channel so far.
        /// </summary>
        /// <returns>
        /// A new array holding every byte written through <see cref="WriteAsync"/>.
        /// </returns>
        public byte[] GetWrittenBytes()
        {
            return _outbound.ToArray();
        }

        /// <inheritdoc/>
        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            int remaining = _inbound.Length - _readPosition;
            if (remaining <= 0)
            {
                return Task.FromResult(0);
            }

            int n = remaining < count ? remaining : count;
            Array.Copy(_inbound, _readPosition, buffer, offset, n);
            _readPosition += n;
            return Task.FromResult(n);
        }

        /// <inheritdoc/>
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            ReadOnlySpan<byte> span = data.Span;
            for (int i = 0; i < span.Length; i++)
            {
                _outbound.Add(span[i]);
            }

            return Task.CompletedTask;
        }
    }
}
