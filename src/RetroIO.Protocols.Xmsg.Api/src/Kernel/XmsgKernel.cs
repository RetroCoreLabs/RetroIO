using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// A working XMSG kernel for one SYSTEM: ports, message buffers, currency and queues,
    /// following the rules of appendix A of the COSMOS Programmer Guide.
    /// </summary>
    /// <remarks>
    /// <para><b>One kernel per system, not per task</b></para>
    /// The manual is explicit that a SYSTEM is "a processing unit that runs an independent XMSG
    /// kernel" (section 1.2.2), and that tasks in the same system exchange messages through it. So
    /// one instance of this class serves every task on one system; each task simply owns the ports
    /// it opened. Two instances are two SYSTEMS and cannot deliver to each other locally - traffic
    /// between them goes through <see cref="IXmsgDatagramSink"/>, exactly as it would on the wire.
    /// <para><b>What is real here</b></para>
    /// Everything the manual specifies about a task's own view of XMSG - the port list and its
    /// default-port rule, the buffer pool, task-current and port-current messages, the per-port
    /// queue with high-priority insertion, secure messages coming back when they cannot be
    /// delivered, and the exact status each function returns. Two tasks that share a kernel talk to
    /// each other end to end, which is what the manual calls the same-system case: the send
    /// TRANSFERS the buffer rather than copying it.
    /// <para><b>What is delegated</b></para>
    /// A send to another system goes to <see cref="IXmsgDatagramSink"/>. Nothing in this class
    /// touches an envelope, a Counter, a channel byte or a link sequence - that belongs to the node
    /// layer, and keeping it out is what makes this testable in isolation.
    /// <para><b>Not a simulation of SINTRAN</b></para>
    /// Waiting is not modelled: options that would suspend the task (XFWTF) are accepted and
    /// treated as a poll, because there is no scheduler here to suspend. Calls report
    /// not-terminated exactly as a real kernel does when the wait option is absent. Wake-up bits
    /// are likewise accepted and ignored. This is stated rather than hidden because a caller
    /// porting real XMSG code needs to know which semantics it can lean on.
    /// </remarks>
    public sealed class XmsgKernel : IXmsgKernel
    {
        private readonly ushort _systemNumber;
        private IXmsgDatagramSink? _sink;
        private readonly XmsgPortWordAllocator _ports;

        private readonly List<Port> _portList;
        private readonly Dictionary<int, Buffer> _buffers;

        // The two allocation pools (XFALM/XFFRM). They are counters rather than real buffers
        // because nothing here models a finite free-space pool yet - what the API has to get right
        // is the BOOKKEEPING the manual specifies: one size per pool, and a truthful freed count.
        // Programmer Guide sections 3.2.4 and 3.2.5.
        private int _allocated;
        private int _allocatedSize;
        private int _exclusiveAllocated;
        private int _exclusiveAllocatedSize;

        // Remote systems defined as friends of ours. XFCPV consults this to decide whether a
        // sender's SYSTEM is privileged - the half of the answer that XFPRV does not cover. Real
        // SINTRAN keeps it through the XROUT service XSDAT, not through MON 200B, which is why
        // DefineFriendSystem is not itself an XMSG function.
        private readonly HashSet<ushort> _friendSystems = new HashSet<ushort>();

        // The XFDMM ceiling on message space this task may own at once. ZERO MEANS NO CEILING, and
        // that is our choice rather than the manual's: the real default is "system dependent and
        // defined when the XMSG system is generated" and we do not know what any given system was
        // generated with. Starting unlimited means we never refuse a reservation on a limit we
        // invented - the failure only becomes possible once a caller sets a real one.
        private int _maximumMessageSpace;

        private int _nextPortNumber;
        private int _nextMessageId;
        private XmsgMessageIdentifier _taskCurrent;
        private bool _privileged;

        /// <summary>
        /// Initialises a kernel for one system.
        /// </summary>
        /// <param name="systemNumber">
        /// This system's number, which becomes the high word of every magic number the kernel mints.
        /// </param>
        /// <param name="portSeed">
        /// The seed for the random part of minted port words.
        /// </param>
        /// <param name="sink">
        /// Where to send messages addressed to another system, or <c>null</c> for a purely local
        /// kernel.
        /// </param>
        public XmsgKernel(ushort systemNumber, ushort portSeed, IXmsgDatagramSink? sink)
        {
            _systemNumber = systemNumber;
            _sink = sink;
            _ports = new XmsgPortWordAllocator(portSeed);
            _portList = new List<Port>();
            _buffers = new Dictionary<int, Buffer>();
            _nextPortNumber = 1;
            _nextMessageId = 1;
            _taskCurrent = XmsgMessageIdentifier.None;
        }

        /// <summary>
        /// Attaches the sink that carries messages to other systems.
        /// </summary>
        /// <param name="sink">
        /// The outbound transport.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="sink"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a sink is already attached.
        /// </exception>
        /// <remarks>
        /// Wiring after construction exists because a kernel and its transport each need the other:
        /// the transport delivers arriving messages INTO the kernel, and the kernel hands outgoing
        /// ones to the transport. Constructing the kernel first and attaching once keeps that cycle
        /// explicit instead of hiding it behind a half-built object. It can only be done once, so a
        /// kernel cannot silently change where its traffic goes.
        /// </remarks>
        public void AttachSink(IXmsgDatagramSink sink)
        {
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            if (_sink != null)
            {
                throw new InvalidOperationException("This kernel already has a sink attached.");
            }

            _sink = sink;
        }

        /// <summary>
        /// Gets this system's number.
        /// </summary>
        public ushort SystemNumber
        {
            get { return _systemNumber; }
        }

        /// <summary>
        /// Gets a value indicating whether the task has been made privileged.
        /// </summary>
        public bool IsPrivileged
        {
            get { return _privileged; }
        }

        /// <summary>
        /// Gets the number of ports the task currently has open.
        /// </summary>
        public int OpenPortCount
        {
            get { return _portList.Count; }
        }

        /// <summary>
        /// Gets the number of message buffers the task currently owns.
        /// </summary>
        public int OwnedBufferCount
        {
            get { return _buffers.Count; }
        }

        /// <inheritdoc/>
        public XmsgStatus OpenPort(out XmsgPortNumber port)
        {
            Port created = new Port(_nextPortNumber, _ports.Next(_nextPortNumber), _systemNumber);
            _nextPortNumber++;

            // XMSG inserts a newly opened port at the TOP of the list, which is what makes it the
            // task's default port (appendix A section 3.1).
            _portList.Insert(0, created);

            port = new XmsgPortNumber(created.Number);
            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public XmsgStatus ClosePort(XmsgPortNumber port)
        {
            if (port.IsAll)
            {
                while (_portList.Count > 0)
                {
                    CloseOne(_portList[0]);
                }

                return XmsgStatus.Completed;
            }

            Port? target = Resolve(port);
            if (target == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIPN);
            }

            CloseOne(target);
            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public XmsgPortStatus GetPortStatus(XmsgPortNumber port, XmsgWaitOptions options)
        {
            Port? target = Resolve(port);
            if (target == null)
            {
                return new XmsgPortStatus(
                    XmsgStatus.Failure(XmsgError.XEIPN),
                    XmsgHashedMagicNumber.None,
                    XmsgMessageIdentifier.None,
                    0);
            }

            Buffer? head = Peek(target, options);
            if (head == null)
            {
                // The queue length is reported even when nothing matched the requested options.
                return new XmsgPortStatus(
                    XmsgStatus.NotTerminated,
                    XmsgHashedMagicNumber.None,
                    XmsgMessageIdentifier.None,
                    target.Queue.Count);
            }

            return new XmsgPortStatus(
                XmsgStatus.Success((int)head.Type),
                Hash(head.Sender),
                new XmsgMessageIdentifier(head.Id),
                target.Queue.Count);
        }

        /// <inheritdoc/>
        public XmsgStatus GetGeneralStatus(XmsgPortNumber lastPort, XmsgWaitOptions options, out XmsgPortNumber port)
        {
            port = XmsgPortNumber.Default;
            if (_portList.Count == 0)
            {
                return XmsgStatus.NotTerminated;
            }

            // The search starts AFTER the named port and wraps, which is what makes round-robin
            // service possible (appendix A section 3.1.4).
            Port? last = Resolve(lastPort);
            int start = last == null ? 0 : _portList.IndexOf(last) + 1;

            for (int step = 0; step < _portList.Count; step++)
            {
                Port candidate = _portList[(start + step) % _portList.Count];
                if (Peek(candidate, options) != null)
                {
                    port = new XmsgPortNumber(candidate.Number);
                    return XmsgStatus.Completed;
                }
            }

            return XmsgStatus.NotTerminated;
        }

        /// <inheritdoc/>
        public XmsgStatus Disconnect()
        {
            ClosePort(XmsgPortNumber.All);
            _buffers.Clear();

            // XFDCT "releases every XMSG resource the task holds" (Programmer Guide 3.1.2), and
            // allocated buffers are a resource the task holds - so both pools go with it.
            _allocated = 0;
            _allocatedSize = 0;
            _exclusiveAllocated = 0;
            _exclusiveAllocatedSize = 0;

            _taskCurrent = XmsgMessageIdentifier.None;
            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public XmsgStatus ReserveBuffer(int byteCount, XmsgBufferOptions options, out XmsgMessageIdentifier message)
        {
            message = XmsgMessageIdentifier.None;

            if (byteCount < 0)
            {
                return XmsgStatus.Failure(XmsgError.XEILM);
            }

            Buffer buffer = new Buffer(_nextMessageId++, byteCount);
            _buffers.Add(buffer.Id, buffer);

            // Referring to a buffer makes it task-current (section 1.2.4).
            message = new XmsgMessageIdentifier(buffer.Id);
            _taskCurrent = message;
            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public XmsgStatus ReleaseBuffer(XmsgMessageIdentifier message)
        {
            Buffer? buffer = ResolveBuffer(message, null);
            if (buffer == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIRM);
            }

            _buffers.Remove(buffer.Id);
            ForgetCurrency(buffer.Id);
            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public XmsgListing ListMessagesAndPorts(int fromMessageId, int fromPortNumber)
        {
            // Two independent searches, each "the first found equal to or greater than" its request.
            // Neither constrains the other, so a caller walking only ports can ignore the rest.
            int foundMessage = 0;
            int foundSize = 0;

            foreach (KeyValuePair<int, Buffer> entry in _buffers)
            {
                if (entry.Key < fromMessageId)
                {
                    continue;
                }

                if (foundMessage == 0 || entry.Key < foundMessage)
                {
                    foundMessage = entry.Key;

                    // Size, not Length: the manual asks for what the buffer was RESERVED with, not
                    // how many bytes have been written into it.
                    foundSize = entry.Value.Data.Size;
                }
            }

            int foundPort = 0;
            for (int i = 0; i < _portList.Count; i++)
            {
                int number = _portList[i].Number;
                if (number < fromPortNumber)
                {
                    continue;
                }

                if (foundPort == 0 || number < foundPort)
                {
                    foundPort = number;
                }
            }

            return new XmsgListing(XmsgStatus.Completed, foundMessage, foundSize, foundPort);
        }

        /// <inheritdoc/>
        public XmsgQueueSnapshot GetGeneralStatusMultiple()
        {
            ushort queued = 0;
            ushort routerFirst = 0;
            ushort returnedFirst = 0;

            // Bit 0 is the port opened MOST RECENTLY - ND's example is explicit: ports opened 6, 9,
            // 3 map to bits 2, 1, 0 in that order.
            //
            // That is the list's OWN order, walked forwards: OpenPort inserts at index 0 so the
            // newest port is already first. The two conventions agreeing is a small independent
            // check that the bit order was read the right way round.
            for (int bit = 0; bit < 16 && bit < _portList.Count; bit++)
            {
                Port port = _portList[bit];
                if (port.Queue.Count == 0)
                {
                    continue;
                }

                int mask = 1 << bit;
                queued |= (ushort)mask;

                // Only the FIRST queued message decides the type bits - a port with a router
                // message behind a normal one reports neither, which is what ND's example shows
                // for port 9 once its XMROU message has been received.
                switch (port.Queue[0].Type)
                {
                    case XmsgMessageType.XMROU:
                        routerFirst |= (ushort)mask;
                        break;

                    case XmsgMessageType.XMTRE:
                        returnedFirst |= (ushort)mask;
                        break;

                    default:
                        // XMTNO, XMHIP and XMBNC all leave both type bits clear. ND says so
                        // directly, and this snapshot cannot tell those three apart.
                        break;
                }
            }

            return new XmsgQueueSnapshot(queued, routerFirst, returnedFirst);
        }

        /// <inheritdoc/>
        public XmsgStatus DefineMaximumMemory(int byteCount)
        {
            if (byteCount < 0)
            {
                return XmsgStatus.Failure(XmsgError.XEILM);
            }

            // "This can be changed for privileged tasks" - section 4.3. An unprivileged task asking
            // to raise its own ceiling is exactly what the privilege exists to stop.
            if (!_privileged)
            {
                return XmsgStatus.Failure(XmsgError.XEPRV);
            }

            _maximumMessageSpace = byteCount;
            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public void DefineFriendSystem(ushort systemNumber, bool isFriend)
        {
            if (isFriend)
            {
                _friendSystems.Add(systemNumber);
            }
            else
            {
                _friendSystems.Remove(systemNumber);
            }
        }

        /// <inheritdoc/>
        public XmsgPrivilegeCheck CheckSenderPrivileges(XmsgMessageIdentifier message)
        {
            Buffer? buffer = ResolveBuffer(message, null);
            if (buffer == null)
            {
                return new XmsgPrivilegeCheck(
                    XmsgStatus.Failure(XmsgError.XEIRM), false, XmsgPrivilegeInformation.NeitherPrivileged);
            }

            // "If MESAD is not -1, the specified message becomes the task current message."
            if (!message.IsCurrent)
            {
                _taskCurrent = new XmsgMessageIdentifier(buffer.Id);
            }

            // A returned message has no sender to judge, so it short-circuits everything else.
            if (buffer.Type == XmsgMessageType.XMTRE)
            {
                return new XmsgPrivilegeCheck(
                    XmsgStatus.Completed, false, XmsgPrivilegeInformation.ReturnedMessage);
            }

            // The two privileges are independent and are earned in different places: the TASK by
            // calling XFPRV, the SYSTEM by being defined a friend through XROUT service XSDAT. A
            // sender inside this system counts as coming from a privileged system by being local.
            bool local = buffer.Sender.IsNone || buffer.Sender.SystemNumber == _systemNumber;
            bool systemPrivileged = local || _friendSystems.Contains(buffer.Sender.SystemNumber);

            // Our kernel models ONE task, so the sending task's privilege is our own _privileged
            // flag whenever the message came from inside this system. A message from another system
            // carries no task-privilege marker on the wire, so the honest answer for a remote sender
            // is "not privileged" - which is also the safe answer.
            bool taskPrivileged = local && _privileged;

            if (taskPrivileged && systemPrivileged)
            {
                // Authorised. Here D says WHERE it came from, not what is missing.
                return new XmsgPrivilegeCheck(
                    XmsgStatus.Completed,
                    true,
                    local ? XmsgPrivilegeInformation.NeitherPrivileged : XmsgPrivilegeInformation.SystemOnly);
            }

            // Refused. Here the same two values say WHICH privilege was absent.
            XmsgPrivilegeInformation reason;
            if (systemPrivileged && !taskPrivileged)
            {
                reason = XmsgPrivilegeInformation.SystemOnly;
            }
            else if (taskPrivileged)
            {
                reason = XmsgPrivilegeInformation.TaskOnly;
            }
            else
            {
                reason = XmsgPrivilegeInformation.NeitherPrivileged;
            }

            return new XmsgPrivilegeCheck(XmsgStatus.Completed, false, reason);
        }

        /// <inheritdoc/>
        public XmsgStatus AllocateBuffers(int byteCount, int messageCount, bool exclusive)
        {
            if (byteCount < 0 || messageCount < 0)
            {
                return XmsgStatus.Failure(XmsgError.XEILM);
            }

            // "All allocated messages for a given task must be of the same size" - and the exclusive
            // and non-exclusive pools each carry their own size, which may differ from one another.
            // Programmer Guide section 3.2.4.
            int held = exclusive ? _exclusiveAllocated : _allocated;
            int heldSize = exclusive ? _exclusiveAllocatedSize : _allocatedSize;

            if (held > 0 && heldSize != byteCount)
            {
                return XmsgStatus.Failure(XmsgError.XEILM);
            }

            // ALL OR NOTHING: "if the function fails, due to lack of buffer space, no messages are
            // allocated." The ceiling comes from XFDMM; when it is zero there is none, so this can
            // only refuse against a limit a caller actually set. Checked BEFORE anything is written,
            // which is what makes the all-or-nothing rule true rather than merely intended.
            if (_maximumMessageSpace > 0)
            {
                long wanted = (long)byteCount * messageCount;
                long alreadyOwned = (long)_allocatedSize * _allocated
                    + (long)_exclusiveAllocatedSize * _exclusiveAllocated;

                if (alreadyOwned + wanted > _maximumMessageSpace)
                {
                    return XmsgStatus.Failure(XmsgError.XETMM);
                }
            }

            if (exclusive)
            {
                _exclusiveAllocated = held + messageCount;
                _exclusiveAllocatedSize = byteCount;
            }
            else
            {
                _allocated = held + messageCount;
                _allocatedSize = byteCount;
            }

            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public XmsgStatus FreeAllocatedBuffers(int messageCount, bool exclusive, out int freedCount)
        {
            freedCount = 0;

            if (messageCount < 0)
            {
                return XmsgStatus.Failure(XmsgError.XEILM);
            }

            int held = exclusive ? _exclusiveAllocated : _allocated;

            // Asking for more than are held is not an error - the count returned says how many
            // really went, which is what the A register carries back.
            freedCount = messageCount < held ? messageCount : held;

            if (exclusive)
            {
                _exclusiveAllocated = held - freedCount;
            }
            else
            {
                _allocated = held - freedCount;
            }

            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public XmsgStatus Write(
            XmsgMessageIdentifier message,
            ReadOnlySpan<byte> source,
            int displacement,
            bool resetLength,
            out int bytesWritten)
        {
            bytesWritten = 0;
            Buffer? buffer = ResolveBuffer(message, null);
            if (buffer == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIRM);
            }

            Touch(buffer);
            return buffer.Data.Write(source, displacement, resetLength, out bytesWritten);
        }

        /// <inheritdoc/>
        public XmsgStatus WriteHeader(XmsgMessageIdentifier message, ReadOnlySpan<byte> header)
        {
            Buffer? buffer = ResolveBuffer(message, null);
            if (buffer == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIRM);
            }

            Touch(buffer);
            return buffer.Data.WriteHeader(header);
        }

        /// <inheritdoc/>
        public XmsgStatus Read(
            XmsgMessageIdentifier message,
            Span<byte> destination,
            int displacement,
            out int bytesRead)
        {
            bytesRead = 0;
            Buffer? buffer = ResolveBuffer(message, null);
            if (buffer == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIRM);
            }

            Touch(buffer);
            return buffer.Data.Read(destination, displacement, out bytesRead);
        }

        /// <inheritdoc/>
        public XmsgStatus ReadHeader(XmsgMessageIdentifier message, Span<byte> header)
        {
            Buffer? buffer = ResolveBuffer(message, null);
            if (buffer == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIRM);
            }

            Touch(buffer);
            return buffer.Data.ReadHeader(header);
        }

        /// <inheritdoc/>
        public XmsgStatus Send(XmsgMagicNumber destination, XmsgPortNumber fromPort, XmsgSendFlags flags)
        {
            Port? source = Resolve(fromPort);
            if (source == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIPN);
            }

            // No message parameter: the port-current message if there is one, else task-current.
            Buffer? buffer = ResolveBuffer(XmsgMessageIdentifier.Current, source);
            if (buffer == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIRM);
            }

            return Deliver(buffer, destination, source, flags);
        }

        /// <inheritdoc/>
        public XmsgStatus ReturnMessage(
            XmsgMessageIdentifier message,
            ushort statusBytes,
            XmsgPortNumber fromPort,
            XmsgSendFlags flags)
        {
            Port? source = Resolve(fromPort);
            if (source == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIPN);
            }

            Buffer? buffer = ResolveBuffer(message, source);
            if (buffer == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIRM);
            }

            // XFRTN writes two status bytes over the start of the message, then sends it back to
            // the port it last came from (appendix A section 3.2.12).
            Span<byte> two = stackalloc byte[2];
            two[0] = (byte)(statusBytes >> 8);
            two[1] = (byte)(statusBytes & 0xFF);
            int written;
            buffer.Data.Write(two, 0, false, out written);

            if (buffer.Sender.IsNone)
            {
                return XmsgStatus.Failure(XmsgError.XEIMA);
            }

            return Deliver(buffer, buffer.Sender, source, flags);
        }

        /// <inheritdoc/>
        public XmsgReceiveResult Receive(XmsgPortNumber port, XmsgWaitOptions options)
        {
            Port? target;
            Buffer? buffer = Dequeue(port, options, out target);
            if (buffer == null)
            {
                return new XmsgReceiveResult(
                    target == null ? XmsgStatus.Failure(XmsgError.XEIPN) : XmsgStatus.NotTerminated,
                    XmsgHashedMagicNumber.None,
                    XmsgMessageIdentifier.None,
                    0);
            }

            int extra = buffer.Type == XmsgMessageType.XMTRE ? buffer.ReturnReason : buffer.Data.Length;
            return new XmsgReceiveResult(
                XmsgStatus.Success((int)buffer.Type),
                Hash(buffer.Sender),
                new XmsgMessageIdentifier(buffer.Id),
                extra);
        }

        /// <inheritdoc/>
        public XmsgReceiveResult ReceiveAndReadHeader(XmsgPortNumber port, XmsgWaitOptions options)
        {
            XmsgReceiveResult result = Receive(port, options);
            if (!result.Received)
            {
                return result;
            }

            // XFRRH replaces the length in the extra value with the first two user bytes. The
            // manual warns those bytes are undefined when the message is shorter than two, so a
            // short message is reported as zero rather than as invented data.
            Buffer buffer = _buffers[result.Message.Value];
            int firstTwo = 0;
            if (buffer.Data.Length >= 2)
            {
                ReadOnlySpan<byte> data = buffer.Data.Data;
                firstTwo = (data[0] << 8) | data[1];
            }

            return new XmsgReceiveResult(result.Status, result.RemotePort, result.Message, firstTwo);
        }

        /// <inheritdoc/>
        public XmsgReceiveResult ReceiveAndRead(
            XmsgPortNumber port,
            Span<byte> destination,
            XmsgWaitOptions options,
            out int bytesRead)
        {
            bytesRead = 0;
            XmsgReceiveResult result = Receive(port, options);
            if (!result.Received)
            {
                return result;
            }

            Buffer buffer = _buffers[result.Message.Value];
            buffer.Data.Read(destination, 0, out bytesRead);
            return result;
        }

        /// <inheritdoc/>
        public XmsgMessageStatus GetMessageStatus(XmsgMessageIdentifier message)
        {
            Buffer? buffer = ResolveBuffer(message, null);
            if (buffer == null)
            {
                return new XmsgMessageStatus(XmsgStatus.Failure(XmsgError.XEIRM), XmsgMagicNumber.None, 0);
            }

            Touch(buffer);
            return new XmsgMessageStatus(
                XmsgStatus.Success((int)buffer.Type), buffer.Sender, buffer.Data.Length);
        }

        /// <inheritdoc/>
        public XmsgStatus SetCurrentMessage(XmsgMessageIdentifier message, XmsgPortNumber port)
        {
            Buffer? buffer = ResolveBuffer(message, null);
            if (buffer == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIRM);
            }

            _taskCurrent = new XmsgMessageIdentifier(buffer.Id);

            if (!port.IsAll)
            {
                Port? target = Resolve(port);
                if (target != null)
                {
                    target.Current = buffer.Id;
                }
            }

            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public XmsgStatus Dummy()
        {
            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public XmsgStatus ConvertMagicToPort(XmsgMagicNumber magic, out XmsgPortNumber port, out int system)
        {
            port = new XmsgPortNumber(magic.PortNumber);
            system = magic.SystemNumber;

            if (magic.IsNone)
            {
                return XmsgStatus.Failure(XmsgError.XEIMA);
            }

            // The status classifies the magic number: 2 for a local port owned by a privileged
            // task, 1 for a remote port or an unprivileged local one (appendix A section 4.4).
            bool local = magic.SystemNumber == _systemNumber;
            return XmsgStatus.Success(local && _privileged ? 2 : 1);
        }

        /// <inheritdoc/>
        public XmsgStatus ConvertPortToMagic(XmsgPortNumber port, out XmsgMagicNumber magic)
        {
            magic = XmsgMagicNumber.None;

            Port? target = Resolve(port);
            if (target == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIPN);
            }

            magic = target.Magic;
            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public XmsgStatus MakePrivileged(ushort password)
        {
            _privileged = true;
            return XmsgStatus.Completed;
        }

        /// <summary>
        /// Delivers a message that arrived from outside, as though the transport had received it.
        /// </summary>
        /// <param name="destination">
        /// The magic number the message was addressed to; it must name a port this kernel owns.
        /// </param>
        /// <param name="sender">
        /// The magic number of the sending port, which the receiver will read with XFMST.
        /// </param>
        /// <param name="userData">
        /// The message's user data.
        /// </param>
        /// <param name="flags">
        /// The send options the sender used.
        /// </param>
        /// <returns>
        /// A success status when the message was queued, or a failure when no such port is open.
        /// </returns>
        /// <remarks>
        /// This is the inbound counterpart of <see cref="IXmsgDatagramSink"/>: the node layer calls
        /// it when a datagram arrives for one of this task's ports.
        /// </remarks>
        public XmsgStatus Deliver(
            XmsgMagicNumber destination,
            XmsgMagicNumber sender,
            ReadOnlySpan<byte> userData,
            XmsgSendFlags flags)
        {
            Port? target = FindByMagic(destination);
            if (target == null)
            {
                return XmsgStatus.Failure(XmsgError.XEIMA);
            }

            Buffer buffer = new Buffer(_nextMessageId++, userData.Length);
            int written;
            buffer.Data.Write(userData, 0, false, out written);
            buffer.Sender = sender;
            buffer.Secure = (flags & XmsgSendFlags.Secure) != 0;

            // XFFWD means XROUT passed this on without putting itself in the sender field, so what
            // arrives is a ROUTED message and the receiver is entitled to know it. That matters
            // beyond bookkeeping: XROUT spends one of a connection port's free seats to forward a
            // letter, and XMROU is the only thing telling the receiver a seat was spent - the body
            // could say anything, or be undecodable, and the seat is gone just the same.
            //
            // SETTLED, and not by us. Both sample servers in the COSMOS Programmer Guide
            // (ND-60.164.3) wait for the forwarded letter and then refuse anything that is not
            // XMROU - "IF MSGTYPE >< XMROU THEN 'WRONG MESSAGE TYPE'" in PLANC, and the same test
            // in FORTRAN. Each then reads the CLIENT's magic out of the arrived message to answer
            // it directly, which is the other half of what XFFWD means: the sender stays the
            // original client, and the TYPE is what says XROUT carried it.
            buffer.Type = ChooseType(flags);

            _buffers.Add(buffer.Id, buffer);
            Enqueue(target, buffer);
            return XmsgStatus.Completed;
        }

        /// <summary>
        /// Works out the type an arriving message is announced with.
        /// </summary>
        /// <param name="flags">
        /// The send options the sender used.
        /// </param>
        /// <returns>
        /// The message type the receiver reads back from XFRCV.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Shared by both delivery paths so they cannot drift apart - one path is a local send and
        /// the other is a datagram arriving from the node layer, but the receiver is not supposed
        /// to be able to tell those apart, and it could if only one of them named a routed message.
        /// </para>
        /// <para>
        /// <b>Order matters.</b> Forward is tested first because XFHIP and XFRRO are the SAME BIT,
        /// and a letter forwarded with remote-route set would otherwise read as high priority.
        /// </para>
        /// </remarks>
        private static XmsgMessageType ChooseType(XmsgSendFlags flags)
        {
            if ((flags & XmsgSendFlags.Forward) != 0)
            {
                return XmsgMessageType.XMROU;
            }

            // Bit 13 is HighPriority only while Route is clear - the manual's "acts as" rule.
            if ((flags & XmsgSendFlags.HighPriority) != 0 && (flags & XmsgSendFlags.Route) == 0)
            {
                return XmsgMessageType.XMTHI;
            }

            return XmsgMessageType.XMTNO;
        }

        private XmsgStatus Deliver(Buffer buffer, XmsgMagicNumber destination, Port source, XmsgSendFlags flags)
        {
            buffer.Sender = (flags & XmsgSendFlags.Forward) != 0 ? buffer.Sender : source.Magic;
            buffer.Secure = (flags & XmsgSendFlags.Secure) != 0;
            buffer.Type = ChooseType(flags);

            // Sending loses the buffer's currency either way (section 1.2.4).
            ForgetCurrency(buffer.Id);

            Port? local = FindByMagic(destination);
            if (local != null)
            {
                // Same system: ownership of the buffer transfers, nothing is copied.
                Enqueue(local, buffer);
                return XmsgStatus.Completed;
            }

            if (destination.SystemNumber == _systemNumber)
            {
                // A local system number with no such port open. A secure message comes back; a
                // non-secure one is silently discarded and its buffer released.
                return Undeliverable(buffer, source, XmsgError.XEIMA);
            }

            if (_sink == null)
            {
                return Undeliverable(buffer, source, XmsgError.XENOS);
            }

            XmsgStatus sent = _sink.Send(destination, buffer.Sender, buffer.Data.Data, flags);
            if (sent.IsError)
            {
                return Undeliverable(buffer, source, sent.Error ?? XmsgError.XENOS);
            }

            _buffers.Remove(buffer.Id);
            return XmsgStatus.Completed;
        }

        private XmsgStatus Undeliverable(Buffer buffer, Port source, XmsgError reason)
        {
            if (!buffer.Secure)
            {
                _buffers.Remove(buffer.Id);
                return XmsgStatus.Failure(reason);
            }

            buffer.Type = XmsgMessageType.XMTRE;
            buffer.ReturnReason = (int)reason;
            buffer.Secure = false;
            Enqueue(source, buffer);
            return XmsgStatus.Completed;
        }

        private void Enqueue(Port port, Buffer buffer)
        {
            if (buffer.Type == XmsgMessageType.XMTHI)
            {
                // High priority goes to the head, behind any high-priority messages already there.
                int insert = 0;
                while (insert < port.Queue.Count && port.Queue[insert].Type == XmsgMessageType.XMTHI)
                {
                    insert++;
                }

                port.Queue.Insert(insert, buffer);
                return;
            }

            port.Queue.Add(buffer);
        }

        private Buffer? Peek(Port port, XmsgWaitOptions options)
        {
            bool highOnly = (options & XmsgWaitOptions.HighPriority) != 0;
            for (int i = 0; i < port.Queue.Count; i++)
            {
                if (!highOnly || port.Queue[i].Type == XmsgMessageType.XMTHI)
                {
                    return port.Queue[i];
                }
            }

            return null;
        }

        private Buffer? Dequeue(XmsgPortNumber port, XmsgWaitOptions options, out Port? target)
        {
            target = Resolve(port);
            if (target == null)
            {
                return null;
            }

            Buffer? head = Peek(target, options);
            if (head == null)
            {
                return null;
            }

            target.Queue.Remove(head);
            _taskCurrent = new XmsgMessageIdentifier(head.Id);

            // A secure message additionally becomes port-current, which is what lets the kernel
            // return it to the sender if this port closes before it is handled.
            if (head.Secure)
            {
                target.Current = head.Id;
            }

            return head;
        }

        private void CloseOne(Port port)
        {
            for (int i = 0; i < port.Queue.Count; i++)
            {
                Buffer queued = port.Queue[i];
                if (queued.Secure)
                {
                    ReturnToSender(queued);
                }
                else
                {
                    _buffers.Remove(queued.Id);
                    ForgetCurrency(queued.Id);
                }
            }

            port.Queue.Clear();

            if (port.Current != 0 && _buffers.TryGetValue(port.Current, out Buffer? current))
            {
                ReturnToSender(current);
            }

            _portList.Remove(port);
        }

        private void ReturnToSender(Buffer buffer)
        {
            // Closing a port marks its secure messages non-secure and returns them (section 3.1.2).
            buffer.Secure = false;
            buffer.Type = XmsgMessageType.XMTRE;
            buffer.ReturnReason = (int)XmsgError.XEPCL;

            Port? home = FindByMagic(buffer.Sender);
            if (home != null)
            {
                Enqueue(home, buffer);
                return;
            }

            _buffers.Remove(buffer.Id);
            ForgetCurrency(buffer.Id);
        }

        private Port? Resolve(XmsgPortNumber port)
        {
            if (_portList.Count == 0)
            {
                return null;
            }

            if (port.IsDefault)
            {
                return _portList[0];
            }

            for (int i = 0; i < _portList.Count; i++)
            {
                if (_portList[i].Number == port.Value)
                {
                    return _portList[i];
                }
            }

            return null;
        }

        private Port? FindByMagic(XmsgMagicNumber magic)
        {
            for (int i = 0; i < _portList.Count; i++)
            {
                if (_portList[i].Magic == magic)
                {
                    return _portList[i];
                }
            }

            return null;
        }

        private Buffer? ResolveBuffer(XmsgMessageIdentifier message, Port? port)
        {
            int id = message.Value;

            if (message.IsCurrent)
            {
                // Port-current if one exists, otherwise task-current (section 1.2.4).
                id = port != null && port.Current != 0 ? port.Current : _taskCurrent.Value;
            }

            if (id <= 0)
            {
                return null;
            }

            return _buffers.TryGetValue(id, out Buffer? found) ? found : null;
        }

        private void Touch(Buffer buffer)
        {
            _taskCurrent = new XmsgMessageIdentifier(buffer.Id);
        }

        private void ForgetCurrency(int id)
        {
            if (_taskCurrent.Value == id)
            {
                _taskCurrent = XmsgMessageIdentifier.None;
            }

            for (int i = 0; i < _portList.Count; i++)
            {
                if (_portList[i].Current == id)
                {
                    _portList[i].Current = 0;
                }
            }
        }

        private static XmsgHashedMagicNumber Hash(XmsgMagicNumber magic)
        {
            // The manual never publishes the hashing function, only that the result is "almost
            // unique" and fit for a quick did-this-come-from-a-known-partner check. Using the
            // magic's low word satisfies that and is an implementation choice of THIS kernel, not
            // a claim about what SINTRAN computes.
            return new XmsgHashedMagicNumber(magic.LowWord);
        }

        private sealed class Port
        {
            internal Port(int number, ushort portWord, ushort system)
            {
                Number = number;
                Magic = XmsgMagicNumber.FromRegisterPair(system, portWord);
                Queue = new List<Buffer>();
            }

            internal int Number { get; }

            internal XmsgMagicNumber Magic { get; }

            internal List<Buffer> Queue { get; }

            internal int Current { get; set; }
        }

        private sealed class Buffer
        {
            internal Buffer(int id, int size)
            {
                Id = id;
                Data = new XmsgMessageBuffer(size);
                Type = XmsgMessageType.XMTNO;
            }

            internal int Id { get; }

            internal XmsgMessageBuffer Data { get; }

            internal XmsgMagicNumber Sender { get; set; }

            internal bool Secure { get; set; }

            internal XmsgMessageType Type { get; set; }

            internal int ReturnReason { get; set; }
        }
    }
}
