//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// What a router decided for one incoming call.
    /// </summary>
    public enum X25CallAction
    {
        /// <summary>
        /// The call is for this node: a local service or a program waiting for calls takes it.
        /// </summary>
        DeliverLocally,

        /// <summary>
        /// The call goes on out of another line (a switch port); <see cref="X25CallDecision.OutPort"/> says which.
        /// </summary>
        Forward,

        /// <summary>
        /// The call is refused: the line it came in on clears it with <see cref="X25CallDecision.Cause"/> and
        /// <see cref="X25CallDecision.Diagnostic"/>.
        /// </summary>
        Refuse,
    }

    /// <summary>
    /// The answer of the one routing decision for an incoming call. A line never decides this itself: it
    /// hands the call to its router and carries out what comes back. A refusal is sent by the line's own
    /// X.25 engine as a Clear Request (X.25 4.1.7) on the call's channel, with the cause and diagnostic
    /// given here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The causes are the ones X.25 (10/96) gives a network for a call it cannot complete. The text is
    /// <c>Specs\X25\T-REC-X.25-199610-I!!PDF-E.txt</c> in the specs folder beside the repositories; the
    /// numbers below are lines of that file.
    /// </para>
    ///  - No route, or a number that does not exist here: "Not obtainable" 0x0D with diagnostic 67,
    ///    "Invalid called DTE address". Table C.1, under "If the virtual call cannot be established by the
    ///    network", row 24 "Unknown number" (lines 15312-15336); row 7 gives the same for an invalid called
    ///    address (lines 15188-15192). Cause bits: Table 5-7 (line 7969).
    ///  - Barred by a rule: "Access barred" 0x0B with diagnostic 70, "Incoming call barred". Table C.1
    ///    row 25 (lines 15340-15344); Table 5-7 line 7933.
    ///  - The destination exists but its line is out of order: "Out of order" 0x09, diagnostic 0. Table C.1
    ///    "Called DTE out of order" (lines 15390-15394), and 4.6.4 item 1: during an out of order
    ///    condition "the DCE will clear any incoming virtual call with the cause Out of order"
    ///    (lines 5920-5924); Table 5-7 line 7861.
    ///  - The destination has no free logical channel: "Number busy" 0x01, diagnostic 71. Table C.1 row 31
    ///    (lines 15399-15403); Table 5-7 line 7860.
    ///  - The called side itself refuses: "DTE originated" 0x00. 4.1.7: "The called DTE may refuse an
    ///    incoming call by clearing it" (lines 5201-5202); a Clear Indication that results from it carries
    ///    "DTE originated" and the called DTE's diagnostic unchanged (Table 5-7 line 7833, text lines
    ///    8022-8024).
    /// <para>
    /// Until 2026-11-18 "no route" was 0x0B (access barred) on the runtime's router and 0x0D on the local
    /// service lookup and in the switch, both with diagnostic 0. Table C.1 names "Not obtainable" for an
    /// unknown number and keeps "Access barred" for a call a facility or a rule bars, so no route is 0x0D.
    /// </para>
    /// </remarks>
    public readonly struct X25CallDecision
    {
        private X25CallDecision(X25CallAction action, int outPort, byte cause, byte diagnostic, string? calledAddress = null)
        {
            Action = action;
            OutPort = outPort;
            Cause = cause;
            Diagnostic = diagnostic;
            CalledAddress = calledAddress;
        }

        /// <summary>
        /// The called number to send on with a forwarded call, written the way the out port asks (the
        /// international prefix taken off toward another network, X.121 7.8.1 and 7.12.6-7.12.7); null when
        /// the number goes on as it came in.
        /// </summary>
        public string? CalledAddress { get; }

        /// <summary>
        /// What happens to the call.
        /// </summary>
        public X25CallAction Action { get; }

        /// <summary>
        /// The port the call goes out of when <see cref="Action"/> is <see cref="X25CallAction.Forward"/>; -1 otherwise.
        /// </summary>
        public int OutPort { get; }

        /// <summary>
        /// The clearing cause (X.25 Table 5-7) when <see cref="Action"/> is <see cref="X25CallAction.Refuse"/>; 0 otherwise.
        /// </summary>
        public byte Cause { get; }

        /// <summary>
        /// The diagnostic code (X.25 Annex E) when <see cref="Action"/> is <see cref="X25CallAction.Refuse"/>; 0 otherwise.
        /// </summary>
        public byte Diagnostic { get; }

        /// <summary>
        /// True when the call is refused.
        /// </summary>
        public bool IsRefused => Action == X25CallAction.Refuse;

        /// <summary>
        /// The call is for this node.
        /// </summary>
        /// <returns>
        /// A decision to deliver the call locally.
        /// </returns>
        public static X25CallDecision DeliverLocally()
        {
            return new X25CallDecision(X25CallAction.DeliverLocally, -1, 0, 0);
        }

        /// <summary>
        /// The call goes on out of another port.
        /// </summary>
        /// <param name="outPort">
        /// The port it goes out of.
        /// </param>
        /// <returns>
        /// A decision to forward the call.
        /// </returns>
        public static X25CallDecision Forward(int outPort)
        {
            return new X25CallDecision(X25CallAction.Forward, outPort, 0, 0);
        }

        /// <summary>
        /// The call goes on out of another port, with the called number written anew.
        /// </summary>
        /// <param name="outPort">
        /// The port it goes out of.
        /// </param>
        /// <param name="calledAddress">
        /// The called number to send on; null to send it as it came in.
        /// </param>
        /// <returns>
        /// A decision to forward the call.
        /// </returns>
        public static X25CallDecision Forward(int outPort, string? calledAddress)
        {
            return new X25CallDecision(X25CallAction.Forward, outPort, 0, 0, calledAddress);
        }

        /// <summary>
        /// The call is refused with a cause and a diagnostic of the caller's choice.
        /// </summary>
        /// <param name="cause">
        /// Clearing cause (X.25 Table 5-7).
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code (X.25 Annex E).
        /// </param>
        /// <returns>
        /// A refusal.
        /// </returns>
        public static X25CallDecision Refuse(byte cause, byte diagnostic)
        {
            return new X25CallDecision(X25CallAction.Refuse, -1, cause, diagnostic);
        }

        /// <summary>
        /// No route to the called number, or the number does not exist here: Not obtainable, diagnostic 67
        /// (Table C.1 row 24).
        /// </summary>
        /// <returns>
        /// A refusal.
        /// </returns>
        public static X25CallDecision NoRoute()
        {
            return Refuse(X25Cause.NotObtainable, X25Diagnostic.InvalidCalledAddress);
        }

        /// <summary>
        /// A rule of this node bars the call: Access barred, diagnostic 70 (Table C.1 row 25).
        /// </summary>
        /// <returns>
        /// A refusal.
        /// </returns>
        public static X25CallDecision Barred()
        {
            return Refuse(X25Cause.AccessBarred, X25Diagnostic.IncomingCallBarred);
        }

        /// <summary>
        /// The destination's line is out of order: Out of order, diagnostic 0 (Table C.1, 4.6.4).
        /// </summary>
        /// <returns>
        /// A refusal.
        /// </returns>
        public static X25CallDecision DestinationOutOfOrder()
        {
            return Refuse(X25Cause.OutOfOrder, X25Diagnostic.None);
        }

        /// <summary>
        /// The destination has no free logical channel: Number busy, diagnostic 71 (Table C.1 row 31).
        /// </summary>
        /// <returns>
        /// A refusal.
        /// </returns>
        public static X25CallDecision Busy()
        {
            return Refuse(X25Cause.NumberBusy, X25Diagnostic.NoLogicalChannelAvailable);
        }

        /// <summary>
        /// The called side refused the call itself: DTE originated, diagnostic 0 (4.1.7, Table 5-7).
        /// </summary>
        /// <returns>
        /// A refusal.
        /// </returns>
        public static X25CallDecision CalledSideRefused()
        {
            return Refuse(X25Cause.DteOriginated, X25Diagnostic.None);
        }

        /// <summary>
        /// A one-line description for logs.
        /// </summary>
        /// <returns>
        /// The action, with the port or the cause and diagnostic.
        /// </returns>
        public override string ToString()
        {
            switch (Action)
            {
                case X25CallAction.Forward:
                    return "forward to port " + OutPort;
                case X25CallAction.Refuse:
                    return "refuse, cause 0x" + Cause.ToString("X2") + " diagnostic " + Diagnostic;
                default:
                    return "deliver locally";
            }
        }
    }
}
