//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;

namespace RetroIO.Storage.Local.Partitions
{
    /// <summary>
    /// Stream wrapper that provides access to a specific partition within a disk image file.
    /// Transparently offsets all read/write operations by the partition's starting sector.
    /// This allows filesystem drivers to treat a partition as if it were a whole disk.
    /// </summary>
    public sealed class PartitionStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _partitionStartOffset;
        private readonly long _partitionLength;
        private long _position;
        private bool _disposed;

        /// <summary>
        /// Creates a partition stream wrapper.
        /// </summary>
        /// <param name="baseStream">
        /// The underlying stream containing the full disk image.
        /// </param>
        /// <param name="partitionStartSector">
        /// Starting sector of the partition (512-byte sectors).
        /// </param>
        /// <param name="partitionSizeSectors">
        /// Size of the partition in 512-byte sectors.
        /// </param>
        public PartitionStream(Stream baseStream, uint partitionStartSector, uint partitionSizeSectors)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
            _partitionStartOffset = partitionStartSector * 512L;
            _partitionLength = partitionSizeSectors * 512L;
            _position = 0;

            if (!baseStream.CanSeek)
            {
                throw new ArgumentException("Base stream must support seeking", nameof(baseStream));
            }

            if (_partitionStartOffset + _partitionLength > baseStream.Length)
            {
                throw new ArgumentException(
                    $"Partition extends beyond base stream. " +
                    $"Partition end: {_partitionStartOffset + _partitionLength}, Stream length: {baseStream.Length}");
            }
        }

        /// <summary>
        /// Gets whether the base stream can be read.
        /// </summary>
        public override bool CanRead => _baseStream.CanRead;
        /// <summary>
        /// Gets true, because the constructor requires a seekable base stream.
        /// </summary>
        public override bool CanSeek => true;
        /// <summary>
        /// Gets whether the base stream can be written.
        /// </summary>
        public override bool CanWrite => _baseStream.CanWrite;
        /// <summary>
        /// Gets the length of the partition in bytes.
        /// </summary>
        public override long Length => _partitionLength;

        /// <summary>
        /// Gets or sets the position inside the partition.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the value is negative or past the end of the partition.
        /// </exception>
        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _partitionLength)
                {
                    throw new ArgumentOutOfRangeException(nameof(value),
                        $"Position must be between 0 and {_partitionLength}");
                }
                _position = value;
            }
        }

        /// <summary>
        /// Reads bytes from the current position of the partition. A read stops at the end of the partition.
        /// </summary>
        /// <param name="buffer">
        /// Receives the bytes.
        /// </param>
        /// <param name="offset">
        /// Index in <paramref name="buffer"/> where the first byte is stored.
        /// </param>
        /// <param name="count">
        /// The most bytes to read.
        /// </param>
        /// <returns>
        /// The number of bytes read, or 0 at the end of the partition.
        /// </returns>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (offset + count > buffer.Length) throw new ArgumentException("Buffer too small");

            // Clamp read to partition bounds
            long remaining = _partitionLength - _position;
            if (remaining <= 0) return 0;

            int bytesToRead = (int)Math.Min(count, remaining);

            // Seek to correct position in base stream
            long absolutePosition = _partitionStartOffset + _position;
            _baseStream.Seek(absolutePosition, SeekOrigin.Begin);

            // Read from base stream
            int bytesRead = _baseStream.Read(buffer, offset, bytesToRead);
            _position += bytesRead;

            return bytesRead;
        }

        /// <summary>
        /// Writes bytes at the current position of the partition.
        /// </summary>
        /// <param name="buffer">
        /// The bytes to write.
        /// </param>
        /// <param name="offset">
        /// Index in <paramref name="buffer"/> of the first byte to write.
        /// </param>
        /// <param name="count">
        /// The number of bytes to write.
        /// </param>
        /// <exception cref="IOException">
        /// Thrown when the write would go past the end of the partition.
        /// </exception>
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (offset + count > buffer.Length) throw new ArgumentException("Buffer too small");

            // Check partition bounds
            if (_position + count > _partitionLength)
            {
                throw new IOException($"Write would exceed partition bounds (size: {_partitionLength} bytes)");
            }

            // Seek to correct position in base stream
            long absolutePosition = _partitionStartOffset + _position;
            _baseStream.Seek(absolutePosition, SeekOrigin.Begin);

            // Write to base stream
            _baseStream.Write(buffer, offset, count);
            _position += count;
        }

        /// <summary>
        /// Moves the position inside the partition.
        /// </summary>
        /// <param name="offset">
        /// The distance to move, relative to <paramref name="origin"/>.
        /// </param>
        /// <param name="origin">
        /// The point the offset is counted from.
        /// </param>
        /// <returns>
        /// The new position.
        /// </returns>
        /// <exception cref="IOException">
        /// Thrown when the new position is outside the partition.
        /// </exception>
        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _partitionLength + offset,
                _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
            };

            if (newPosition < 0 || newPosition > _partitionLength)
            {
                throw new IOException($"Seek position {newPosition} is outside partition bounds (0-{_partitionLength})");
            }

            _position = newPosition;
            return _position;
        }

        /// <summary>
        /// Not supported. Always throws.
        /// </summary>
        /// <param name="value">
        /// Not used.
        /// </param>
        /// <exception cref="NotSupportedException">
        /// Always thrown.
        /// </exception>
        public override void SetLength(long value)
        {
            throw new NotSupportedException("Cannot resize a partition stream");
        }

        /// <summary>
        /// Flushes the base stream.
        /// </summary>
        public override void Flush()
        {
            _baseStream.Flush();
        }

        /// <summary>
        /// Marks the stream as disposed. The base stream is not disposed, because it may be shared.
        /// </summary>
        /// <param name="disposing">
        /// True when called from <c>Dispose</c>, false when called from a finalizer.
        /// </param>
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                // Do NOT dispose base stream - it may be shared by multiple partition streams
                // Base stream lifecycle is managed by the caller
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
