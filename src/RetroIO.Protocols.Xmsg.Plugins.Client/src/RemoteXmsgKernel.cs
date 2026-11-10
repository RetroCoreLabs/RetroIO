//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using Google.Protobuf;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Plugins.Contracts;

namespace RetroIO.Protocols.Xmsg.Plugins.Client
{
    /// <summary>
    /// The XMSG kernel a plug-in's task code calls, with ports living on the node.
    /// </summary>
    /// <remarks>
    /// <para><b>What goes to the node and what stays here</b></para>
    /// Ports are the node's: opening, closing and sending go over the stream, and a port's magic
    /// number is the one the node gave it. Message buffers are the plug-in's own: reserving,
    /// writing, reading, releasing and the message status are done by a real
    /// <see cref="XmsgKernel"/> inside this object, so they behave exactly as they do in a node. What
    /// arrives on a port is put into that local kernel on the matching local port, with the
    /// sender's magic number and the message type the node reported, and the task receives it the
    /// ordinary way.
    /// <para><b>Calls this does not carry</b></para>
    /// Calls about the whole machine or about waiting (general status, friend systems, privileges,
    /// returning a message, memory limits) are not part of the plug-in contract. They throw
    /// <see cref="NotSupportedException"/> rather than answer with something made up.
    /// </remarks>
    public sealed class RemoteXmsgKernel : IXmsgKernel
    {
        private readonly PluginConnection _connection;
        private readonly XmsgKernel _local;
        private readonly object _gate;

        // node magic -> local port, and local port -> node magic.
        private readonly Dictionary<uint, XmsgPortNumber> _portOf;
        private readonly Dictionary<int, XmsgMagicNumber> _magicOf;

        // The message XFSND would take: the last one reserved, received or made current.
        private XmsgMessageIdentifier _current;
        private bool _hasCurrent;

        internal RemoteXmsgKernel(PluginConnection connection)
        {
            _connection = connection;
            _local = new XmsgKernel(connection.SystemNumber, 0x2B67, null);
            _gate = new object();
            _portOf = new Dictionary<uint, XmsgPortNumber>();
            _magicOf = new Dictionary<int, XmsgMagicNumber>();
        }

        /// <inheritdoc/>
        public ushort SystemNumber
        {
            get { return _connection.SystemNumber; }
        }

        /// <summary>
        /// Gets how many arrivals had no matching port here and were dropped. A non-zero count means
        /// the node and this side disagree about which ports are open.
        /// </summary>
        public long UnmatchedArrivals { get; private set; }

        /// <summary>
        /// Gets how many arrivals were of a message type this side cannot put into its kernel (only
        /// normal, high-priority and routed messages can be) and were dropped.
        /// </summary>
        public long UnsupportedArrivals { get; private set; }

        /// <inheritdoc/>
        public XmsgStatus OpenPort(out XmsgPortNumber port)
        {
            port = default;
            Result? answer = _connection.Call(new PluginRequest { OpenPort = new OpenPort() });
            if (answer == null)
            {
                return XmsgStatus.Failure(XmsgError.XENRU);
            }

            if (answer.Status != XmsgStatus.Completed.Value)
            {
                return new XmsgStatus(answer.Status);
            }

            XmsgStatus opened = _local.OpenPort(out port);
            if (opened.IsError)
            {
                // The node opened one; give it back rather than leave it orphaned there.
                _connection.Call(new PluginRequest { ClosePort = new ClosePort { Magic = answer.Value } });
                return opened;
            }

            lock (_gate)
            {
                _portOf[answer.Value] = port;
                _magicOf[port.Value] = new XmsgMagicNumber(answer.Value);
            }

            return XmsgStatus.Completed;
        }

        /// <inheritdoc/>
        public XmsgStatus ClosePort(XmsgPortNumber port)
        {
            XmsgMagicNumber magic;
            lock (_gate)
            {
                if (!_magicOf.TryGetValue(port.Value, out magic))
                {
                    return XmsgStatus.Failure(XmsgError.XEIPN);
                }

                _magicOf.Remove(port.Value);
                _portOf.Remove(magic.Value);
            }

            _local.ClosePort(port);
            Result? answer = _connection.Call(new PluginRequest { ClosePort = new ClosePort { Magic = magic.Value } });
            return answer == null ? XmsgStatus.Failure(XmsgError.XENRU) : new XmsgStatus(answer.Status);
        }

        /// <inheritdoc/>
        public XmsgStatus ConvertPortToMagic(XmsgPortNumber port, out XmsgMagicNumber magic)
        {
            lock (_gate)
            {
                if (_magicOf.TryGetValue(port.Value, out magic))
                {
                    return XmsgStatus.Completed;
                }
            }

            magic = default;
            return XmsgStatus.Failure(XmsgError.XEIPN);
        }

        /// <inheritdoc/>
        public XmsgStatus ConvertMagicToPort(XmsgMagicNumber magic, out XmsgPortNumber port, out int system)
        {
            system = magic.SystemNumber;
            lock (_gate)
            {
                if (_portOf.TryGetValue(magic.Value, out port))
                {
                    return XmsgStatus.Completed;
                }
            }

            port = default;
            return XmsgStatus.Failure(XmsgError.XEIMA);
        }

