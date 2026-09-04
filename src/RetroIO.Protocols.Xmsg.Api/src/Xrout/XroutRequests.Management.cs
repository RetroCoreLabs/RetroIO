using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The management half of the XROUT service set: tracing, crash handling, attributes and
    /// network-server information (appendix B sections 3.18 to 3.24).
    /// </summary>
    /// <remarks>
    /// Split from the naming and routing builders because these are the operator-facing services -
    /// what the XMSG-COMMAND program sends - and almost all of them are privileged. The framing
    /// note on the other half applies here too: serialise with
    /// <see cref="XroutMessageFraming.BodyOnly"/> for the wire.
    /// Several of these are SUB-SERVICE dispatched: parameter 1 selects the sub-service and the
    /// rest depend on it. The sub-service numbers come from the generated enums
    /// (<see cref="XroutSetCrashInfoSubservice"/>, <see cref="XroutGetAttributeSubservice"/>,
    /// <see cref="XroutDefineAttributeSubservice"/>) rather than being repeated here.
    /// </remarks>
    public static partial class XroutRequests
    {
        /// <summary>
        /// Builds a trace-initialise request (XSTIN).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="traceFileName">
        /// The file XROUT should open for trace output.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged; this is what the OPEN-TRACE operator command sends. XROUT opens and
        /// initialises the file and starts the XTRACE dump program, re-enabling whichever trace
        /// events were on for the previous trace. A file-system failure comes back as a SINTRAN
        /// error code in parameter 1.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="traceFileName"/> is null.
        /// </exception>
        public static XroutMessage OpenTrace(byte serial, string traceFileName)
        {
            if (traceFileName == null)
            {
                throw new ArgumentNullException(nameof(traceFileName));
            }

            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSTIN)
                .AddString(1, traceFileName)
                .Build();
        }

        /// <summary>
        /// Builds a trace-close request (XSTCL).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged. Flushes the last trace blocks, closes the file and stops XTRACE.
        /// </remarks>
        public static XroutMessage CloseTrace(byte serial)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSTCL)
                .Build();
        }

        /// <summary>
        /// Builds a define-trace-conditions request (XSTDC).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="traceEvent">
        /// The event to control, encoded in the sign: positive ENABLES that event number, negative
        /// disables it, and zero disables every event at once.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged. Events 0 (clock) and 1 (trace management) are always on. The ones that
        /// matter for protocol work are 8 and 9 - XMSG calls and their returns, whose bodies carry
        /// the caller's T, A and D registers, the XT-block address and X - then 11 to 13 for link
        /// layer frames and 14 to 18 and 21 for network layer datagrams.
        /// </remarks>
        public static XroutMessage DefineTraceConditions(byte serial, short traceEvent)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSTDC)
                .AddInteger16(1, unchecked((ushort)traceEvent))
                .Build();
        }

        /// <summary>
        /// Builds a crash-information request (XSSCI) for a sub-service that takes no arguments.
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="subservice">
        /// The sub-service: dump XMSG now, or read back what is currently defined.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged. Reserve a buffer large enough for the REPLY - the get sub-services append
        /// file names to the message XROUT sends back, and a buffer sized only for the request
        /// will fail.
        /// </remarks>
        public static XroutMessage CrashInfo(byte serial, XroutSetCrashInfoSubservice subservice)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSSCI)
                .AddInteger16(1, (ushort)subservice)
                .Build();
        }

        /// <summary>
        /// Builds a request to enable or disable automatic XMSG restart (XSSCI, sub-service XSDAR).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="enabled">
        /// True to enable the auto-restart facility, false to disable it.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged. Enabling alone does nothing useful - the restart files must also be defined,
        /// or there is nothing for XROUT to append to the batch queues when XMSG crashes.
        /// </remarks>
        public static XroutMessage SetAutoRestart(byte serial, bool enabled)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSSCI)
                .AddInteger16(1, (ushort)XroutSetCrashInfoSubservice.XSDAR)
                .AddInteger16(2, enabled ? (ushort)1 : (ushort)0)
                .Build();
        }

        /// <summary>
        /// Builds a define-restart-files request (XSSCI, sub-service XSDRF).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="batchInputFile">
        /// The batch input file name, or null to CLEAR the restart file names.
        /// </param>
        /// <param name="batchOutputFile">
        /// The batch output file name, or null to clear.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged. Omitting either name clears the pair. The files default to user SYSTEM, so
        /// one owned by anybody else must carry that user as a prefix.
        /// </remarks>
        public static XroutMessage DefineRestartFiles(
            byte serial, string? batchInputFile, string? batchOutputFile)
        {
            XroutMessageBuilder builder = new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSSCI)
                .AddInteger16(1, (ushort)XroutSetCrashInfoSubservice.XSDRF);

            if (batchInputFile != null)
            {
                builder.AddString(2, batchInputFile);
            }

            if (batchOutputFile != null)
            {
                builder.AddString(3, batchOutputFile);
            }

            return builder.Build();
        }

        /// <summary>
        /// Builds a define-dump-files request (XSSCI, sub-service XSDDF).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="segmentFile">
        /// The dump file for segments 33 and 34, or null to clear the names.
        /// </param>
        /// <param name="tableFile">
        /// The dump file for the XMSG tables, or null to clear.
        /// </param>
        /// <param name="bufferPoolFile">
        /// The dump file for the message-buffer pool, or null to clear.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged. Omitting any of the three clears the set. File names must NOT be quoted; an
        /// existing file has to have been created as an indexed file, and a missing one is created
        /// by XROUT.
        /// </remarks>
        public static XroutMessage DefineDumpFiles(
            byte serial, string? segmentFile, string? tableFile, string? bufferPoolFile)
        {
            XroutMessageBuilder builder = new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSSCI)
                .AddInteger16(1, (ushort)XroutSetCrashInfoSubservice.XSDDF);

            if (segmentFile != null)
            {
                builder.AddString(2, segmentFile);
            }

            if (tableFile != null)
            {
                builder.AddString(3, tableFile);
            }

            if (bufferPoolFile != null)
            {
                builder.AddString(4, bufferPoolFile);
            }

            return builder.Build();
        }

        /// <summary>
        /// Builds a get-XMSG-version request (XSGAT, sub-service XSGXV).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// The reply carries version and revision as STRINGS - "J" and "00", for instance - and the
        /// patch level as an integer. Reading the first two as numbers is the obvious mistake.
        /// </remarks>
        public static XroutMessage GetXmsgVersion(byte serial)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSGAT)
                .AddInteger16(1, (ushort)XroutGetAttributeSubservice.XSGXV)
                .Build();
        }

        /// <summary>
        /// Builds a check-magic-number request (XSGAT, sub-service XSCMG).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="magic">
        /// The magic number to validate.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Unprivileged, and carefully limited so it discloses nothing: the reply is 1 when the
        /// magic number names an OPEN port in the XROUT being asked, and 0 for a closed port, a
        /// system, or a port belonging to another system. It confirms validity without revealing
        /// whose port it is.
        /// </remarks>
        public static XroutMessage CheckMagicNumber(byte serial, XmsgMagicNumber magic)
        {
            XroutMessage message = new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSGAT)
                .AddInteger16(1, (ushort)XroutGetAttributeSubservice.XSCMG)
                .Build();

            message.AddParameter(XroutParameter.Integer32(2, magic.Value));
            return message;
        }

        /// <summary>
        /// Builds a deabbreviate-name request (XSGAT, sub-service XSGCN).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="abbreviatedName">
        /// The shortened system or port name.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// The reply gives the full name as parameter 2, plus either the port number (parameter 1)
        /// when the name was a port name or the system number (parameter 3) when it was a system
        /// name - which is also how the caller finds out which kind it was.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="abbreviatedName"/> is null.
        /// </exception>
        public static XroutMessage DeabbreviateName(byte serial, string abbreviatedName)
        {
            if (abbreviatedName == null)
            {
                throw new ArgumentNullException(nameof(abbreviatedName));
            }

            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSGAT)
                .AddInteger16(1, (ushort)XroutGetAttributeSubservice.XSGCN)
                .AddString(2, abbreviatedName)
                .Build();
        }

        /// <summary>
        /// Builds a get-friend-system request (XSGAT, sub-service XSGFR).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="systemNumber">
        /// The system to start from; the reply carries the first friend system greater than or
        /// equal to it, so repeating the call walks the friend table.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged; this is what LIST-FRIEND-SYSTEMS sends. A returned system number of zero
        /// ends the walk.
        /// </remarks>
        public static XroutMessage GetFriendSystem(byte serial, ushort systemNumber)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSGAT)
                .AddInteger16(1, (ushort)XroutGetAttributeSubservice.XSGFR)
                .AddInteger16(2, systemNumber)
                .Build();
        }

        /// <summary>
        /// Builds a define- or remove-friend-system request (XSDAT, sub-service XSDFR or XSRFR).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="systemNumber">
        /// The system to add to or remove from the friend table.
        /// </param>
        /// <param name="define">
        /// True to declare the system a friend, false to remove it.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged. Friendship is NOT reciprocal - a system cannot declare ITSELF a friend of
        /// another, only the other way round.
        /// </remarks>
        public static XroutMessage SetFriendSystem(byte serial, ushort systemNumber, bool define)
        {
            XroutDefineAttributeSubservice subservice = define
                ? XroutDefineAttributeSubservice.XSDFR
                : XroutDefineAttributeSubservice.XSRFR;

            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSDAT)
                .AddInteger16(1, (ushort)subservice)
                .AddInteger16(2, systemNumber)
                .Build();
        }

        /// <summary>
        /// Builds a get-network-server-information request (XSNSI).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="virtualSystemNumber">
        /// The virtual system to start from; the reply carries the first found greater than or
        /// equal to it, so repeating the call enumerates the network servers.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged; this is what LIST-NETWORK-SERVERS sends. The reply runs to eight parameters
        /// - virtual system, server name, link index, network type (0 local area, 1 wide area),
        /// server port, gateway port, and the receive-buffer and transmit-message counts - so the
        /// buffer has to be big enough to hold them all.
        /// </remarks>
        public static XroutMessage GetNetworkServerInformation(byte serial, ushort virtualSystemNumber)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSNSI)
                .AddInteger16(1, virtualSystemNumber)
                .Build();
        }

        /// <summary>
        /// Builds a get-link-information request (<c>XSLIN</c>).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="linkNumber">
        /// The link (XL-block) index to report on.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Privileged; this is what <c>LIST-LINKS</c> sends. From XMSG version L the service is
        /// available to REMOTE privileged tasks as well as local ones - under version K the calling
        /// system had to be declared a friend of the system being asked.
        /// </para>
        /// <para><b>Version differences in the reply, which the caller must expect</b></para>
        /// Version L extends the reply with three parameters carrying the link-table utilisation -
        /// see <see cref="XmsgLinkInformation"/>. A version-K system does not return them, and ND
        /// say so explicitly: asking a K system means "the link table status information will not
        /// and cannot be printed". So a short reply is a version difference, not an error.
        /// </remarks>
        public static XroutMessage GetLinkInformation(byte serial, ushort linkNumber)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSLIN)
                .AddInteger16(1, linkNumber)
                .Build();
        }

        /// <summary>
        /// Builds an enable- or disable-checksum request (<c>XSDAT</c>, sub-service
        /// <c>XSECS</c> or <c>XSDCS</c>).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="systemNumber">
        /// The remote system to turn datagram checksumming on or off for.
        /// </param>
        /// <param name="enable">
        /// True to enable checksumming, false to disable it.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// <para><b>This is a DATAGRAM checksum, not header word 6</b></para>
        /// Do not confuse the two. Header word 6 is a ones-complement sum over the six words in
        /// front of it, present on every frame and never optional. THIS is a separate, optional
        /// checksum over the whole datagram, new in XMSG version L, and it is negotiated: ND's
        /// wording is that it applies when the remote system is "able and willing to handle
        /// checksum on datagrams". Its failure code is <c>XENCE</c>, itself new in version L.
        /// <para><b>Cost and constraints</b></para>
        ///  - Throughput drops by about 5 per cent with it enabled, per ND.
        ///  - Privileged, and both systems must be running version L or later; an older XROUT
        ///    answers with an error.
        ///  - Sent to a REMOTE XROUT, the request is accepted only if
        ///    <paramref name="systemNumber"/> equals the SENDING system's own number. A system may
        ///    therefore ask a peer to checksum traffic aimed at itself, but cannot reach across and
        ///    configure two other systems' link.
        /// <para><b>The manual's "XSDSCS" is an OCR error, and the symbol width proves it</b></para>
        /// Section 7.6 of the scanned manual spells the disable sub-service <c>XSDSCS</c>. That
        /// cannot be an ND symbol: every one of the 300 symbols in <c>XMSG-PL-VALUES-M.INCL</c> is
        /// EXACTLY five characters, a uniform width rather than a maximum. <c>XSDSCS</c> is six.
        /// <para>
        /// The machine-readable sources agree with each other and name it <c>XSDCS</c> - the
        /// <c>.INCL</c>, and <c>XMSG-VALUES-M.SYMB</c> which carries ND's own comment:
        /// <code>
        /// SYMBOL XSECS=3     % Enable checksum
        /// SYMBOL XSDCS=4     % Disable checksum
        /// </code>
        /// So this is the scan misreading a letter, not ND being inconsistent. Worth recording
        /// because the five-character rule settles this class of question outright: a symbol of any
        /// other length in an OCR'd manual is a scanning artefact, full stop.
        /// </para>
        /// <para>
        /// NOT captured. The sub-service codes come from the symbol files and the parameter layout
        /// from the version-L program description; no recording of this exchange exists, so nothing
        /// here is confirmed against a live machine.
        /// </para>
        /// </remarks>
        public static XroutMessage SetDatagramChecksum(byte serial, ushort systemNumber, bool enable)
        {
            XroutDefineAttributeSubservice subservice = enable
                ? XroutDefineAttributeSubservice.XSECS
                : XroutDefineAttributeSubservice.XSDCS;

            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSDAT)
                .AddInteger16(1, (ushort)subservice)
                .AddInteger16(2, systemNumber)
                .Build();
        }
    }
}
