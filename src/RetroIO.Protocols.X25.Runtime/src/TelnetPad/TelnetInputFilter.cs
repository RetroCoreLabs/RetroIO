//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Runtime
{
    /// <summary>
    /// What <see cref="TelnetInputFilter.Feed"/> found in one received byte.
    /// </summary>
    internal readonly struct TelnetFilterResult
    {
        /// <summary>
        /// True when <see cref="Data"/> is a terminal character to give to the PAD.
        /// </summary>
        public bool HasData { get; }

        /// <summary>
        /// The terminal character.
        /// </summary>
        public byte Data { get; }

        /// <summary>
        /// True when the server must answer with the telnet command <c>IAC ReplyVerb ReplyOption</c>.
        /// </summary>
        public bool HasReply { get; }

        /// <summary>
        /// The reply verb (WILL, WONT, DO or DONT).
        /// </summary>
        public byte ReplyVerb { get; }

        /// <summary>
        /// The option the reply is about.
        /// </summary>
        public byte ReplyOption { get; }

        /// <summary>
        /// Creates a result.
        /// </summary>
        /// <param name="hasData">True when a terminal character was found.</param>
        /// <param name="data">The terminal character.</param>
        /// <param name="hasReply">True when a reply must be sent.</param>
        /// <param name="replyVerb">The reply verb.</param>
        /// <param name="replyOption">The reply option.</param>
        public TelnetFilterResult(bool hasData, byte data, bool hasReply, byte replyVerb, byte replyOption)
        {
            HasData = hasData;
            Data = data;
            HasReply = hasReply;
            ReplyVerb = replyVerb;
            ReplyOption = replyOption;
        }
    }

    /// <summary>
    /// Splits the bytes a telnet client sends into terminal characters and telnet commands (RFC 854).
    /// </summary>
    /// <remarks>
    /// The server offers WILL ECHO, WILL SUPPRESS-GO-AHEAD and DO SUPPRESS-GO-AHEAD when a client connects,
    /// so the client sends every key at once and does not echo: the PAD does the echo (X.3 parameter 2).
    /// The filter answers a request for any other option with a refusal and never answers a refusal, so
    /// two ends cannot loop. <c>IAC IAC</c> is one 0xFF character. CR followed by NUL or by LF is one CR
    /// (X.28 4.15: a terminal sends CR alone; telnet sends CR NUL for the Enter key, and some clients send
    /// CR LF). <c>IAC IP</c> and <c>IAC BRK</c> reach the PAD as Ctrl+C, the same character the Desktop PAD
    /// window passes on for that key. Sub-negotiations are skipped.
    /// </remarks>
    internal sealed class TelnetInputFilter
    {
        public const byte Iac = 255;
        public const byte Dont = 254;
        public const byte Do = 253;
        public const byte Wont = 252;
        public const byte Will = 251;
        public const byte Sb = 250;
        public const byte Se = 240;
        public const byte Brk = 243;
        public const byte Ip = 244;
        public const byte OptionEcho = 1;
        public const byte OptionSuppressGoAhead = 3;

        private enum State
        {
            Data,
            AfterIac,
            AfterWill,
            AfterWont,
            AfterDo,
            AfterDont,
            Subnegotiation,
            SubnegotiationIac
        }

        private State _state = State.Data;
        private bool _afterCr;

        /// <summary>
        /// Feeds one received byte.
        /// </summary>
        /// <param name="b">
        /// The byte.
        /// </param>
        /// <returns>
        /// A terminal character, a reply, both, or neither.
        /// </returns>
        public TelnetFilterResult Feed(byte b)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == Iac)
                    {
                        _state = State.AfterIac;
                        _afterCr = false;
                        return default;
                    }

                    if (_afterCr)
                    {
                        _afterCr = false;
                        // CR NUL and CR LF are both "Enter": the CR was already given to the PAD.
                        if (b == 0 || b == (byte)'\n')
                        {
                            return default;
                        }
                    }

                    _afterCr = b == (byte)'\r';
                    return new TelnetFilterResult(true, b, false, 0, 0);

                case State.AfterIac:
                    switch (b)
                    {
                        case Iac:
                            _state = State.Data;
                            return new TelnetFilterResult(true, 0xFF, false, 0, 0);
                        case Will:
                            _state = State.AfterWill;
                            return default;
                        case Wont:
                            _state = State.AfterWont;
                            return default;
                        case Do:
                            _state = State.AfterDo;
                            return default;
                        case Dont:
                            _state = State.AfterDont;
                            return default;
                        case Sb:
                            _state = State.Subnegotiation;
                            return default;
                        case Ip:
                        case Brk:
                            _state = State.Data;
                            return new TelnetFilterResult(true, 0x03, false, 0, 0);
                        default:
                            // NOP, AYT, GA and the rest need no action here.
                            _state = State.Data;
                            return default;
                    }

                case State.AfterWill:
                    _state = State.Data;
                    // WILL SGA answers our DO SGA. Anything else the client offers is refused.
                    return b == OptionSuppressGoAhead ? default : new TelnetFilterResult(false, 0, true, Dont, b);

                case State.AfterDo:
                    _state = State.Data;
                    // DO ECHO and DO SGA answer what we offered. Anything else is refused.
                    return (b == OptionEcho || b == OptionSuppressGoAhead) ? default : new TelnetFilterResult(false, 0, true, Wont, b);

                case State.AfterWont:
                case State.AfterDont:
                    // A refusal needs no answer (answering one is what makes two ends loop).
                    _state = State.Data;
                    return default;

                case State.Subnegotiation:
                    if (b == Iac)
                    {
                        _state = State.SubnegotiationIac;
                    }

                    return default;

                case State.SubnegotiationIac:
                    _state = b == Se ? State.Data : State.Subnegotiation;
                    return default;
            }

            return default;
        }
    }
}
