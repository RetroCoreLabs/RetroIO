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
    public sealed class XmsgKernelServer : IXmsgServer, IXmsgDatagramSink, IXmsgNameOwner
    {
        private readonly XmsgKernel _kernel;
        private readonly List<XmsgFrame> _outbound;
        private readonly HashSet<ushort> _ownedPorts;

        private IXmsgServerTransport? _transport;

        /// <summary>
        /// Where a datagram's trailer starts: 13-byte SINTRAN header plus 19-byte sub-header.
        /// </summary>
        /// <summary>
        /// Where a message's data starts in a data frame, and where an XROUT letter starts: after the
        /// SINTRAN header, the sub-header and the XMCSM word.
        /// </summary>
        private const int BodyOffset = 28;

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
            : this(kernel, name, logicalPort, DefaultWirePortSeed)
        {
        }

        /// <summary>
        /// Initialises a bridge around a kernel, with an explicit seed for the random part of the
        /// wire port.
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
        /// <param name="wirePortSeed">
        /// The seed of the <see cref="XmsgRandomGenerator"/> that supplies the low seven bits of
        /// <see cref="WirePort"/>. It is drawn from once, here.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="kernel"/> or <paramref name="name"/> is null.
        /// </exception>
        public XmsgKernelServer(XmsgKernel kernel, string name, int logicalPort, ushort wirePortSeed)
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
            // The wire port is the low word of an XMSG magic number: the logical port in the high
            // nine bits and the kernel's random part (RNMAG, one ZRAND draw) in the low seven.
            // It is drawn once per registration, so it stays the same for the life of this object.
            WirePort = new XmsgPortWordAllocator(wirePortSeed).Next(logicalPort);

            FrameFlags = DefaultFrameFlags;
            Role = DefaultRole;
        }

        /// <summary>
        /// The generator seed used for the wire port's random part when the caller gives none.
        /// </summary>
        /// <remarks>
        /// The real kernel seeds from a SINTRAN cell whose content is unknown, so no value is the
        /// correct one; any seed is as faithful as any other.
        /// </remarks>
        public const ushort DefaultWirePortSeed = 0x2B67;

        /// <summary>
        /// The sub-header frame-flags byte used by default: <see cref="XmsgFrameFlags.ControlBare"/>, 0x82.
        /// </summary>
        /// <remarks>
        /// MEASURED. An ordinary port-to-port message from a SINTRAN task carries frame flags 0x82
        /// and role 0x84: D100's own chat Welcome, captured off the wire (RetroXmsg
        /// <c>ChatLoadRun.cs</c>), and every file-access message in the ND-to-ND capture. Until
        /// 2026-11-10 this was 0x86 with control service 0x01080000 and the data at offset 32, a
        /// layout of our own that no machine was seen to use.
        /// </remarks>
        public const byte DefaultFrameFlags = (byte)XmsgFrameFlags.ControlBare;

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
        /// Gets or sets the XROUT name table the tasks on this kernel claim their names in, or null.
        /// </summary>
        /// <remarks>
        /// With a table set, a letter from the wire to any name in it is handed to this server
        /// (<see cref="AnswersName"/>) and forwarded whole, XSLET header first, to the task that
        /// claimed the name - the same table the tasks' own letters to each other go through.
        /// </remarks>
        public XroutDirectory? Directory { get; set; }

        /// <inheritdoc/>
        public bool AnswersName(string name)
        {
            XroutDirectory? directory = Directory;
            return directory != null && directory.Contains(name);
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
            XmsgKernelServer server = new XmsgKernelServer(kernel, name, logicalPort, portSeed);
            kernel.AttachSink(server);
            return server;
        }

        /// <summary>
        /// Gets the name this server registers under in the node's registry, as given to the
        /// constructor.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the logical port the name resolves to, as given to the constructor.
        /// </summary>
        public int LogicalPort { get; }

        /// <summary>
        /// Gets the wire port: the low word of this server's magic number.
        /// </summary>
        /// <remarks>
        /// The high nine bits are <see cref="LogicalPort"/>; the low seven bits are one draw from
        /// <see cref="XmsgRandomGenerator"/>, taken when the server is built and kept.
        /// </remarks>
        public ushort WirePort { get; }

        /// <summary>
        /// Gets the number of kernel ports currently registered with <see cref="RegisterPort"/>.
        /// </summary>
        public int SessionCount
        {
            get { return _ownedPorts.Count; }
        }

        /// <summary>
        /// Gets the maximum number of concurrent sessions, which is unlimited here:
        /// <see cref="int.MaxValue"/>.
        /// </summary>
        public int SessionCapacity
        {
            get { return int.MaxValue; }
        }


        /// <summary>
        /// Gets or sets the frame-flags byte stamped on outgoing application datagrams.
        /// </summary>
        public byte FrameFlags { get; set; }

        /// <summary>
        /// Gets or sets the role byte stamped on outgoing application datagrams.
        /// </summary>
        public byte Role { get; set; }


        /// <summary>
        /// Copies a letter frame's whole XROUT letter, from <see cref="BodyOffset"/> to the end.
        /// </summary>
        /// <param name="frame">
        /// The letter frame.
        /// </param>
        /// <returns>
        /// The letter: XSLET header, parameter blocks, then the sender's data.
        /// </returns>
        private static byte[] WholeLetter(XmsgFrame frame)
        {
            byte[]? raw = frame.RawBytes;
            if (raw != null && raw.Length > BodyOffset)
            {
                byte[] letter = new byte[raw.Length - BodyOffset];
                Array.Copy(raw, BodyOffset, letter, 0, letter.Length);
                return letter;
            }

            // A frame built in memory has no raw bytes; its letter is the parsed body plus
            // whatever followed the declared length.
            return frame.GetBodyBytes();
        }

        /// <summary>
        /// Takes an ordinary message's data out of a data frame.
        /// </summary>
        /// <param name="frame">
        /// The received frame.
        /// </param>
        /// <returns>
        /// The data, or an empty array when the frame carries none.
        /// </returns>
        /// <remarks>
        /// MEASURED, D100's own chat Welcome off the wire (RetroXmsg <c>ChatLoadRun.cs</c>):
        /// <c>2113000E4E1F0064047F000A8BD2 21 00 82 84 4E1F 0211 0064 0632 000A ...</c> - XMCSM at wire
        /// 26-27 is the data's length (0x000A, ten bytes), and the data starts at wire 28. The same rule
        /// holds for every file-access message in the 2026-08-04 captures
        /// (<see cref="IXmsgServerTransport.BuildFragmentedBodyDatagram"/>). A message that came in
        /// fragments has been put back together by the node before it gets here, so XMCSM is the
        /// whole length. A frame whose XMCSM claims more than it carries is cut to what it carries;
        /// that is a damaged frame, not a convention.
        /// </remarks>
        private static byte[] ExtractBody(XmsgFrame frame)
        {
            byte[] raw = frame.RawBytes ?? frame.ToArray();
            if (raw.Length <= BodyOffset || frame.SubHeader == null)
            {
                return Array.Empty<byte>();
            }

            int available = raw.Length - BodyOffset;
            int length = frame.SubHeader.Xmcsm;
            if (length > available)
            {
                length = available;
            }

            byte[] body = new byte[length];
            Array.Copy(raw, BodyOffset, body, 0, length);
            return body;
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

        /// <summary>
        /// Returns whether a wire port was registered with <see cref="RegisterPort"/>, so the node
        /// routes datagrams for it here.
        /// </summary>
        /// <param name="port">
        /// The destination wire port of an incoming datagram.
        /// </param>
        /// <returns>
        /// <c>true</c> when the port is registered.
        /// </returns>
        public bool OwnsPort(ushort port)
        {
            return _ownedPorts.Contains(port);
        }

        /// <summary>
        /// Delivers a datagram routed to this server into the kernel, as a message from the sending
        /// port to the destination port named in its sub-header.
        /// </summary>
        /// <param name="incoming">
        /// The received datagram.
        /// </param>
        /// <param name="transport">
        /// The node transport; it is kept and used to build any datagrams the kernel sends.
        /// </param>
        /// <returns>
        /// Every datagram queued for sending, in order: those queued before this call and not yet
        /// taken by <see cref="DrainPending"/>, followed by those queued while the message was
        /// delivered. Empty when there were none or when <paramref name="incoming"/> has no
        /// sub-header (in that case the queue is left as it was).
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="incoming"/> is <c>null</c>.
        /// </exception>
        /// <remarks>
        /// The payload is the raw trailer of the frame, not a decoded view of it. The send options
        /// are taken back out of the sub-header role byte, so a secure message stays secure. The
        /// status the kernel returns for the delivery is not reported. The outgoing queue is
        /// not emptied before delivery, so no queued datagram is lost; the queue is emptied
        /// after delivery, because everything in it is handed back in the result.
        /// </remarks>
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

            XmsgMagicNumber destination = XmsgMagicNumber.FromRegisterPair(
                incoming.SubHeader.DestinationSystem, incoming.SubHeader.DestinationPort);
            XmsgMagicNumber sender = XmsgMagicNumber.FromRegisterPair(
                incoming.SubHeader.SourceSystem, incoming.SubHeader.SourcePort);

            // Take the RAW trailer, not the decoded view. XmsgFrame.Parse interprets a trailer
            // according to the XMCSM class - as an XROUT letter or a TAD chain - and only leaves
            // TrailingBytes populated when it could not. Application payload is opaque to all of
            // that, so reading the decoded view would silently deliver nothing.
            byte[] payload = ExtractBody(incoming);

            // The role byte is the send-option high byte, so the options the sender used come back
            // out of it - which is how a secure message stays secure across the wire.
            XmsgSendFlags flags = (XmsgSendFlags)XmsgOptionConversion.FromRoleByte(
                (XmsgSendOptions)incoming.SubHeader.Role);

            XroutDirectory? directory = Directory;
            if (incoming.SubHeader.DestinationPort == 0 && directory != null)
            {
                // A letter to a NAME (XSLET to port 0, the XROUT port). XROUT forwards the whole
                // letter, header included, so the task gets exactly what a SINTRAN task gets
                // (XroutLetter). The letter starts at frame offset 28: serial, service, length,
                // the parameter blocks, then the sender's data (XroutLetterFramingTests).
                //
                // NOT DONE: when the name is unknown or full, a SINTRAN XROUT answers the sender
                // with an XROUT error; this returns nothing. Listed in BUGS.md.
                directory.ForwardLetter(sender, WholeLetter(incoming));
            }
            else
            {
                _kernel.Deliver(destination, sender, payload, flags);
            }

            XmsgFrame[] result = _outbound.ToArray();
            _outbound.Clear();
            return result;
        }

        /// <summary>
        /// Hands over every datagram the kernel has queued for sending since the last drain.
        /// </summary>
        /// <param name="transport">
        /// The node transport; it is kept and used to build any datagrams the kernel sends later.
        /// </param>
        /// <returns>
        /// The queued datagrams, in order; empty when nothing is pending.
        /// </returns>
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

        /// <summary>
        /// Called when a remote node acknowledged one of our frames. This server does nothing with
        /// it.
        /// </summary>
        /// <param name="remoteNode">
        /// The node that acknowledged. It is not read.
        /// </param>
        /// <param name="ackedFlags1">
        /// The Flags 1 the acknowledgement echoes. It is not read.
        /// </param>
        /// <remarks>
        /// The request-response layer paces itself on its own messages, not on the transport's
        /// acknowledgements, so there is nothing to release here.
        /// </remarks>
        public void NotifyAck(ushort remoteNode, ushort ackedFlags1)
        {
        }

        /// <summary>
        /// Gets whether this server's output advances on the remote's acknowledgement. Always
        /// <c>false</c>.
        /// </summary>
        /// <remarks>
        /// False: this server queues an outgoing datagram the moment the kernel sends it, so an
        /// incoming acknowledgement never releases anything that was being held back.
        /// </remarks>
        public bool AdvancesOutputOnAck
        {
            get { return false; }
        }

        /// <summary>
        /// Builds a datagram for a message the kernel cannot deliver locally and queues it for the
        /// node to transmit.
        /// </summary>
        /// <param name="destination">
        /// The magic number of the receiving port. Its system number is used as both the
        /// destination node and the destination system.
        /// </param>
        /// <param name="sender">
        /// The magic number of the sending port, so the receiver can answer.
        /// </param>
        /// <param name="userData">
        /// The message buffer's user data; it is copied into the datagram.
        /// </param>
        /// <param name="flags">
        /// The send options, carried in the sub-header role byte. When they give an empty role
        /// byte, <see cref="Role"/> is used instead.
        /// </param>
        /// <returns>
        /// The completed status when the datagram was queued, or XENRU when no transport is
        /// attached yet.
        /// </returns>
        /// <remarks>
        /// The datagram is queued, not sent. Whichever of <see cref="Handle"/> or
        /// <see cref="DrainPending"/> is called next returns it, once. It is laid out as a SINTRAN task's
        /// ordinary message is on the wire: the data at offset 28, its length as XMCSM,
        /// <see cref="FrameFlags"/> (see <see cref="DefaultFrameFlags"/> for the measurement), split into
        /// fragments when it is too long for one frame. It is built as a fresh message, not an answer.
        /// </remarks>
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

            // SECURE IS NOT A WIRE BIT. It tells the SENDING machine to return the message (XMTRE)
            // when it cannot be delivered; the frame itself does not carry it. MEASURED: CHATSV.PLNC
            // sends every message with 2**XFSEC (its one xmpsend call), and D100's Welcome from that
            // server came off the wire with role 0x84, no 0x02 bit (RetroXmsg ChatLoadRun.cs). The
            // secure bit has never been seen set in any capture (see XmsgSendOptions.Secure).
            // Not proven: that the capture was made with exactly that build of CHATSV; its notes
            // date the secure change (2026-08-18) before the load run.
            XmsgSendOptions role = XmsgOptionConversion.ToRoleByte((XmsgOption)(flags & ~XmsgSendFlags.Secure));

            IReadOnlyList<XmsgFrame> frames = _transport.BuildFragmentedBodyDatagram(
                destination.SystemNumber,
                destination.SystemNumber,
                destination.PortWord,
                sender.PortWord,
                FrameFlags,
                (byte)(role == XmsgSendOptions.None ? (XmsgSendOptions)Role : role),
                userData.ToArray(),
                // ORIGINATED: the API's Send is a fresh message, not an answer to a received one.
                XmsgAnsweredFlags1.None);

            for (int i = 0; i < frames.Count; i++)
            {
                _outbound.Add(frames[i]);
            }

            return XmsgStatus.Completed;
        }
    }
}
