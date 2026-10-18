//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

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

        /// <summary>
        /// Copies the next pre-loaded inbound bytes into a buffer. Never blocks.
        /// </summary>
        /// <param name="buffer">
        /// The destination buffer to fill.
        /// </param>
        /// <param name="offset">
        /// The zero-based offset in <paramref name="buffer"/> at which to start writing.
        /// </param>
        /// <param name="count">
        /// The maximum number of bytes to read.
        /// </param>
        /// <param name="cancellationToken">
        /// Not used: the read finishes at once.
        /// </param>
        /// <returns>
        /// A completed task yielding the number of bytes copied, or 0 once all pre-loaded bytes have
        /// been read.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="buffer"/> is null.
        /// </exception>
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

        /// <summary>
        /// Appends a block of bytes to the captured outbound buffer.
        /// </summary>
        /// <param name="data">
        /// The bytes to capture.
        /// </param>
        /// <param name="cancellationToken">
        /// Not used: the write finishes at once.
        /// </param>
        /// <returns>
        /// A completed task.
        /// </returns>
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