        /// <inheritdoc/>
        public XmsgStatus ReserveBuffer(int byteCount, XmsgBufferOptions options, out XmsgMessageIdentifier message)
        {
            XmsgStatus reserved = _local.ReserveBuffer(byteCount, options, out message);
            if (!reserved.IsError)
            {
                SetCurrent(message);
            }

            return reserved;
        }

        /// <inheritdoc/>
        public XmsgStatus ReleaseBuffer(XmsgMessageIdentifier message)
        {
            XmsgStatus released = _local.ReleaseBuffer(message);
            lock (_gate)
            {
                if (_hasCurrent && _current.Equals(message))
                {
                    _hasCurrent = false;
                }
            }

            return released;
        }

        /// <inheritdoc/>
        public XmsgStatus Write(XmsgMessageIdentifier message, ReadOnlySpan<byte> source, int displacement, bool resetLength, out int bytesWritten)
        {
            return _local.Write(message, source, displacement, resetLength, out bytesWritten);
        }

        /// <inheritdoc/>
        public XmsgStatus WriteHeader(XmsgMessageIdentifier message, ReadOnlySpan<byte> header)
        {
            return _local.WriteHeader(message, header);
        }

        /// <inheritdoc/>
        public XmsgStatus Read(XmsgMessageIdentifier message, Span<byte> destination, int displacement, out int bytesRead)
        {
            return _local.Read(message, destination, displacement, out bytesRead);
        }

        /// <inheritdoc/>
        public XmsgStatus ReadHeader(XmsgMessageIdentifier message, Span<byte> header)
        {
            return _local.ReadHeader(message, header);
        }

        /// <inheritdoc/>
        public XmsgMessageStatus GetMessageStatus(XmsgMessageIdentifier message)
        {
            return _local.GetMessageStatus(message);
        }

        /// <inheritdoc/>
        public XmsgStatus SetCurrentMessage(XmsgMessageIdentifier message, XmsgPortNumber port)
        {
            XmsgStatus set = _local.SetCurrentMessage(message, port);
            if (!set.IsError)
            {
                SetCurrent(message);
            }

            return set;
        }

        /// <inheritdoc/>
        public XmsgStatus Send(XmsgMagicNumber destination, XmsgPortNumber fromPort, XmsgSendFlags flags)
        {
            XmsgMagicNumber from;
            XmsgMessageIdentifier message;
            lock (_gate)
            {
                if (!_magicOf.TryGetValue(fromPort.Value, out from))
                {
                    return XmsgStatus.Failure(XmsgError.XEIPN);
                }

                if (!_hasCurrent)
                {
                    // XFSND sends the current message; with none there is nothing to send.
                    return XmsgStatus.Failure(XmsgError.XEIRM);
                }

                message = _current;
                _hasCurrent = false;
            }

            XmsgMessageStatus status = _local.GetMessageStatus(message);
            int length = status.Length > 0 ? status.Length : 0;
            byte[] data = new byte[length];
            int read = 0;
            if (length > 0)
            {
                _local.Read(message, data, 0, out read);
            }

            // Sending hands the buffer over; on the node it is the node's from here on.
            _local.ReleaseBuffer(message);

            Result? answer = _connection.Call(new PluginRequest
            {
                Send = new Send
                {
                    FromMagic = from.Value,
                    ToMagic = destination.Value,
                    Data = ByteString.CopyFrom(data, 0, read),
                    Flags = (uint)flags,
                },
            });

            return answer == null ? XmsgStatus.Failure(XmsgError.XENRU) : new XmsgStatus(answer.Status);
        }

        /// <inheritdoc/>
        public XmsgReceiveResult Receive(XmsgPortNumber port, XmsgWaitOptions options)
        {
            XmsgReceiveResult result = _local.Receive(port, options);
            if (result.Received)
            {
                SetCurrent(result.Message);
            }

            return result;
        }

        /// <inheritdoc/>
        public XmsgReceiveResult ReceiveAndReadHeader(XmsgPortNumber port, XmsgWaitOptions options)
        {
            XmsgReceiveResult result = _local.ReceiveAndReadHeader(port, options);
            if (result.Received)
            {
                SetCurrent(result.Message);
            }

            return result;
        }

        /// <inheritdoc/>
        public XmsgReceiveResult ReceiveAndRead(XmsgPortNumber port, Span<byte> destination, XmsgWaitOptions options, out int bytesRead)
        {
            XmsgReceiveResult result = _local.ReceiveAndRead(port, destination, options, out bytesRead);
            if (result.Received)
            {
                SetCurrent(result.Message);
            }

            return result;
        }

        /// <inheritdoc/>
        public XmsgStatus Dummy()
        {
            return _local.Dummy();
        }

        /// <inheritdoc/>
        public XmsgPortStatus GetPortStatus(XmsgPortNumber port, XmsgWaitOptions options)
        {
            throw NotCarried(nameof(GetPortStatus));
        }

