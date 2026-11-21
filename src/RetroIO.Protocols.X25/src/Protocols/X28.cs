//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Protocols
{
    /// <summary>
    /// X.28 DTE/PAD Interface protocol implementation according to ITU-T X.28 standard
    /// Handles the interface between start-stop mode DTE (terminal) and PAD facility
    /// Manages command mode, data transfer mode, and character processing
    /// </summary>
    public class X28
    {
        private readonly ILogger? _logger;
        private readonly X3 _x3Parameters;
        private readonly StringBuilder _commandBuffer = new StringBuilder();
        private readonly StringBuilder _inputBuffer = new StringBuilder();
        
        /// <summary>
        /// PAD operating modes
        /// </summary>
        public enum PadMode
        {
            /// <summary>
            /// Command mode: the PAD accepts PAD commands.
            /// </summary>
            Command,        // Command mode - accepting PAD commands
            /// <summary>
            /// Data transfer mode: data passes through unchanged.
            /// </summary>
            DataTransfer,   // Data transfer mode - transparent data flow
            /// <summary>
            /// Escape mode: a temporary return to command mode.
            /// </summary>
            Escape         // Escape mode - temporary return to command mode
        }

        /// <summary>
        /// Data forwarding trigger conditions
        /// </summary>
        [Flags]
        public enum ForwardingCondition
        {
            /// <summary>
            /// No forwarding condition.
            /// </summary>
            None = 0,
            /// <summary>
            /// Data is forwarded when the idle timer expires.
            /// </summary>
            Timer = 1,                      // Idle timer expiry
            /// <summary>
            /// Data is forwarded when a carriage return is received.
            /// </summary>
            CarriageReturn = 2,             // CR character received
            /// <summary>
            /// Data is forwarded when another control character is received.
            /// </summary>
            ControlCharacter = 4,           // Other control characters
            /// <summary>
            /// Data is forwarded when the input buffer is full.
            /// </summary>
            BufferFull = 8,                 // Input buffer full
            /// <summary>
            /// Data is forwarded on an explicit forward request.
            /// </summary>
            ManualForward = 16,             // Explicit forward request
            /// <summary>
            /// Every character is forwarded immediately.
            /// </summary>
            CharacterByCharacter = 32,      // Every character forwarded immediately
            /// <summary>
            /// Data is forwarded when a specific printable character is received.
            /// </summary>
            SpecificCharacter = 64,         // Specific printable character
            /// <summary>
            /// Data is forwarded when an alphanumeric character is received (parameter 3 = 127).
            /// </summary>
            AlphanumericCharacter = 128     // Alphanumeric character (parameter 3 = 127)
        }

        /// <summary>
        /// Standard PAD commands according to X.28
        /// </summary>
        public static class Commands
        {
            /// <summary>
            /// Command text "C": initiate call.
            /// </summary>
            public const string CALL = "C";         // Initiate call
            /// <summary>
            /// Command text "CLR": clear call.
            /// </summary>
            public const string CLEAR = "CLR";      // Clear call
            /// <summary>
            /// Command text "STAT": show status.
            /// </summary>
            public const string STATUS = "STAT";    // Show status
            /// <summary>
            /// Command text "INT": send interrupt.
            /// </summary>
            public const string INTERRUPT = "INT";  // Send interrupt
            /// <summary>
            /// Command text "RESET": reset virtual circuit.
            /// </summary>
            public const string RESET = "RESET";    // Reset virtual circuit
            /// <summary>
            /// Command text "SET": set x.3 parameters.
            /// </summary>
            public const string SET = "SET";        // Set X.3 parameters
            /// <summary>
            /// Command text "PAR": read x.3 parameters.
            /// </summary>
            public const string PARAMETER = "PAR";  // Read X.3 parameters
            /// <summary>
            /// Command text "PROF": set parameter profile.
            /// </summary>
            public const string PROFILE = "PROF";   // Set parameter profile
            /// <summary>
            /// Command text "DATA": return to data transfer mode.
            /// </summary>
            public const string DATA = "DATA";      // Return to data transfer mode
            /// <summary>
            /// Command text "HELP": list the commands.
            /// </summary>
            public const string HELP = "HELP";      // List the commands
            /// <summary>
            /// Command text "EXIT": leave the PAD program. Not an X.28 command (a real PAD has no program to leave); the
            /// PAD clears any call and asks the program that hosts it to end (<c>PadClient.ExitRequested</c>).
            /// </summary>
            public const string EXIT = "EXIT";      // Leave the PAD program
        }

        // Events for terminal interface - PROPER ARCHITECTURE
        /// <summary>
        /// Raised when a PAD command line has been parsed.
        /// </summary>
        public event EventHandler<X28CommandEventArgs>? CommandReceived;
        /// <summary>
        /// Raised when buffered data is forwarded.
        /// </summary>
        public event EventHandler<X28DataEventArgs>? DataForwarded;
        /// <summary>
        /// Raised when the PAD mode changes.
        /// </summary>
        public event EventHandler<X28ModeChangedEventArgs>? ModeChanged;
        /// <summary>
        /// Raised once for each character that ProcessCharacter processed successfully, after the work
        /// of that character (echo, data forwarding, command handling) is done.
        /// Not raised for a character that was not processed.
        /// The Response of the event arguments is always null: single characters have no response text of their own.
        /// </summary>
        public event EventHandler<X28CharacterEventArgs>? CharacterProcessed;
        
        // NEW PROPER EVENTS - Replace string returns
        /// <summary>
        /// Raised when a character is to be echoed to the terminal.
        /// </summary>
        public event EventHandler<CharacterEchoEventArgs>? CharacterEcho;
        /// <summary>
        /// Raised when a command produces response text.
        /// </summary>
        public event EventHandler<CommandResponseEventArgs>? CommandResponse;
        // There is no separate status event. PAD status text (COM, CLR, RESET, INT, CONNECTED and the
        // like) is sent through CommandResponse, which PadClient forwards to the UI as received data.
        // A StatusUpdate event was declared in the 2024 move from string returns to events, but it
        // was never raised and nothing listened to it; it was removed with the owner's agreement.
        /// <summary>
        /// Raised when an error message is produced.
        /// </summary>
        public event EventHandler<ErrorMessageEventArgs>? ErrorMessage;

        private PadMode _currentMode = PadMode.Command;

        // A call is up: set when the call is connected, cleared when it is cleared. The escape to the prompt
        // during a call leaves it set (the call stays), which is what STAT reports as ENGAGED.
        private bool _callUp;
        private DateTime _lastCharacterTime = DateTime.Now;
        private System.Threading.Timer? _idleTimer;

        /// <summary>
        /// Creates the X.28 interface in command mode.
        /// </summary>
        /// <param name="x3Parameters">
        /// The X.3 parameters that control the PAD behavior.
        /// </param>
        /// <param name="logger">
        /// Optional diagnostics logger; may be null.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="x3Parameters"/> is null.
        /// </exception>
        public X28(X3 x3Parameters, ILogger? logger = null)
        {
            _x3Parameters = x3Parameters ?? throw new ArgumentNullException(nameof(x3Parameters));
            _logger = logger;
            
            _logger?.LogDebug("X.28: Terminal interface initialized in command mode");
        }

        /// <summary>
        /// Current PAD operating mode
        /// </summary>
        public PadMode CurrentMode => _currentMode;

        /// <summary>
        /// Switch to data transfer mode (called when X.25 connection is established)
        /// </summary>
        public void SwitchToDataTransferMode()
        {
            _logger?.LogDebug("🎯 X.28: SwitchToDataTransferMode() called!");
            _callUp = true;
            SwitchMode(PadMode.DataTransfer);
        }

        /// <summary>
        /// Switch to command mode (called when X.25 connection is cleared)
        /// </summary>
        public void SwitchToCommandMode()
        {
            _callUp = false;
            SwitchMode(PadMode.Command);
        }

        /// <summary>
        /// Process incoming character from terminal
        /// </summary>
        /// <param name="character">
        /// Character received from terminal
        /// </param>
        /// <returns>
        /// True if character was processed successfully
        /// </returns>
        public bool ProcessCharacter(char character)
        {
            _lastCharacterTime = DateTime.Now;
            
            try
            {
                bool processed = _currentMode switch
                {
                    PadMode.Command => ProcessCommandModeCharacter(character),
                    PadMode.DataTransfer => ProcessDataTransferCharacter(character),
                    PadMode.Escape => ProcessEscapeModeCharacter(character),
                    _ => false
                };

                // Tell the listeners after the work of the character is done (echo, forwarding, command).
                // A character the PAD did not process raises nothing. The PAD has no response text of its
                // own for a single character (echo and command replies have their own events), so Response is null.
                if (processed)
                {
                    CharacterProcessed?.Invoke(this, new X28CharacterEventArgs(character, null));
                }

                return processed;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "X.28: Error processing character {Char} in mode {Mode}", 
                    character, _currentMode);
                ErrorMessage?.Invoke(this, new ErrorMessageEventArgs($"Error processing character: {ex.Message}"));
                return false;
            }
        }

        /// <summary>
        /// Builds the X.28 clear indication service signal for an X.25 clear cause, for example
        /// <c>CLR OCC C:1 D:0</c>.
        /// </summary>
        /// <param name="cause">
        /// The cause octet of the X.25 Clear Indication packet.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic octet of the packet (decimal in the signal).
        /// </param>
        /// <returns>
        /// The signal text without line ends.
        /// </returns>
        /// <remarks>
        /// Format: X.28 (12/97) 3.5.17.1: "CLR" SP, then the cause mnemonic, then " C:" and the decimal
        /// cause, then " D:" and the decimal diagnostic. The mnemonics are the ones in X.28 Table 6. Table 6
        /// has no column of X.25 cause numbers (it points to X.96, which was not available), so the
        /// pairing below is made by the wording of the X.25 cause table (Table 5-7, X.25 10/96) and
        /// the Table 6 text, except DTE and ERR, which Annex A.2 ties to a cause. A cause that has no
        /// Table 6 mnemonic with the same wording (21 ROA out of order) is printed without a mnemonic,
        /// as the format makes the cause field optional, rather than with a guessed one.
        /// </remarks>
        public static string ClearSignal(byte cause, byte diagnostic) => ClearSignal(cause, diagnostic, false);

        /// <summary>
        /// The clear indication PAD service signal, with the extended dialogue mode text when
        /// <paramref name="extended"/> (X.28 (12/97) 3.5.17.1: <c>&lt;text&gt; ::= SP - SP</c> and the Table 6 text
        /// for the cause), e.g. <c>CLR OCC C:1 D:0 - Call cleared, number busy</c>. A cause with no Table 6 mnemonic
        /// gets no text either.
        /// </summary>
        /// <param name="cause">Cause octet of the clear packet.</param>
        /// <param name="diagnostic">Diagnostic octet of the clear packet.</param>
        /// <param name="extended">The PAD is in extended dialogue mode (<see cref="IsExtendedDialogue"/>).</param>
        /// <returns>The service signal, without line ends.</returns>
        public static string ClearSignal(byte cause, byte diagnostic, bool extended)
        {
            string signal = StandardClearSignal(cause, diagnostic, out string? mnemonic);
            string? text = extended && mnemonic != null ? ExtendedClearText(mnemonic) : null;
            return text == null ? signal : signal + " - " + text;
        }

        /// <summary>
        /// The reset PAD service signal (X.28 (12/97) 3.5.7): <c>RESET</c>, the cause from Table 5, the decimal
        /// diagnostic, and in extended dialogue mode <c> - </c> and the Table 5 text, e.g. <c>RESET NC 0 - Reset due to
        /// temporary network problem, data may be lost</c>. The cause is paired with the X.25 resetting cause of
        /// Table 5-8 (X.25 (10/96) page 81) by wording: DTE originated (0, or bit 8 set) is DTE, local procedure
        /// error (0x05) ERR, network congestion (0x07) NC, remote procedure error (0x03) RPE. The other Table 5-8
        /// causes (out of order, remote DTE operational, network operational, incompatible destination, network out
        /// of order) have no Table 5 cause: then only <c>RESET</c> and the diagnostic, with no text.
        /// </summary>
        /// <param name="cause">The resetting cause octet.</param>
        /// <param name="diagnostic">The diagnostic octet.</param>
        /// <param name="extended">The PAD is in extended dialogue mode.</param>
        /// <returns>The service signal, without line ends.</returns>
        public static string ResetSignal(byte cause, byte diagnostic, bool extended)
        {
            string? mnemonic;
            string? text;
            if (cause == 0x00 || (cause & 0x80) != 0)
            {
                mnemonic = "DTE";
                text = "Reset by remote device, data may be lost";
            }
            else
            {
                switch (cause)
                {
                    case 0x05: mnemonic = "ERR"; text = "Reset by network, local procedure error, data may be lost"; break;
                    case 0x07: mnemonic = "NC"; text = "Reset due to temporary network problem, data may be lost"; break;
                    case 0x03: mnemonic = "RPE"; text = "Reset by network, remote procedure error, data may be lost"; break;
                    default: mnemonic = null; text = null; break;
                }
            }

            string signal = mnemonic == null ? "RESET " + diagnostic : "RESET " + mnemonic + " " + diagnostic;
            return extended && text != null ? signal + " - " + text : signal;
        }

        /// <summary>
        /// The clear indication as BT PSS printed it (RetroX25 docs/research-os.md 1.2, a recorded 1987 session,
        /// verbatim: <c>CLR PAD (00) 00:00:00:32 13 17</c>): CLR, the X.28 Table 6 cause, the diagnostic in brackets as
        /// two decimal digits, the call time as days:hours:minutes:seconds, and two packet counts.
        /// </summary>
        /// <remarks>
        /// NOT VERIFIED, and ours: which count is which. The sources give two numbers and no names; this writes the
        /// packets the PAD sent first, then the ones it received. Also ours: a cause with no Table 6 mnemonic is written
        /// as C:cause in its place, as the X.28 signal does.
        /// </remarks>
        /// <param name="mnemonic">The Table 6 cause (PAD for a clear the host invited), or null.</param>
        /// <param name="cause">The cause octet, written when there is no mnemonic.</param>
        /// <param name="diagnostic">The diagnostic octet.</param>
        /// <param name="callTime">How long the call was up.</param>
        /// <param name="sent">Data packets the PAD sent on the call.</param>
        /// <param name="received">Data packets the PAD received on the call.</param>
        /// <returns>The signal, without line ends.</returns>
        public static string PssClearSignal(string? mnemonic, byte cause, byte diagnostic, TimeSpan callTime, long sent, long received)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            string what = mnemonic ?? ("C:" + cause.ToString(c));
            return "CLR " + what + " (" + diagnostic.ToString("00", c) + ") "
                + callTime.Days.ToString("00", c) + ":" + callTime.Hours.ToString("00", c) + ":"
                + callTime.Minutes.ToString("00", c) + ":" + callTime.Seconds.ToString("00", c)
                + " " + sent.ToString(c) + " " + received.ToString(c);
        }

        /// <summary>
        /// As <see cref="PssClearSignal(string?, byte, byte, TimeSpan, long, long)"/> for a link that cannot count
        /// packets: the line ends after the call time. Ours - printing 0 0 would be a count nobody made.
        /// </summary>
        /// <param name="mnemonic">The Table 6 cause, or null.</param>
        /// <param name="cause">The cause octet.</param>
        /// <param name="diagnostic">The diagnostic octet.</param>
        /// <param name="callTime">How long the call was up.</param>
        /// <returns>The signal, without line ends.</returns>
        public static string PssClearSignal(string? mnemonic, byte cause, byte diagnostic, TimeSpan callTime)
        {
            string full = PssClearSignal(mnemonic, cause, diagnostic, callTime, 0, 0);
            return full.Substring(0, full.Length - 4);
        }

        /// <summary>The Table 6 mnemonic for a clearing cause, or null (the rule of <see cref="ClearSignal(byte, byte)"/>).</summary>
        /// <param name="cause">The cause octet.</param>
        /// <returns>The mnemonic, or null.</returns>
        public static string? ClearMnemonic(byte cause)
        {
            StandardClearSignal(cause, 0, out string? mnemonic);
            return mnemonic;
        }

        /// <summary>
        /// The clear indication PAD service signal for a call the PAD cleared because the host asked it to (the X.29
        /// invitation to clear): <c>CLR PAD</c>, and in extended dialogue mode <c> - Call cleared, remote request</c>
        /// (X.28 (12/97) 3.5.17.1, Table 6). The cause code and diagnostic fields are optional (3.5.17.1 note) and
        /// left out: the PAD sent the clear itself, so there is no network cause to show.
        /// </summary>
        /// <param name="extended">The PAD is in extended dialogue mode.</param>
        /// <returns>The service signal, without line ends.</returns>
        public static string PadClearSignal(bool extended) => extended ? "CLR PAD - " + ExtendedClearText("PAD") : "CLR PAD";

        /// <summary>
        /// The clear confirmation PAD service signal: <c>CLR CONF</c>, and in extended dialogue mode
        /// <c> - Call cleared, confirmed</c> (X.28 (12/97) 3.5.18, Table 7).
        /// </summary>
        /// <param name="extended">The PAD is in extended dialogue mode.</param>
        /// <returns>The service signal, without line ends.</returns>
        public static string ClearConfirmation(bool extended) => extended ? "CLR CONF - Call cleared, confirmed" : "CLR CONF";

        /// <summary>
        /// X.28 (12/97) 3.5: "If the high order 4 bits of parameter 6 are not all zero, the PAD will operate in the
        /// extended dialogue mode."
        /// </summary>
        /// <param name="parameter6">The value of X.3 parameter 6.</param>
        /// <returns>True in extended dialogue mode.</returns>
        public static bool IsExtendedDialogue(byte parameter6) => (parameter6 & 0xF0) != 0;

        // X.28 (12/97) Table 6, the text for each cause mnemonic, word for word. ROO (X.96) is in the table but no
        // X.25 cause is paired with it above, so it never comes here.
        private static string? ExtendedClearText(string mnemonic) => mnemonic switch
        {
            "OCC" => "Call cleared, number busy",
            "NC" => "Call cleared, temporary network problem",
            "INV" => "Call cleared, invalid facility requested",
            "NA" => "Call cleared, access to this number is barred",
            "ERR" => "Call cleared, network detected local procedure error",
            "RPE" => "Call cleared, network detected remote procedure error",
            "NP" => "Call cleared, number not assigned",
            "DER" => "Call cleared, number out of order",
            "PAD" => "Call cleared, remote request",
            "DTE" => "Call cleared, by remote device, data may be lost",
            "RNA" => "Call cleared, reverse charging refused",
            "ID" => "Call cleared, incompatible destination",
            "SA" => "Call cleared, ship cannot be contacted",
            "FNA" => "Call cleared, fast select refused",
            "ROO" => "Call cleared, cannot be routed as requested",
            _ => null,
        };

        private static string StandardClearSignal(byte cause, byte diagnostic, out string? mnemonic)
        {
            // Cause 128-255 (bit 8 set) is "DTE originated" with the DTE's own code in the low bits.
            if ((cause & 0x80) != 0)
            {
                mnemonic = "DTE";
            }
            else
            {
                switch (cause)
                {
                    case 0x00: mnemonic = "DTE"; break;   // DTE originated
                    case 0x01: mnemonic = "OCC"; break;   // Number busy
                    case 0x03: mnemonic = "INV"; break;   // Invalid facility request
                    case 0x05: mnemonic = "NC"; break;    // Network congestion
                    case 0x09: mnemonic = "DER"; break;   // Out of order
                    case 0x0B: mnemonic = "NA"; break;    // Access barred
                    case 0x0D: mnemonic = "NP"; break;    // Not obtainable
                    case 0x11: mnemonic = "RPE"; break;   // Remote procedure error
                    case 0x13: mnemonic = "ERR"; break;   // Local procedure error
                    case 0x19: mnemonic = "RNA"; break;   // Reverse charging acceptance not subscribed
                    case 0x21: mnemonic = "ID"; break;    // Incompatible destination
                    case 0x29: mnemonic = "FNA"; break;   // Fast select acceptance not subscribed
                    case 0x39: mnemonic = "SA"; break;    // Ship absent
                    default: mnemonic = null; break;
                }
            }

            string number = (cause & 0x80) != 0 ? "0" : cause.ToString();
            return mnemonic == null
                ? $"CLR C:{number} D:{diagnostic}"
                : $"CLR {mnemonic} C:{number} D:{diagnostic}";
        }

        /// <summary>
        /// Process character in command mode
        /// </summary>
        private bool _lastCommandCharWasCr;

        private bool ProcessCommandModeCharacter(char c)
        {
            // Handle special characters
            // X.28 4.15: a terminal ends a command line with CR alone. An LF straight after a CR
            // (a terminal that sends CR LF) belongs to the same line end and is dropped.
            if (c == '\n' && _lastCommandCharWasCr)
            {
                _lastCommandCharWasCr = false;
                return true;
            }

            _lastCommandCharWasCr = c == '\r';

            if (c == '\n' || c == '\r')
            {
                // Fire immediate newline echo
                CharacterEcho?.Invoke(this, new CharacterEchoEventArgs('\r', EchoType.Normal));
                CharacterEcho?.Invoke(this, new CharacterEchoEventArgs('\n', EchoType.Normal));
                
                // Process the command
                ProcessCommandLine();
                return true;
            }
            
            if (c == '\b' || c == 0x7F) // Backspace or DEL
            {
                return HandleCommandBackspace();
            }
            
            // Add to command buffer
            _commandBuffer.Append(c);

            // X.28 5.2 NOTE 1: "The characters that follow the characters <ID> (i.e. the <NUI string>) should not be
            // echoed." The two letters ID themselves are echoed; everything after them on the line is not.
            if (IsNuiOnLine(_commandBuffer, _commandBuffer.Length - 1) || IsInNuiFacilityRequest(_commandBuffer, _commandBuffer.Length - 1))
            {
                return true;
            }

            // Echo character (in command mode everything else is echoed)
            CharacterEcho?.Invoke(this, new CharacterEchoEventArgs(c, EchoType.Normal));
            return true;
        }

        /// <summary>
        /// The NUI this terminal user gave with the NUI ON command (X.28 5.2, <c>ID</c> followed by the NUI), or null.
        /// "This identification will apply for all subsequent calls until either the access path is disconnected or a
        /// NUI OFF PAD command signal is received by the PAD."
        /// </summary>
        public string? Nui { get; private set; }

        /// <summary>
        /// Gives the NUI as if the user had typed <c>ID</c> and the NUI - for a PAD started with a NUI already known
        /// (x25emu's <c>x25pad -n</c>). The same rules as the command: the discarded characters are taken out, and a NUI
        /// with a character that may not be in one, an empty one, or <c>OFF</c> is refused.
        /// </summary>
        /// <param name="raw">The NUI as given.</param>
        /// <returns>False, and the NUI unchanged, when it is refused.</returns>
        public bool SetNui(string raw)
        {
            if (!TryReadNui(raw, out string nui) || nui.Length == 0 || string.Equals(nui, "OFF", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Nui = nui;
            return true;
        }

        /// <summary>
        /// True when the character at <paramref name="index"/> of a command line comes after a leading <c>ID</c>, so it
        /// is part of a NUI and is not echoed.
        /// </summary>
        private static bool IsNuiOnLine(System.Text.StringBuilder line, int index)
        {
            int start = 0;
            while (start < line.Length && line[start] == ' ')
            {
                start++;
            }

            return index >= start + 2 && line.Length >= start + 2
                && char.ToUpperInvariant(line[start]) == 'I' && char.ToUpperInvariant(line[start + 1]) == 'D';
        }

        /// <summary>
        /// Reads a NUI string by X.28 3.5.15.1.1: characters in columns 2 to 7 of IA5, except SP, DEL, '-', ',' and '+';
        /// "Characters in columns 0 and 1, except 0/13 (CR), and characters 7/15 (DEL), and 2/0 (SP) may be included
        /// during the transmission of the NUI string but will not form part of the NUI string and will be discarded."
        /// </summary>
        /// <param name="raw">What followed <c>ID</c>, as typed.</param>
        /// <param name="nui">The NUI, the discarded characters taken out.</param>
        /// <returns>False when a character that may not be in a NUI is present.</returns>
        public static bool TryReadNui(string raw, out string nui)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (ch < 0x20 || ch == ' ' || ch == (char)0x7F)
                {
                    continue;
                }

                if (ch > 0x7E || ch == '-' || ch == ',' || ch == '+')
                {
                    nui = string.Empty;
                    return false;
                }

                sb.Append(ch);
            }

            nui = sb.ToString();
            return true;
        }

        /// <summary>
        /// Process complete command line
        /// </summary>
        private void ProcessCommandLine()
        {
            string typed = _commandBuffer.ToString().Trim();
            string commandLine = typed.ToUpper();
            _commandBuffer.Clear();

            if (string.IsNullOrEmpty(commandLine))
                return;

            // X.28 5.2: NUI ON is <ID><NUI string>, NUI OFF is <IDOFF>. Handled before the word parser: the NUI is not
            // split on spaces (SP is discarded inside it) and keeps its case, and it is never written to the log.
            if (commandLine.StartsWith("ID", StringComparison.Ordinal))
            {
                HandleNuiCommand(typed.Substring(2));
                return;
            }

            // X.28 3.5.15: a selection with a facility request block, "<facility request>[,<facility request>...]-" then
            // the address, typed bare or after CALL. Read from the line as typed so a NUI keeps its case; never logged.
            int blockAt = SelectionBlockStart(typed);
            if (blockAt >= 0 && typed.IndexOf('-', blockAt) > blockAt)
            {
                HandleSelectionWithFacilities(typed.Substring(blockAt));
                return;
            }
                
            _logger?.LogDebug("X.28: Processing command: {Command}", commandLine);
            
            var command = ParseCommand(commandLine);
            
            // Process command and fire response event immediately
            var result = ProcessPadCommand(command);
            
            // Fire command event AFTER response is sent
            // This allows the response to be displayed before any connection events
            CommandReceived?.Invoke(this, new X28CommandEventArgs(command));
        }

        /// <summary>
        /// Parse command line into command structure
        /// </summary>
        private X28Command ParseCommand(string commandLine)
        {
            // Read, set, and set and read (X.28 3.5.4.1, 3.5.6.1): the verb may be followed at once by the list
            // ("PAR?1"), and the list items are separated by commas with optional spaces ("SET 2:0, 3:2, 9:4"). Until
            // 2026-11-19 the line was split on spaces, so the X.28 example itself failed ("2:0," did not match) and
            // SET? was unknown.
            X28Command? parameterCommand = ParseParameterCommand(commandLine);
            if (parameterCommand != null)
            {
                return parameterCommand;
            }

            var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var arguments = new List<string>();
            for (int i = 1; i < parts.Length; i++)
            {
                arguments.Add(parts[i]);
            }
            var command = new X28Command
            {
                Verb = parts[0],
                Arguments = arguments,
                OriginalLine = commandLine
            };
            
            // Parse common command patterns and handle user variations
            switch (command.Verb)
            {
                case "C":
                case "CALL":
                    command.Verb = Commands.CALL;
                    if (command.Arguments.Count > 0)
                        command.Address = command.Arguments[0];
                    break;
                    
                case "CLR":
                case "CLEAR":
                    command.Verb = Commands.CLEAR;
                    break;
                    
                case "STAT":
                case "STATUS":
                    command.Verb = Commands.STATUS;
                    break;
                    
                case "PAR":
                case "PAR?":
                case "PARAMETER":
                    command.Verb = Commands.PARAMETER;
                    break;
                    
                case Commands.PROFILE:
                    command.Verb = Commands.PROFILE;
                    if (command.Arguments.Count > 0)
                        command.ProfileName = command.Arguments[0];
                    break;
                    
                case "DATA":
                    command.Verb = Commands.DATA;
                    break;

                case "HELP":
                case "H":
                case "?":
                    command.Verb = Commands.HELP;
                    break;

                case "EXIT":
                case "QUIT":
                    command.Verb = Commands.EXIT;
                    break;
            }

            return command;
        }

        /// <summary>
        /// Parses a read (<c>PAR?</c>), set (<c>SET</c>) or set and read (<c>SET?</c>) command signal (X.28 3.5.4.1,
        /// 3.5.6.1), or returns null for any other line. The bare <c>PAR</c> (no <c>?</c>) this PAD has always taken as a
        /// read of all parameters is kept. Each list item is kept as typed in <see cref="X28Command.Items"/>; the
        /// handler resolves it, so an item it cannot read is answered INV rather than dropped.
        /// </summary>
        /// <param name="commandLine">The command line in upper case.</param>
        /// <returns>The command, or null.</returns>
        private static X28Command? ParseParameterCommand(string commandLine)
        {
            string verb;
            string rest;
            if (commandLine.StartsWith("PAR?", StringComparison.Ordinal))
            {
                verb = Commands.PARAMETER;
                rest = commandLine.Substring(4);
            }
            else if (commandLine == "PAR" || commandLine == "PARAMETER")
            {
                verb = Commands.PARAMETER;
                rest = string.Empty;
            }
            else if (commandLine.StartsWith("SET?", StringComparison.Ordinal))
            {
                verb = SetAndRead;
                rest = commandLine.Substring(4);
            }
            else if (commandLine.StartsWith("SET", StringComparison.Ordinal) && (commandLine.Length == 3 || commandLine[3] == ' ' || char.IsDigit(commandLine[3])))
            {
                verb = Commands.SET;
                rest = commandLine.Substring(3);
            }
            else
            {
                return null;
            }

            var command = new X28Command { Verb = verb, OriginalLine = commandLine };
            string[] items = rest.Split(',');
            for (int i = 0; i < items.Length; i++)
            {
                string item = items[i].Trim();
                if (item.Length > 0)
                {
                    command.Items.Add(item);
                    command.Arguments.Add(item);
                }
            }

            return command;
        }

        /// <summary>The verb this class gives the set and read command signal, <c>SET?</c> (X.28 3.5.6.1).</summary>
        internal const string SetAndRead = "SET?";

        /// <summary>
        /// The abbreviated PAD parameter names of X.28 (12/97) Table 8 (page 28), for parameters 1 to 29: the full name,
        /// and how many letters of it are the abbreviation that must be typed (the part outside the brackets in the
        /// table, e.g. "esc(ape)").
        /// </summary>
        private static readonly (string Name, int Abbreviation, string Description)[] ParameterNames =
        {
            ("escape", 3, "PAD recall using a character"), ("echo", 3, "Echo"),
            ("forward", 3, "Selection of data forwarding characters"), ("idle", 3, "Selection of idle timer delay"),
            ("device", 3, "Ancillary device control"), ("signals", 3, "Control of PAD service signals"),
            ("break", 3, "Operation on receipt of break signal"), ("discard", 3, "Discard output"),
            ("CRpad", 3, "Padding after carriage return"), ("folding", 3, "Line folding"),
            ("speed", 3, "Binary speed of start-stop mode DTE"), ("flow", 3, "Flow control of the PAD"),
            ("LFinsert", 3, "Linefeed insertion after carriage return"), ("LFpad", 3, "Padding after linefeed"),
            ("Edit", 3, "Editing"), ("Cdelete", 4, "Character delete"), ("Ldelete", 4, "Line delete"),
            ("Ldisplay", 4, "Line display"), ("Esignals", 4, "Editing PAD service signals"), ("Mask", 3, "Echo mask"),
            ("parity", 3, "Parity treatment"), ("page", 3, "Page wait"), ("input", 3, "Size of input field"),
            ("frame", 3, "End-of-frame signal"), ("Xforward", 4, "Extended forwarding signals"),
            ("Dinterrupt", 4, "Display interrupt character"), ("Dconfirmation", 4, "Display interrupt confirmation"),
            ("Coding", 3, "Diacritic character coding scheme"), ("Xecho", 4, "Extended echo mask"),
        };

        /// <summary>
        /// The X.28 Table 8 name of a PAD parameter (e.g. <c>escape</c> for 1), or null for a number the table has no
        /// name for.
        /// </summary>
        /// <param name="reference">The parameter reference number.</param>
        /// <returns>The name, or null.</returns>
        public static string? ParameterName(int reference)
        {
            return reference >= 1 && reference <= ParameterNames.Length ? ParameterNames[reference - 1].Name : null;
        }

        /// <summary>
        /// Reads a parameter reference: a decimal number, or in extended dialogue mode a Table 8 name typed as its
        /// abbreviation or any longer part of the name, in any case (X.28 3.5 and Table 8 note 1: "The name or
        /// abbreviation should be accepted in the read, set, or set and read PAD command signals").
        /// </summary>
        /// <param name="text">The reference as typed.</param>
        /// <param name="extended">The PAD is in extended dialogue mode.</param>
        /// <param name="reference">The parameter number.</param>
        /// <returns>False when the text is neither.</returns>
        public static bool TryParameterReference(string text, bool extended, out byte reference)
        {
            if (byte.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out reference))
            {
                return true;
            }

            if (extended)
            {
                for (int i = 0; i < ParameterNames.Length; i++)
                {
                    string name = ParameterNames[i].Name;
                    if (text.Length >= ParameterNames[i].Abbreviation && text.Length <= name.Length
                        && string.Compare(text, 0, name, 0, text.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        reference = (byte)(i + 1);
                        return true;
                    }
                }
            }

            reference = 0;
            return false;
        }

        /// <summary>
        /// One entry of the parameter value PAD service signal (X.28 3.5.14): <c>reference:value</c>, with
        /// <c> - </c> and the Table 8 name in extended dialogue mode; <c>INV</c> in place of the value for a reference
        /// or value that is not valid.
        /// </summary>
        private static string ParameterEntry(string reference, string value, int number, bool extended)
        {
            string? name = extended ? ParameterName(number) : null;
            return name == null ? reference + ":" + value : reference + ":" + value + " - " + name;
        }

        /// <summary>
        /// Process PAD command and return response
        /// Only processes commands when in Command mode (not DataTransfer mode)
        /// </summary>
        private CommandResult ProcessPadCommand(X28Command command)
        {
            return command.Verb switch
            {
                Commands.CALL => HandleCallCommand(command),
                Commands.CLEAR => HandleClearCommand(command),
                Commands.STATUS => HandleStatusCommand(command),
                Commands.SET => HandleSetCommand(command),
                SetAndRead => HandleSetCommand(command),
                Commands.PARAMETER => HandleParameterCommand(command),
                Commands.PROFILE => HandleProfileCommand(command),
                Commands.RESET => HandleResetCommand(command),
                Commands.INTERRUPT => HandleInterruptCommand(command),
                Commands.DATA => HandleDataCommand(command),
                Commands.HELP => HandleHelpCommand(command),
                // Acted on by the PAD (PadClient), which hears the command through CommandReceived.
                Commands.EXIT => CommandResult.Success,
                _ => HandleInvalidCommand(command.Verb)
            };
        }

        /// <summary>
        /// Where a selection's facility request block starts in a command line, or -1: after an optional <c>CALL </c> or
        /// <c>C </c>, a line whose first character is the NUI facility request code <c>N</c> (X.28 Table 4: 4/14). Only N
        /// is recognised as the start of a block; it is the only facility request this PAD implements.
        /// </summary>
        private static int SelectionBlockStart(string line)
        {
            int at = 0;
            while (at < line.Length && line[at] == ' ')
            {
                at++;
            }

            if (line.Length - at > 5 && string.Compare(line, at, "CALL ", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                at += 5;
            }
            else if (line.Length - at > 2 && (line[at] == 'C' || line[at] == 'c') && line[at + 1] == ' ')
            {
                at += 2;
            }

            while (at < line.Length && line[at] == ' ')
            {
                at++;
            }

            return at < line.Length && line[at] == 'N' ? at : -1;
        }

        /// <summary>
        /// A selection with a facility request block (X.28 3.5.15): the requests before the <c>-</c>, separated by
        /// <c>,</c>, then the address. N gives the call's NUI (3.5.15.1.1). Any other request, or a NUI that cannot be
        /// read, is answered ERR and no call is made - a facility the user asked for is never silently left out.
        /// </summary>
        /// <param name="selection">The selection, from the block's first character.</param>
        private void HandleSelectionWithFacilities(string selection)
        {
            int dash = selection.IndexOf('-');
            string block = selection.Substring(0, dash);
            string address = selection.Substring(dash + 1).Trim();
            string? nui = null;
            string[] requests = block.Split(',');
            for (int i = 0; i < requests.Length; i++)
            {
                string request = requests[i];
                if (request.Length < 2 || request[0] != 'N' || !TryReadNui(request.Substring(1), out string read) || read.Length == 0)
                {
                    CommandResponse?.Invoke(this, new CommandResponseEventArgs("ERR\r\n", ResponseType.Error));
                    return;
                }

                nui = read;
            }

            X28Command command = new X28Command
            {
                Verb = Commands.CALL,
                Arguments = new List<string> { address },
                OriginalLine = "CALL " + address,
                Address = address,
                CallNui = nui,
            };

            if (ProcessPadCommand(command) == CommandResult.Success)
            {
                CommandReceived?.Invoke(this, new X28CommandEventArgs(command));
            }
        }

        /// <summary>
        /// True when the character at <paramref name="index"/> is inside an N facility request of a selection being typed,
        /// after the N: X.28 3.5.15.1.1 NOTE 1, the PAD "should turn off the echo ... for the duration of the NUI facility
        /// request". The request ends at <c>,</c> or <c>-</c>, which are echoed.
        /// </summary>
        private static bool IsInNuiFacilityRequest(System.Text.StringBuilder line, int index)
        {
            string text = line.ToString();
            int at = SelectionBlockStart(text);
            if (at < 0 || index <= at)
            {
                return false;
            }

            int end = text.IndexOf('-', at);
            if (end >= 0 && index >= end)
            {
                return false;
            }

            // Walk back to the start of this request.
            int start = index;
            while (start > at && text[start - 1] != ',')
            {
                start--;
            }

            char c = text[index];
            return text[start] == 'N' && index > start && c != ',' && c != '-';
        }

        /// <summary>
        /// NUI ON (<c>ID</c> and a NUI) and NUI OFF (<c>IDOFF</c>), X.28 5.2. X.28 defines no service signal in answer to
        /// either, so a good one is answered with nothing; one that cannot be read gets the error signal ERR.
        /// </summary>
        /// <param name="rest">What followed <c>ID</c> on the line, as typed.</param>
        private void HandleNuiCommand(string rest)
        {
            if (string.Equals(rest.Trim(), "OFF", StringComparison.OrdinalIgnoreCase))
            {
                // "NOTE 2 - The character sequence <OFF> is not permitted as the NUI string": so IDOFF is always NUI OFF.
                Nui = null;
                _logger?.LogInformation("X.28: NUI off");
                return;
            }

            // "<NUI string> is optional"; with it omitted, "the PAD may request additional user input" - this PAD has no
            // prompting, so ID alone is refused rather than guessed at.
            if (!TryReadNui(rest, out string nui) || nui.Length == 0)
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("ERR\r\n", ResponseType.Error));
                return;
            }

            Nui = nui;
            _logger?.LogInformation("X.28: NUI on ({Length} characters)", nui.Length);
        }

        /// <summary>
        /// The commands this PAD carries out, each with the one line HELP shows for it (HELP COMMAND lists them all,
        /// HELP followed by the command shows its own line). Exactly the ones it implements.
        /// </summary>
        private static readonly (string Name, string Line)[] HelpCommands =
        {
            ("CALL", "CALL <address>     place a call (also C <address>, or the address alone)"),
            ("CLR", "CLR                clear the call (CLEAR)"),
            ("STAT", "STAT               FREE or ENGAGED: is there a call"),
            ("PAR?", "PAR? [n, n ...]    read X.3 parameters (all when none is named)"),
            ("SET", "SET n:v, n:v ...   set X.3 parameters"),
            ("SET?", "SET? n:v, n:v ...  set X.3 parameters and show the new values"),
            ("PROF", "PROF <profile>     set a standard profile (HELP PROFILES)"),
            ("RESET", "RESET              reset the call: data in the network is thrown away, the call stays"),
            ("INT", "INT                send an interrupt"),
            ("ID", "ID<nui>            give a network user identity for the next calls (IDOFF takes it away)"),
            ("DATA", "DATA               return to the call"),
            ("HELP", "HELP [subject]     help: HELP LIST shows the subjects"),
            ("EXIT", "EXIT               clear any call and leave the PAD (QUIT)"),
        };

        /// <summary>
        /// HELP with the subjects of X.28 (12/97) 5.4 and Table 10: none (how to use HELP), LIST, COMMAND, PARAMETER,
        /// PARAMETER n, PROFILES, PROFILE id, or a command name. The texts are this PAD's own: X.28 says their contents
        /// are network dependent (Table 10 note 1). MAP is not offered: this PAD has no Multi-Aspect PAD. A subject it
        /// does not know answers ERR (3.5.19).
        /// </summary>
        private CommandResult HandleHelpCommand(X28Command command)
        {
            string subject = string.Join(" ", command.Arguments);
            var text = new StringBuilder();
            if (subject.Length == 0 || subject == "HELP")
            {
                // The telnet PAD's greeting says "Type HELP for the commands", so the bare HELP lists them too.
                text.Append("PAD commands:\r\n");
                for (int i = 0; i < HelpCommands.Length; i++)
                {
                    text.Append("  ").Append(HelpCommands[i].Line).Append("\r\n");
                }

                text.Append("HELP <subject> gives more: LIST, COMMAND, PARAMETER, PARAMETER <n>, PROFILES, PROFILE <id>, or a command name.\r\n");
                text.Append("In a call, the PAD escape character (X.3 parameter 1, Ctrl+P) brings you to the PAD.\r\n");
            }
            else if (subject == "LIST")
            {
                text.Append("LIST, COMMAND, PARAMETER, PARAMETER <n>, PROFILES, PROFILE <id>, ");
                for (int i = 0; i < HelpCommands.Length; i++)
                {
                    text.Append(i == 0 ? string.Empty : ", ").Append(HelpCommands[i].Name);
                }

                text.Append("\r\n");
            }
            else if (subject == "COMMAND")
            {
                for (int i = 0; i < HelpCommands.Length; i++)
                {
                    text.Append(HelpCommands[i].Line).Append("\r\n");
                }
            }
            else if (subject == "PARAMETER")
            {
                Dictionary<byte, byte> all = _x3Parameters.GetAllParameters();
                byte[] numbers = new byte[all.Count];
                all.Keys.CopyTo(numbers, 0);
                Array.Sort(numbers);
                for (int i = 0; i < numbers.Length; i++)
                {
                    AppendParameterHelp(text, numbers[i], all[numbers[i]]);
                }
            }
            else if (subject.StartsWith("PARAMETER ", StringComparison.Ordinal))
            {
                Dictionary<byte, byte> all = _x3Parameters.GetAllParameters();
                bool extended = IsExtendedDialogue(_x3Parameters.GetParameter(X3.ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS));
                if (!TryParameterReference(subject.Substring(10).Trim(), extended, out byte reference) || !all.TryGetValue(reference, out byte value))
                {
                    return HelpError();
                }

                AppendParameterHelp(text, reference, value);
            }
            else if (subject == "PROFILES")
            {
                text.Append("90 simple standard profile (also SIMPLE)\r\n");
                text.Append("91 transparent standard profile (also TRANS)\r\n");
                text.Append("STD this PAD's own starting values\r\n");
            }
            else if (subject.StartsWith("PROFILE ", StringComparison.Ordinal))
            {
                string id = subject.Substring(8).Trim();
                var probe = new X3();
                if (!probe.SetProfile(id))
                {
                    return HelpError();
                }

                Dictionary<byte, byte> values = probe.GetAllParameters();
                byte[] numbers = new byte[values.Count];
                values.Keys.CopyTo(numbers, 0);
                Array.Sort(numbers);
                text.Append(id).Append(": PAR ");
                for (int i = 0; i < numbers.Length; i++)
                {
                    text.Append(i == 0 ? string.Empty : ", ").Append(numbers[i]).Append(':').Append(values[numbers[i]]);
                }

                text.Append("\r\n");
            }
            else
            {
                string name = subject == "C" || subject == "CLEAR" ? (subject == "C" ? "CALL" : "CLR")
                    : subject == "PAR" ? "PAR?" : subject == "QUIT" ? "EXIT" : subject == "IDOFF" ? "ID" : subject;
                int found = -1;
                for (int i = 0; i < HelpCommands.Length; i++)
                {
                    if (HelpCommands[i].Name == name)
                    {
                        found = i;
                        break;
                    }
                }

                if (found < 0)
                {
                    return HelpError();
                }

                text.Append(HelpCommands[found].Line).Append("\r\n");
            }

            CommandResponse?.Invoke(this, new CommandResponseEventArgs(text.ToString(), ResponseType.Success));
            return CommandResult.Success;
        }

        private static void AppendParameterHelp(StringBuilder text, byte reference, byte value)
        {
            int index = reference - 1;
            text.Append(reference).Append(':').Append(value);
            if (index >= 0 && index < ParameterNames.Length)
            {
                text.Append(" - ").Append(ParameterNames[index].Name).Append(", ").Append(ParameterNames[index].Description);
            }

            text.Append("\r\n");
        }

        private CommandResult HelpError()
        {
            CommandResponse?.Invoke(this, new CommandResponseEventArgs("ERR\r\n", ResponseType.Error));
            return CommandResult.InvalidParameters;
        }

        /// <summary>
        /// Handle CALL command
        /// </summary>
        private CommandResult HandleCallCommand(X28Command command)
        {
            if (string.IsNullOrEmpty(command.Address))
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("Address required\r\n", ResponseType.Error));
                return CommandResult.InvalidParameters;
            }
                
            // Validate address format
            var x121 = new X121(_logger);
            if (!x121.IsValidAddress(command.Address))
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("Invalid address format\r\n", ResponseType.Error));
                return CommandResult.InvalidParameters;
            }
                
            // DON'T switch mode here - let the connection success/failure determine the mode
            // Mode will be switched by PadClient.HandleCallAccepted() when connection is actually established
            
            // No "COM" here. X.28 prints the service signal COM when the call is CONNECTED, that is when
            // the far end has accepted it. Printing it at the request made the window claim a
            // connection that did not exist (2026-11-11: COM with no switch running at all).
            // PadClient.NotifyCallAccepted prints COM.

            _logger?.LogInformation("X.28: Call request to {Address}", command.Address);
            return CommandResult.Success;
        }

        /// <summary>
        /// Handle parameter-related commands
        /// </summary>
        /// <summary>
        /// SET and SET? (X.28 3.3.2, 3.5.6.1, 3.5.14). Valid entries are set (also when others in the line are not).
        /// With parameter 6 not 0: SET? answers the parameter value PAD service signal with the new values; SET answers
        /// the acknowledgement (a format effector, 3.5.3) when every entry was valid, and otherwise the parameter value
        /// signal naming only the invalid ones, each with INV. With parameter 6 at 0 nothing is printed.
        /// </summary>
        private CommandResult HandleSetCommand(X28Command command)
        {
            bool read = command.Verb == SetAndRead;
            bool extended = IsExtendedDialogue(_x3Parameters.GetParameter(X3.ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS));
            command.Parameters = new Dictionary<byte, byte>();
            var entries = new List<string>();
            bool invalid = false;
            for (int i = 0; i < command.Items.Count; i++)
            {
                string item = command.Items[i];
                int colon = item.IndexOf(':');
                string referenceText = (colon < 0 ? item : item.Substring(0, colon)).Trim();
                string valueText = colon < 0 ? string.Empty : item.Substring(colon + 1).Trim();
                bool known = TryParameterReference(referenceText, extended, out byte reference);
                bool set = known
                    && byte.TryParse(valueText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out byte value)
                    && _x3Parameters.SetParameter(reference, value);
                if (set)
                {
                    command.Parameters[reference] = _x3Parameters.GetParameter(reference);
                    if (read)
                    {
                        entries.Add(ParameterEntry(reference.ToString(System.Globalization.CultureInfo.InvariantCulture), command.Parameters[reference].ToString(System.Globalization.CultureInfo.InvariantCulture), reference, extended));
                    }
                }
                else
                {
                    invalid = true;
                    entries.Add(ParameterEntry(known ? reference.ToString(System.Globalization.CultureInfo.InvariantCulture) : referenceText, "INV", known ? reference : 0, extended));
                }
            }

            // Parameter 6 is read again: the line may have just set it.
            if (_x3Parameters.GetParameter(X3.ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS) != 0)
            {
                string answer = read || invalid ? "PAR " + string.Join(", ", entries) + "\r\n" : "\r\n";
                CommandResponse?.Invoke(this, new CommandResponseEventArgs(answer, invalid ? ResponseType.Error : ResponseType.Success));
            }

            return invalid ? CommandResult.InvalidParameters : CommandResult.Success;
        }

        /// <summary>
        /// PAR? (X.28 3.4, 3.5.4.1, 3.5.14): the parameter value PAD service signal, <c>PAR 1:1, 2:1, ...</c>; all
        /// parameters when none is named. A reference this PAD does not have answers INV.
        /// </summary>
        private CommandResult HandleParameterCommand(X28Command command)
        {
            bool extended = IsExtendedDialogue(_x3Parameters.GetParameter(X3.ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS));
            Dictionary<byte, byte> all = _x3Parameters.GetAllParameters();
            var entries = new List<string>();
            if (command.Items.Count == 0)
            {
                byte[] numbers = new byte[all.Count];
                all.Keys.CopyTo(numbers, 0);
                Array.Sort(numbers);
                for (int i = 0; i < numbers.Length; i++)
                {
                    entries.Add(ParameterEntry(numbers[i].ToString(System.Globalization.CultureInfo.InvariantCulture), all[numbers[i]].ToString(System.Globalization.CultureInfo.InvariantCulture), numbers[i], extended));
                }
            }
            else
            {
                for (int i = 0; i < command.Items.Count; i++)
                {
                    string text = command.Items[i];
                    if (TryParameterReference(text, extended, out byte reference) && all.TryGetValue(reference, out byte value))
                    {
                        entries.Add(ParameterEntry(reference.ToString(System.Globalization.CultureInfo.InvariantCulture), value.ToString(System.Globalization.CultureInfo.InvariantCulture), reference, extended));
                    }
                    else
                    {
                        entries.Add(ParameterEntry(text, "INV", 0, extended));
                    }
                }
            }

            CommandResponse?.Invoke(this, new CommandResponseEventArgs("PAR " + string.Join(", ", entries) + "\r\n", ResponseType.Success));
            return CommandResult.Success;
        }

        private CommandResult HandleProfileCommand(X28Command command)
        {
            if (string.IsNullOrEmpty(command.ProfileName))
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("Profile name required\r\n", ResponseType.Error));
                return CommandResult.InvalidParameters;
            }
                
            if (_x3Parameters.SetProfile(command.ProfileName))
            {
                // X.28 3.3.1: acknowledged with the acknowledgement PAD service signal (a format effector, 3.5.3) when
                // parameter 6 is not 0 - read after the profile, which sets it. It answered "<name> SET" until 2026-11-19.
                if (_x3Parameters.GetParameter(X3.ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS) != 0)
                {
                    CommandResponse?.Invoke(this, new CommandResponseEventArgs("\r\n", ResponseType.Success));
                }

                return CommandResult.Success;
            }
            else
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs($"Unknown profile: {command.ProfileName}\r\n", ResponseType.Error));
                return CommandResult.InvalidParameters;
            }
        }

        /// <summary>
        /// Handle invalid commands
        /// </summary>
        private CommandResult HandleInvalidCommand(string verb)
        {
            CommandResponse?.Invoke(this, new CommandResponseEventArgs($"Invalid command: {verb}\r\n", ResponseType.Error));
            return CommandResult.InvalidCommand;
        }

        private CommandResult HandleStatusCommand(X28Command command)
        {
            CommandResponse?.Invoke(this, new CommandResponseEventArgs(StatusSignal() + "\r\n", ResponseType.Success));
            return CommandResult.Success;
        }

        /// <summary>
        /// The answer to STAT (X.28 (12/97) 3.5.11): <c>ENGAGED</c> while a call is up (also at the prompt after the
        /// escape, where the call stays), <c>FREE</c> when there is none; in extended dialogue mode followed by
        /// <c> - Call Established</c> or <c> - No Call Established</c>. No MAP status block: this PAD has no MAP.
        /// </summary>
        /// <returns>The service signal, without line ends.</returns>
        public string StatusSignal()
        {
            bool extended = IsExtendedDialogue(_x3Parameters.GetParameter(X3.ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS));
            if (_callUp)
            {
                return extended ? "ENGAGED - Call Established" : "ENGAGED";
            }

            return extended ? "FREE - No Call Established" : "FREE";
        }

        private CommandResult HandleClearCommand(X28Command command)
        {
            // With a call up, the clear itself and its CLR CONF (X.28 3.5.18) belong to the PAD that owns the call
            // (PadClient.ClearCallFromTerminal); until 2026-11-19 this also printed "CLEARED", so the person saw both.
            // With no call there is nothing to clear: X.28 does not say what the answer is, so the PAD gives its error
            // service signal ERR (3.5.19). That choice is ours, not the spec's.
            if (!_callUp)
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("ERR\r\n", ResponseType.Error));
                return CommandResult.Success;
            }

            SwitchMode(PadMode.Command);
            return CommandResult.Success;
        }

        private CommandResult HandleResetCommand(X28Command command)
        {
            // X.28 4.9.2.3: the PAD resets the call and acknowledges the command (3.5.3, a format effector). Sending
            // the reset and the acknowledgement belong to the PAD that owns the call (PadClient), which knows whether
            // the reset went out. With no call there is nothing to reset: ERR (3.5.19), as for CLR. Until
            // 2026-11-19 this answered "RESET" and nothing was sent.
            if (!_callUp)
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("ERR\r\n", ResponseType.Error));
            }

            return CommandResult.Success;
        }

        private CommandResult HandleInterruptCommand(X28Command command)
        {
            CommandResponse?.Invoke(this, new CommandResponseEventArgs("INT\r\n", ResponseType.Success));
            return CommandResult.Success;
        }

        /// <summary>
        /// Handle DATA command - return to data transfer mode when connection is active
        /// </summary>
        private CommandResult HandleDataCommand(X28Command command)
        {
            if (_currentMode == PadMode.Command)
            {
                SwitchMode(PadMode.DataTransfer);
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("*** Resuming Data Transfer Mode ***\r\n", ResponseType.Success));
                return CommandResult.Success;
            }
            CommandResponse?.Invoke(this, new CommandResponseEventArgs("Already in data transfer mode\r\n", ResponseType.Error));
            return CommandResult.NotAllowedInCurrentMode;
        }

        /// <summary>
        /// Handle backspace in command mode
        /// </summary>
        private bool HandleCommandBackspace()
        {
            if (_commandBuffer.Length > 0)
            {
                _commandBuffer.Length--;
                CharacterEcho?.Invoke(this, new CharacterEchoEventArgs('\b', EchoType.Backspace));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Process character in data transfer mode
        /// </summary>
        /// <summary>
        /// The size of the editing buffer in the data transfer state, and of a full packet. X.28 (12/97) 3.6.1.2: the
        /// editing buffer "will not be smaller than the full packet size and will not be smaller than the value of
        /// parameter 10 plus one. It should be noted however, that in some networks the size of the editing buffer may be
        /// limited to a maximum of 128 octets." 128 octets is also the standard maximum user data field of X.25 (4.3.2),
        /// the packet this PAD forwards.
        /// </summary>
        private const int EditingBufferSize = 128;

        /// <summary>The full packet the PAD forwards: 128 octets (X.25 4.3.2).</summary>
        private const int FullPacketSize = 128;

        /// <summary>
        /// The first character of a two-character delete sequence (X.3 3.16, parameter 16 = 128, 129 or 130) that has
        /// been received and not yet acted on; 0 when there is none.
        /// </summary>
        private char _pendingDeleteLead;

        /// <summary>
        /// BS (0/8). While editing is on, it deletes a character as well as the delete character set in parameter 16.
        /// X.3 names only one delete character; this is our choice, not the standard (decided 2026-11-18): the
        /// Backspace key of a Windows console and many terminal programs sends BS, while hosts such as the Altos chat
        /// set DEL (7/15), so Backspace was sent on as data.
        /// </summary>
        private const char BackspaceDelete = (char)0x08;

        /// <summary>
        /// One character from the terminal in the data transfer state (X.28 (12/97) clause 4): PAD recall, the editing
        /// functions of 3.6 when parameter 15 is 1 (4.17), echo (4.10, X.3 3.2 and 3.20), data forwarding (4.4, X.3 3.3,
        /// 3.15) and linefeed insertion (X.3 3.13).
        /// </summary>
        private bool ProcessDataTransferCharacter(char c)
        {
            _logger?.LogDebug("X.28: Processing data transfer character '{Char}' (0x{Code:X2})", c, (int)c);

            // Check for escape to command mode
            if (IsEscapeCharacter(c))
            {
                return HandleEscapeCharacter();
            }

            bool editing = _x3Parameters.GetParameter(X3.ParameterNumber.EDITING) == 1;
            if (editing)
            {
                // X.3 3.16: values 128 to 130 are two-character sequences. The first character is held until the
                // second shows whether it is the sequence or an ordinary character.
                byte deleteValue = _x3Parameters.GetParameter(X3.ParameterNumber.CHARACTER_DELETE);
                if (deleteValue >= 128)
                {
                    char lead = deleteValue == 130 ? '*' : (char)0x13; // 128: 1/3 4/7; 129: 1/3 1/3; 130: 2/10 2/10
                    char second = deleteValue == 128 ? 'G' : deleteValue == 129 ? (char)0x13 : '*';
                    if (_pendingDeleteLead != 0)
                    {
                        char held = _pendingDeleteLead;
                        _pendingDeleteLead = (char)0;
                        if (c == second)
                        {
                            EchoTyped(held, true);
                            EchoTyped(c, true);
                            HandleCharacterDelete();
                            return true;
                        }

                        TakeDataCharacter(held, editing);
                    }

                    if (c == BackspaceDelete)
                    {
                        EchoTyped(c, true);
                        HandleCharacterDelete();
                        return true;
                    }

                    if (c == lead)
                    {
                        _pendingDeleteLead = c;
                        return true;
                    }
                }
                else if (c == deleteValue || c == BackspaceDelete)
                {
                    // X.28 3.6.2.1. The character itself is echoed unless parameter 20 masks it (4.10).
                    EchoTyped(c, true);
                    HandleCharacterDelete();
                    return true;
                }

                if (c == _x3Parameters.GetParameter(X3.ParameterNumber.LINE_DELETE))
                {
                    EchoTyped(c, true);
                    HandleLineDelete();
                    return true;
                }

                if (c == _x3Parameters.GetParameter(X3.ParameterNumber.LINE_DISPLAY))
                {
                    EchoTyped(c, true);
                    HandleLineDisplay();
                    return true;
                }
            }

            TakeDataCharacter(c, editing);
            return true;
        }

        /// <summary>
        /// A character that is data for the host: it is echoed, put in the buffer and the forwarding conditions are
        /// looked at.
        /// </summary>
        private void TakeDataCharacter(char c, bool editing)
        {
            // X.28 4.10: echoed "at the earliest opportunity" - before the line goes, so the echo of the CR that ends a
            // line is on the screen before anything the host answers to that line.
            EchoTyped(c, false);

            _inputBuffer.Append(c);

            // X.3 3.13 value 2: "insert linefeed after each carriage return in the data stream from the start-stop mode DTE".
            if (c == '\r' && (_x3Parameters.GetParameter(X3.ParameterNumber.LINE_FEED_INSERTION) & 2) != 0)
            {
                _inputBuffer.Append('\n');
            }

            var forwardingCondition = CheckForwardingCondition(c);
            if (forwardingCondition != ForwardingCondition.None)
            {
                ForwardBufferedData(forwardingCondition);
            }
            else if (editing)
            {
                // X.3 3.15: editing suspends "data forwarding on full packet until the editing buffer is full". X.28
                // 3.6.1.3: "Whenever more than enough data has been received to fill the editing buffer, one full packet
                // will be forwarded. The remaining characters will be placed in the beginning of the editing buffer and
                // editing of these characters remains possible."
                if (_inputBuffer.Length > EditingBufferSize)
                {
                    ForwardFirst(FullPacketSize);
                }
            }
            else if (_inputBuffer.Length >= FullPacketSize)
            {
                ForwardBufferedData(ForwardingCondition.BufferFull);
            }

            // X.3 3.15: editing also suspends "data forwarding on idle timer period expiry"; RestartIdleTimer looks.
            RestartIdleTimer();
        }

        /// <summary>
        /// Check if character should trigger data forwarding per ITU-T X.3 specification
        ///
        /// ITU-T X.3 (03/00) section 3.3, reference 3: parameter 3 is a sum of flags, not a character code.
        /// Value 0 means no data forwarding character. Otherwise the characters that forward are the union of:
        /// 1 = alphanumeric characters (A to Z, a to z, 0 to 9),
        /// 2 = CR,
        /// 4 = ESC, BEL, ENQ, ACK,
        /// 8 = DEL, CAN, DC2,
        /// 16 = ETX, EOT,
        /// 32 = HT, LF, VT, FF,
        /// 64 = all other characters in columns 0 and 1 of International Alphabet No. 5.
        /// So the value 2 forwards on CR, and the value 13 (1 + 4 + 8) does not.
        /// </summary>
        private ForwardingCondition CheckForwardingCondition(char c)
        {
            byte forwarding = _x3Parameters.GetParameter(X3.ParameterNumber.DATA_FORWARDING);

            // No forwarding
            if (forwarding == 0)
                return ForwardingCondition.None;

            if ((forwarding & X3.DataForwardingFlag.ALPHANUMERIC) != 0 && char.IsAsciiLetterOrDigit(c))
            {
                return ForwardingCondition.AlphanumericCharacter;
            }

            if ((forwarding & X3.DataForwardingFlag.CARRIAGE_RETURN) != 0 && c == '\r')
            {
                return ForwardingCondition.CarriageReturn;
            }

            // The groups of control characters below are columns 0 and 1 of IA5 plus DEL.
            // A character that is in a group whose flag is off does not fall into flag 64:
            // flag 64 is only for the characters that are in none of the groups.
            if (c < 32 || c == 127)
            {
                byte group = GetControlCharacterGroup(c);
                if ((forwarding & group) != 0)
                {
                    return ForwardingCondition.ControlCharacter;
                }
            }

            // A full packet is not a character condition; TakeDataCharacter looks at it, because editing changes it.
            return ForwardingCondition.None;
        }

        /// <summary>
        /// Gives the X.3 reference 3 flag that covers a control character (columns 0 and 1 of IA5, and DEL).
        /// </summary>
        /// <param name="c">
        /// A character below 32, or DEL.
        /// </param>
        /// <returns>
        /// The flag of the group the character belongs to; flag 64 for a character in none of the named groups.
        /// </returns>
        private static byte GetControlCharacterGroup(char c)
        {
            switch (c)
            {
                case (char)27: // ESC
                case (char)7:  // BEL
                case (char)5:  // ENQ
                case (char)6:  // ACK
                    return X3.DataForwardingFlag.ESC_BEL_ENQ_ACK;
                case (char)127: // DEL
                case (char)24:  // CAN
                case (char)18:  // DC2
                    return X3.DataForwardingFlag.DEL_CAN_DC2;
                case (char)3: // ETX
                case (char)4: // EOT
                    return X3.DataForwardingFlag.ETX_EOT;
                case '\t':    // HT
                case '\n':    // LF
                case (char)11: // VT
                case (char)12: // FF
                    return X3.DataForwardingFlag.HT_LF_VT_FF;
                case '\r':    // CR has its own flag, handled before this is called
                    return 0;
                default:
                    return X3.DataForwardingFlag.OTHER_COLUMNS_0_1;
            }
        }

        /// <summary>
        /// Forward buffered data to network
        /// </summary>
        private void ForwardBufferedData(ForwardingCondition condition)
        {
            if (_inputBuffer.Length == 0)
                return;
                
            string data = _inputBuffer.ToString();
            _inputBuffer.Clear();
            
            _logger?.LogTrace("X.28: Forwarding {Length} characters (condition: {Condition})", 
                data.Length, condition);
            
            // Fire data forwarded event
            DataForwarded?.Invoke(this, new X28DataEventArgs(data, condition));
        }

        /// <summary>
        /// Forwards the first <paramref name="count"/> characters of the buffer as one packet and keeps the rest in the
        /// buffer, still editable (X.28 3.6.1.3).
        /// </summary>
        private void ForwardFirst(int count)
        {
            string data = _inputBuffer.ToString(0, count);
            _inputBuffer.Remove(0, count);
            DataForwarded?.Invoke(this, new X28DataEventArgs(data, ForwardingCondition.BufferFull));
        }

        /// <summary>
        /// Echoes one typed character as X.28 4.10 and X.3 3.2, 3.13 and 3.20 say.
        /// </summary>
        /// <param name="c">The character the terminal sent.</param>
        /// <param name="isEditingCharacter">True when the character is doing an editing function of parameter 16, 17 or 18
        /// (editing is then on: X.3 3.20 Note 6, value 64 "does not apply if editing is not enabled").</param>
        private void EchoTyped(char c, bool isEditingCharacter)
        {
            // X.3 3.2: 0 no echo; 1 echo; 2 "echo all characters except the data forwarding sequence defined by parameter
            // 25" - this PAD has no parameter 25, so there is no such sequence and 2 echoes like 1; 32 to 126 "scrambled
            // echo character", "The decimal value represents the character to be transmitted back" (Note 2).
            byte echo = _x3Parameters.GetParameter(X3.ParameterNumber.ECHO);
            if (echo == 0 || IsEchoMasked(c, isEditingCharacter))
            {
                return;
            }

            char shown = echo >= 32 && echo <= 126 ? (char)echo : c;
            CharacterEcho?.Invoke(this, new CharacterEchoEventArgs(shown, EchoType.Normal));

            // X.3 3.13 value 4: "insert linefeed after each carriage return in the echo stream to the start-stop mode DTE".
            if (c == '\r' && shown == '\r' && (_x3Parameters.GetParameter(X3.ParameterNumber.LINE_FEED_INSERTION) & 4) != 0)
            {
                CharacterEcho?.Invoke(this, new CharacterEchoEventArgs('\n', EchoType.Normal));
            }
        }

        /// <summary>
        /// True when parameter 20 (echo mask, X.3 3.20) or its notes say the character is not echoed. "A character need
        /// only be specified by a single value of parameter 20 in order for it not to be echoed" (Note 4).
        /// </summary>
        private bool IsEchoMasked(char c, bool isEditingCharacter)
        {
            // Note 2: "If parameter 5, 12 or 22 is set to a non-zero value, then X-ON and X-OFF are not echoed."
            if ((c == (char)0x11 || c == (char)0x13)
                && (_x3Parameters.GetParameter(X3.ParameterNumber.ANCILLARY_DEVICE_CONTROL) != 0
                    || _x3Parameters.GetParameter(X3.ParameterNumber.FLOW_CONTROL_X_ON_X_OFF) != 0
                    || _x3Parameters.GetParameter(X3.ParameterNumber.PAGE_WAIT) != 0))
            {
                return true;
            }

            byte mask = _x3Parameters.GetParameter(X3.ParameterNumber.ECHO_MASK);
            if (mask == 0)
            {
                return false;
            }

            // 64: "no echo of editing characters as designated by parameters 16, 17 and 18".
            if ((mask & 64) != 0 && isEditingCharacter)
            {
                return true;
            }

            switch (c)
            {
                case '\r':
                    return (mask & 1) != 0;   // "no echo of character CR"
                case '\n':
                    return (mask & 2) != 0;   // "no echo of character LF"
                case (char)0x0B:
                case '\t':
                case (char)0x0C:
                    return (mask & 4) != 0;   // "no echo of characters VT, HT, FF"
                case (char)0x07:
                case (char)0x08:
                    return (mask & 8) != 0;   // "no echo of characters BEL, BS"
                case (char)0x1B:
                case (char)0x05:
                    return (mask & 16) != 0;  // "no echo of characters ESC, ENQ"
                case (char)0x06:
                case (char)0x15:
                case (char)0x02:
                case (char)0x01:
                case (char)0x04:
                case (char)0x17:
                case (char)0x03:
                    return (mask & 32) != 0;  // "no echo of characters ACK, NAK, STX, SOH, EOT, ETB, ETX"
            }

            // 128: "no echo of all other characters in columns 0 and 1 of IA5 not mentioned above, and the character DEL".
            if (c < (char)0x20 || c == (char)0x7F)
            {
                return (mask & 128) != 0;
            }

            return false;
        }

        /// <summary>
        /// True when parameter 6 lets editing PAD service signals out and parameter 19 asks for some (X.3 3.19: "This
        /// parameter does not apply if the value of parameter 6 is set to 0").
        /// </summary>
        private byte EditingSignalStyle()
        {
            if (_x3Parameters.GetParameter(X3.ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS) == 0)
            {
                return 0;
            }

            return _x3Parameters.GetParameter(X3.ParameterNumber.EDITING_PAD_SERVICE_SIGNALS);
        }

        private void SendSignal(char c)
        {
            CharacterEcho?.Invoke(this, new CharacterEchoEventArgs(c, EchoType.Delete));
        }

        /// <summary>
        /// The format effector, X.28 3.5.2: "The characters 0/13 (CR) 0/10 (LF) will be sent by the PAD", followed by
        /// padding: parameter 9's count, or when it is 0 a count set by the terminal speed (<see cref="X3.ServiceSignalPadding"/>).
        /// The padding character is NUL (3.5.20). Until 2026-11-21 no padding was sent (RetroIO BUGS.md).
        /// </summary>
        private void SendFormatEffector()
        {
            SendSignal('\r');
            SendSignal('\n');
            int padding = _x3Parameters.ServiceSignalPadding();
            for (int i = 0; i < padding; i++)
            {
                SendSignal('\0');
            }
        }

        private static bool IsGraphic(char c)
        {
            return c >= (char)0x20 && c <= (char)0x7E;
        }

        /// <summary>
        /// X.28 3.6.2.1: "The receipt of each character delete character causes the last character currently in the
        /// editing buffer to be deleted. Subsequently, if the value of parameter 6 is not set to 0, if the value of
        /// parameter 19 is set to 2 or 8 and a graphic character is removed from the editing buffer, or, if the value of
        /// parameter 19 is set to 1 or 32 to 126, and a character is removed from the editing buffer, the character
        /// deleted PAD service signal will be sent by the PAD." Its format is 3.5.24.
        /// </summary>
        private void HandleCharacterDelete()
        {
            if (_inputBuffer.Length == 0)
            {
                return;
            }

            char removed = _inputBuffer[_inputBuffer.Length - 1];
            _inputBuffer.Length--;

            byte style = EditingSignalStyle();
            if (style == 1)
            {
                SendSignal('\\');                // 3.5.24 "Value 1: the character 5/12 (\) will be sent"
            }
            else if (style == 2 && IsGraphic(removed))
            {
                SendSignal('\b');                // "Value 2: the characters 0/8 (BS) 2/0 (SP) 0/8 (BS) will be sent"
                SendSignal(' ');
                SendSignal('\b');
            }
            else if (style == 8 && IsGraphic(removed))
            {
                SendSignal((char)8);             // "Value 8, 32-126: one character from International Alphabet No. 5"
            }
            else if (style >= 32 && style <= 126)
            {
                SendSignal((char)style);
            }
        }

        /// <summary>
        /// X.28 3.6.2.2: "The receipt of the line delete character causes the deletion of the current contents of the
        /// editing buffer. The PAD will transmit, if parameter 6 and parameter 19 are not set to 0 and the editing buffer
        /// is not empty, a line deleted PAD service signal." Its format is 3.5.25.
        /// </summary>
        private void HandleLineDelete()
        {
            if (_inputBuffer.Length == 0)
            {
                return;
            }

            int graphic = 0;
            for (int i = 0; i < _inputBuffer.Length; i++)
            {
                if (IsGraphic(_inputBuffer[i]))
                {
                    graphic++;
                }
            }

            _inputBuffer.Clear();

            byte style = EditingSignalStyle();
            if (style == 2)
            {
                // "Value 2: the characters 0/8 (BS), 2/0 (SP), 0/8 (BS) will be sent the number of times equal to the
                // number of graphic characters being deleted from the buffer."
                for (int i = 0; i < graphic; i++)
                {
                    SendSignal('\b');
                    SendSignal(' ');
                    SendSignal('\b');
                }
            }
            else if (style == 1 || style == 8 || (style >= 32 && style <= 126))
            {
                // "Value 1, 8, 32-126: the characters 5/8 (X), 5/8 (X), 5/8 (X) will be sent followed by the format effector".
                SendSignal('X');
                SendSignal('X');
                SendSignal('X');
                SendFormatEffector();
            }
        }

        /// <summary>
        /// X.28 3.6.2.3: "The receipt of the line display character causes the PAD to transmit to the start-stop mode DTE
        /// a format effector followed by the characters currently stored in the editing buffer."
        /// </summary>
        private void HandleLineDisplay()
        {
            SendFormatEffector();
            for (int i = 0; i < _inputBuffer.Length; i++)
            {
                SendSignal(_inputBuffer[i]);
            }
        }

        /// <summary>
        /// True when a character typed in the data transfer state is the PAD recall character set in parameter 1.
        /// </summary>
        /// <remarks>
        /// X.3 (03/2000) 3.1, PAD recall using a character:
        ///  - 0: not possible.
        ///  - 1: the character 1/0, DLE (Ctrl-P).
        ///  - 32 to 126: "graphic character defined by the user", the character whose T.50 code is that value (43 is '+').
        /// Until 2026-11-21 only value 1 recalled: <see cref="X3.IsValidParameter"/> accepted 32 to 126 and the PAD then
        /// never recalled on that character (RetroIO BUGS 47).
        /// </remarks>
        /// <param name="c">
        /// The character from the terminal.
        /// </param>
        /// <returns>
        /// True when <paramref name="c"/> recalls the PAD.
        /// </returns>
        private bool IsEscapeCharacter(char c)
        {
            byte escapeParam = _x3Parameters.GetParameter(X3.ParameterNumber.ESCAPE);
            if (escapeParam == 1)
            {
                return c == 0x10; // DLE
            }

            return escapeParam >= 32 && escapeParam <= 126 && c == (char)escapeParam;
        }

        /// <summary>
        /// Handle escape character processing - temporarily return to command mode
        /// Connection remains active but user can issue PAD commands
        /// </summary>
        private bool HandleEscapeCharacter()
        {
            _logger?.LogDebug("X.28: PAD escape character detected, switching to command mode (connection maintained)");
            SwitchMode(PadMode.Command);
            // X.28 3.1.3 / 3.5.23: the PAD shows it is ready for a command with the prompt PAD service signal, after a
            // format effector. Until 2026-11-19 this printed "*** PAD Command Mode (connection maintained) ***" and
            // "CMD: ", which X.28 has no signal for, and differed from the PAD's own prompt.
            CommandResponse?.Invoke(this, new CommandResponseEventArgs("\r\n" + PromptText, ResponseType.Success));
            return true;
        }

        /// <summary>
        /// The prompt PAD service signal this PAD shows (X.28 3.5.23). The standard one is <c>*</c>; X.28 permits other
        /// characters "for an interim period", and this PAD has always shown <c>PAD&gt; </c>.
        /// </summary>
        public const string PromptText = "PAD> ";

        /// <summary>
        /// Process character in escape mode (not implemented in basic X.28)
        /// </summary>
        private bool ProcessEscapeModeCharacter(char c)
        {
            // Return to data transfer mode
            SwitchMode(PadMode.DataTransfer);
            return ProcessDataTransferCharacter(c);
        }

        /// <summary>
        /// Switch PAD operating mode
        /// </summary>
        private void SwitchMode(PadMode newMode)
        {
            if (_currentMode != newMode)
            {
                var oldMode = _currentMode;
                _currentMode = newMode;
                
                _logger?.LogDebug("🚨 X.28: MODE CHANGED: {OldMode} → {NewMode} 🚨", oldMode, newMode);
                
                // Clean up based on mode transition
                if (newMode == PadMode.Command)
                {
                    StopIdleTimer();
                    if (_inputBuffer.Length > 0)
                    {
                        _logger?.LogDebug("X.28: Forwarding {Length} buffered characters before switching to command mode", _inputBuffer.Length);
                        ForwardBufferedData(ForwardingCondition.ManualForward);
                    }
                }
                else if (newMode == PadMode.DataTransfer)
                {
                    _logger?.LogDebug("X.28: Switched to data transfer mode - clearing command buffer and starting idle timer");
                    _commandBuffer.Clear();
                    RestartIdleTimer();
                }
                
                // Fire mode changed event
                ModeChanged?.Invoke(this, new X28ModeChangedEventArgs(oldMode, newMode));
            }
            else
            {
                _logger?.LogDebug("X.28: Mode switch requested to {Mode} but already in that mode", newMode);
            }
        }

        /// <summary>
        /// Restart idle timer based on X.3 parameter 4
        /// </summary>
        private void RestartIdleTimer()
        {
            StopIdleTimer();
            
            byte idleTimeout = _x3Parameters.GetParameter(X3.ParameterNumber.IDLE_TIMER);

            // X.3 3.15: editing "suspends ... data forwarding on idle timer period expiry"; X.28 3.6.1.3 a) "the PAD will
            // not take account of the value of the idle time delay defined by the value of parameter 4".
            bool editing = _x3Parameters.GetParameter(X3.ParameterNumber.EDITING) == 1;
            if (idleTimeout > 0 && !editing && _currentMode == PadMode.DataTransfer)
            {
                int timeoutMs = idleTimeout * 50; // 50ms units (1/20 second)
                _idleTimer = new System.Threading.Timer(OnIdleTimeout, null, timeoutMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Stop idle timer
        /// </summary>
        private void StopIdleTimer()
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        /// <summary>
        /// Handle idle timer expiry
        /// </summary>
        private void OnIdleTimeout(object? state)
        {
            try
            {
                // Editing may have been turned on (a host's set) after the timer started: it then forwards nothing.
                if (_inputBuffer.Length > 0 && _x3Parameters.GetParameter(X3.ParameterNumber.EDITING) != 1)
                {
                    ForwardBufferedData(ForwardingCondition.Timer);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "X.28: Error in idle timeout handler");
            }
        }

        /// <summary>
        /// Force forward any buffered data
        /// </summary>
        public void ForceForwardData()
        {
            // A held first character of a delete sequence (parameter 16 = 128 to 130) was data after all.
            if (_pendingDeleteLead != 0)
            {
                _inputBuffer.Append(_pendingDeleteLead);
                _pendingDeleteLead = (char)0;
            }

            if (_inputBuffer.Length > 0)
            {
                ForwardBufferedData(ForwardingCondition.ManualForward);
            }
        }

        /// <summary>
        /// Get current input buffer content
        /// </summary>
        public string GetBufferedInput()
        {
            return _inputBuffer.ToString();
        }

        /// <summary>
        /// Clear input buffer
        /// </summary>
        public void ClearInputBuffer()
        {
            _inputBuffer.Clear();
            _logger?.LogTrace("X.28: Input buffer cleared");
        }

        /// <summary>
        /// Process PAD command and return response
        /// </summary>
        /// <param name="command">
        /// Command string to process
        /// </param>
        /// <returns>
        /// Response string
        /// </returns>
        public CommandResult ProcessCommand(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                ErrorMessage?.Invoke(this, new ErrorMessageEventArgs("Empty command"));
                return CommandResult.InvalidCommand;
            }

            var cmd = command.Trim().ToUpperInvariant();
            
            // Common X.28 commands
            switch (cmd)
            {
                case "STAT":
                    CommandResponse?.Invoke(this, new CommandResponseEventArgs(StatusSignal() + "\r\n", ResponseType.Success));
                    return CommandResult.Success;
                case "CLR":
                    return CommandResult.Success; // Clear - handled by disconnect
                case "RESET":
                    CommandResponse?.Invoke(this, new CommandResponseEventArgs("\r\n", ResponseType.Success));
                    return CommandResult.Success;
                default:
                    CommandResponse?.Invoke(this, new CommandResponseEventArgs(command + "\r\n", ResponseType.Success)); // Pass through unknown commands
                    return CommandResult.Success;
            }
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            StopIdleTimer();
            _commandBuffer.Clear();
            _inputBuffer.Clear();
        }
    }

    /// <summary>
    /// Which network's PAD this PAD looks like where the networks differed from plain X.28 (RetroX25
    /// docs/research-os.md section 1).
    /// </summary>
    public enum PadStyle
    {
        /// <summary>X.28 (12/97) as written.</summary>
        X28 = 0,

        /// <summary>BT PSS (UK): the clear indication carries the call time and the packet counts.</summary>
        Pss = 1,
    }

    /// <summary>
    /// X.28 command structure
    /// </summary>
    public class X28Command
    {
        /// <summary>
        /// Gets or sets the command verb.
        /// </summary>
        public string Verb { get; set; } = "";
        /// <summary>
        /// Gets or sets the command arguments.
        /// </summary>
        public List<string> Arguments { get; set; } = new List<string>();
        /// <summary>
        /// Gets or sets the command line as typed.
        /// </summary>
        public string OriginalLine { get; set; } = "";
        /// <summary>
        /// Gets or sets the called address of a call command, or null.
        /// </summary>
        public string? Address { get; set; }
        /// <summary>
        /// Gets or sets the NUI given for this one call in its facility request block (<c>N&lt;nui&gt;-&lt;address&gt;</c>,
        /// X.28 3.5.15.1.1), or null. It is used instead of the one given with <c>ID</c>.
        /// </summary>
        public string? CallNui { get; set; }
        /// <summary>
        /// Gets or sets the profile name of a profile command, or null.
        /// </summary>
        public string? ProfileName { get; set; }
        /// <summary>
        /// Gets or sets the parameter number and value pairs of a command, or null.
        /// </summary>
        public Dictionary<byte, byte>? Parameters { get; set; }
        /// <summary>
        /// The list items of a read, set or set and read command (X.28 3.5.4.1, 3.5.6.1), as typed between the commas:
        /// <c>2:0</c>, <c>ech:0</c>, <c>5</c>.
        /// </summary>
        public List<string> Items { get; } = new List<string>();
    }

    /// <summary>
    /// Event arguments for X.28 interface events
    /// </summary>
    public class X28CommandEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the parsed command.
        /// </summary>
        public X28Command Command { get; }
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="command">
        /// The parsed command.
        /// </param>
        public X28CommandEventArgs(X28Command command) => Command = command;
    }

    /// <summary>
    /// Event arguments for forwarded data.
    /// </summary>
    public class X28DataEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the forwarded data.
        /// </summary>
        public string Data { get; }
        /// <summary>
        /// Gets the condition that triggered the forwarding.
        /// </summary>
        public X28.ForwardingCondition Condition { get; }
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="data">
        /// The forwarded data.
        /// </param>
        /// <param name="condition">
        /// The condition that triggered the forwarding.
        /// </param>
        public X28DataEventArgs(string data, X28.ForwardingCondition condition)
        {
            Data = data;
            Condition = condition;
        }
    }

    /// <summary>
    /// Event arguments for a PAD mode change.
    /// </summary>
    public class X28ModeChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the mode before the change.
        /// </summary>
        public X28.PadMode OldMode { get; }
        /// <summary>
        /// Gets the mode after the change.
        /// </summary>
        public X28.PadMode NewMode { get; }
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="oldMode">
        /// The mode before the change.
        /// </param>
        /// <param name="newMode">
        /// The mode after the change.
        /// </param>
        public X28ModeChangedEventArgs(X28.PadMode oldMode, X28.PadMode newMode)
        {
            OldMode = oldMode;
            NewMode = newMode;
        }
    }

    /// <summary>
    /// Event arguments for character-level processing.
    /// </summary>
    public class X28CharacterEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the character.
        /// </summary>
        public char Character { get; }
        /// <summary>
        /// Gets the response text, or null when there is none.
        /// </summary>
        public string? Response { get; }
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="character">
        /// The character.
        /// </param>
        /// <param name="response">
        /// The response text, or null.
        /// </param>
        public X28CharacterEventArgs(char character, string? response)
        {
            Character = character;
            Response = response;
        }
    }

    /// <summary>
    /// Command processing results - PROPER ARCHITECTURE
    /// </summary>
    public enum CommandResult 
    {
        /// <summary>
        /// The command succeeded.
        /// </summary>
        Success,
        /// <summary>
        /// The command was not recognized.
        /// </summary>
        InvalidCommand,
        /// <summary>
        /// The command parameters were not valid.
        /// </summary>
        InvalidParameters, 
        /// <summary>
        /// The command is not allowed in the current mode.
        /// </summary>
        NotAllowedInCurrentMode,
        /// <summary>
        /// The command needs a connection.
        /// </summary>
        ConnectionRequired,
        /// <summary>
        /// The command failed.
        /// </summary>
        Error
    }

    /// <summary>
    /// Character echo types
    /// </summary>
    public enum EchoType
    {
        /// <summary>
        /// A normal character.
        /// </summary>
        Normal,
        /// <summary>
        /// A backspace.
        /// </summary>
        Backspace,
        /// <summary>
        /// A delete.
        /// </summary>
        Delete,
        /// <summary>
        /// A carriage return.
        /// </summary>
        CarriageReturn,
        /// <summary>
        /// A line feed.
        /// </summary>
        LineFeed
    }

    /// <summary>
    /// Response types for command responses
    /// </summary>
    public enum ResponseType
    {
        /// <summary>
        /// Response to a successful command.
        /// </summary>
        Success,
        /// <summary>
        /// Response to a failed command.
        /// </summary>
        Error,
        /// <summary>
        /// Status text.
        /// </summary>
        Status,
        /// <summary>
        /// A response that is shown immediately, such as COM.
        /// </summary>
        Immediate,  // For immediate responses like "COM"
        /// <summary>
        /// A prompt.
        /// </summary>
        Prompt
    }

    /// <summary>
    /// Event args for character echo - PROPER ARCHITECTURE
    /// </summary>
    public class CharacterEchoEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the echoed character.
        /// </summary>
        public char Character { get; }
        /// <summary>
        /// Gets the kind of echo.
        /// </summary>
        public EchoType Type { get; }
        
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="character">
        /// The echoed character.
        /// </param>
        /// <param name="type">
        /// The kind of echo.
        /// </param>
        public CharacterEchoEventArgs(char character, EchoType type = EchoType.Normal)
        {
            Character = character;
            Type = type;
        }
    }

    /// <summary>
    /// Event args for command responses - PROPER ARCHITECTURE
    /// </summary>
    public class CommandResponseEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the response text.
        /// </summary>
        public string Response { get; }
        /// <summary>
        /// Gets whether the response is shown immediately.
        /// </summary>
        public bool IsImmediate { get; }
        /// <summary>
        /// Gets the response type.
        /// </summary>
        public ResponseType Type { get; }
        
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="response">
        /// The response text.
        /// </param>
        /// <param name="type">
        /// The response type.
        /// </param>
        /// <param name="isImmediate">
        /// Whether the response is shown immediately.
        /// </param>
        public CommandResponseEventArgs(string response, ResponseType type = ResponseType.Success, bool isImmediate = false)
        {
            Response = response;
            Type = type;
            IsImmediate = isImmediate;
        }
    }

    /// <summary>
    /// Event args for error messages - PROPER ARCHITECTURE
    /// </summary>
    public class ErrorMessageEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the error message text.
        /// </summary>
        public string ErrorMessage { get; }
        
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="errorMessage">
        /// The error message text.
        /// </param>
        public ErrorMessageEventArgs(string errorMessage)
        {
            ErrorMessage = errorMessage;
        }
    }
}