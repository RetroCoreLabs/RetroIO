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
    /// A stream that exposes a portion of another stream.
    /// Useful for skipping headers in disk images (e.g., VDK header).
    /// </summary>
    public class SubStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _offset;
        private readonly long _length;
        private long _position;

        /// <summary>
        /// Creates a stream that shows a part of the base stream.
        /// </summary>
        /// <param name="baseStream">
        /// The stream to read from and write to.
        /// </param>
        /// <param name="offset">
        /// Position in <paramref name="baseStream"/> where the part starts.
        /// </param>
        /// <param name="length">
        /// Length of the part in bytes.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="baseStream"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="offset"/> or <paramref name="length"/> is negative, or the part goes past the end of the base stream.
        /// </exception>
        public SubStream(Stream baseStream, long offset, long length)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
            _offset = offset;
            _length = length;
            _position = 0;

            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
            }

            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative");
            }

            if (offset + length > baseStream.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Offset + length exceeds base stream length");
            }
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
        /// Gets the length of the part in bytes.
        /// </summary>
        public override long Length => _length;

        /// <summary>
        /// Gets or sets the position inside the part.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the value is negative or past the end of the part.
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
        /// Reads bytes from the current position of the part. A read stops at the end of the part.
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
        /// The number of bytes read, or 0 at the end of the part.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="buffer"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="offset"/> or <paramref name="count"/> is outside the buffer.
        /// </exception>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || offset >= buffer.Length)
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

            _baseStream.Position = _offset + _position;
            int bytesRead = _baseStream.Read(buffer, offset, toRead);

            _position += bytesRead;
            return bytesRead;
        }

        /// <summary>
        /// Moves the position inside the part.
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
        /// Thrown when the new position is outside the part.
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
            throw new NotSupportedException("Cannot set length of SubStream");
        }

        /// <summary>
        /// Writes bytes at the current position of the part.
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
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="buffer"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="offset"/> or <paramref name="count"/> is outside the buffer.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the write would go past the end of the part.
        /// </exception>
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || offset >= buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            long remaining = _length - _position;
            if (count > remaining)
            {
                throw new IOException("Write would exceed SubStream length");
            }

            _baseStream.Position = _offset + _position;
            _baseStream.Write(buffer, offset, count);

            _position += count;
        }

        /// <summary>
        /// Disposes this stream. The base stream is not disposed.
        /// </summary>
        /// <param name="disposing">
        /// True when called from <c>Dispose</c>, false when called from a finalizer.
        /// </param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Don't dispose the base stream - that's the caller's responsibility
            }
            base.Dispose(disposing);
        }
    }
}
