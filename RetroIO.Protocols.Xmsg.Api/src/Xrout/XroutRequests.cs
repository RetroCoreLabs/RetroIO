using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// Builds the XROUT service requests of appendix B as ready-to-send XROUT messages.
    /// </summary>
    /// <remarks>
    /// <para><b>How XROUT is called</b></para>
    /// An XROUT service is not a separate transport: you write the request into an ordinary
    /// message buffer and send it with the route option set, at which point XMSG hands it to the
    /// local (or, with remote-route, a named remote) XROUT task instead of a port. XROUT replies by
    /// overwriting byte 1 of the SAME buffer with the return status and sending it back - so the
    /// buffer must be large enough for the reply, not just the request (appendix B section 2).
    /// Replies arrive with message type XMROU, which is how they are told apart from ordinary
    /// traffic.
    /// <para><b>Parameter numbering</b></para>
    /// Every builder here uses the parameter numbers exactly as tabulated in appendix B section 3.
    /// Where the manual marks a parameter optional, the builder takes a nullable argument and omits
    /// the block entirely when it is null - omitting a parameter is meaningful to XROUT (for
    /// XSDRN it means "clear the name", for XSDSY it means "remove the routing entry").
    /// <para><b>Framing: the header is not on the wire</b></para>
    /// The <c>serial</c> parameter every builder takes fills byte 0 of the manual's four-byte
    /// XROUT header - which exists in a MESSAGE BUFFER but is NOT carried by the XMSG data frames
    /// in our captures. Serialise with <see cref="XroutMessageFraming.BodyOnly"/> for anything
    /// destined for the wire, where the service travels in the frame's XMCSM word instead, and
    /// keep <see cref="XroutMessageFraming.WithHeader"/> for the buffer form. See
    /// <see cref="XroutMessageFraming"/> for the capture evidence.
    /// <para><b>Privilege</b></para>
    /// Many of these services only succeed for a privileged task; the remarks on each builder say
    /// which. Privilege is acquired with the make-privileged function, not through XROUT.
    /// </remarks>
    public static partial class XroutRequests
    {
        /// <summary>
        /// Builds a name-a-port request (XSNAM).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number, returned unchanged so replies can be matched to requests.
        /// Bit 7 must be zero to comply with the ND standard message format.
        /// </param>
        /// <param name="portName">
        /// The name to give the port. XROUT silently truncates beyond the generated maximum
        /// (32 bytes by default).
        /// </param>
        /// <returns>
        /// The request message, to be sent FROM the port that is to be named.
        /// </returns>
        /// <remarks>
        /// Fails if another open port already holds the name. Naming a port that already has a name
        /// renames it. By ND convention the names of standard products begin with two asterisks
        /// followed by a product code, which is why the observed servers are called *TADADM,
        /// *XM-FIDO and so on.
        /// </remarks>
        public static XroutMessage NamePort(byte serial, string portName)
        {
            if (portName == null)
            {
                throw new ArgumentNullException(nameof(portName));
            }

            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSNAM)
                .AddString(1, portName)
                .Build();
        }

        /// <summary>
        /// Builds a create-connection-port request (XSCRS).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="portName">
        /// The connection name. Unlike a plain named port, several connection ports may share a
        /// name unless <paramref name="unique"/> says otherwise - that is what lets XROUT spread
        /// callers across a pool of server ports.
        /// </param>
        /// <param name="maximumConnections">
        /// The initial free-connection count: how many simultaneous users this port accepts.
        /// </param>
        /// <param name="unique">
        /// Non-zero to demand that the name be unique.
        /// </param>
        /// <returns>
        /// The request message, to be sent FROM the port that is to be named.
        /// </returns>
        /// <remarks>
        /// When a letter arrives for a connection name, XROUT forwards it only if the free count is
        /// above zero, decrementing it; otherwise it tries the next port with the same name and,
        /// failing that, returns the letter to the sender with an error.
        /// </remarks>
        public static XroutMessage CreateConnectionPort(
            byte serial,
            string portName,
            ushort maximumConnections,
            ushort unique)
        {
            if (portName == null)
            {
                throw new ArgumentNullException(nameof(portName));
            }

            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSCRS)
                .AddString(1, portName)
                .AddInteger16(2, maximumConnections)
                .AddInteger16(3, unique)
                .Build();
        }

        /// <summary>
        /// Builds a create-connection-port request (XSCRS) with no uniqueness flag, which is the
        /// form the real COSMOS servers use.
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="portName">
        /// The connection name.
        /// </param>
        /// <param name="maximumConnections">
        /// The initial free-connection count.
        /// </param>
        /// <returns>
        /// The request message, to be sent FROM the port that is to be named.
        /// </returns>
        /// <remarks>
        /// Parameter 3 is optional and OBSERVED to be omitted: a MON 200 trace of *FA-FSA,
        /// *FA-FSA-I and *FA-SERVER registering shows a string parameter 1 followed only by
        /// integer parameter 2. Every one of them passed ZERO as the initial count and then raised
        /// it with <see cref="AdjustFreeConnections"/>, one call per service point, which is what
        /// produces the free-SP totals the operator sees. See
        /// DOC/XMSG-XSCRS-CONNECTION-PORTS-CAPTURED-2026-07-27.md.
        /// </remarks>
        public static XroutMessage CreateConnectionPort(
            byte serial,
            string portName,
            ushort maximumConnections)
        {
            if (portName == null)
            {
                throw new ArgumentNullException(nameof(portName));
            }

            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSCRS)
                .AddString(1, portName)
                .AddInteger16(2, maximumConnections)
                .Build();
        }

        /// <summary>
        /// Builds a create-connection-port request (XSCRS) carrying nothing but the name.
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="portName">
        /// The connection name.
        /// </param>
        /// <returns>
        /// The request message, to be sent FROM the port that is to be named.
        /// </returns>
        /// <remarks>
        /// The barest observed form: the file-transfer server *XFTRA registers with the name alone,
        /// no count parameter at all, and then issues a single XSNSP of +1. So parameter 2 is
        /// optional as well, and an absent count behaves as zero.
        /// </remarks>
        public static XroutMessage CreateConnectionPort(byte serial, string portName)
        {
            if (portName == null)
            {
                throw new ArgumentNullException(nameof(portName));
            }

            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSCRS)
                .AddString(1, portName)
                .Build();
        }

        /// <summary>
        /// Builds a request to adjust a connection port's free-connection count (XSNSP).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="delta">
        /// The change to apply: positive to release connections back, negative to withdraw them.
        /// XROUT returns an error if the count would go negative.
        /// </param>
        /// <returns>
        /// The request message, to be sent FROM the connection port.
        /// </returns>
        /// <remarks>
        /// A server increments the count when a session ends, which is what re-opens the port to
        /// the next caller.
        /// </remarks>
        public static XroutMessage AdjustFreeConnections(byte serial, short delta)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSNSP)
                .AddInteger16(1, unchecked((ushort)delta))
                .Build();
        }

        /// <summary>
        /// Builds a send-letter request (XSLET).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="portName">
        /// The destination port or connection name.
        /// </param>
        /// <param name="systemName">
        /// The destination system name, or null for the local system. A name here must have been
        /// defined with the define-remote-name service.
        /// </param>
        /// <param name="localAreaOnly">
        /// Optional flag: when non-zero the letter is forwarded only if the destination is reachable
        /// on the local network, so it never travels over a chargeable wide-area link.
        /// </param>
        /// <param name="payload">
        /// Optional additional parameter blocks carrying data for the receiving task - typically a
        /// user name and password, so the server can vet the caller BEFORE replying and thereby
        /// disclosing its own magic number.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// A letter is the only way to reach a task whose magic number you do not know. XROUT never
        /// hands out somebody else's magic number; it forwards your letter, and the recipient
        /// learns YOUR address from it and may choose to answer. That asymmetry is the whole point
        /// of the letter mechanism (section 1.3).
        /// </remarks>
        public static XroutMessage SendLetter(
            byte serial,
            string portName,
            string? systemName,
            ushort? localAreaOnly,
            params XroutParameter[]? payload)
        {
            if (portName == null)
            {
                throw new ArgumentNullException(nameof(portName));
            }

            XroutMessageBuilder builder = new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSLET)
                .AddString(1, portName);

            if (systemName != null)
            {
                builder.AddString(2, systemName);
            }

            if (localAreaOnly.HasValue)
            {
                builder.AddInteger16(4, localAreaOnly.Value);
            }

            XroutMessage message = builder.Build();
            AppendPayload(message, payload);
            return message;
        }

        /// <summary>
        /// Builds a send-letter-and-kick request (XSLEK).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="portName">
        /// The destination port or connection name.
        /// </param>
        /// <param name="systemName">
        /// The destination system name, or null for the local system.
        /// </param>
        /// <param name="rtProgramName">
        /// The RT program the destination XROUT should start if it does not know the port name.
        /// </param>
        /// <param name="abortFirst">
        /// Optional flag: pass 1 to precede the RT start with an ABORT.
        /// </param>
        /// <param name="payload">
        /// Optional additional parameter blocks carrying data for the receiving task.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged. This is how a dormant server gets started on demand: the letter doubles as a
        /// wake-up. The requester can tell whether the target task or XROUT itself answered by
        /// checking the message type of the reply.
        /// </remarks>
        public static XroutMessage SendLetterAndKick(
            byte serial,
            string portName,
            string? systemName,
            string rtProgramName,
            ushort? abortFirst,
            params XroutParameter[]? payload)
        {
            if (portName == null)
            {
                throw new ArgumentNullException(nameof(portName));
            }

            if (rtProgramName == null)
            {
                throw new ArgumentNullException(nameof(rtProgramName));
            }

            XroutMessageBuilder builder = new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSLEK)
                .AddString(1, portName);

            if (systemName != null)
            {
                builder.AddString(2, systemName);
            }

            builder.AddString(3, rtProgramName);

            if (abortFirst.HasValue)
            {
                builder.AddInteger16(4, abortFirst.Value);
            }

            XroutMessage message = builder.Build();
            AppendPayload(message, payload);
            return message;
        }

        /// <summary>
        /// Builds a null-status request (XSNUL).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// XROUT answers with two bytes - the reference number and zero. It exists for testing and
        /// benchmarking, and is the cheapest way to prove an XROUT is alive.
        /// </remarks>
        public static XroutMessage Null(byte serial)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSNUL)
                .Build();
        }

        /// <summary>
        /// Builds a get-name-from-magic-number request (XSGNM).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="magic">
        /// The magic number whose name is wanted.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// The name comes back as parameter 2 - but only if the buffer had room for it, so reserve
        /// a buffer big enough for the reply.
        /// </remarks>
        public static XroutMessage GetNameFromMagic(byte serial, XmsgMagicNumber magic)
        {
            XroutMessage message = new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSGNM)
                .Build();
            message.AddParameter(XroutParameter.Integer32(1, magic.Value));
            return message;
        }

        /// <summary>
        /// Builds a walk-the-name-table request (XSGNI).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="magic">
        /// The lower bound. XROUT returns the first name whose magic number is greater than or
        /// equal to this. Pass null to ask for the name of the LOCAL system instead.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Repeatedly calling this with the previously returned magic number plus one is how the
        /// whole name table is enumerated. A returned parameter 1 of zero means the walk is done;
        /// parameter 3, when present, is the number of free connection points for a service name.
        /// </remarks>
        public static XroutMessage GetNextName(byte serial, XmsgMagicNumber? magic)
        {
            XroutMessage message = new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSGNI)
                .Build();

            if (magic.HasValue)
            {
                message.AddParameter(XroutParameter.Integer32(1, magic.Value.Value));
            }

            return message;
        }

        /// <summary>
        /// Builds a clear-port-name request (XSCNM).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <returns>
        /// The request message, to be sent FROM the port whose name is to be cleared.
        /// </returns>
        /// <remarks>
        /// Only needed to retire a name early; XROUT clears names by itself when it notices the
        /// port has closed.
        /// </remarks>
        public static XroutMessage ClearPortName(byte serial)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSCNM)
                .Build();
        }

        /// <summary>
        /// Builds a get-magic-number-from-name request (XSGMG).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="name">
        /// The port name to look up.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged - this is the one service that DOES hand out another task's magic number,
        /// which is why an ordinary task must use a letter instead. When several connection ports
        /// share the name, the most recently defined one is returned.
        /// </remarks>
        public static XroutMessage GetMagicFromName(byte serial, string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSGMG)
                .AddString(1, name)
                .Build();
        }

        /// <summary>
        /// Builds a get-information-about-name request (XSGIN).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="name">
        /// The port or system name to look up.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// The reply always carries the system number as parameter 2; parameter 1 appears only when
        /// the name was a PORT name rather than a system name. This is the unprivileged way to ask
        /// "where does this name live", without learning the magic number.
        /// </remarks>
        public static XroutMessage GetNameInformation(byte serial, string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSGIN)
                .AddString(1, name)
                .Build();
        }

        /// <summary>
        /// Builds a define-remote-name request (XSDRN).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="systemName">
        /// The system name to define; it must be unique in the name table.
        /// </param>
        /// <param name="systemNumber">
        /// The XMSG system number the name maps to, or null to CLEAR the name.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged. This defines the names usable as parameter 2 of a letter. A system may carry
        /// several names, and the manual advises naming the FUNCTION rather than the box -
        /// SIBAS-BACKEND or MAIL-HANDLER rather than ND-100-377.
        /// </remarks>
        public static XroutMessage DefineRemoteName(byte serial, string systemName, ushort? systemNumber)
        {
            if (systemName == null)
            {
                throw new ArgumentNullException(nameof(systemName));
            }

            XroutMessageBuilder builder = new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSDRN)
                .AddString(1, systemName);

            if (systemNumber.HasValue)
            {
                builder.AddInteger16(2, systemNumber.Value);
            }

            return builder.Build();
        }

        /// <summary>
        /// Builds a define-local-system request (XSDLO).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="systemNumber">
        /// The local system number, or zero to adopt the SINTRAN system number.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged, and normally unnecessary - XMSG defines the local system number when it
        /// starts, and redefining it is not allowed. The manual's numbering convention is: an
        /// ND-100 uses its serial number, an ND-500 its serial number plus 5000, an ND-10 plus
        /// 9000, and a satellite plus 10000.
        /// </remarks>
        public static XroutMessage DefineLocalSystem(byte serial, ushort systemNumber)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSDLO)
                .AddInteger16(1, systemNumber)
                .Build();
        }

        /// <summary>
        /// Builds a define-system-routing request (XSDSY).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="systemNumber">
        /// The system being routed to.
        /// </param>
        /// <param name="viaSystemNumber">
        /// The neighbour to route through; zero marks the system NOT AVAILABLE, and null REMOVES
        /// the entry from the routing table altogether.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged. Only needed for systems reached through a neighbour: XROUT discovers
        /// directly connected systems by itself when the link comes up. Without an entry here, a
        /// non-neighbour system cannot communicate with the local system at all.
        /// </remarks>
        public static XroutMessage DefineSystemRouting(byte serial, ushort systemNumber, ushort? viaSystemNumber)
        {
            XroutMessageBuilder builder = new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSDSY)
                .AddInteger16(1, systemNumber);

            if (viaSystemNumber.HasValue)
            {
                builder.AddInteger16(2, viaSystemNumber.Value);
            }

            return builder.Build();
        }

        /// <summary>
        /// Builds a get-routing-information request (XSGSY).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="systemNumber">
        /// The object system number. The reply carries the first system found greater than or equal
        /// to this, so repeating the call walks the whole routing table.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Parse the reply with <see cref="XroutReply.TryGetRoutingEntry"/>. This is the service behind
        /// the LIST-ROUTE operator command.
        /// </remarks>
        public static XroutMessage GetRoutingInformation(byte serial, ushort systemNumber)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSGSY)
                .AddInteger16(1, systemNumber)
                .Build();
        }

        /// <summary>
        /// Builds a start, stop or status request for an inter-system link (XSLKI).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="logicalUnit">
        /// The link's logical unit number.
        /// </param>
        /// <param name="timeoutUnits">
        /// The timeout in XMSG time units of 0.1 second each.
        /// </param>
        /// <param name="frameCount">
        /// What to do, encoded in the sign: greater than zero STARTS the link and allocates that
        /// many frames (the window size plus one); less than zero CLOSES it; exactly zero is a
        /// STATUS request, answered with the link state in parameter 1.
        /// </param>
        /// <param name="repeatCount">
        /// How many SABM frames to send while calling; negative means keep trying forever.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged; this is what the START-LINK and STOP-LINK operator commands send. The reply
        /// comes back as soon as the link enters the CALL state, not when it reaches RUN - see
        /// <see cref="XmsgLinkState"/> for the state values.
        /// </remarks>
        public static XroutMessage ControlLink(
            byte serial,
            ushort logicalUnit,
            ushort timeoutUnits,
            short frameCount,
            short repeatCount)
        {
            return new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSLKI)
                .AddInteger16(1, logicalUnit)
                .AddInteger16(2, timeoutUnits)
                .AddInteger16(3, unchecked((ushort)frameCount))
                .AddInteger16(4, unchecked((ushort)repeatCount))
                .Build();
        }

        /// <summary>
        /// Builds a start, stop or status request for a network server (XSNET).
        /// </summary>
        /// <param name="serial">
        /// The caller's serial number.
        /// </param>
        /// <param name="serverMagic">
        /// The magic number of the network server's port, obtained beforehand by talking to the
        /// server directly.
        /// </param>
        /// <param name="bufferCount">
        /// What to do, encoded in the sign, exactly as for a link: greater than zero starts the
        /// server with that many buffers, less than zero stops it, zero is a status request.
        /// </param>
        /// <param name="isWideArea">
        /// Non-zero to mark this server as a wide-area path. Letters sent with the local-area-only
        /// flag stop at such a server rather than travel over a chargeable network.
        /// </param>
        /// <returns>
        /// The request message.
        /// </returns>
        /// <remarks>
        /// Privileged. A network server substitutes any other network for HDLC or Megalink by
        /// carrying the same frames the XMSG link layer would have sent.
        /// </remarks>
        public static XroutMessage ControlNetworkServer(
            byte serial,
            XmsgMagicNumber serverMagic,
            short bufferCount,
            ushort isWideArea)
        {
            XroutMessage message = new XroutMessageBuilder()
                .WithSerial(serial)
                .WithService(XroutService.XSNET)
                .Build();

            message.AddParameter(XroutParameter.Integer32(1, serverMagic.Value));
            message.AddParameter(XroutParameter.Integer16(3, unchecked((ushort)bufferCount)));
            message.AddParameter(XroutParameter.Integer16(4, isWideArea));
            return message;
        }

        /// <summary>
        /// Appends caller-supplied parameter blocks to a built request.
        /// </summary>
        /// <param name="message">
        /// The message to extend.
        /// </param>
        /// <param name="payload">
        /// The blocks to append; null or empty appends nothing.
        /// </param>
        private static void AppendPayload(XroutMessage message, XroutParameter[]? payload)
        {
            if (payload == null)
            {
                return;
            }

            for (int i = 0; i < payload.Length; i++)
            {
                message.AddParameter(payload[i]);
            }
        }
    }
}
