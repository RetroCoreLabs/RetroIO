//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// What ND's own software does when an XMSG or XROUT call fails with a given error.
    /// </summary>
    /// <remarks>
    /// <para><b>Where these names come from</b></para>
    /// These are ND's four <c>SIII_*</c> dispositions, carried verbatim from the NDIX
    /// release-3 error tables (<c>xmsgerrors.h</c> and <c>xrouterrors.h</c>, both stamped
    /// <c>3.1 88/08/12</c>). NDIX is a second, independent XMSG client - a Unix host talking
    /// the same monitor-call surface - so its tables record how ND themselves classified
    /// every error, which is exactly the knowledge our numeric enums lack.
    /// <para>
    /// This is a classification, not a policy. It says what ND considered the error to mean;
    /// it does not retry anything, sleep, or impose a retry count. Callers decide.
    /// </para>
    /// <para><b>Why it matters in practice</b></para>
    /// A concrete case: <c>XEIMA</c> ("invalid magic number", -19) is <see cref="Retry"/>,
    /// not a fault. A real D100 answers our FA close with <c>XEIMA</c> and the transfer is
    /// nonetheless complete and correct every time. Without this table that looked like an
    /// unexplained protocol defect; with it, it is the peer saying "that conversation is
    /// already gone, ask again if you meant it".
    /// </remarks>
    public enum XmsgErrorDisposition
    {
        /// <summary>
        /// Not an error at all (ND's <c>SIII_OK</c>).
        /// </summary>
        Ok = 0,

        /// <summary>
        /// Transient - the same call may succeed if repeated (ND's <c>SIII_RETRY</c>).
        /// </summary>
        /// <remarks>
        /// The overwhelming majority of XMSG errors sit here, including several that read like
        /// hard faults ("illegal port number", "buffer not yours"). ND's reading is that the
        /// caller's own state is usually stale rather than wrong, so re-deriving it and calling
        /// again is the normal cure.
        /// </remarks>
        Retry = 1,

        /// <summary>
        /// Permanent - repeating the call cannot help (ND's <c>SIII_GIVE_UP</c>).
        /// </summary>
        /// <remarks>
        /// Wrong function code, missing privilege, an undefined remote system, a facility that
        /// was never implemented. Nothing the caller can wait for.
        /// </remarks>
        GiveUp = 2,

        /// <summary>
        /// Wait for the remote side to come back, then try again (ND's <c>SIII_SUSPEND</c>).
        /// </summary>
        /// <remarks>
        /// Only two errors in either table: <c>XERNA</c> (remote system not available) and
        /// <c>XRNRO</c> (no access to remote system). Both mean the network, not the request,
        /// is at fault.
        /// </remarks>
        Suspend = 3,

        /// <summary>
        /// Wait for the local XMSG kernel itself, then try again (ND's <c>SIII_SLEEP</c>).
        /// </summary>
        /// <remarks>
        /// Exactly one error carries this: <c>XENRU</c>, "XMSG not running". The subsystem has
        /// not been started or has been stopped on this machine, so there is nobody to talk to
        /// locally - distinct from <see cref="Suspend"/>, where the local kernel is fine.
        /// </remarks>
        Sleep = 4,

        /// <summary>
        /// ND's tables do not classify this code (their <c>SIII_UNKNOWN</c> terminator).
        /// </summary>
        /// <remarks>
        /// Returned for any error the 1988 NDIX tables do not list. That includes codes added to
        /// XMSG after release 3 and the range-base sentinels such as <c>XKXXX</c> / <c>XRXXX</c>,
        /// which are not errors at all. Deliberately not guessed - an invented disposition would
        /// send a caller into an endless retry against a permanent failure.
        /// </remarks>
        Unknown = 5,
    }
}
