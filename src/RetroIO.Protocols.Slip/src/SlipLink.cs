//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Slip
{
    /// <summary>
    /// One end of a SLIP line (RFC 1055): turns line bytes into IP packets and IP packets into line
    /// bytes.
    /// </summary>
    /// <remarks>
    /// The framing, from RFC 1055:
    ///  - A packet ends with END (C0h). A sender also sends END before a packet, to flush any line
    ///    noise collected since the last one; the receiver ignores the empty packet that makes.
    ///  - A C0h byte inside a packet is sent as ESC ESC_END (DBh DCh), a DBh byte as ESC ESC_ESC
    ///    (DBh DDh).
    /// <para>
    /// RFC 1055 lets a receiver put a byte that follows ESC but is not DCh or DDh into the packet as
    /// it is, and drop the bytes past its buffer. Here both are reported to the error handler and
    /// the packet is not delivered: a packet changed by the line is never passed on as good. The
    /// next END starts a fresh packet.
    /// </para>
    /// <para>
    /// There are no threads, no timers and no allocation per byte. Handlers are called directly from
    /// inside <see cref="ReceiveFromLine"/> and <see cref="SendPacket"/>.
    /// </para>
    /// </remarks>
    public sealed class SlipLink
    {
        private readonly byte[] _packet;
        private readonly byte[] _out;
        private int _length;
        private bool _escape;
        private SlipError _bad;
        private long _errors;

        /// <summary>
        /// END: the end of a packet (RFC 1055).
        /// </summary>
        public const byte End = 0xC0;

        /// <summary>
        /// ESC: the next byte is an escaped END or ESC (RFC 1055).
        /// </summary>
        public const byte Esc = 0xDB;

        /// <summary>
        /// ESC_END: after ESC, stands for a C0h data byte (RFC 1055).
        /// </summary>
        public const byte EscEnd = 0xDC;

        /// <summary>
        /// ESC_ESC: after ESC, stands for a DBh data byte (RFC 1055).
        /// </summary>
        public const byte EscEsc = 0xDD;

        /// <summary>
        /// The largest packet RFC 1055 says Berkeley UNIX SLIP takes, in bytes.
        /// </summary>
        public const int DefaultMaxPacket = 1006;

        /// <summary>
        /// Called with each whole packet received from the line.
        /// </summary>
        /// <param name="packet">
        /// The packet, valid only during the call.
        /// </param>
        public delegate void PacketReceived(ReadOnlySpan<byte> packet);

        /// <summary>
        /// Called with bytes to send down the line.
        /// </summary>
        /// <param name="bytes">
        /// The bytes, valid only during the call.
        /// </param>
        public delegate void LineOutput(ReadOnlySpan<byte> bytes);

        /// <summary>
        /// Called when the line delivers something that is not a good packet.
        /// </summary>
        /// <param name="error">
        /// What was wrong.
        /// </param>
        /// <param name="errorCount">
        /// How many errors this link has had so far, this one included.
        /// </param>
        public delegate void LinkError(SlipError error, long errorCount);

        /// <summary>
        /// Raised for each whole packet received from the line.
        /// </summary>
        public event PacketReceived? OnPacket;

        /// <summary>
        /// Raised with bytes to send down the line.
        /// </summary>
        public event LineOutput? OnLineOutput;

        /// <summary>
        /// Raised when a packet from the line is dropped because it is bad.
        /// </summary>
        public event LinkError? OnError;

        /// <summary>
        /// Creates a SLIP line end.
        /// </summary>
        /// <param name="maxPacket">
        /// The largest packet taken or sent, in bytes.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="maxPacket"/> is less than 1.
        /// </exception>
        public SlipLink(int maxPacket = DefaultMaxPacket)
        {
            if (maxPacket < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPacket), maxPacket, "a packet holds at least one byte");
            }

            MaxPacket = maxPacket;
            _packet = new byte[maxPacket];

            // Worst case: END, every byte escaped, END.
            _out = new byte[2 + 2 * maxPacket];
        }

        /// <summary>
        /// Gets the largest packet taken or sent, in bytes.
        /// </summary>
        public int MaxPacket { get; }

        /// <summary>
        /// Gets how many bad packets have been dropped.
        /// </summary>
        public long ErrorCount => _errors;

        /// <summary>
        /// Takes bytes received from the line.
        /// </summary>
        /// <param name="bytes">
        /// The bytes, in any split: a packet may arrive over many calls, or many packets in one.
        /// </param>
        public void ReceiveFromLine(ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (b == End)
                {
                    EndOfPacket();
                    continue;
                }

                if (_bad != SlipError.None)
                {
                    // Skip to the next END; the error is reported there.
                    continue;
                }

                if (_escape)
                {
                    _escape = false;
                    if (b == EscEnd)
                    {
                        b = End;
                    }
                    else if (b == EscEsc)
                    {
                        b = Esc;
                    }
                    else
                    {
                        _bad = SlipError.BadEscape;
                        continue;
                    }
                }
                else if (b == Esc)
                {
                    _escape = true;
                    continue;
                }

                if (_length == _packet.Length)
                {
                    _bad = SlipError.TooLong;
                    continue;
                }

                _packet[_length++] = b;
            }
        }

        /// <summary>
        /// Sends one IP packet down the line.
        /// </summary>
        /// <param name="packet">
        /// The packet.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="packet"/> is empty or longer than <see cref="MaxPacket"/>.
        /// </exception>
        public void SendPacket(ReadOnlySpan<byte> packet)
        {
            if (packet.Length == 0 || packet.Length > MaxPacket)
            {
                throw new ArgumentException("a packet is 1 to " + MaxPacket + " bytes, not " + packet.Length, nameof(packet));
            }

            int n = 0;
            _out[n++] = End;
            for (int i = 0; i < packet.Length; i++)
            {
                byte b = packet[i];
                if (b == End)
                {
                    _out[n++] = Esc;
                    _out[n++] = EscEnd;
                }
                else if (b == Esc)
                {
                    _out[n++] = Esc;
                    _out[n++] = EscEsc;
                }
                else
                {
                    _out[n++] = b;
                }
            }

            _out[n++] = End;
            OnLineOutput?.Invoke(_out.AsSpan(0, n));
        }

        private void EndOfPacket()
        {
            SlipError bad = _bad;

            // ESC then END: the escaped byte never came.
            if (bad == SlipError.None && _escape)
            {
                bad = SlipError.BadEscape;
            }

            int length = _length;
            _length = 0;
            _escape = false;
            _bad = SlipError.None;
            if (bad != SlipError.None)
            {
                _errors++;
                OnError?.Invoke(bad, _errors);
                return;
            }

            // Empty: the END a sender puts before each packet, or two ENDs in a row.
            if (length > 0)
            {
                OnPacket?.Invoke(_packet.AsSpan(0, length));
            }
        }
    }

    /// <summary>
    /// Why a packet from a SLIP line was dropped.
    /// </summary>
    public enum SlipError
    {
        /// <summary>
        /// No error.
        /// </summary>
        None,

        /// <summary>
        /// ESC was followed by a byte other than ESC_END or ESC_ESC, or by END.
        /// </summary>
        BadEscape,

        /// <summary>
        /// The packet was longer than <see cref="SlipLink.MaxPacket"/>.
        /// </summary>
        TooLong,
    }
}
