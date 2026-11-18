//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// What became of a call this node placed.
    /// </summary>
    public enum X25CallOutcomeKind
    {
        /// <summary>
        /// The call never left: no line could take it (no route, no line in service, the connection could not be
        /// opened, or the engine refused it).
        /// </summary>
        NotPlaced = 0,

        /// <summary>
        /// The call was handed to the X.25 engine of a registered line. That engine reports the peer's answer to the
        /// program that placed the call (Call Connected or Clear Indication); the router does not see it.
        /// </summary>
        HandedToLine = 1,

        /// <summary>
        /// The called end accepted the call: a Call Connected packet arrived and the channel is in the data transfer
        /// state p4 (X.25 4.1.5).
        /// </summary>
        Accepted = 2,

        /// <summary>
        /// The call was cleared before it was accepted: a Clear Indication arrived, or the connection under it
        /// closed. <see cref="X25CallOutcome.Cause"/> and <see cref="X25CallOutcome.Diagnostic"/> say why.
        /// </summary>
        Cleared = 3,

        /// <summary>
        /// No answer within time-limit T21 (X.25 Annex D, Table D.2: 200 s, started when the DTE issues a call request,
        /// state p2; on expiry "To transmit a clear request"). This end sent the Clear Request; the diagnostic is 49,
        /// time expired for incoming call (Annex E).
        /// </summary>
        TimedOut = 4,
    }

    /// <summary>
    /// The result of placing a call: accepted, cleared with the cause and diagnostic that came back, timed out,
    /// handed to a line's engine, or never placed.
    /// </summary>
    public sealed class X25CallOutcome
    {
        private X25CallOutcome(X25CallOutcomeKind kind, byte cause, byte diagnostic, string explanation, object? call)
        {
            Kind = kind;
            Cause = cause;
            Diagnostic = diagnostic;
            Explanation = explanation;
            Call = call;
        }

        /// <summary>
        /// What became of the call.
        /// </summary>
        public X25CallOutcomeKind Kind { get; }

        /// <summary>
        /// The clearing cause (X.25 Table 5-7) for <see cref="X25CallOutcomeKind.Cleared"/> and
        /// <see cref="X25CallOutcomeKind.TimedOut"/>; 0 otherwise.
        /// </summary>
        public byte Cause { get; }

        /// <summary>
        /// The diagnostic code (X.25 Annex E) for <see cref="X25CallOutcomeKind.Cleared"/> and
        /// <see cref="X25CallOutcomeKind.TimedOut"/>; 0 otherwise.
        /// </summary>
        public byte Diagnostic { get; }

        /// <summary>
        /// Why, in words, for the log.
        /// </summary>
        public string Explanation { get; }

        /// <summary>
        /// For <see cref="X25CallOutcomeKind.Accepted"/>: the established call that carries the data from now on
        /// (an <see cref="RetroIO.Protocols.X25.Transport.XotOutgoingCall"/>). Null for every other outcome.
        /// </summary>
        public object? Call { get; }

        /// <summary>
        /// True when the call is up (<see cref="X25CallOutcomeKind.Accepted"/>) or was taken by a line whose engine
        /// will report the answer (<see cref="X25CallOutcomeKind.HandedToLine"/>).
        /// </summary>
        public bool Succeeded => Kind == X25CallOutcomeKind.Accepted || Kind == X25CallOutcomeKind.HandedToLine;

        /// <summary>
        /// The call never left.
        /// </summary>
        /// <param name="why">
        /// Why, in words.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        public static X25CallOutcome NotPlaced(string why) => new X25CallOutcome(X25CallOutcomeKind.NotPlaced, 0, 0, why, null);

        /// <summary>
        /// The call was handed to a registered line's engine.
        /// </summary>
        /// <param name="line">
        /// The line's name.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        public static X25CallOutcome HandedToLine(string line) =>
            new X25CallOutcome(X25CallOutcomeKind.HandedToLine, 0, 0, "handed to the X.25 engine of " + line, null);

        /// <summary>
        /// The call was accepted.
        /// </summary>
        /// <param name="call">
        /// The established call.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        public static X25CallOutcome Accepted(object call) =>
            new X25CallOutcome(X25CallOutcomeKind.Accepted, 0, 0, "accepted (Call Connected)", call);

        /// <summary>
        /// The call was cleared before it was accepted.
        /// </summary>
        /// <param name="cause">
        /// The clearing cause.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic code.
        /// </param>
        /// <param name="why">
        /// Why, in words.
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        public static X25CallOutcome Cleared(byte cause, byte diagnostic, string why) =>
            new X25CallOutcome(X25CallOutcomeKind.Cleared, cause, diagnostic, why, null);

        /// <summary>
        /// T21 expired with no answer, and this end sent the Clear Request.
        /// </summary>
        /// <param name="cause">
        /// The clearing cause this end sent.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic this end sent (49).
        /// </param>
        /// <returns>
        /// The outcome.
        /// </returns>
        public static X25CallOutcome TimedOut(byte cause, byte diagnostic) =>
            new X25CallOutcome(X25CallOutcomeKind.TimedOut, cause, diagnostic, "no answer within T21 (X.25 Table D.2); cleared by this end", null);

        /// <inheritdoc/>
        public override string ToString() => Kind + " cause 0x" + Cause.ToString("X2") + " diag 0x" + Diagnostic.ToString("X2") + ": " + Explanation;
    }
}
