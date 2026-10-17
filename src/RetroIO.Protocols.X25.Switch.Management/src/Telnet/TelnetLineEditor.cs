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
    ///  - A byte <c>0xFF</c> (IAC) causes the next two bytes to be discarded; no option negotiation is sent.
    ///  - CR or LF completes the current line and echoes <c>\r\n</c>; each of CR and LF completes a line, so
    ///    a CR-LF pair completes the typed line and then an empty line.
    ///  - <c>0x7F</c> or <c>0x08</c> erases the last character (echoing <c>\b \b</c>) when the line is non-empty.
    ///  - Printable bytes (>= 32) are stored and echoed, up to a 511-character line.
    /// </remarks>
    public sealed class TelnetLineEditor
    {
        private const int MaxLine = 511;

        private readonly StringBuilder _line = new StringBuilder();
        private int _iacSkip;

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
            if (_iacSkip > 0)
            {
                _iacSkip--;
                return new TelnetInput(string.Empty, false, string.Empty);
            }

            if (b == 0xFF)
            {
                _iacSkip = 2;
                return new TelnetInput(string.Empty, false, string.Empty);
            }

            if (b == (byte)'\r' || b == (byte)'\n')
            {
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
