//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Node.Services;

namespace RetroIO.Protocols.Xmsg.Api.Node
{
    /// <summary>
    /// Runs an <see cref="XmsgKernel"/> as a node server, so tasks written against the manual's API
    /// can talk to another system over the real transport.
    /// </summary>
    /// <remarks>
    /// <para><b>Both halves of the bridge</b></para>
    /// Inbound, this is an <see cref="IXmsgServer"/>: the node routes a datagram here and it becomes
    /// a message on one of the kernel's ports, addressed by magic number. Outbound, it is the
    /// kernel's <see cref="IXmsgDatagramSink"/>: a send the kernel cannot satisfy locally is turned
    /// into a datagram and queued for the node to transmit.
    /// <para><b>Node number and system number</b></para>
    /// The two are treated as the same value, which is what every capture shows (nodes 100, 102 and
    /// 103 appear as both the header node and the sub-header system). If a topology ever separates
    /// them, this is the one place that assumption lives.
    /// <para><b>The frame class is OURS</b></para>
    /// A datagram needs an XMCSM control word, a frame-flags byte and a role byte. The values used
    /// for application traffic are configurable and default to the ones observed on data-carrying
    /// frames; they describe OUR traffic and are not a claim about what any ND product sends. The
    /// letters and TAD sessions decoded elsewhere in this repo are unaffected - they are built by
    /// their own code paths.
    /// </remarks>
    public sealed class XmsgKernelServer : IXmsgServer, IXmsgDatagramSink
    {
        private readonly XmsgKernel _kernel;
        private readonly List<XmsgFrame> _outbound;
        private readonly HashSet<ushort> _ownedPorts;

        private IXmsgServerTransport? _transport;

        /// <summary>
        /// Where a datagram's trailer starts: 13-byte SINTRAN header plus 19-byte sub-header.
        /// </summary>
        private const int TrailerOffset = 32;

