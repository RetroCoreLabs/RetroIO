//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Protocols.Xmsg.Live
{
    /// <summary>
    /// A bidirectional raw-byte channel: the transport beneath the HDLC byte stream.
    /// </summary>
    /// <remarks>
    /// Implemented by <see cref="TcpBridgeTransport"/> over the <c>nd100x --hdlc</c> TCP
    /// bridge and by <see cref="InMemoryDuplex"/> for deterministic tests.
    /// </remarks>
    public interface IByteDuplex
    {
        /// <summary>
        /// Reads the next available bytes into a buffer.
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
        /// A token that cancels the read.
        /// </param>
        /// <returns>
        /// A task yielding the number of bytes read, or 0 at end of stream.
        /// </returns>
        Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);

        /// <summary>
        /// Writes a block of bytes to the channel.
        /// </summary>
        /// <param name="data">
        /// The bytes to send.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that cancels the write.
        /// </param>
        /// <returns>
        /// A task that completes when the bytes have been handed to the transport.
        /// </returns>
        Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    }
}
