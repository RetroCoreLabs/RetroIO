//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;

namespace RetroIO.Protocols.X25.Switch.Management.Telnet
{
    /// <summary>
    /// The result of feeding one byte to a <see cref="TelnetLineEditor"/>.
    /// </summary>
    public readonly struct TelnetInput
    {
        /// <summary>
        /// Bytes to echo back to the client for this input (may be empty).
        /// </summary>
        public string Echo { get; }

        /// <summary>
        /// True when this byte completed a line.
        /// </summary>
        public bool LineComplete { get; }

        /// <summary>
        /// The completed line (valid only when <see cref="LineComplete"/> is true).
        /// </summary>
        public string Line { get; }

        /// <summary>
        /// Creates an input result.
        /// </summary>
        /// <param name="echo">
        /// Bytes to echo.
        /// </param>
        /// <param name="lineComplete">
        /// Whether a line was completed.
        /// </param>
        /// <param name="line">
        /// The completed line.
        /// </param>
        public TelnetInput(string echo, bool lineComplete, string line)
        {
            Echo = echo;
            LineComplete = lineComplete;
            Line = line;
        }
    }

    /// <summary>
    /// A character-at-a-time line editor for a raw telnet management console: it echoes printable input,
    /// handles backspace, discards telnet IAC sequences, and reports a completed line on CR or LF.
    /// </summary>
    /// <remarks>
    /// Behaviour, matching the management console protocol:
    ///  - Telnet commands are discarded (RFC 854): IAC (<c>0xFF</c>) and one command byte; WILL, WONT, DO and DONT
    ///    (<c>0xFB</c>-<c>0xFE</c>) take one option byte more; a subnegotiation runs from IAC SB to IAC SE; IAC IAC is
    ///    the data byte 255, which is not printable here and is dropped. The server's own offer (WILL ECHO, WILL
    ///    SUPPRESS-GO-AHEAD) is sent by <see cref="TelnetCliServer"/>.
    ///  - CR, LF, CR LF and CR NUL each complete the current line once and echo <c>\r\n</c>. RFC 854 makes CR LF the
    ///    end of a line and CR NUL a bare carriage return, and the Windows telnet client sends CR LF for Enter.
    ///    Until 2026-11-18 CR and LF each completed a line, so Enter at the login gave the user name and then an
    ///    empty password, and every login from that client failed.
    ///  - <c>0x7F</c> or <c>0x08</c> erases the last character (echoing <c>\b \b</c>) when the line is non-empty.
    ///  - Printable bytes (>= 32) are stored and echoed, up to a 511-character line.
    /// </remarks>
    public sealed class TelnetLineEditor
    {
        private const int MaxLine = 511;

        private readonly StringBuilder _line = new StringBuilder();
        // Where the editor is inside a telnet command (RFC 854).
        private enum CommandState
        {
            None,       // ordinary data
            Iac,        // after IAC: the command byte comes next
            Option,     // after IAC WILL/WONT/DO/DONT: the option byte comes next
            Sub,        // inside IAC SB ...: data until IAC SE
            SubIac,     // inside a subnegotiation, after IAC
        }

        private const byte Iac = 0xFF;
        private const byte Sb = 0xFA;
        private const byte Se = 0xF0;
        private const byte Will = 0xFB;
        private const byte Dont = 0xFE;

        private CommandState _command;

        // The last data byte was a CR that completed a line: an LF or NUL right after it belongs to the same line end.
        private bool _afterCr;

        /// <summary>
        /// Feeds one received byte and returns what to echo and whether a line completed.
        /// </summary>
        /// <param name="b">
        /// The received byte.
        /// </param>
        /// <returns>
        /// The echo bytes and any completed line.
        /// </returns>
        public TelnetInput Feed(byte b)
        {
            TelnetInput nothing = new TelnetInput(string.Empty, false, string.Empty);
            switch (_command)
            {
                case CommandState.Iac:
                    // IAC IAC is the data byte 255: not printable, dropped. SB starts a subnegotiation; WILL..DONT take
                    // an option byte; every other command is this one byte.
                    _command = b == Sb ? CommandState.Sub : (b >= Will && b <= Dont ? CommandState.Option : CommandState.None);
                    return nothing;
                case CommandState.Option:
                    _command = CommandState.None;
                    return nothing;
                case CommandState.Sub:
                    _command = b == Iac ? CommandState.SubIac : CommandState.Sub;
                    return nothing;
                case CommandState.SubIac:
                    _command = b == Se ? CommandState.None : CommandState.Sub;
                    return nothing;
            }

            if (b == Iac)
            {
                _command = CommandState.Iac;
                return nothing;
            }

            // The LF or NUL of a CR LF or CR NUL: the line was completed by the CR.
            bool secondHalf = _afterCr && (b == (byte)'\n' || b == 0);
            _afterCr = false;
            if (secondHalf)
            {
                return nothing;
            }

            if (b == (byte)'\r' || b == (byte)'\n')
            {
                _afterCr = b == (byte)'\r';
                string line = _line.ToString();
                _line.Clear();
                return new TelnetInput("\r\n", true, line);
            }

            if (b == 0x7F || b == 0x08)
            {
                if (_line.Length > 0)
                {
                    _line.Length--;
                    return new TelnetInput("\b \b", false, string.Empty);
                }

                return new TelnetInput(string.Empty, false, string.Empty);
            }

            if (b >= 32 && _line.Length < MaxLine)
            {
                char c = (char)b;
                _line.Append(c);
                return new TelnetInput(c.ToString(), false, string.Empty);
            }

            return new TelnetInput(string.Empty, false, string.Empty);
        }
    }
}