        /// <inheritdoc/>
        public XmsgStatus GetGeneralStatus(XmsgPortNumber lastPort, XmsgWaitOptions options, out XmsgPortNumber port)
        {
            throw NotCarried(nameof(GetGeneralStatus));
        }

        /// <inheritdoc/>
        public XmsgStatus Disconnect()
        {
            throw NotCarried(nameof(Disconnect));
        }

        /// <inheritdoc/>
        public XmsgStatus AllocateBuffers(int byteCount, int messageCount, bool exclusive)
        {
            throw NotCarried(nameof(AllocateBuffers));
        }

        /// <inheritdoc/>
        public XmsgStatus FreeAllocatedBuffers(int messageCount, bool exclusive, out int freedCount)
        {
            throw NotCarried(nameof(FreeAllocatedBuffers));
        }

        /// <inheritdoc/>
        public XmsgPrivilegeCheck CheckSenderPrivileges(XmsgMessageIdentifier message)
        {
            throw NotCarried(nameof(CheckSenderPrivileges));
        }

        /// <inheritdoc/>
        public void DefineFriendSystem(ushort systemNumber, bool isFriend)
        {
            throw NotCarried(nameof(DefineFriendSystem));
        }

        /// <inheritdoc/>
        public XmsgListing ListMessagesAndPorts(int fromMessageId, int fromPortNumber)
        {
            throw NotCarried(nameof(ListMessagesAndPorts));
        }

        /// <inheritdoc/>
        public XmsgQueueSnapshot GetGeneralStatusMultiple()
        {
            throw NotCarried(nameof(GetGeneralStatusMultiple));
        }

        /// <inheritdoc/>
        public XmsgStatus DefineMaximumMemory(int byteCount)
        {
            throw NotCarried(nameof(DefineMaximumMemory));
        }

        /// <inheritdoc/>
        public XmsgStatus ReturnMessage(XmsgMessageIdentifier message, ushort statusBytes, XmsgPortNumber fromPort, XmsgSendFlags flags)
        {
            throw NotCarried(nameof(ReturnMessage));
        }

        /// <inheritdoc/>
        public XmsgStatus MakePrivileged(ushort password)
        {
            throw NotCarried(nameof(MakePrivileged));
        }

        /// <summary>
        /// Gets the node magic number of one of this side's ports, for the name calls.
        /// </summary>
        /// <param name="magic">
        /// The magic number as the task knows it, which is already the node's.
        /// </param>
        /// <returns>
        /// True when it is one of this plug-in's ports.
        /// </returns>
        internal bool OwnsMagic(XmsgMagicNumber magic)
        {
            lock (_gate)
            {
                return _portOf.ContainsKey(magic.Value);
            }
        }

        /// <summary>
        /// Puts a message that arrived on the node into the local kernel, on the matching port.
        /// Called on the stream's reading thread; the local kernel locks for itself.
        /// </summary>
        /// <param name="arrived">
        /// The arrival.
        /// </param>
        internal void Arrive(Arrived arrived)
        {
            XmsgPortNumber port;
            lock (_gate)
            {
                if (!_portOf.TryGetValue(arrived.ToMagic, out port))
                {
                    UnmatchedArrivals++;
                    return;
                }
            }

            // The local kernel decides the message type from the send options, so the type the
            // node reported is turned back into the option that produces it (XmsgKernel.ChooseType).
            XmsgSendFlags flags;
            switch ((XmsgMessageType)arrived.MessageType)
            {
                case XmsgMessageType.XMROU:
                    flags = XmsgSendFlags.Forward;
                    break;
                case XmsgMessageType.XMTHI:
                    flags = XmsgSendFlags.HighPriority;
                    break;
                case XmsgMessageType.XMTNO:
                    flags = XmsgSendFlags.None;
                    break;
                case XmsgMessageType.XMTRE:
                    // A message this program sent secure came back: put it on the port as a return,
                    // with the reason, and the port it could not reach as its sender.
                    XmsgMagicNumber home;
                    _local.ConvertPortToMagic(port, out home);
                    _local.DeliverReturned(
                        home, new XmsgMagicNumber(arrived.FromMagic), arrived.Data.Span, (XmsgError)arrived.ReturnReason);
                    return;
                default:
                    // The rarer types cannot be made by delivering into a kernel. Counted, not
                    // silently turned into something else.
                    UnsupportedArrivals++;
                    return;
            }

            XmsgMagicNumber localMagic;
            _local.ConvertPortToMagic(port, out localMagic);
            _local.Deliver(localMagic, new XmsgMagicNumber(arrived.FromMagic), arrived.Data.Span, flags);
        }

        private void SetCurrent(XmsgMessageIdentifier message)
        {
            lock (_gate)
            {
                _current = message;
                _hasCurrent = true;
            }
        }

        private static NotSupportedException NotCarried(string call)
        {
            return new NotSupportedException(
                call + " is not carried to the node by the plug-in contract (docs/XMSG-PLUGIN-SERVERS.md in RetroServer).");
        }
    }
}
