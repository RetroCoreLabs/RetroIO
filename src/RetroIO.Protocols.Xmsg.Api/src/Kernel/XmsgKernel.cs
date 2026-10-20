//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

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

        // How many buffers of each pool are out as live messages right now. The rest of the pool
        // is the task's AAML, the "Available Allocated Message List" of section 3.2.4: the
        // buffers a reservation of the pool's size takes first.
        private int _sharedInUse;
        private int _exclusiveInUse;

        // The bytes held by buffers that are new space, that is, not taken from either pool.
        private long _looseBytes;

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

        // The port the local XROUT task receives on (see AttachRouter), or null when none is
        // attached.
        private Port? _router;

        // One lock for the whole kernel. The node layer delivers arriving datagrams from its own
        // thread while the task calls in from another, and a blocking wait polls the queues for a
        // long time, so every public call takes this lock. It is reentrant, so a public call may
        // use another one.
        private readonly object _gate = new object();

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
            lock (_gate)
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
        /// Gets or sets the kind of program that owns the task this kernel serves.
        /// </summary>
        /// <remarks>
        /// <see cref="MakePrivileged"/> reads it: the guide lets only some kinds of program become
        /// privileged. The default is <see cref="XmsgTaskKind.Unspecified"/>, which is refused,
        /// so a kernel never grants privilege to a caller that did not say what it is.
        /// </remarks>
        public XmsgTaskKind TaskKind { get; set; }

        /// <summary>
        /// Gets or sets the XMSG password (the version code, XPASW) of the system this kernel
        /// stands for.
        /// </summary>
        /// <remarks>
        /// The guide says the password is "returned by the dummy function (XFDUM)" and is the
        /// version code of the XMSG tables; it does not give the value, which belongs to the XMSG
        /// version being stood for. Zero means none has been set, and then no privilege request
        /// can succeed, because zero is the value that gives privilege up.
        /// </remarks>
        public ushort VersionCode { get; set; }

        /// <summary>
        /// Gets the number of ports the task currently has open.
        /// </summary>
        public int OpenPortCount
        {
            get { lock (_gate) { return _portList.Count; } }
        }

        /// <summary>
        /// Gets the number of message buffers the task currently owns.
        /// </summary>
        public int OwnedBufferCount
        {
            get { lock (_gate) { return _buffers.Count; } }
        }

        /// <summary>
        /// Opens a port and returns the identifier this kernel assigned to it (XFOPN).
        /// </summary>
        /// <param name="port">
        /// On return, the new port's identifier. The new port is put at the top of the port list,
        /// which makes it the task's default port.
        /// </param>
        /// <returns>
        /// Always the completed status.
        /// </returns>
        /// <remarks>
        /// Port numbers count up from 1 for the life of the kernel. The port's magic number is
        /// built from this system's number and a port word minted from the port number.
        /// </remarks>
        public XmsgStatus OpenPort(out XmsgPortNumber port)
        {
            lock (_gate)
            {
                Port created = new Port(_nextPortNumber, _ports.Next(_nextPortNumber), _systemNumber);
                _nextPortNumber++;

                // XMSG inserts a newly opened port at the TOP of the list, which is what makes it the
                // task's default port (appendix A section 3.1).
                _portList.Insert(0, created);

                port = new XmsgPortNumber(created.Number);
                return XmsgStatus.Completed;
            }
        }

        /// <summary>
        /// Closes a port, or every port owned by the task (XFCLS).
        /// </summary>
        /// <param name="port">
        /// The port to close; <see cref="XmsgPortNumber.Default"/> closes the most recently opened
        /// port and <see cref="XmsgPortNumber.All"/> (any negative number) closes them all.
        /// </param>
        /// <returns>
        /// The completed status, or XEIPN when the named port is not open.
        /// </returns>
        /// <remarks>
        /// Closing a port releases its queued non-secure messages. Its queued secure messages and
        /// its port-current message are marked non-secure and returned to the sending port as
        /// returned messages (reason XEPCL) when that port is open in this kernel; otherwise they
        /// are released. This kernel keeps no XROUT name table, so no name is cleared.
        /// </remarks>
        public XmsgStatus ClosePort(XmsgPortNumber port)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Reports what is waiting on one port (XFPST).
        /// </summary>
        /// <param name="port">
        /// The port to check; the default port when unspecified.
        /// </param>
        /// <param name="options">
        /// The options to apply. Only <see cref="XmsgWaitOptions.HighPriority"/> has an effect
        /// here: it makes the check look at high-priority messages only.
        /// </param>
        /// <returns>
        /// The queue length plus, when a message matches, its type, identifier and sender hash. The
        /// status is XEIPN when the port is not open, and not-terminated when no message matches;
        /// the queue length is still reported in that case.
        /// </returns>
        /// <remarks>
        /// The message is not taken off the queue. Waiting and wake-up bits are not modelled by
        /// this kernel: a wait is treated as a poll and wake-up state is not touched.
        /// </remarks>
        public XmsgPortStatus GetPortStatus(XmsgPortNumber port, XmsgWaitOptions options)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Scans all of the task's ports for a waiting message (XFGST).
        /// </summary>
        /// <param name="lastPort">
        /// The LAST port to be scanned. The search starts at the port after this one in the task's
        /// port list and wraps; when it is not an open port, the search starts at the top of the
        /// list.
        /// </param>
        /// <param name="options">
        /// The options to apply. Only <see cref="XmsgWaitOptions.HighPriority"/> has an effect
        /// here: it makes the scan look for high-priority messages only.
        /// </param>
        /// <param name="port">
        /// On return, the port a message is waiting on, or <see cref="XmsgPortNumber.Default"/>
        /// when none was found.
        /// </param>
        /// <returns>
        /// The completed status when a port has a matching message; not-terminated when no port
        /// has one or no port is open.
        /// </returns>
        /// <remarks>
        /// Waiting is not modelled by this kernel: a wait is treated as a poll.
        /// </remarks>
        public XmsgStatus GetGeneralStatus(XmsgPortNumber lastPort, XmsgWaitOptions options, out XmsgPortNumber port)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Releases every XMSG resource held by the task (XFDCT).
        /// </summary>
        /// <returns>
        /// Always the completed status.
        /// </returns>
        /// <remarks>
        /// Closes all the task's ports (with the same return-to-sender handling as
        /// <see cref="ClosePort"/>), drops every message buffer, empties both allocation pools and
        /// clears the task-current message.
        /// </remarks>
        public XmsgStatus Disconnect()
        {
            lock (_gate)
            {
                ClosePort(XmsgPortNumber.All);
                _buffers.Clear();
                _sharedInUse = 0;
                _exclusiveInUse = 0;
                _looseBytes = 0;

                // XFDCT "releases every XMSG resource the task holds" (Programmer Guide 3.1.2), and
                // allocated buffers are a resource the task holds - so both pools go with it.
                _allocated = 0;
                _allocatedSize = 0;
                _exclusiveAllocated = 0;
                _exclusiveAllocatedSize = 0;

                _taskCurrent = XmsgMessageIdentifier.None;
                return XmsgStatus.Completed;
            }
        }

        /// <summary>
        /// Reserves a message buffer (XFGET).
        /// </summary>
        /// <param name="byteCount">
        /// The requested capacity in bytes. Zero reserves an empty buffer.
        /// </param>
        /// <param name="options">
        /// The reserve options. <see cref="XmsgBufferOptions.Exclusive"/> takes one of the buffers
        /// set aside with <see cref="AllocateBuffers"/> for exclusive use.
        /// <see cref="XmsgBufferOptions.Wait"/> is accepted but not acted on, because this kernel
        /// has no scheduler to suspend the task on; the call fails at once, as it would without it.
        /// </param>
        /// <param name="message">
        /// On return, the identifier of the reserved buffer, or
        /// <see cref="XmsgMessageIdentifier.None"/> on failure.
        /// </param>
        /// <returns>
        /// The completed status, or one of these failures:
        ///  - XEILM when <paramref name="byteCount"/> is negative, or when the exclusive option is
        ///    given and the exclusive pool holds no free buffer of that size.
        ///  - XETMM when the buffer would take the space the task owns over the ceiling set with
        ///    <see cref="DefineMaximumMemory"/>.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Guide appendix A section 3.2.1: "At any particular time, the total space owned by a
        /// task cannot exceed another limit ... changed by privileged tasks using the Define
        /// Maximum Memory function"; section 3.2.4: "When an XFGET is executed by that task for a
        /// buffer of that size, XMSG will first look at the task's AAML and take a message buffer
        /// from it, if one is available"; and "to reserve one of these exclusive buffers, the task
        /// must execute the XFGET function with the option XFEXC set". The error text of XETMM is
        /// "the calling task is not allowed to reserve/allocate the message buffer(s) of the
        /// requested size"; that of XEILM covers XFEXC with no exclusive buffer of the requested
        /// size available.
        /// </para>
        /// <para>
        /// A buffer taken from a pool is space the task already owns, so it is not counted twice
        /// against the ceiling. Any other buffer is new space. The space owned is the two pools
        /// plus the buffers that did not come from a pool; this kernel serves one task, so every
        /// buffer it holds counts. The new buffer becomes task-current. The largest buffer size,
        /// which the guide leaves to system generation, is not known here and is not checked.
        /// </para>
        /// </remarks>
        public XmsgStatus ReserveBuffer(int byteCount, XmsgBufferOptions options, out XmsgMessageIdentifier message)
        {
            lock (_gate)
            {
                message = XmsgMessageIdentifier.None;

                if (byteCount < 0)
                {
                    return XmsgStatus.Failure(XmsgError.XEILM);
                }

                BufferPool pool = BufferPool.None;

                if ((options & XmsgBufferOptions.Exclusive) != 0)
                {
                    // Only a free exclusive buffer of exactly this size will do.
                    bool free = _exclusiveAllocated - _exclusiveInUse > 0;
                    if (!free || _exclusiveAllocatedSize != byteCount)
                    {
                        return XmsgStatus.Failure(XmsgError.XEILM);
                    }

                    pool = BufferPool.Exclusive;
                }
                else if (_allocated - _sharedInUse > 0 && _allocatedSize == byteCount)
                {
                    pool = BufferPool.Shared;
                }

                if (pool == BufferPool.None && _maximumMessageSpace > 0)
                {
                    if (OwnedSpace() + byteCount > _maximumMessageSpace)
                    {
                        return XmsgStatus.Failure(XmsgError.XETMM);
                    }
                }

                Buffer buffer = new Buffer(_nextMessageId++, byteCount);
                buffer.Pool = pool;
                if (pool == BufferPool.Exclusive)
                {
                    _exclusiveInUse++;
                }
                else if (pool == BufferPool.Shared)
                {
                    _sharedInUse++;
                }

                AddBuffer(buffer);

                // Referring to a buffer makes it task-current (section 1.2.4).
                message = new XmsgMessageIdentifier(buffer.Id);
                _taskCurrent = message;
                return XmsgStatus.Completed;
            }
        }

        /// <summary>
        /// Releases a message buffer (XFREL).
        /// </summary>
        /// <param name="message">
        /// The buffer to release, or <see cref="XmsgMessageIdentifier.Current"/> for the
        /// task-current message.
        /// </param>
        /// <returns>
        /// The completed status, or XEIRM when the buffer is not owned by the task.
        /// </returns>
        /// <remarks>
        /// The buffer also stops being task-current or port-current wherever it was.
        /// </remarks>
        public XmsgStatus ReleaseBuffer(XmsgMessageIdentifier message)
        {
            lock (_gate)
            {
                Buffer? buffer = ResolveBuffer(message, null);
                if (buffer == null)
                {
                    return XmsgStatus.Failure(XmsgError.XEIRM);
                }

                DropBuffer(buffer);
                ForgetCurrency(buffer.Id);
                return XmsgStatus.Completed;
            }
        }

        /// <summary>
        /// Walks the task's own open ports and message buffers (XFLMP).
        /// </summary>
        /// <param name="fromMessageId">
        /// Start the message search at this identifier; zero starts from the beginning.
        /// </param>
        /// <param name="fromPortNumber">
        /// Start the port search at this number; zero starts from the beginning.
        /// </param>
        /// <returns>
        /// The completed status with the lowest message identifier at or above
        /// <paramref name="fromMessageId"/> and its reserved size, and the lowest open port number
        /// at or above <paramref name="fromPortNumber"/>. Each is zero when there is none.
        /// </returns>
        /// <remarks>
        /// The two searches are independent. The size reported is what the buffer was reserved
        /// with, not how many bytes have been written into it.
        /// </remarks>
        public XmsgListing ListMessagesAndPorts(int fromMessageId, int fromPortNumber)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Takes a snapshot of which of this task's ports have a message waiting - <c>XFGSM</c>.
        /// </summary>
        /// <returns>
        /// Three 16-bit masks over the task's ports, bit 0 being the port opened most recently:
        ///  - ports with at least one queued message.
        ///  - ports whose first queued message is a routed message (XMROU).
        ///  - ports whose first queued message is a returned message (XMTRE).
        /// </returns>
        /// <remarks>
        /// Only the first 16 ports in the list are covered. Only the first queued message on each
        /// port decides its type bits; any other type leaves both type bits clear.
        /// </remarks>
        public XmsgQueueSnapshot GetGeneralStatusMultiple()
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Sets the maximum message space this task may own at one time (XFDMM).
        /// </summary>
        /// <param name="byteCount">
        /// The ceiling in bytes. Zero removes the ceiling.
        /// </param>
        /// <returns>
        /// The completed status; XEILM when <paramref name="byteCount"/> is negative, and XEPRV
        /// when the task has not been made privileged with <see cref="MakePrivileged"/>.
        /// </returns>
        /// <remarks>
        /// The kernel starts with no ceiling. The ceiling is checked only by
        /// <see cref="AllocateBuffers"/>.
        /// </remarks>
        public XmsgStatus DefineMaximumMemory(int byteCount)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Records a remote system as a friend, so its tasks count as coming from a privileged
        /// system.
        /// </summary>
        /// <param name="systemNumber">
        /// The remote system's number.
        /// </param>
        /// <param name="isFriend">
        /// <c>true</c> to define the friendship, <c>false</c> to remove it.
        /// </param>
        /// <remarks>
        /// This is the state the XROUT service XSDAT maintains, kept here so
        /// <see cref="CheckSenderPrivileges"/> has something to consult. It is not itself an XMSG
        /// function.
        /// </remarks>
        public void DefineFriendSystem(ushort systemNumber, bool isFriend)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Checks whether the sender of a received message may update this system's routing tables
        /// (XFCPV).
        /// </summary>
        /// <param name="message">
        /// The message whose sender is being judged, or <see cref="XmsgMessageIdentifier.Current"/>.
        /// </param>
        /// <returns>
        /// XEIRM when the message is not owned by the task. Otherwise the completed status with
        /// the verdict and the detail behind it:
        ///  - a returned message (XMTRE) is never authorised and reports a returned message.
        ///  - when authorised, the detail says where the sender is: a local sender reports neither,
        ///    a friend system reports system only.
        ///  - when refused, the detail says which privilege was present.
        /// </returns>
        /// <remarks>
        /// Naming a message other than <see cref="XmsgMessageIdentifier.Current"/> makes it the
        /// task-current message. A sender inside this system, or one with no sender, counts as a
        /// privileged system; a remote system counts only after <see cref="DefineFriendSystem"/>.
        /// The kernel models one task, so a local sender's task privilege is this kernel's own
        /// privilege flag, and a remote sender's task is always taken as not privileged.
        /// </remarks>
        public XmsgPrivilegeCheck CheckSenderPrivileges(XmsgMessageIdentifier message)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Allocates message buffers to this task for the long term (XFALM).
        /// </summary>
        /// <param name="byteCount">
        /// The size of each buffer.
        /// </param>
        /// <param name="messageCount">
        /// How many buffers to allocate.
        /// </param>
        /// <param name="exclusive">
        /// <c>true</c> to add to the exclusive pool, <c>false</c> for the non-exclusive pool.
        /// </param>
        /// <returns>
        /// The completed status, or one of these failures:
        ///  - XEILM when either count is negative, or when the pool already holds buffers of a
        ///    different size.
        ///  - XETMM when a ceiling is set with <see cref="DefineMaximumMemory"/> and the total
        ///    held by both pools plus this request would go over it.
        /// </returns>
        /// <remarks>
        /// The pools are counters only; no buffer space is set aside. The call is all or nothing:
        /// on a failure neither pool changes. Each pool keeps its own buffer size.
        /// </remarks>
        public XmsgStatus AllocateBuffers(int byteCount, int messageCount, bool exclusive)
        {
            lock (_gate)
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
                    long alreadyOwned = OwnedSpace();

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
        }

        /// <summary>
        /// Frees buffers previously allocated with <see cref="AllocateBuffers"/> (XFFRM).
        /// </summary>
        /// <param name="messageCount">
        /// How many to free.
        /// </param>
        /// <param name="exclusive">
        /// <c>true</c> to free exclusive buffers, <c>false</c> to free non-exclusive ones.
        /// </param>
        /// <param name="freedCount">
        /// On return, how many were actually freed: the smaller of
        /// <paramref name="messageCount"/> and the number held in that pool.
        /// </param>
        /// <returns>
        /// The completed status, or XEILM when <paramref name="messageCount"/> is negative.
        /// </returns>
        /// <remarks>
        /// Asking to free more than are held is not an error.
        /// </remarks>
        public XmsgStatus FreeAllocatedBuffers(int messageCount, bool exclusive, out int freedCount)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Copies user data into a message buffer (XFWRI).
        /// </summary>
        /// <param name="message">
        /// The target buffer, or <see cref="XmsgMessageIdentifier.Current"/> for the task-current
        /// message.
        /// </param>
        /// <param name="source">
        /// The user data to copy in.
        /// </param>
        /// <param name="displacement">
        /// The displacement in bytes, or -1 to append at the buffer's current displacement.
        /// </param>
        /// <param name="resetLength">
        /// The XFRES option: reset the message length to zero before writing.
        /// </param>
        /// <param name="bytesWritten">
        /// On return, the number of bytes actually written.
        /// </param>
        /// <returns>
        /// XEIRM when the buffer is not owned by the task; otherwise the status of the write into
        /// the buffer.
        /// </returns>
        /// <remarks>
        /// The buffer becomes task-current.
        /// </remarks>
        public XmsgStatus Write(
            XmsgMessageIdentifier message,
            ReadOnlySpan<byte> source,
            int displacement,
            bool resetLength,
            out int bytesWritten)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Writes the six-byte user header of a message buffer (XFWHD).
        /// </summary>
        /// <param name="message">
        /// The target buffer, or <see cref="XmsgMessageIdentifier.Current"/> for the task-current
        /// message.
        /// </param>
        /// <param name="header">
        /// The six header bytes, corresponding to the A, D and X registers in that order.
        /// </param>
        /// <returns>
        /// XEIRM when the buffer is not owned by the task; otherwise the status of the header
        /// write into the buffer.
        /// </returns>
        /// <remarks>
        /// The buffer becomes task-current.
        /// </remarks>
        public XmsgStatus WriteHeader(XmsgMessageIdentifier message, ReadOnlySpan<byte> header)
        {
            lock (_gate)
            {
                Buffer? buffer = ResolveBuffer(message, null);
                if (buffer == null)
                {
                    return XmsgStatus.Failure(XmsgError.XEIRM);
                }

                Touch(buffer);
                return buffer.Data.WriteHeader(header);
            }
        }

        /// <summary>
        /// Copies user data out of a message buffer (XFREA).
        /// </summary>
        /// <param name="message">
        /// The source buffer, or <see cref="XmsgMessageIdentifier.Current"/> for the task-current
        /// message.
        /// </param>
        /// <param name="destination">
        /// The caller's buffer; its length is the requested byte count.
        /// </param>
        /// <param name="displacement">
        /// The displacement in bytes, or -1 to resume from the buffer's current displacement.
        /// </param>
        /// <param name="bytesRead">
        /// On return, the number of bytes actually read.
        /// </param>
        /// <returns>
        /// XEIRM when the buffer is not owned by the task; otherwise the status of the read from
        /// the buffer.
        /// </returns>
        /// <remarks>
        /// The buffer becomes task-current.
        /// </remarks>
        public XmsgStatus Read(
            XmsgMessageIdentifier message,
            Span<byte> destination,
            int displacement,
            out int bytesRead)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Reads the six-byte user header of a message buffer (XFRHD).
        /// </summary>
        /// <param name="message">
        /// The source buffer, or <see cref="XmsgMessageIdentifier.Current"/> for the task-current
        /// message.
        /// </param>
        /// <param name="header">
        /// The destination receiving the A, D and X register contents in that order.
        /// </param>
        /// <returns>
        /// XEIRM when the buffer is not owned by the task; otherwise the status of the header read
        /// from the buffer.
        /// </returns>
        /// <remarks>
        /// The buffer becomes task-current.
        /// </remarks>
        public XmsgStatus ReadHeader(XmsgMessageIdentifier message, Span<byte> header)
        {
            lock (_gate)
            {
                Buffer? buffer = ResolveBuffer(message, null);
                if (buffer == null)
                {
                    return XmsgStatus.Failure(XmsgError.XEIRM);
                }

                Touch(buffer);
                return buffer.Data.ReadHeader(header);
            }
        }

        /// <summary>
        /// Sends the current message buffer to a port (XFSND).
        /// </summary>
        /// <param name="destination">
        /// The magic number of the receiving port. It is ignored when
        /// <see cref="XmsgSendFlags.Route"/> is set, except that with the remote-route bit also
        /// set its high word names the system whose XROUT gets the message.
        /// </param>
        /// <param name="fromPort">
        /// The sending port; the default port when unspecified.
        /// </param>
        /// <param name="flags">
        /// The send options. Forward keeps the original sender and marks the message routed;
        /// HighPriority (with Route clear) queues it ahead of normal messages; Secure makes an
        /// undeliverable message come back instead of being dropped.
        /// </param>
        /// <returns>
        /// The completed status; XEIPN when the sending port is not open; XEIRM when there is no
        /// current message; or, for an undeliverable non-secure message, the reason it could not
        /// be delivered.
        /// </returns>
        /// <remarks>
        /// There is no message parameter: the port-current message is sent if one exists, otherwise
        /// the task-current message. The message loses its currency. A destination port in this
        /// kernel gets the buffer itself, with nothing copied. A different system number goes to
        /// the attached <see cref="IXmsgDatagramSink"/>. An undeliverable secure message is queued
        /// back on the sending port as a returned message (XMTRE) and the call completes; a
        /// non-secure one is released.
        /// <para>
        /// With <see cref="XmsgSendFlags.Route"/> set, the guide (appendix A section 3.2.11 and the
        /// XMPFSND page) says: "Route message. Ignore the MAGNO parameter and send the message to
        /// the local routing task (XROUT)"; and "Remote route message. If the XFROU bit is also
        /// set, then the message is sent to a remote routing task (XROUT)", taking the system
        /// number from the high word of the magic number. XFHIP is the same bit as XFRRO, so with
        /// Route set it means remote route. The captures show XROUT at port 0 of its system
        /// (DOC/XMSG-PROTOCOL.md section 7.1; DOC/COSMOS-RE/ENNS0-Startup-RE-2026-07-23
        /// ENNS0-XROUT-UNKNOWN-NAME-ROOTCAUSE: "Receiving port: 0x00000000"), so the destination
        /// becomes port 0 of this system, or of the named system. Port 0 of this system reaches
        /// the port given to <see cref="AttachRouter"/>; with none attached the message is
        /// undeliverable (XEIMA).
        /// </para>
        /// </remarks>
        public XmsgStatus Send(XmsgMagicNumber destination, XmsgPortNumber fromPort, XmsgSendFlags flags)
        {
            lock (_gate)
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

                if ((flags & XmsgSendFlags.Route) != 0)
                {
                    // XFRRO and XFHIP are the same bit; with XFROU set it is the remote-route bit.
                    bool remote = (flags & XmsgSendFlags.RemoteRoute) != 0;
                    ushort routerSystem = remote ? destination.SystemNumber : _systemNumber;
                    destination = XmsgMagicNumber.Create(routerSystem, 0, 0);
                }

                return Deliver(buffer, destination, source, flags);
            }
        }

        /// <summary>
        /// Writes two status bytes into a message and returns it to its sender (XFRTN).
        /// </summary>
        /// <param name="message">
        /// The buffer to return, or <see cref="XmsgMessageIdentifier.Current"/> for the
        /// port-current message of <paramref name="fromPort"/>, or the task-current one when that
        /// port has none.
        /// </param>
        /// <param name="statusBytes">
        /// The two bytes written at the start of the user data, high byte first.
        /// </param>
        /// <param name="fromPort">
        /// The sending port; the default port when unspecified.
        /// </param>
        /// <param name="flags">
        /// The send options, applied as in <see cref="Send"/>.
        /// </param>
        /// <returns>
        /// The completed status; XEIPN when the sending port is not open; XEIRM when the buffer is
        /// not owned by the task; XEIMA when the message has no sender; or the delivery result as
        /// for <see cref="Send"/>.
        /// </returns>
        /// <remarks>
        /// The destination is the message's recorded sender. The two status bytes are written
        /// before the sender is checked, so they are in the buffer even when XEIMA is returned.
        /// </remarks>
        public XmsgStatus ReturnMessage(
            XmsgMessageIdentifier message,
            ushort statusBytes,
            XmsgPortNumber fromPort,
            XmsgSendFlags flags)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Receives the next message queued on a port (XFRCV).
        /// </summary>
        /// <param name="port">
        /// The receiving port; the default port when unspecified.
        /// </param>
        /// <param name="options">
        /// The options to apply. Only <see cref="XmsgWaitOptions.HighPriority"/> has an effect
        /// here: it receives the first high-priority message only.
        /// </param>
        /// <returns>
        /// The message type, sender hash, identifier and an extra value: the message length, or
        /// for a returned message (XMTRE) the reason it came back. The status is XEIPN when the
        /// port is not open, and not-terminated when no message matches.
        /// </returns>
        /// <remarks>
        /// A received message is taken off the queue and becomes task-current, and port-current as
        /// well when it was sent secure. Waiting and wake-up are not modelled: a wait is treated as
        /// a poll. The sender hash is the low word of the sender's magic number, which is this
        /// kernel's own choice.
        /// </remarks>
        public XmsgReceiveResult Receive(XmsgPortNumber port, XmsgWaitOptions options)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Receives the next message and its first two user bytes (XFRRH).
        /// </summary>
        /// <param name="port">
        /// The receiving port; the default port when unspecified.
        /// </param>
        /// <param name="options">
        /// The options to apply, as in <see cref="Receive"/>.
        /// </param>
        /// <returns>
        /// The same shape as <see cref="Receive"/>, except that on success the extra value is the
        /// first two user bytes (first byte high) instead of the length. It is zero when the
        /// message is shorter than two bytes.
        /// </returns>
        public XmsgReceiveResult ReceiveAndReadHeader(XmsgPortNumber port, XmsgWaitOptions options)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Receives the next message and immediately reads it into a user buffer (XFRRE).
        /// </summary>
        /// <param name="port">
        /// The receiving port; the default port when unspecified.
        /// </param>
        /// <param name="destination">
        /// The caller's buffer; its length is the requested byte count. Reading starts at the first
        /// byte of the message.
        /// </param>
        /// <param name="options">
        /// The options to apply, as in <see cref="Receive"/>.
        /// </param>
        /// <param name="bytesRead">
        /// On return, the number of bytes copied into <paramref name="destination"/>; zero when no
        /// message was received.
        /// </param>
        /// <returns>
        /// The result of <see cref="Receive"/>.
        /// </returns>
        public XmsgReceiveResult ReceiveAndRead(
            XmsgPortNumber port,
            Span<byte> destination,
            XmsgWaitOptions options,
            out int bytesRead)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Reports the state of a message buffer, including its sender (XFMST).
        /// </summary>
        /// <param name="message">
        /// The buffer to inspect, or <see cref="XmsgMessageIdentifier.Current"/> for the
        /// task-current message.
        /// </param>
        /// <returns>
        /// The message type as the status, the magic number of the port it came from, and its
        /// length; or XEIRM with no sender when the buffer is not owned by the task.
        /// </returns>
        /// <remarks>
        /// The buffer becomes task-current.
        /// </remarks>
        public XmsgMessageStatus GetMessageStatus(XmsgMessageIdentifier message)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Makes a buffer the task-current message, and optionally port-current too (XFSCM).
        /// </summary>
        /// <param name="message">
        /// The buffer to make current.
        /// </param>
        /// <param name="port">
        /// The port for which the message should additionally become port-current; the default
        /// port when unspecified. A negative port number sets task currency only. A port that is
        /// not open is ignored without an error.
        /// </param>
        /// <returns>
        /// The completed status, or XEIRM when the buffer is not owned by the task.
        /// </returns>
        public XmsgStatus SetCurrentMessage(XmsgMessageIdentifier message, XmsgPortNumber port)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Performs no operation, used to check that XMSG is reachable (XFDUM).
        /// </summary>
        /// <returns>
        /// Always the completed status.
        /// </returns>
        public XmsgStatus Dummy()
        {
            lock (_gate)
            {
                return XmsgStatus.Completed;
            }
        }

        /// <summary>
        /// Performs the dummy function and also returns the XMSG password (XFDUM).
        /// </summary>
        /// <param name="password">
        /// On return, the XMSG password (the version code, XPASW) that a task must present to
        /// <see cref="MakePrivileged"/>.
        /// </param>
        /// <returns>
        /// Always the completed status.
        /// </returns>
        /// <remarks>
        /// Guide appendix A section 4.1: the A register returns "the password which is needed in
        /// order to become a privileged XMSG task (see function XFPRV)". The same section returns
        /// a configuration mask (D) and a restart count (X); this kernel has neither, so they are
        /// not returned.
        /// </remarks>
        public XmsgStatus Dummy(out ushort password)
        {
            lock (_gate)
            {
                password = VersionCode;
                return XmsgStatus.Completed;
            }
        }

        /// <summary>
        /// Tells the kernel which open port belongs to the local routing task, XROUT.
        /// </summary>
        /// <param name="port">
        /// The port XROUT receives on.
        /// </param>
        /// <returns>
        /// The completed status, or XEIPN when the port is not open.
        /// </returns>
        /// <remarks>
        /// A send with the Route option goes to this port. It is also the port that answers to port
        /// number 0 of this system, which is where the captures show every XROUT letter and service
        /// request arriving. Closing the port detaches the router.
        /// </remarks>
        public XmsgStatus AttachRouter(XmsgPortNumber port)
        {
            lock (_gate)
            {
                Port? target = Resolve(port);
                if (target == null)
                {
                    return XmsgStatus.Failure(XmsgError.XEIPN);
                }

                _router = target;
                return XmsgStatus.Completed;
            }
        }

        /// <summary>
        /// Converts a magic number into the port and system it identifies (XFM2P).
        /// </summary>
        /// <param name="magic">
        /// The magic number to decode.
        /// </param>
        /// <param name="port">
        /// On return, the port number taken from <paramref name="magic"/>.
        /// </param>
        /// <param name="system">
        /// On return, the system number taken from <paramref name="magic"/>.
        /// </param>
        /// <returns>
        /// A success status that classifies the magic number: 3 for a system (the port number is
        /// zero), 2 for a port in this system while this task is privileged, 1 for anything else.
        /// </returns>
        /// <remarks>
        /// Guide appendix A section 4.4: "If the magic number was that of a system, then T=3. If
        /// the magic number was that of a local port, and the task of the port owner is a
        /// privileged task, then T=2. If the magic number was that of a remote port, or that of a
        /// local port, and the task of the port owner is unprivileged, then T=1." The carved kernel
        /// routine MFM2P (DOC/XMSG-MAGIC-NUMBER-LAYOUT-CARVED-2026-07-26.md, section 3) tests the
        /// port field first; when it is zero it returns T=3 with port 0 and the high word as the
        /// system number, and does not look at the system or at privilege. So a zero magic number
        /// is system 0, not an error.
        /// </remarks>
        public XmsgStatus ConvertMagicToPort(XmsgMagicNumber magic, out XmsgPortNumber port, out int system)
        {
            lock (_gate)
            {
                port = new XmsgPortNumber(magic.PortNumber);
                system = magic.SystemNumber;

                if (magic.PortNumber == 0)
                {
                    return XmsgStatus.Success(3);
                }

                bool local = magic.SystemNumber == _systemNumber;
                return XmsgStatus.Success(local && _privileged ? 2 : 1);
            }
        }

        /// <summary>
        /// Converts a local port number into its magic number (XFP2M).
        /// </summary>
        /// <param name="port">
        /// The local port to encode; the default port when unspecified.
        /// </param>
        /// <param name="magic">
        /// On return, the magic number, or <see cref="XmsgMagicNumber.None"/> on failure.
        /// </param>
        /// <returns>
        /// The completed status, or XEIPN when the port is not open in this kernel.
        /// </returns>
        public XmsgStatus ConvertPortToMagic(XmsgPortNumber port, out XmsgMagicNumber magic)
        {
            lock (_gate)
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
        }

        /// <summary>
        /// Makes the calling task privileged for XMSG, or takes the privilege away (XFPRV).
        /// </summary>
        /// <param name="password">
        /// The XMSG password (XPASW), which must equal <see cref="VersionCode"/>; or zero to give
        /// the privilege up.
        /// </param>
        /// <returns>
        /// The completed status, or XEPVR ("Privilege request refused") when the password is wrong
        /// or the task is not a kind of program that may become privileged.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Guide appendix A section 4.8: "the caller must be either a driver, direct task,
        /// foreground program or background program, logged in as user SYSTEM. Besides this, the
        /// program must also specify the current XMSG password"; "When the task should no longer
        /// be privileged, the same call should be used, but with the A register equal to zero."
        /// The XEPVR error text narrows the foreground case to ring 2 or 3. See
        /// <see cref="XmsgTaskKind"/>. The password comes from <see cref="Dummy(out ushort)"/>.
        /// </para>
        /// <para>
        /// Privilege is what <see cref="DefineMaximumMemory"/>, <see cref="CheckSenderPrivileges"/>
        /// and <see cref="ConvertMagicToPort"/> consult. A refused request leaves the privilege
        /// the task already had as it was; the guide does not say otherwise.
        /// </para>
        /// </remarks>
        public XmsgStatus MakePrivileged(ushort password)
        {
            lock (_gate)
            {
                if (password == 0)
                {
                    _privileged = false;
                    return XmsgStatus.Completed;
                }

                if (!MayBecomePrivileged(TaskKind) || password != VersionCode)
                {
                    return XmsgStatus.Failure(XmsgError.XEPVR);
                }

                _privileged = true;
                return XmsgStatus.Completed;
            }
        }

        private static bool MayBecomePrivileged(XmsgTaskKind kind)
        {
            switch (kind)
            {
                case XmsgTaskKind.Driver:
                case XmsgTaskKind.DirectTask:
                case XmsgTaskKind.ForegroundRing2:
                case XmsgTaskKind.ForegroundRing3:
                case XmsgTaskKind.BackgroundSystemUser:
                    return true;
                default:
                    return false;
            }
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
            lock (_gate)
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

                AddBuffer(buffer);
                Enqueue(target, buffer);
                return XmsgStatus.Completed;
            }
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
        /// Order matters. Forward is tested first because XFHIP and XFRRO are the SAME BIT,
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

            DropBuffer(buffer);
            return XmsgStatus.Completed;
        }

        private XmsgStatus Undeliverable(Buffer buffer, Port source, XmsgError reason)
        {
            if (!buffer.Secure)
            {
                DropBuffer(buffer);
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
                    DropBuffer(queued);
                    ForgetCurrency(queued.Id);
                }
            }

            port.Queue.Clear();

            if (port.Current != 0 && _buffers.TryGetValue(port.Current, out Buffer? current))
            {
                ReturnToSender(current);
            }

            _portList.Remove(port);

            if (_router == port)
            {
                _router = null;
            }
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

            DropBuffer(buffer);
            ForgetCurrency(buffer.Id);
        }

        // Adds a buffer to the kernel and counts new-space bytes toward the space the task owns.
        private void AddBuffer(Buffer buffer)
        {
            _buffers.Add(buffer.Id, buffer);
            if (buffer.Pool == BufferPool.None)
            {
                _looseBytes += buffer.Data.Size;
            }
        }

        // Takes a buffer out of the kernel. A buffer that came from an allocated pool goes back
        // to that pool's available list ("when they are released, however, or sent out of the
        // local system, the message buffer is put back on the originating task's AAML").
        private void DropBuffer(Buffer buffer)
        {
            if (!_buffers.Remove(buffer.Id))
            {
                return;
            }

            if (buffer.Pool == BufferPool.Exclusive && _exclusiveInUse > 0)
            {
                _exclusiveInUse--;
            }
            else if (buffer.Pool == BufferPool.Shared && _sharedInUse > 0)
            {
                _sharedInUse--;
            }
            else if (buffer.Pool == BufferPool.None)
            {
                _looseBytes -= buffer.Data.Size;
            }
        }

        // The message space the task owns: both allocated pools, plus every buffer that is new
        // space rather than a pool buffer.
        private long OwnedSpace()
        {
            return (long)_allocatedSize * _allocated
                + (long)_exclusiveAllocatedSize * _exclusiveAllocated
                + _looseBytes;
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
            // Port 0 of this system is XROUT, the fixed letter and service sink.
            if (magic.PortNumber == 0 && magic.SystemNumber == _systemNumber)
            {
                return _router;
            }

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

        private enum BufferPool
        {
            None,
            Shared,
            Exclusive,
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

            // The allocated pool this buffer was taken from, or None for new space.
            internal BufferPool Pool { get; set; }
        }
    }
}
