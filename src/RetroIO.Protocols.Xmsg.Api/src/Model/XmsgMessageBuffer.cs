//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// An XMSG message buffer: a reserved variable-length area holding user data, plus the
    /// descriptor state (size, current length, current displacement) that XMSG maintains for it.
    /// </summary>
    /// <remarks>
    /// <para><b>Model</b></para>
    /// Per the manual (section 1.2.4 and appendix A section 3.2) a buffer is reserved with XFGET,
    /// owned by exactly one task, and carries three distinct quantities that are easy to confuse:
    ///  - SIZE - the reserved capacity, fixed when the buffer is reserved.
    ///  - LENGTH - how many bytes of user data are currently meaningful; always less than or equal
    ///    to the size, and grown by writes that extend past the previous end.
    ///  - DISPLACEMENT - the current read/write cursor, used when a call passes -1 for DISP.
    /// The "header" that XFWHD and XFRHD talk about is NOT an XMSG header: it is the first six
    /// bytes of USER data, which programs conventionally use for their own protocol header.
    /// <para><b>Even-displacement rule</b></para>
    /// XMSG rounds every displacement UP to the next even byte before transferring. On a write
    /// that means an odd DISP has one added and a zero byte inserted, which is the "garbage byte"
    /// the manual warns about in section 1.2.4. Keep displacements even, or transfer an even
    /// number of bytes, and the problem disappears.
    /// <para><b>Whole-message-read flag</b></para>
    /// When a read consumes the last byte of the message, XMSG sets a flag and zeroes the
    /// displacement; the NEXT write or header-write then resets the length to zero first, so the
    /// buffer is naturally recycled for a reply. This class reproduces that behaviour exactly.
    /// </remarks>
    public sealed class XmsgMessageBuffer
    {
        /// <summary>
        /// The number of user bytes transferred by the header read and write functions.
        /// </summary>
        public const int HeaderSize = 6;

        private readonly byte[] _data;

        private int _length;
        private int _displacement;
        private bool _wholeMessageRead;

        /// <summary>
        /// Initialises a buffer of the requested size, as the reserve-buffer function would.
        /// </summary>
        /// <param name="size">
        /// The buffer capacity in bytes. Zero reserves a descriptor only, which is what privileged
        /// tasks do before associating physical memory with it; such a buffer may not be sent out
        /// of the system.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="size"/> is negative.
        /// </exception>
        public XmsgMessageBuffer(int size)
        {
            if (size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "A message buffer size cannot be negative.");
            }

            _data = new byte[size];
        }

        /// <summary>
        /// Gets the reserved capacity of the buffer in bytes.
        /// </summary>
        public int Size
        {
            get { return _data.Length; }
        }

        /// <summary>
        /// Gets the number of user-data bytes currently held.
        /// </summary>
        public int Length
        {
            get { return _length; }
        }

        /// <summary>
        /// Gets the current message displacement, that is the read/write cursor.
        /// </summary>
        public int Displacement
        {
            get { return _displacement; }
        }

        /// <summary>
        /// Gets a value indicating whether the last read consumed the final byte of the message.
        /// </summary>
        /// <remarks>
        /// While set, the next write or header write resets <see cref="Length"/> to zero.
        /// </remarks>
        public bool WholeMessageRead
        {
            get { return _wholeMessageRead; }
        }

        /// <summary>
        /// Gets a read-only view of the meaningful user data.
        /// </summary>
        /// <returns>
        /// The first <see cref="Length"/> bytes of the buffer.
        /// </returns>
        public ReadOnlySpan<byte> Data
        {
            get { return new ReadOnlySpan<byte>(_data, 0, _length); }
        }

        /// <summary>
        /// Writes user data into the buffer, reproducing the XFWRI semantics.
        /// </summary>
        /// <param name="source">
        /// The user data to copy in.
        /// </param>
        /// <param name="displacement">
        /// The displacement within the message in bytes, or -1 to append at the current
        /// displacement. An odd value is rounded up and a zero byte is left in the gap.
        /// </param>
        /// <param name="resetLength">
        /// The XFRES option: reset the message length to zero before the data is written.
        /// </param>
        /// <param name="bytesWritten">
        /// On return, the number of bytes actually written.
        /// </param>
        /// <returns>
        /// A successful status, or a failure status. The transferred byte count is reported
        /// through the out parameter, exactly as XMSG reports it in a separate register.
        /// </returns>
        /// <remarks>
        /// Fails with XEIDP when the effective displacement plus the length would exceed the
        /// buffer size, matching the manual's "if DISP+NBYTES is greater than the message size, an
        /// error return occurs".
        /// </remarks>
        public XmsgStatus Write(ReadOnlySpan<byte> source, int displacement, bool resetLength, out int bytesWritten)
        {
            bytesWritten = 0;

            // A pending whole-message-read always clears the length first, whether or not XFRES was asked for.
            if (_wholeMessageRead)
            {
                _wholeMessageRead = false;
                _length = 0;
            }

            if (resetLength)
            {
                _length = 0;
            }

            int target = displacement < 0 ? _displacement : displacement;

            // Odd displacements are rounded UP; the skipped byte stays zero (the manual's "garbage byte").
            if ((target & 1) != 0)
            {
                target++;
            }

            if (target < 0 || target + source.Length > _data.Length)
            {
                return XmsgStatus.Failure(XmsgError.XEIDP);
            }

            source.CopyTo(new Span<byte>(_data, target, source.Length));

            _displacement = target + source.Length;
            if (_displacement > _length)
            {
                _length = _displacement;
            }

            bytesWritten = source.Length;
            return XmsgStatus.Completed;
        }

        /// <summary>
        /// Reads user data out of the buffer, reproducing the XFREA semantics.
        /// </summary>
        /// <param name="destination">
        /// The caller's buffer to copy into; its length is the requested byte count.
        /// </param>
        /// <param name="displacement">
        /// The displacement within the message in bytes, or -1 to resume from the current
        /// displacement. An odd value is rounded up.
        /// </param>
        /// <param name="bytesRead">
        /// On return, the number of bytes actually read, which is less than requested when the
        /// message ends first.
        /// </param>
        /// <returns>
        /// A successful status, or a failure status. The transferred byte count is reported
        /// through the out parameter, exactly as XMSG reports it in a separate register.
        /// </returns>
        /// <remarks>
        /// When the read consumes the last byte of the message the displacement is reset to zero
        /// and <see cref="WholeMessageRead"/> is set. A zero-length read leaves the displacement
        /// untouched, exactly as the manual specifies.
        /// </remarks>
        public XmsgStatus Read(Span<byte> destination, int displacement, out int bytesRead)
        {
            bytesRead = 0;

            int target = displacement < 0 ? _displacement : displacement;

            if ((target & 1) != 0)
            {
                target++;
            }

            if (target < 0 || target > _length)
            {
                return XmsgStatus.Failure(XmsgError.XEIDP);
            }

            int available = _length - target;
            int count = destination.Length < available ? destination.Length : available;

            if (count > 0)
            {
                new ReadOnlySpan<byte>(_data, target, count).CopyTo(destination);
            }

            bytesRead = count;

            if (count == 0)
            {
                // A zero-byte transfer does not move the cursor.
                return XmsgStatus.Completed;
            }

            if (target + count >= _length)
            {
                _displacement = 0;
                _wholeMessageRead = true;
            }
            else
            {
                _displacement = target + count;
            }

            return XmsgStatus.Completed;
        }

        /// <summary>
        /// Writes the six-byte user header, reproducing the XFWHD semantics.
        /// </summary>
        /// <param name="header">
        /// Exactly six bytes, corresponding to the A, D and X registers in that order.
        /// </param>
        /// <returns>
        /// A success status, or a failure status when the buffer is smaller than six bytes.
        /// </returns>
        /// <remarks>
        /// Sets the displacement to six and, when this made the message longer, the length to six.
        /// </remarks>
        public XmsgStatus WriteHeader(ReadOnlySpan<byte> header)
        {
            if (header.Length != HeaderSize)
            {
                throw new ArgumentException("The user header is exactly six bytes.", nameof(header));
            }

            if (_data.Length < HeaderSize)
            {
                return XmsgStatus.Failure(XmsgError.XEILM);
            }

            if (_wholeMessageRead)
            {
                _wholeMessageRead = false;
                _length = 0;
            }

            header.CopyTo(new Span<byte>(_data, 0, HeaderSize));

            if (_length < HeaderSize)
            {
                _length = HeaderSize;
            }

            _displacement = HeaderSize;
            return XmsgStatus.Completed;
        }

        /// <summary>
        /// Reads the six-byte user header, reproducing the XFRHD semantics.
        /// </summary>
        /// <param name="header">
        /// A six-byte destination that receives the A, D and X register contents in that order.
        /// </param>
        /// <returns>
        /// A success status, or a failure status when the buffer is smaller than six bytes.
        /// </returns>
        /// <remarks>
        /// Sets the displacement to six. Unlike a normal read it never sets the
        /// whole-message-read flag.
        /// </remarks>
        public XmsgStatus ReadHeader(Span<byte> header)
        {
            if (header.Length != HeaderSize)
            {
                throw new ArgumentException("The user header is exactly six bytes.", nameof(header));
            }

            if (_data.Length < HeaderSize || _length < HeaderSize)
            {
                return XmsgStatus.Failure(XmsgError.XEILM);
            }

            new ReadOnlySpan<byte>(_data, 0, HeaderSize).CopyTo(header);
            _displacement = HeaderSize;
            return XmsgStatus.Completed;
        }

        /// <summary>
        /// Sets the current displacement without transferring data.
        /// </summary>
        /// <param name="displacement">
        /// The new displacement; rounded up to the next even byte.
        /// </param>
        /// <returns>
        /// A success status, or a failure status when the displacement is outside the buffer.
        /// </returns>
        public XmsgStatus Seek(int displacement)
        {
            int target = displacement;
            if ((target & 1) != 0)
            {
                target++;
            }

            if (target < 0 || target > _data.Length)
            {
                return XmsgStatus.Failure(XmsgError.XEIDP);
            }

            _displacement = target;
            return XmsgStatus.Completed;
        }

        /// <summary>
        /// Copies the meaningful user data into a new array.
        /// </summary>
        /// <returns>
        /// A copy of the first <see cref="Length"/> bytes.
        /// </returns>
        public byte[] ToArray()
        {
            byte[] copy = new byte[_length];
            new ReadOnlySpan<byte>(_data, 0, _length).CopyTo(copy);
            return copy;
        }
    }
}
