//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Telnet
{
    /// <summary>
    /// Splits a received telnet byte stream into data, commands, option negotiation,
    /// subnegotiation and end-of-record marks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reader keeps its state between calls to <see cref="Feed"/>, so a command cut in two by
    /// the network (IAC at the end of one read, the verb at the start of the next) is handled.
    /// The TN3270 capture from the TK5 host on 22 November 2026 showed the host's commands
    /// split differently on every run (docs/spec/IBM3270/TN3270-NEGOTIATION.md in RetroTerm),
    /// so nothing here may assume one command per read.
    /// </para>
    /// <para>
    /// It does NOT do the NVT carriage-return rules (CR NUL, CR LF). Those apply only to text in
    /// non-binary mode, which is the caller's business; in BINARY mode (RFC 856) CR is plain data.
    /// </para>
    /// <para>
    /// One case RFC 854 and RFC 855 (as read) do not settle: inside a subnegotiation, IAC followed
    /// by a byte other than SE or IAC. This reader then ends the subnegotiation there (it is
    /// delivered as it stands) and treats the byte as the start of a new command. That choice is
    /// ours and is stated in the README's open questions.
    /// </para>
    /// </remarks>
    public sealed class TelnetReader
    {
        private enum State
        {
            Data,
            Iac,
            Option,
            SubOption,
            SubData,
            SubIac,
        }

        private readonly ITelnetReaderHandler _handler;

        // Data waiting to be handed over. Collected so a run of data arrives in one call even when
        // IAC IAC pairs sit inside it, and flushed before every other event to keep the order.
        private byte[] _data = new byte[256];
        private int _dataLength;

        // Subnegotiation parameters, without the option byte.
        private byte[] _sub = new byte[64];
        private int _subLength;
        private byte _subOption;

        private State _state = State.Data;
        private byte _verb;

        /// <summary>
        /// Creates a reader that reports to the given handler.
        /// </summary>
        /// <param name="handler">
        /// Receives everything found in the stream.
        /// </param>
        public TelnetReader(ITelnetReaderHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _handler = handler;
        }

        /// <summary>
        /// Processes the next received bytes. Any amount may be passed, down to one byte.
        /// </summary>
        /// <param name="bytes">
        /// The bytes just received.
        /// </param>
        public void Feed(ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                switch (_state)
                {
                    case State.Data:
                        if (b == TelnetCommand.Iac)
                        {
                            _state = State.Iac;
                        }
                        else
                        {
                            AppendData(b);
                        }

                        break;

                    case State.Iac:
                        HandleCommandByte(b);
                        break;

                    case State.Option:
                        FlushData();
                        _state = State.Data;
                        _handler.OnNegotiation(_verb, b);
                        break;

                    case State.SubOption:
                        _subOption = b;
                        _subLength = 0;
                        _state = State.SubData;
                        break;

                    case State.SubData:
                        if (b == TelnetCommand.Iac)
                        {
                            _state = State.SubIac;
                        }
                        else
                        {
                            AppendSub(b);
                        }

                        break;

                    case State.SubIac:
                        if (b == TelnetCommand.Iac)
                        {
                            // IAC IAC inside a subnegotiation is one 255 parameter byte (RFC 855).
                            AppendSub(b);
                            _state = State.SubData;
                        }
                        else if (b == TelnetCommand.SubnegotiationEnd)
                        {
                            _state = State.Data;
                            _handler.OnSubnegotiation(_subOption, _sub.AsSpan(0, _subLength));
                        }
                        else
                        {
                            // Not settled by the RFCs as read - see the class remarks.
                            _handler.OnSubnegotiation(_subOption, _sub.AsSpan(0, _subLength));
                            HandleCommandByte(b);
                        }

                        break;
                }
            }

            // Hand over the data now; holding it back until the next read would delay a screen.
            FlushData();
        }

        private void HandleCommandByte(byte b)
        {
            switch (b)
            {
                case TelnetCommand.Iac:
                    // IAC IAC is one data byte of 255 (RFC 854).
                    AppendData(b);
                    _state = State.Data;
                    break;

                case TelnetCommand.Will:
                case TelnetCommand.Wont:
                case TelnetCommand.Do:
                case TelnetCommand.Dont:
                    _verb = b;
                    _state = State.Option;
                    break;

                case TelnetCommand.SubnegotiationBegin:
                    FlushData();
                    _state = State.SubOption;
                    break;

                case TelnetCommand.EndOfRecord:
                    FlushData();
                    _state = State.Data;
                    _handler.OnEndOfRecord();
                    break;

                default:
                    FlushData();
                    _state = State.Data;
                    _handler.OnCommand(b);
                    break;
            }
        }

        private void AppendData(byte b)
        {
            if (_dataLength == _data.Length)
            {
                Array.Resize(ref _data, _data.Length * 2);
            }

            _data[_dataLength++] = b;
        }

        private void AppendSub(byte b)
        {
            if (_subLength == _sub.Length)
            {
                Array.Resize(ref _sub, _sub.Length * 2);
            }

            _sub[_subLength++] = b;
        }

        private void FlushData()
        {
            if (_dataLength > 0)
            {
                int length = _dataLength;
                _dataLength = 0;
                _handler.OnData(_data.AsSpan(0, length));
            }
        }
    }
}
