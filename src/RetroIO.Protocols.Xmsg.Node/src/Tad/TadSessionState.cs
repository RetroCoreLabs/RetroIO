//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The phases of a TAD (Terminal Access and Directory) connect-to session as
    /// reconstructed from the captured connect-to scenarios.
    /// </summary>
    /// <remarks>
    /// <para><b>Provenance</b></para>
    /// INFERRED phase model. The wire has no explicit "phase" field; these phases are a
    /// convenience abstraction over the observed message order in
    /// <c>new-conn-to-102-from-100.pcapng</c> and <c>conn-to-102-from103-via100.pcapng</c>:
    ///  - the XROUT XSLET setup letter to the directory service opens the exchange.
    ///  - a run of config messages (TMOD / TTYP / DESC / OPSV / ECKM / BMMX) negotiates
    ///    the terminal parameters.
    ///  - BDAT / RFI traffic marks the established data phase.
    ///  - RESE (reset) and DCON (disconnect) are control transitions seen in the capture.
    /// The exact boundaries between phases are a modelling choice, not a protocol field.
    /// </remarks>
    public enum TadSessionState
    {
        /// <summary>
        /// No connect-to activity has been observed yet.
        /// </summary>
        Idle,

        /// <summary>
        /// The XROUT XSLET setup letter (name resolution to the directory service) has
        /// been seen; the target remote name is being resolved.
        /// </summary>
        XroutSetup,

        /// <summary>
        /// Terminal parameters are being negotiated (echo strategy, terminal mode,
        /// terminal type, escape character, OS/protocol version).
        /// </summary>
        Negotiating,

        /// <summary>
        /// The session is established and exchanging terminal data (BDAT) with
        /// ready-for-input (RFI) flow-control credit.
        /// </summary>
        DataPhase,

        /// <summary>
        /// A reset (RESE) has been observed; the connection is being re-synchronised and
        /// awaits its reset-confirm (RECO).
        /// </summary>
        Resetting,

        /// <summary>
        /// A disconnect (DCON) has been observed; the session is torn down.
        /// </summary>
        Disconnected,
    }
}
