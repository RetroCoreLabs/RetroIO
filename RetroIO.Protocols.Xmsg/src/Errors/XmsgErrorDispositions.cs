namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Maps an XMSG or XROUT error code to the disposition ND gave it.
    /// </summary>
    /// <remarks>
    /// <para><b>Source</b></para>
    /// Every value below is transcribed from the NDIX release-3 tables:
    ///  - <c>xmsgerrors.h</c> (48 rows plus the <c>SIII_UNKNOWN</c> terminator), stamped <c>3.1 88/08/12</c>.
    ///  - <c>xrouterrors.h</c> (47 rows plus the terminator), same stamp.
    /// Nothing here is inferred. A code ND does not list returns
    /// <see cref="XmsgErrorDisposition.Unknown"/> rather than a plausible-looking guess.
    /// <para><b>Coverage against our enums</b></para>
    /// Of the 45 codes in <see cref="XmsgError"/>, 42 appear in ND's table. The three that do
    /// not - <c>XENIR</c>, <c>XENCE</c> and <c>XEICM</c> - postdate release 3 or are internal,
    /// and so are <see cref="XmsgErrorDisposition.Unknown"/>. Going the other way, five rows in
    /// <c>xmsgerrors.h</c> (<c>XMXEACB</c>, <c>XMXEIOP</c>, <c>XMXECND</c>, <c>XMXEWRE</c>,
    /// <c>XMXEBLS</c>) have no counterpart in the SINTRAN include at all - they are NDIX's own
    /// host-side errors, not XMSG kernel errors, so they are correctly absent from our enum.
    /// <para>
    /// For <see cref="XroutError"/>, ND classifies 0..46. Everything in that range is
    /// <see cref="XmsgErrorDisposition.GiveUp"/> bar two exceptions, which is itself the finding:
    /// an XROUT failure is a naming or configuration fault, not a transient one. Our codes 47..55
    /// are later additions with no ND row.
    /// </para>
    /// <para><b>One disagreement in ND's own tables</b></para>
    /// <c>XRBFE</c> ("Xrout buffer format error") appears in BOTH files - as
    /// <c>SIII_RETRY</c> in <c>xmsgerrors.h</c> and <c>SIII_GIVE_UP</c> in
    /// <c>xrouterrors.h</c>. It is not in either of our enums, so nothing here has to resolve it;
    /// it is recorded because it shows the tables were maintained separately, and a future import
    /// must not assume they agree.
    /// </remarks>
    public static class XmsgErrorDispositions
    {
        /// <summary>
        /// Returns the disposition ND recorded for an XMSG error code.
        /// </summary>
        /// <param name="error">
        /// The error code, as returned negative in the T-register.
        /// </param>
        /// <returns>
        /// The matching disposition, or <see cref="XmsgErrorDisposition.Unknown"/> when ND's
        /// release-3 table does not list <paramref name="error"/>.
        /// </returns>
        public static XmsgErrorDisposition Of(XmsgError error)
        {
            // A switch, not a dictionary: no allocation, no static initialisation order to get
            // wrong, and the compiler turns a dense signed range into a jump table.
            switch (error)
            {
                // --- Give up: the request itself is wrong, or the facility does not exist. ---
                case XmsgError.XECRA:    // XMSG crash, information in Basefield.
                case XmsgError.XEPCL:    // Remote port closed while message queued.
                case XmsgError.XERND:    // Remote system not defined.
                case XmsgError.XEPVR:    // Privilege request refused.
                case XmsgError.XEPRV:    // Privileged function called without privilege.
                case XmsgError.XEILF:    // Illegal function code in monitor call.
                case XmsgError.XEAIN:    // XMSG kernel already initialised.
                case XmsgError.XEIDR:    // Function not available to drivers.
                case XmsgError.XEIRT:    // Illegal function for RT-programs (drivers only).
                case XmsgError.XENIM:    // Facility not yet implemented.
                case XmsgError.XEIRM:    // Non-local remote port illegal here.
                    return XmsgErrorDisposition.GiveUp;

                // --- Suspend: the far end is unreachable; wait for the network. ---
                case XmsgError.XERNA:    // Remote system not available.
                    return XmsgErrorDisposition.Suspend;

                // --- Sleep: our own XMSG is not up. Nothing to talk to locally. ---
                case XmsgError.XENRU:    // XMSG not running.
                    return XmsgErrorDisposition.Sleep;

                // --- Retry: stale caller state, a full queue, or a transient network event. ---
                case XmsgError.XETMU:    // Too many multicalls.
                case XmsgError.XEIXT:    // Driver called XMSG with an illegal XT-block.
                case XmsgError.XEREJ:    // Network remote reject (request retransmit).
                case XmsgError.XENUS:    // No user segment information defined.
                case XmsgError.XENTO:    // Timeout detected by network layer.
                case XmsgError.XENSE:    // Network sequencing.
                case XmsgError.XENOS:    // Indirect buffer not on a valid segment.
                case XmsgError.XEILR:    // Illegal use of reentrant segment in XFBID.
                case XmsgError.XEIDP:    // Illegal displacement in read/write.
                case XmsgError.XEITL:    // Illegal transfer length for read/write.
                case XmsgError.XENDP:    // No port open.
                case XmsgError.XEDRI:    // Driver re-entered XMSG before the previous call returned.
                case XmsgError.XEXBF:    // Message already has an XMSG buffer.
                case XmsgError.XEROV:    // Remote task space overflow.
                case XmsgError.XEIPN:    // Illegal port number.
                case XmsgError.XEILM:    // Illegal message space, or not enough left.
                case XmsgError.XEMFL:    // Message space full.
                case XmsgError.XEIMA:    // Invalid magic number - see the class remarks.
                case XmsgError.XENVI:    // No valid indirect buffer defined.
                case XmsgError.XEWNA:    // Write not allowed.
                case XmsgError.XEBNC:    // Return of a bounce message.
                case XmsgError.XEBFC:    // Message is in a queue.
                case XmsgError.XEMCH:    // Message is already chained.
                case XmsgError.XENDM:    // No default message.
                case XmsgError.XENOP:    // No more ports available.
                case XmsgError.XEBNY:    // Message buffer not yours.
                case XmsgError.XEIBP:    // Illegal message buffer pointer.
                case XmsgError.XETMM:    // Task is not allowed any more memory.
                case XmsgError.XENOT:    // No more XT-blocks free.
                    return XmsgErrorDisposition.Retry;

                // XENIR, XENCE, XEICM and the XKXXX range base have no row in ND's table.
                default:
                    return XmsgErrorDisposition.Unknown;
            }
        }

        /// <summary>
        /// Returns the disposition ND recorded for an XROUT error code.
        /// </summary>
        /// <param name="error">
        /// The XROUT error code, as carried in a reply letter.
        /// </param>
        /// <returns>
        /// The matching disposition, or <see cref="XmsgErrorDisposition.Unknown"/> when ND's
        /// release-3 table does not list <paramref name="error"/>.
        /// </returns>
        /// <remarks>
        /// Written as a range test rather than 47 case labels because ND's own table is that
        /// uniform: every XROUT error from 1 to 46 is <c>SIII_GIVE_UP</c> except <c>XRNRO</c>.
        /// Spelling out 45 identical arms would hide the one that differs.
        /// </remarks>
        public static XmsgErrorDisposition Of(XroutError error)
        {
            if (error == XroutError.XRSOK)
            {
                return XmsgErrorDisposition.Ok;
            }

            // The single exception in the whole table: no access to the remote system is a
            // network condition, so wait for it rather than abandoning the request.
            if (error == XroutError.XRNRO)
            {
                return XmsgErrorDisposition.Suspend;
            }

            // XRISN=1 .. XRNCO=46 are the codes ND's release-3 table classifies, all GIVE_UP.
            int value = (int)error;

            if (value >= (int)XroutError.XRISN && value <= (int)XroutError.XRNCO)
            {
                return XmsgErrorDisposition.GiveUp;
            }

            // XRAMB=47 upward are later additions, and XRXXX is the range base, not an error.
            return XmsgErrorDisposition.Unknown;
        }

        /// <summary>
        /// Indicates whether repeating the call could plausibly succeed.
        /// </summary>
        /// <param name="error">
        /// The XMSG error code to test.
        /// </param>
        /// <returns>
        /// <c>true</c> for <see cref="XmsgErrorDisposition.Retry"/>,
        /// <see cref="XmsgErrorDisposition.Suspend"/> and
        /// <see cref="XmsgErrorDisposition.Sleep"/>; otherwise <c>false</c>.
        /// </returns>
        /// <remarks>
        /// <see cref="XmsgErrorDisposition.Unknown"/> deliberately answers <c>false</c>. An
        /// unclassified error is not evidence that retrying is safe, and treating it as
        /// retryable is how a client ends up spinning forever against a permanent fault.
        /// </remarks>
        public static bool IsWorthRetrying(XmsgError error)
        {
            XmsgErrorDisposition disposition = Of(error);

            return disposition == XmsgErrorDisposition.Retry
                || disposition == XmsgErrorDisposition.Suspend
                || disposition == XmsgErrorDisposition.Sleep;
        }
    }
}