        /// <summary>
        /// Initialises a bridge around a kernel.
        /// </summary>
        /// <param name="kernel">
        /// The kernel whose ports this server exposes to the node.
        /// </param>
        /// <param name="name">
        /// The name this server registers under in the node's registry.
        /// </param>
        /// <param name="logicalPort">
        /// The logical port the name resolves to.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="kernel"/> or <paramref name="name"/> is null.
        /// </exception>
        public XmsgKernelServer(XmsgKernel kernel, string name, int logicalPort)
        {
            if (kernel == null)
            {
                throw new ArgumentNullException(nameof(kernel));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            _kernel = kernel;
            _outbound = new List<XmsgFrame>();
            _ownedPorts = new HashSet<ushort>();

            Name = name;
            LogicalPort = logicalPort;
            WirePort = (ushort)(logicalPort << 7);

            ControlService = DefaultControlService;
            FrameFlags = DefaultFrameFlags;
            Role = DefaultRole;
        }

        /// <summary>
        /// The XMCSM control word used for application traffic by default.
        /// </summary>
        /// <remarks>
        /// Chosen from the data-carrying frames in the captures. It is OUR choice for OUR traffic.
        /// </remarks>
        public const uint DefaultControlService = 0x01080000;

        /// <summary>
        /// The sub-header frame-flags byte used by default.
        /// </summary>
        public const byte DefaultFrameFlags = 0x86;

        /// <summary>
        /// The sub-header role byte used by default.
        /// </summary>
        /// <remarks>
        /// XFWTF together with XFROU, the composition observed on asker data frames.
        /// </remarks>
        public const byte DefaultRole = 0x84;

        /// <summary>
        /// Gets the kernel this server exposes.
        /// </summary>
        public XmsgKernel Kernel
        {
            get { return _kernel; }
        }

        /// <summary>
        /// Creates a kernel and the bridge that carries its traffic, already wired together.
        /// </summary>
        /// <param name="systemNumber">
        /// This system's number.
        /// </param>
        /// <param name="portSeed">
        /// The seed for the random part of minted port words.
        /// </param>
        /// <param name="name">
        /// The name to register in the node's registry.
        /// </param>
        /// <param name="logicalPort">
        /// The logical port the name resolves to.
        /// </param>
        /// <returns>
        /// The bridge, with its <see cref="Kernel"/> ready to use.
        /// </returns>
        /// <remarks>
        /// The two objects reference each other, so this does the wiring in the one order that
        /// works: build the kernel, build the bridge around it, then attach the bridge as the
        /// kernel's sink.
        /// </remarks>
        public static XmsgKernelServer Create(ushort systemNumber, ushort portSeed, string name, int logicalPort)
        {
            XmsgKernel kernel = new XmsgKernel(systemNumber, portSeed, null);
            XmsgKernelServer server = new XmsgKernelServer(kernel, name, logicalPort);
            kernel.AttachSink(server);
            return server;
        }

        /// <inheritdoc/>
        public string Name { get; }

        /// <inheritdoc/>
        public int LogicalPort { get; }

        /// <inheritdoc/>
        public ushort WirePort { get; }

        /// <inheritdoc/>
        public int SessionCount
        {
            get { return _ownedPorts.Count; }
        }

        /// <inheritdoc/>
        public int SessionCapacity
        {
            get { return int.MaxValue; }
        }

        /// <summary>
        /// Gets or sets the XMCSM control word stamped on outgoing application datagrams.
        /// </summary>
        public uint ControlService { get; set; }

        /// <summary>
        /// Gets or sets the frame-flags byte stamped on outgoing application datagrams.
        /// </summary>
        public byte FrameFlags { get; set; }

        /// <summary>
        /// Gets or sets the role byte stamped on outgoing application datagrams.
        /// </summary>
        public byte Role { get; set; }

        /// <summary>
        /// Extracts a datagram's raw trailer - the application payload - from its wire bytes.
        /// </summary>
        /// <param name="frame">
        /// The received frame.
        /// </param>
        /// <returns>
        /// The payload bytes, or an empty array when the frame carries none.
        /// </returns>
        /// <remarks>
        /// The trailer starts at absolute offset 32: the 14-byte SINTRAN header, the 14-byte XMSG
        /// sub-header, and the 4-byte XROUT header, whose big-endian length word (wire 30-31)
        /// bounds it. The old model reached the same 32 by a wrong sum (13 + 19) and read that
        /// same length byte as a sub-header "XMLEN". Falling back to the whole remainder when the
        /// length overruns keeps a malformed frame from throwing here, and falling back to
        /// TrailingBytes covers a frame that was constructed rather than received.
        /// </remarks>
        private static byte[] ExtractTrailer(XmsgFrame frame)
        {
            byte[]? raw = frame.RawBytes;
            if (raw == null || raw.Length <= TrailerOffset)
            {
                // A frame that was never serialised - one built in memory rather than parsed off
                // the wire - has no raw bytes, so fall back to whatever the builder attached.
                return frame.TrailingBytes ?? Array.Empty<byte>();
            }

            int available = raw.Length - TrailerOffset;
            // Wire 30-31 is the XROUT declared length; its low byte is what used to be read as
            // the sub-header's XMLEN, so this keeps the identical value.
            int length = frame.SubHeader != null ? raw[TrailerOffset - 1] : available;
            if (length > available || length < 0)
            {
                length = available;
            }

            byte[] payload = new byte[length];
            Array.Copy(raw, TrailerOffset, payload, 0, length);
            return payload;
        }

        /// <summary>
        /// Attaches the node transport this server builds its outgoing datagrams with.
        /// </summary>
        /// <param name="transport">
        /// The transport, normally the <c>XmsgServerHost</c> this server is registered with.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="transport"/> is null.
        /// </exception>
        /// <remarks>
        /// Attach this at wiring time, right after registering with the host. Without it the
        /// server has no transport until the node happens to route it a datagram, so an
        /// application's very FIRST send would fail with XENRU even though the link was up - the
        /// kernel has somewhere to send to, but the bridge does not yet know how to build a frame.
        /// </remarks>
        public void AttachTransport(IXmsgServerTransport transport)
        {
            if (transport == null)
            {
                throw new ArgumentNullException(nameof(transport));
            }

            _transport = transport;
        }

        /// <summary>
        /// Tells the bridge that a kernel port should receive datagrams from the node.
        /// </summary>
        /// <param name="portWord">
        /// The wire port word of a port the kernel has open.
        /// </param>
        /// <remarks>
        /// The node routes session data by port, so a port has to be announced before traffic for
        /// it will arrive. Register the port word of every port an application opens.
        /// </remarks>
        public void RegisterPort(ushort portWord)
        {
            _ownedPorts.Add(portWord);
        }

        /// <summary>
        /// Stops routing datagrams for a port.
        /// </summary>
        /// <param name="portWord">
        /// The wire port word to forget.
        /// </param>
        public void UnregisterPort(ushort portWord)
        {
            _ownedPorts.Remove(portWord);
        }

        /// <inheritdoc/>
        public bool OwnsPort(ushort port)
        {
            return _ownedPorts.Contains(port);
        }

        /// <inheritdoc/>
        public IReadOnlyList<XmsgFrame> Handle(XmsgFrame incoming, IXmsgServerTransport transport)
        {
            if (incoming == null)
            {
                throw new ArgumentNullException(nameof(incoming));
            }

            if (incoming.SubHeader == null)
            {
                return Array.Empty<XmsgFrame>();
            }

            _transport = transport;
            _outbound.Clear();

            XmsgMagicNumber destination = XmsgMagicNumber.FromRegisterPair(
                incoming.SubHeader.DestinationSystem, incoming.SubHeader.DestinationPort);
            XmsgMagicNumber sender = XmsgMagicNumber.FromRegisterPair(
                incoming.SubHeader.SourceSystem, incoming.SubHeader.SourcePort);

            // Take the RAW trailer, not the decoded view. XmsgFrame.Parse interprets a trailer
            // according to the XMCSM class - as an XROUT letter or a TAD chain - and only leaves
            // TrailingBytes populated when it could not. Application payload is opaque to all of
            // that, so reading the decoded view would silently deliver nothing.
            byte[] payload = ExtractTrailer(incoming);

            // The role byte is the send-option high byte, so the options the sender used come back
            // out of it - which is how a secure message stays secure across the wire.
            XmsgSendFlags flags = (XmsgSendFlags)XmsgOptionConversion.FromRoleByte(
                (XmsgSendOptions)incoming.SubHeader.Role);

            _kernel.Deliver(destination, sender, payload, flags);

            XmsgFrame[] result = _outbound.ToArray();
            _outbound.Clear();
            return result;
        }

        /// <inheritdoc/>
        public IReadOnlyList<XmsgFrame> DrainPending(IXmsgServerTransport transport)
        {
            _transport = transport;
            if (_outbound.Count == 0)
            {
                return Array.Empty<XmsgFrame>();
            }

            XmsgFrame[] result = _outbound.ToArray();
            _outbound.Clear();
            return result;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The request-response layer paces itself on its own messages, not on the transport's
        /// acknowledgements, so there is nothing to release here.
        /// </remarks>
        public void NotifyAck(ushort remoteNode, ushort ackedFlags1)
        {
        }

        /// <inheritdoc/>
        /// <remarks>
        /// False: this server queues an outgoing datagram the moment the kernel sends it, so an
        /// incoming acknowledgement never releases anything that was being held back.
        /// </remarks>
        public bool AdvancesOutputOnAck
        {
            get { return false; }
        }

        /// <inheritdoc/>
        public XmsgStatus Send(
            XmsgMagicNumber destination,
            XmsgMagicNumber sender,
            ReadOnlySpan<byte> userData,
            XmsgSendFlags flags)
        {
            if (_transport == null)
            {
                // No transport attached and none learned from arriving traffic, so there is no
                // way to reach another system. See AttachTransport.
                return XmsgStatus.Failure(XmsgError.XENRU);
            }

            XmsgSendOptions role = XmsgOptionConversion.ToRoleByte((XmsgOption)flags);

            XmsgFrame frame = _transport.BuildDatagram(
                destination.SystemNumber,
                destination.SystemNumber,
                destination.PortWord,
                sender.PortWord,
                ControlService,
                FrameFlags,
                (byte)(role == XmsgSendOptions.None ? (XmsgSendOptions)Role : role),
                userData.ToArray(),
                // ORIGINATED: the API's Send is a fresh message, not an answer to a received one.
                XmsgAnsweredFlags1.None);

            _outbound.Add(frame);
            return XmsgStatus.Completed;
        }
    }
}
