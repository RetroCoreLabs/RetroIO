//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;

namespace RetroIO.Storage.Local.Streams
{
    /// <summary>
    /// A stream that shows a part of another stream and reads zero bytes where that stream is shorter than the part should be.
    /// </summary>
    /// <remarks>
    /// Disk image files are sometimes cut short: the last track is only partly there. The emulators that load such a file (XRoar does)
    /// take the missing sectors as zero bytes. This stream gives a block device the full length of the disk while the file keeps its real
    /// length. A write behind the end of the real data first fills the gap with zero bytes, so the base stream grows only as far as it must.
    /// </remarks>
    public sealed class PaddedSubStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _offset;
        private readonly long _length;
        private long _realLength;
        private long _position;

        /// <summary>
        /// Creates the stream.
        /// </summary>
        /// <param name="baseStream">
        /// The stream to read from and write to.
        /// </param>
        /// <param name="offset">
        /// Position in <paramref name="baseStream"/> where the part starts.
        /// </param>
        /// <param name="realLength">
        /// The number of bytes of the part that really are in <paramref name="baseStream"/>.
        /// </param>
        /// <param name="length">
        /// The length this stream shows. At least <paramref name="realLength"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="baseStream"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when a number is negative, <paramref name="length"/> is smaller than <paramref name="realLength"/>, or the real part goes past the end of the base stream.
        /// </exception>
        public PaddedSubStream(Stream baseStream, long offset, long realLength, long length)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
            }

            if (realLength < 0 || length < realLength)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Length must not be smaller than the real length, which must not be negative");
            }

            if (offset + realLength > baseStream.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(realLength), "Offset + real length exceeds base stream length");
            }

            _offset = offset;
            _realLength = realLength;
            _length = length;
        }

        /// <summary>
        /// Gets whether the base stream can be read.
        /// </summary>
        public override bool CanRead => _baseStream.CanRead;

        /// <summary>
        /// Gets whether the base stream can seek.
        /// </summary>
        public override bool CanSeek => _baseStream.CanSeek;

        /// <summary>
        /// Gets whether the base stream can be written.
        /// </summary>
        public override bool CanWrite => _baseStream.CanWrite;

        /// <summary>
        /// Gets the length this stream shows, including the zero padding.
        /// </summary>
        public override long Length => _length;

        /// <summary>
        /// Gets the number of bytes of the part that really are in the base stream. It grows when a write reaches past it.
        /// </summary>
        /// <remarks>
        /// A block device reads only this much when it is mounted on this stream, so the padding is never held in memory.
        /// </remarks>
        public long RealLength => _realLength;

        /// <summary>
        /// Gets or sets the position inside the stream.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the value is negative or past the end.
        /// </exception>
        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Position out of range");
                }

                _position = value;
            }
        }

        /// <summary>
        /// Flushes the base stream.
        /// </summary>
        public override void Flush()
        {
            _baseStream.Flush();
        }

        /// <summary>
        /// Reads bytes from the current position. Bytes behind the real data are zero.
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
        /// The number of bytes read, or 0 at the end of the stream.
        /// </returns>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || offset > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            long remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(count, remaining);
            int fromBase = 0;
            if (_position < _realLength)
            {
                fromBase = (int)Math.Min(toRead, _realLength - _position);
                _baseStream.Position = _offset + _position;
                fromBase = _baseStream.Read(buffer, offset, fromBase);
            }

            if (fromBase < toRead && _position + fromBase >= _realLength)
            {
                Array.Clear(buffer, offset + fromBase, toRead - fromBase);
                fromBase = toRead;
            }

            _position += fromBase;
            return fromBase;
        }

        /// <summary>
        /// Moves the position.
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
        /// Thrown when the new position is outside the stream.
        /// </exception>
        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
            };

            if (newPosition < 0 || newPosition > _length)
            {
                throw new IOException("Seek position out of range");
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
            throw new NotSupportedException("Cannot set length of PaddedSubStream");
        }

        /// <summary>
        /// Writes bytes at the current position. A gap between the real data and the position is filled with zero bytes first.
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
        /// Thrown when the write would go past the end of the stream.
        /// </exception>
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || offset > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (count > _length - _position)
            {
                throw new IOException("Write would exceed PaddedSubStream length");
            }

            if (_position > _realLength)
            {
                byte[] zeros = new byte[_position - _realLength];
                _baseStream.Position = _offset + _realLength;
                _baseStream.Write(zeros, 0, zeros.Length);
                _realLength = _position;
            }

            _baseStream.Position = _offset + _position;
            _baseStream.Write(buffer, offset, count);
            _position += count;
            if (_position > _realLength)
            {
                _realLength = _position;
            }
        }

        /// <summary>
        /// Disposes this stream. The base stream is not disposed.
        /// </summary>
        /// <param name="disposing">
        /// True when called from <c>Dispose</c>, false when called from a finalizer.
        /// </param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
