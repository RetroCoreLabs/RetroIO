//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// Where the connection to the host is, as the status line shows it.
    /// </summary>
    public enum StatusLineConnection
    {
        /// <summary>
        /// No connection.
        /// </summary>
        NotConnected,

        /// <summary>
        /// The TCP connection is being made.
        /// </summary>
        Connecting,

        /// <summary>
        /// Connected, with the 3270 data stream running.
        /// </summary>
        Connected,
    }

    /// <summary>
    /// Builds line 25, the operator information area under the screen, as plain text in the
    /// layout wc3270 draws it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 3278 operator's guide (GA27-2890-4 p. 2-19 to 2-23) gives what each indicator means and
    /// the order of the five groups, but not the column each one is in. Ronny named x3270 as the
    /// oracle for line 25 (23 November 2026); its text front end draws the line in
    /// wc3270\screen.c, draw_oia, and that is followed here column for column, with M the number
    /// of columns:
    /// </para>
    ///  - column 0: "4", shown in reverse video (the control unit is ready).
    ///  - column 1: "A", underlined (online, the A set of rules); "B" under TN3270E.
    ///  - column 2: a blank in reverse video (a solid box: connected to the application) when in
    ///    3270 mode, otherwise "?".
    ///  - column 7, 35 wide: the message, "X Wait", "X SYSTEM" and so on (status_connect,
    ///    status_twait, status_syswait, status_oerr).
    ///  - column M-36 + 5: "I" in insert mode.
    ///  - column M-26: the LU name.
    ///  - column M-8: the cursor as row/column from 1, "%03d/%03d".
    /// <para>
    /// Compose, typeahead, reverse input, printer, TLS, screen trace, script and command timing
    /// (columns M-37 to M-29 and M-15) are x3270 features RetroIO does not have; they stay blank,
    /// as wc3270 leaves them when they are off.
    /// </para>
    /// <para>
    /// Mandatory fill and mandatory entry lock the keyboard (GA23-0059-4 p. 4-21, 4-22), but
    /// neither x3270 (which does not have those fields) nor GA23-0059-4, GA23-0061-2 or
    /// GA27-2890-4 names the indicator shown for them. Only the documented Do Not Enter "X" is
    /// shown for those two; the missing reason is a known gap, not a guess.
    /// </para>
    /// </remarks>
    public static class StatusLine3270
    {
        /// <summary>
        /// The column of the reverse-video "4".
        /// </summary>
        public const int ReadyColumn = 0;

        /// <summary>
        /// The column of the underlined "A".
        /// </summary>
        public const int OnlineColumn = 1;

        /// <summary>
        /// The column of the reverse-video box ("?" when not in 3270 mode).
        /// </summary>
        public const int BoxColumn = 2;

        /// <summary>
        /// The first column of the message.
        /// </summary>
        public const int MessageColumn = 7;

        /// <summary>
        /// How many characters of the message are shown (wc3270 prints it with "%-35.35s").
        /// </summary>
        public const int MessageWidth = 35;

        /// <summary>
        /// Writes line 25 into <paramref name="line"/>, which must be as wide as the screen.
        /// </summary>
        /// <param name="display">
        /// The screen whose keyboard lock, insert mode and cursor are shown.
        /// </param>
        /// <param name="connection">
        /// Where the connection to the host is.
        /// </param>
        /// <param name="luName">
        /// The LU name from TN3270E, or null when there is none; at most 8 characters are shown.
        /// </param>
        /// <param name="line">
        /// The characters of the line, one per column; every column is written.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="line"/> is narrower than the layout needs (40 columns).
        /// </exception>
        public static void Build(Display3270 display, StatusLineConnection connection, string? luName, Span<char> line)
        {
            Build(display, connection, luName, tn3270e: false, line);
        }

        /// <summary>
        /// Writes line 25 for a display, saying whether the connection runs TN3270E.
        /// </summary>
        /// <remarks>
        /// The same as the other Build, except that column 1 shows "B" instead of "A" while
        /// TN3270E is in force (wc3270\screen.c, draw_oia: addch(IN_E? 'B': 'A')).
        /// </remarks>
        /// <param name="display">
        /// The screen whose keyboard lock, insert mode and cursor are shown.
        /// </param>
        /// <param name="connection">
        /// Where the connection to the host is.
        /// </param>
        /// <param name="luName">
        /// The LU name from TN3270E, or null when there is none; at most 8 characters are shown.
        /// </param>
        /// <param name="tn3270e">
        /// True while the connection runs TN3270E.
        /// </param>
        /// <param name="line">
        /// The characters of the line, one per column; every column is written.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="line"/> is narrower than the layout needs (40 columns).
        /// </exception>
        public static void Build(Display3270 display, StatusLineConnection connection, string? luName, bool tn3270e, Span<char> line)
        {
            ArgumentNullException.ThrowIfNull(display);
            int m = line.Length - 1; // wc3270's rmargin: the last column
            if (line.Length < 40)
            {
                throw new ArgumentException("line 25 needs at least 40 columns", nameof(line));
            }

            line.Fill(' ');
            bool in3270 = connection == StatusLineConnection.Connected;

            line[ReadyColumn] = '4';
            line[OnlineColumn] = tn3270e ? 'B' : 'A';
            line[BoxColumn] = in3270 ? ' ' : '?';

            Write(line, MessageColumn, Message(display, connection), MessageWidth);

            if (in3270 && display.InsertMode)
            {
                line[m - 36 + 5] = 'I';
            }

            if (luName != null)
            {
                Write(line, m - 25, luName, 8);
            }

            if (in3270)
            {
                int columns = display.Columns;
                int row = (display.CursorAddress / columns) + 1;
                int column = (display.CursorAddress % columns) + 1;
                WriteNumber(line, m - 7, row);
                line[m - 4] = '/';
                WriteNumber(line, m - 3, column);
            }
        }

        /// <summary>
        /// The message for the connection and keyboard state, empty when there is none.
        /// </summary>
        /// <param name="display">
        /// The screen whose keyboard lock is shown.
        /// </param>
        /// <param name="connection">
        /// Where the connection to the host is.
        /// </param>
        /// <returns>
        /// The text wc3270 shows in the message area for that state.
        /// </returns>
        public static string Message(Display3270 display, StatusLineConnection connection)
        {
            ArgumentNullException.ThrowIfNull(display);
            switch (connection)
            {
                case StatusLineConnection.NotConnected:
                    return "X Not Connected";
                case StatusLineConnection.Connecting:
                    return "X [TCP]";
            }

            switch (display.Lock)
            {
                case KeyboardLock.WaitingForHost:
                    return "X Wait";
                case KeyboardLock.QueryReplySent:
                    // x3270 Common\vstatus.c shows its enter-inhibit lock after a query reply as
                    // "X Wait"; the manual's indicator for it is TWAIT (p. 10-5), the same one.
                    return "X Wait";
                case KeyboardLock.SystemLock:
                    return "X SYSTEM";
                case KeyboardLock.OperatorError:
                    return "X Protected";
                case KeyboardLock.TooMuchData:
                    return "X Overflow";
                case KeyboardLock.MandatoryFill:
                case KeyboardLock.MandatoryEntry:
                    return "X";
                case KeyboardLock.MinusFunction:
                    // x3270 Common\vstatus.c, vstatus_minus.
                    return "X -f";
                default:
                    return string.Empty;
            }
        }

        private static void Write(Span<char> line, int column, string text, int width)
        {
            for (int i = 0; i < text.Length && i < width && column + i < line.Length; i++)
            {
                line[column + i] = text[i];
            }
        }

        // Three digits with leading zeros, as "%03d" prints a row or column up to 999.
        private static void WriteNumber(Span<char> line, int column, int value)
        {
            line[column] = (char)('0' + ((value / 100) % 10));
            line[column + 1] = (char)('0' + ((value / 10) % 10));
            line[column + 2] = (char)('0' + (value % 10));
        }
    }
}
