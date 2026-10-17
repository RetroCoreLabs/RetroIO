//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Servers.Tad
{
    /// <summary>
    /// The login phases a TAD server session walks before the command loop is reachable.
    /// </summary>
    public enum TadServerLoginPhase
    {
        /// <summary>
        /// Awaiting the username line (the banner's "ENTER " prompt).
        /// </summary>
        Username,

        /// <summary>
        /// Awaiting the password line (no-echo).
        /// </summary>
        Password,

        /// <summary>
        /// A valid login completed.
        /// </summary>
        LoggedIn,
    }

    /// <summary>
    /// All per-session state for one TAD terminal connection the server answers: the client endpoint,
    /// the allocated session port and TAD number, the login/negotiation state, the connect-letter
    /// metadata, and the pending tty output queue. One instance exists per concurrent connect.
    /// </summary>
    /// <remarks>
    /// The transport SEQUENCING is NOT here - the node owns the per-link Flags 1 - so two sessions to
    /// the same node interleave on one continuous outgoing counter. Output text is queued here and the
    /// server drains it to frames, which is what makes tty inject / wall async and terminal-agnostic.
    /// </remarks>
    public sealed class TadServerSession
    {
        private readonly List<string> _pendingOutput;

        /// <summary>
        /// Initialises a session for a connect from a given client endpoint.
        /// </summary>
        /// <param name="remoteNode">
        /// The client's node number (header source).
        /// </param>
        /// <param name="clientSystem">
        /// The client's system number (sub-header source system).
        /// </param>
        /// <param name="clientPort">
        /// The client's source port (stable for the session's lifetime).
        /// </param>
        /// <param name="sessionWirePort">
        /// The unique session port allocated for this session.
        /// </param>
        /// <param name="tadNumber">
        /// The TAD number (ttyN) shown to operators and used by who/tell/wall.
        /// </param>
        public TadServerSession(ushort remoteNode, ushort clientSystem, ushort clientPort, ushort sessionWirePort, int tadNumber)
        {
            RemoteNode = remoteNode;
            ClientSystem = clientSystem;
            ClientPort = clientPort;
            SessionWirePort = sessionWirePort;
            TadNumber = tadNumber;
            Phase = TadServerLoginPhase.Username;
            PendingUsername = string.Empty;
            Username = string.Empty;
            OsVersion = Array.Empty<byte>();
            ConnectService = string.Empty;
            ConnectTargetName = string.Empty;
            _pendingOutput = new List<string>();
        }

        /// <summary>
        /// Gets the client's node number (header source).
        /// </summary>
        public ushort RemoteNode { get; }

        /// <summary>
        /// Gets the client's system number (sub-header source system).
        /// </summary>
        public ushort ClientSystem { get; }

        /// <summary>
        /// Gets the client's source port (stable for the whole session).
        /// </summary>
        public ushort ClientPort { get; }

        /// <summary>
        /// Gets the unique session port allocated for this session.
        /// </summary>
        public ushort SessionWirePort { get; }

        /// <summary>
        /// Gets the TAD number (ttyN) for this session.
        /// </summary>
        public int TadNumber { get; }

        /// <summary>
        /// Gets or sets the current login phase.
        /// </summary>
        public TadServerLoginPhase Phase { get; set; }

        /// <summary>
        /// Gets or sets the username entered but not yet validated (during the password phase).
        /// </summary>
        public string PendingUsername { get; set; }

        /// <summary>
        /// Gets or sets the logged-in username (empty until a valid login).
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// Gets or sets the count of wrong-credential attempts.
        /// </summary>
        public int LoginFaults { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the MOTD has been sent.
        /// </summary>
        public bool MotdSent { get; set; }

        /// <summary>
        /// Gets or sets the number of RECOs received during bring-up. The captured ladder is
        /// ESCA -> ESRS+RESE#1, first RECO -> RESE#2, second RECO -> MOTD
        /// (XMSG-TAD-REAL-SETUP-REFERENCE-2026-07-07.md section 1).
        /// </summary>
        public int BringupRecoCount { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the escape function is currently ENABLED on our
        /// side of this session. Starts enabled.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the state a CESC message reports: <c>BCESC</c> in the version J driver
        /// (<c>SINTRAN/NPL-SOURCE-2/NPL-CLEAN/06-COS-TAD-RES-CODE.NPL</c>) builds the payload as
        /// <c>IF DFLAG BIT 5IESC THEN 0 ELSE 1</c>, so every <c>CESC 00</c> we send announces that we
        /// have inhibited escape and every <c>CESC 01</c> announces that we have re-enabled it.
        /// </para>
        /// <para>
        /// It matters on the receive side: an ESCA or RLOC arriving while escape is inhibited must be
        /// answered with EDRS (0x29), not ESRS (0x20) - see <c>ESCDIS</c> in
        /// <c>20-COS-TAD-POF-CODE.NPL</c>.
        /// </para>
        /// </remarks>
        public bool EscapeEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the opcode carried by the last REJE (0xFE) the peer sent us, or -1 when the
        /// peer has never rejected anything.
        /// </summary>
        /// <remarks>
        /// A reject means the far driver did not accept a message we sent and has failed its own
        /// caller with TER01. Recording the offending type is the difference between "the session
        /// went quiet" and "they refused our 0x03".
        /// </remarks>
        public int LastRejectedOpcode { get; set; } = -1;

        /// <summary>
        /// Gets a value indicating whether this session has completed login.
        /// </summary>
        public bool IsLoggedIn
        {
            get { return Phase == TadServerLoginPhase.LoggedIn; }
        }

        // --- Connect resync (XENSE) state -------------------------------------------------------

        /// <summary>
        /// Gets or sets the retained connect frame (to rebuild the accept during a resync).
        /// </summary>
        public XmsgFrame? ConnectFrame { get; set; }

        /// <summary>
        /// Gets or sets the Flags 1 our accept currently uses (stepped down on each XENSE).
        /// </summary>
        public ushort AcceptFlags1 { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the session-setup has been seen (resync stops).
        /// </summary>
        public bool SessionSetupSeen { get; set; }

        // --- Negotiation metadata (for the "stat" command) --------------------------------------

        /// <summary>
        /// Gets or sets a value indicating whether the TMOD/TTYP/DESC/OPSV chain was received.
        /// </summary>
        public bool NegotiationSeen { get; set; }

        /// <summary>
        /// Gets or sets the terminal type (TTYP 0x0D).
        /// </summary>
        public ushort TerminalType { get; set; }

        /// <summary>
        /// Gets or sets the terminal mode (TMOD 0x0C).
        /// </summary>
        public byte TerminalMode { get; set; }

        /// <summary>
        /// Gets or sets the escape character (DESC 0x0F).
        /// </summary>
        public byte EscapeChar { get; set; }

        /// <summary>
        /// Gets or sets the host OS-version bytes (OPSV 0x1F).
        /// </summary>
        public byte[] OsVersion { get; set; }

        /// <summary>
        /// Gets or sets the connect-letter service string (for example "*TADADM").
        /// </summary>
        public string ConnectService { get; set; }

        /// <summary>
        /// Gets or sets the connect-letter target system name (for example "D102").
        /// </summary>
        public string ConnectTargetName { get; set; }

        // --- tty output queue -------------------------------------------------------------------

        /// <summary>
        /// Gets a value indicating whether this session has queued output waiting to be drained.
        /// </summary>
        public bool HasPendingOutput
        {
            get { return _pendingOutput.Count != 0; }
        }

        /// <summary>
        /// Queues a segment of terminal text for this session (a command reply or an injected message).
        /// </summary>
        /// <param name="text">
        /// The text to append to the output queue.
        /// </param>
        public void Enqueue(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                _pendingOutput.Add(text);
            }
        }

        /// <summary>
        /// Takes and clears all queued output text, concatenated in order.
        /// </summary>
        /// <returns>
        /// The queued text, or an empty string when nothing was pending.
        /// </returns>
        public string TakePendingOutput()
        {
            if (_pendingOutput.Count == 0)
            {
                return string.Empty;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < _pendingOutput.Count; i++)
            {
                sb.Append(_pendingOutput[i]);
            }

            _pendingOutput.Clear();
            return sb.ToString();
        }

        // --- windowed terminal-output streaming (TAD 22.6 flow-control handshake) ----------------
        // A reply is streamed as bare 255-byte continuations then a short final frame carrying SYCN +
        // prompt + RFI, but only <= 2 output datagrams may be unacked at once, and 100 secure-ACKs each.
        // The session holds the remaining content, the prompt, and the Flags 1 of the sent-but-unacked
        // output frames so an incoming ACK (ConfirmOutputAck) can advance the window.

        private readonly HashSet<ushort> _outstandingOutput = new HashSet<ushort>();

        /// <summary>
        /// Gets the number of 255-byte continuation chunks sent in the current burst.
        /// </summary>
        public int ContinuationsSent { get; private set; }

        /// <summary>
        /// Gets the number of 7DUMMs 100 has sent back during the current burst - its CONSUMPTION signal
        /// (TAD-Message-Formats.md 22.16: DUMMs pair 1:1 with consumed continuations). Kept as a barrier
        /// input for the inter-pair cadence, NOT as the display gate (the window=1 DUMM experiment proved
        /// 1:1 DUMMs alone do not make 100 render the continuations - see 22.16 retraction note).
        /// </summary>
        public int DummsConsumed { get; private set; }

        /// <summary>
        /// Gets or sets the clock time the last continuation chunk was transmitted, so the drain can
        /// enforce the VERIFIED 45-47 ms intra-pair gap (22.16: the real host never fires the two chunks
        /// of a pair back-to-back; the ~46 ms spacing is the prime suspect for why a byte-identical burst
        /// renders only partially).
        /// </summary>
        public DateTime LastContinuationAt { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the first chunk of the current pair has been sent and
        /// the second chunk is still owed (gated only by the intra-pair gap timer, not by an ACK - 22.16
        /// shows the second chunk of a pair goes out BEFORE either chunk is acked).
        /// </summary>
        public bool PairAwaitingSecond { get; set; }

        /// <summary>
        /// Gets the content still to stream (without the trailing prompt).
        /// </summary>
        public string OutputContent { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the prompt emitted as its own BDAT in the final frame (for example "# ").
        /// </summary>
        public string OutputPrompt { get; private set; } = string.Empty;

        /// <summary>
        /// Gets or sets the offset into <see cref="OutputContent"/> already sent.
        /// </summary>
        public int OutputOffset { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the final (terminator) frame has been sent.
        /// </summary>
        public bool OutputFinalSent { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether an output burst is in progress.
        /// </summary>
        public bool OutputActive { get; set; }

        /// <summary>
        /// Gets the number of sent-but-unacked output datagrams (the flow-control window use).
        /// </summary>
        public int OutstandingOutputCount
        {
            get { return _outstandingOutput.Count; }
        }

        /// <summary>
        /// Begins a windowed output burst: the content (prompt already split off) to stream and the
        /// prompt to emit as its own BDAT in the final frame.
        /// </summary>
        /// <param name="content">
        /// The reply content without the trailing prompt.
        /// </param>
        /// <param name="prompt">
        /// The prompt (for example "# "), or empty for none.
        /// </param>
        public void BeginOutput(string content, string prompt)
        {
            OutputContent = content ?? string.Empty;
            OutputPrompt = prompt ?? string.Empty;
            OutputOffset = 0;
            OutputFinalSent = false;
            OutputActive = true;
            ContinuationsSent = 0;
            DummsConsumed = 0;
            LastContinuationAt = default;
            PairAwaitingSecond = false;
        }

        /// <summary>
        /// Records that a continuation chunk was sent (for the DUMM-consumption pacing).
        /// </summary>
        public void MarkContinuationSent()
        {
            ContinuationsSent++;
        }

        /// <summary>
        /// Records that 100 sent a 7DUMM during the burst (it consumed/displayed a continuation).
        /// </summary>
        public void NoteDummConsumed()
        {
            DummsConsumed++;
        }

        /// <summary>
        /// Records that an output datagram with the given Flags 1 was sent and is awaiting its ACK.
        /// </summary>
        /// <param name="flags1">
        /// The Flags 1 the sent output frame carries.
        /// </param>
        public void MarkOutputSent(ushort flags1)
        {
            _outstandingOutput.Add(flags1);
        }

        /// <summary>
        /// Clears an outstanding output datagram when its ACK arrives.
        /// </summary>
        /// <param name="flags1">
        /// The Flags 1 the ACK echoes.
        /// </param>
        /// <returns>
        /// True when the Flags 1 matched one of this session's outstanding output frames.
        /// </returns>
        public bool ConfirmOutputAck(ushort flags1)
        {
            return _outstandingOutput.Remove(flags1);
        }
    }
}
