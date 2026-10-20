//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The machinery a request-response server and client have in common: one XMSG port, a
    /// connection table, and the message framing.
    /// </summary>
    /// <remarks>
    /// The manual splits RR-LIB's calls into server-only, client-only and BOTH (the RRPB prefix) -
    /// initialise, wait, disconnect and end are shared. This class is that shared half, so the two
    /// concrete endpoints hold no duplicated framing or connection bookkeeping between them.
    /// <para><b>Connection references</b></para>
    /// Each side names a connection by a reference of its own. The client's is handed out by the
    /// connection request call itself (RRPCcnrq, "remoteID ... to be used in subsequent calls"),
    /// before the server has seen anything; the server's is handed out when it takes the
    /// connection indication. Every message carries both: the receiver's reference first, then
    /// the sender's, so each side can report events under its own reference. Reference 0 is never
    /// handed out, because RRanyRemote is 0.
    /// </remarks>
    public abstract class RrEndpointBase
    {
        /// <summary>
        /// The size of the request-response header that precedes every payload.
        /// </summary>
        protected const int HeaderSize = XmsgMessageBuffer.HeaderSize;

        private readonly Dictionary<int, XmsgMagicNumber> _connections;

        // For each connection of ours, the reference the other side uses for it. A message to the
        // peer carries this in the "receiver's reference" field.
        private readonly Dictionary<int, int> _peerReferences;

        // Messages a wait has handed out, waiting for the matching call (GetResponse and so on).
        private readonly Queue<Pending> _taken;

        // Messages that arrived but whose event was not asked for yet, in arrival order.
        private readonly List<Pending> _parked;

        private int _nextConnection;

        /// <summary>
        /// Initialises the endpoint over a kernel.
        /// </summary>
        /// <param name="kernel">
        /// The kernel this endpoint's port belongs to.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="kernel"/> is null.
        /// </exception>
        protected RrEndpointBase(XmsgKernel kernel)
        {
            if (kernel == null)
            {
                throw new ArgumentNullException(nameof(kernel));
            }

            Kernel = kernel;
            _connections = new Dictionary<int, XmsgMagicNumber>();
            _peerReferences = new Dictionary<int, int>();
            _taken = new Queue<Pending>();
            _parked = new List<Pending>();
            _nextConnection = 1;
        }

        /// <summary>
        /// Gets the kernel this endpoint runs on.
        /// </summary>
        protected XmsgKernel Kernel { get; }

        /// <summary>
        /// Gets or sets this endpoint's own port.
        /// </summary>
        protected XmsgPortNumber Port { get; set; }

        /// <summary>
        /// Gets a value indicating whether the endpoint has been initialised.
        /// </summary>
        public bool IsInitialised { get; protected set; }

        /// <summary>
        /// Gets the number of connections currently open.
        /// </summary>
        public int ConnectionCount
        {
            get { return _connections.Count; }
        }

        /// <summary>
        /// Opens this endpoint's XMSG port.
        /// </summary>
        /// <returns>
        /// The completion status.
        /// </returns>
        protected RrStatus OpenOwnPort()
        {
            XmsgPortNumber port;
            XmsgStatus opened = Kernel.OpenPort(out port);
            if (opened.IsError)
            {
                return new RrStatus(opened.Value);
            }

            Port = port;
            IsInitialised = true;
            return RrStatus.Ok;
        }

        /// <summary>
        /// Hands out a new connection reference without opening a connection.
        /// </summary>
        /// <returns>
        /// A reference that is not zero and has not been handed out before by this endpoint.
        /// </returns>
        /// <remarks>
        /// The client takes one when it asks for a connection, so it can return it at once.
        /// </remarks>
        protected int AllocateReference()
        {
            return _nextConnection++;
        }

        /// <summary>
        /// Records a connection against the peer's magic number, under a new reference.
        /// </summary>
        /// <param name="peer">
        /// The peer port's magic number.
        /// </param>
        /// <param name="peerReference">
        /// The reference the peer uses for the same connection.
        /// </param>
        /// <returns>
        /// The new connection reference.
        /// </returns>
        protected int AddConnection(XmsgMagicNumber peer, int peerReference)
        {
            int id = AllocateReference();
            AddConnection(id, peer, peerReference);
            return id;
        }

        /// <summary>
        /// Records a connection under a reference this endpoint already handed out.
        /// </summary>
        /// <param name="id">
        /// The connection reference.
        /// </param>
        /// <param name="peer">
        /// The peer port's magic number.
        /// </param>
        /// <param name="peerReference">
        /// The reference the peer uses for the same connection.
        /// </param>
        protected void AddConnection(int id, XmsgMagicNumber peer, int peerReference)
        {
            _connections[id] = peer;
            _peerReferences[id] = peerReference;
            if (id >= _nextConnection)
            {
                _nextConnection = id + 1;
            }
        }

        /// <summary>
        /// Looks a connection up.
        /// </summary>
        /// <param name="id">
        /// The connection reference.
        /// </param>
        /// <param name="peer">
        /// On return, the peer's magic number.
        /// </param>
        /// <returns>
        /// True when the connection is open.
        /// </returns>
        protected bool TryGetConnection(int id, out XmsgMagicNumber peer)
        {
            return _connections.TryGetValue(id, out peer);
        }

        /// <summary>
        /// Forgets a connection.
        /// </summary>
        /// <param name="id">
        /// The connection reference.
        /// </param>
        protected void RemoveConnection(int id)
        {
            _connections.Remove(id);
            _peerReferences.Remove(id);
        }

        /// <summary>
        /// Writes the six-byte header that precedes every payload.
        /// </summary>
        /// <param name="header">
        /// The six bytes to fill in.
        /// </param>
        /// <param name="kind">
        /// The message kind.
        /// </param>
        /// <param name="receiverReference">
        /// The reference the receiver uses for the connection; zero when it has none yet.
        /// </param>
        /// <param name="senderReference">
        /// The reference the sender uses for the connection.
        /// </param>
        protected static void WriteFramedHeader(
            Span<byte> header, RrMessageKind kind, int receiverReference, int senderReference)
        {
            header[0] = (byte)kind;
            header[1] = 0;
            header[2] = (byte)(receiverReference >> 8);
            header[3] = (byte)(receiverReference & 0xFF);
            header[4] = (byte)(senderReference >> 8);
            header[5] = (byte)(senderReference & 0xFF);
        }

        /// <summary>
        /// Sends one framed message to a connection's peer.
        /// </summary>
        /// <param name="connection">
        /// The connection to send on, by this endpoint's own reference.
        /// </param>
        /// <param name="kind">
        /// The message kind.
        /// </param>
        /// <param name="payload">
        /// The caller's data, which follows the header.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        protected RrStatus SendFramed(int connection, RrMessageKind kind, ReadOnlySpan<byte> payload)
        {
            XmsgMagicNumber peer;
            if (!TryGetConnection(connection, out peer))
            {
                return new RrStatus((int)XmsgError.XEIMA);
            }

            int peerReference;
            _peerReferences.TryGetValue(connection, out peerReference);
            return SendFramedTo(peer, peerReference, connection, kind, payload);
        }

        /// <summary>
        /// Sends one framed message to an explicit peer, for the case where no connection exists yet.
        /// </summary>
        /// <param name="peer">
        /// The destination magic number.
        /// </param>
        /// <param name="receiverReference">
        /// The reference the receiver uses for the connection, to stamp into the header.
        /// </param>
        /// <param name="senderReference">
        /// This endpoint's own reference, to stamp into the header.
        /// </param>
        /// <param name="kind">
        /// The message kind.
        /// </param>
        /// <param name="payload">
        /// The caller's data.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        protected RrStatus SendFramedTo(
            XmsgMagicNumber peer,
            int receiverReference,
            int senderReference,
            RrMessageKind kind,
            ReadOnlySpan<byte> payload)
        {
            XmsgMessageIdentifier message;
            XmsgStatus reserved = Kernel.ReserveBuffer(
                HeaderSize + payload.Length, XmsgBufferOptions.None, out message);
            if (reserved.IsError)
            {
                return new RrStatus(reserved.Value);
            }

            Span<byte> header = stackalloc byte[HeaderSize];
            WriteFramedHeader(header, kind, receiverReference, senderReference);

            XmsgStatus wroteHeader = Kernel.WriteHeader(message, header);
            if (wroteHeader.IsError)
            {
                return new RrStatus(wroteHeader.Value);
            }

            if (payload.Length > 0)
            {
                int written;
                XmsgStatus wrote = Kernel.Write(message, payload, HeaderSize, false, out written);
                if (wrote.IsError)
                {
                    return new RrStatus(wrote.Value);
                }
            }

            // Secure, so an undeliverable message comes back rather than vanishing - a
            // request-response protocol has no way to recover from a silent drop.
            XmsgStatus sent = Kernel.Send(peer, Port, XmsgSendFlags.Secure);
            return sent.IsError ? new RrStatus(sent.Value) : RrStatus.Ok;
        }

        /// <summary>
        /// Takes the next message off this endpoint's port and unpacks its framing.
        /// </summary>
        /// <param name="kind">
        /// On return, the message kind.
        /// </param>
        /// <param name="connection">
        /// On return, this endpoint's own reference for the connection, from the header.
        /// </param>
        /// <param name="peerReference">
        /// On return, the reference the sender uses for the connection, from the header.
        /// </param>
        /// <param name="payload">
        /// On return, the data following the header.
        /// </param>
        /// <param name="sender">
        /// On return, the sending port's magic number.
        /// </param>
        /// <param name="returned">
        /// On return, true when this was a secure message coming back undelivered rather than a
        /// message from the peer.
        /// </param>
        /// <returns>
        /// True when a message was taken.
        /// </returns>
        /// <remarks>
        /// A returned message is one this endpoint sent, so its two reference fields are the other
        /// way round; they are swapped back here, and <paramref name="connection"/> is still this
        /// endpoint's own reference.
        /// </remarks>
        protected bool TryReceiveFramed(
            out RrMessageKind kind,
            out int connection,
            out int peerReference,
            out byte[] payload,
            out XmsgMagicNumber sender,
            out bool returned)
        {
            kind = RrMessageKind.None;
            connection = 0;
            peerReference = 0;
            payload = Array.Empty<byte>();
            sender = XmsgMagicNumber.None;
            returned = false;

            XmsgReceiveResult received = Kernel.Receive(Port, XmsgWaitOptions.None);
            if (!received.Received)
            {
                return false;
            }

            returned = received.MessageType == XmsgMessageType.XMTRE;
            sender = Kernel.GetMessageStatus(received.Message).Sender;

            byte[] header = new byte[HeaderSize];
            if (!Kernel.ReadHeader(received.Message, header).IsError)
            {
                kind = (RrMessageKind)header[0];
                connection = (header[2] << 8) | header[3];
                peerReference = (header[4] << 8) | header[5];
                if (returned)
                {
                    int own = peerReference;
                    peerReference = connection;
                    connection = own;
                }
            }

            XmsgMessageStatus status = Kernel.GetMessageStatus(received.Message);
            int payloadLength = status.Length - HeaderSize;
            if (payloadLength > 0)
            {
                payload = new byte[payloadLength];
                int read;
                Kernel.Read(received.Message, payload, HeaderSize, out read);
                if (read != payloadLength)
                {
                    byte[] trimmed = new byte[read];
                    Array.Copy(payload, trimmed, read);
                    payload = trimmed;
                }
            }

            Kernel.ReleaseBuffer(received.Message);
            return true;
        }

        /// <summary>
        /// Works out which RR-LIB event a message of the given kind is, for this kind of endpoint.
        /// </summary>
        /// <param name="kind">
        /// The message kind.
        /// </param>
        /// <returns>
        /// The event, or <see cref="RrEvent.UnknownPort"/> for a kind this endpoint does not own.
        /// </returns>
        protected abstract RrEvent EventFor(RrMessageKind kind);

        /// <summary>
        /// Waits for the next event that was asked for (RRPBWAIT).
        /// </summary>
        /// <param name="wanted">
        /// The set of events to wait for, as a bit mask; <see cref="RrEvent.Any"/> wants all.
        /// </param>
        /// <param name="timeout">
        /// How long to wait. Zero polls once; <see cref="Timeout.InfiniteTimeSpan"/> waits until an
        /// event comes.
        /// </param>
        /// <param name="actual">
        /// On return, the single event that occurred, or <see cref="RrEvent.Timeout"/> when the
        /// time ran out first.
        /// </param>
        /// <returns>
        /// Always OK.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Guide, routine RRPBWAIT: the timeout is "the maximum waiting time", "a value of 0 for
        /// RRTMlength is equivalent to a poll for outstanding RR-LIB events of the requested type";
        /// "With the exception of RREVunkn and RREVothr, you will only receive events permitted by
        /// requestedEvent"; and chapter 4 section 4.7: "Only one event can be signalled in one
        /// RRPBWAIT call". A message whose event was not asked for is not lost. It is kept, in
        /// arrival order, and a later wait that asks for it gets it.
        /// </para>
        /// <para>
        /// The guide does not say what a timeout does when RREVtime was not asked for. The wait
        /// ends and reports <see cref="RrEvent.Timeout"/> anyway, because a wait that could never
        /// return would be of no use.
        /// </para>
        /// <para>
        /// There is no scheduler here, so the wait polls the port every millisecond until the
        /// time is up. The kernel takes its own lock, so messages may arrive from another thread
        /// while this one waits.
        /// </para>
        /// </remarks>
        protected RrStatus WaitForEvent(RrEvent wanted, TimeSpan timeout, out RrEvent actual)
        {
            actual = RrEvent.None;
            Stopwatch clock = Stopwatch.StartNew();

            while (true)
            {
                // Messages set aside earlier came first, so they are looked at first.
                for (int i = 0; i < _parked.Count; i++)
                {
                    if (IsAskedFor(_parked[i].Event, wanted))
                    {
                        Pending found = _parked[i];
                        _parked.RemoveAt(i);
                        _taken.Enqueue(found);
                        actual = found.Event;
                        return RrStatus.Ok;
                    }
                }

                RrMessageKind kind;
                int connection;
                int peerReference;
                byte[] payload;
                XmsgMagicNumber sender;
                bool returned;
                while (TryReceiveFramed(out kind, out connection, out peerReference, out payload, out sender, out returned))
                {
                    // A secure message of ours that came back means the peer is gone.
                    RrMessageKind effective = returned ? RrMessageKind.DisconnectRequest : kind;
                    RrEvent occurred = returned ? RrEvent.DisconnectIndication : EventFor(kind);
                    Pending arrived = new Pending(effective, connection, peerReference, payload, sender, occurred);

                    if (IsAskedFor(occurred, wanted))
                    {
                        _taken.Enqueue(arrived);
                        actual = occurred;
                        return RrStatus.Ok;
                    }

                    _parked.Add(arrived);
                }

                // The infinite value is -1 millisecond, so it has to be told apart before the test
                // for a zero or negative time, which is a poll.
                bool infinite = timeout == Timeout.InfiniteTimeSpan;
                if (!infinite && (timeout <= TimeSpan.Zero || clock.Elapsed >= timeout))
                {
                    actual = RrEvent.Timeout;
                    return RrStatus.Ok;
                }

                Thread.Sleep(1);
            }
        }

        /// <summary>
        /// Takes the message a wait handed out, if it is of the given kind.
        /// </summary>
        /// <param name="kind">
        /// The kind the matching call expects.
        /// </param>
        /// <returns>
        /// The message, or <c>null</c> when the next message a wait handed out is of another kind
        /// or there is none.
        /// </returns>
        protected Pending? TakeTaken(RrMessageKind kind)
        {
            if (_taken.Count == 0 || _taken.Peek().Kind != kind)
            {
                return null;
            }

            return _taken.Dequeue();
        }

        /// <summary>
        /// Takes the message a wait handed out, if it is of one of two kinds.
        /// </summary>
        /// <param name="first">
        /// The first kind the matching call accepts.
        /// </param>
        /// <param name="second">
        /// The second kind the matching call accepts.
        /// </param>
        /// <returns>
        /// The message, or <c>null</c> when the next message a wait handed out is of another kind
        /// or there is none.
        /// </returns>
        protected Pending? TakeTakenEither(RrMessageKind first, RrMessageKind second)
        {
            if (_taken.Count == 0)
            {
                return null;
            }

            RrMessageKind kind = _taken.Peek().Kind;
            if (kind != first && kind != second)
            {
                return null;
            }

            return _taken.Dequeue();
        }

        /// <summary>
        /// Drops every message a wait handed out or set aside.
        /// </summary>
        protected void ClearPending()
        {
            _taken.Clear();
            _parked.Clear();
        }

        /// <summary>
        /// Closes this endpoint's port and forgets every connection.
        /// </summary>
        /// <returns>
        /// The completion status.
        /// </returns>
        protected RrStatus CloseOwnPort()
        {
            _connections.Clear();
            _peerReferences.Clear();

            if (!IsInitialised)
            {
                return RrStatus.Ok;
            }

            XmsgStatus closed = Kernel.ClosePort(Port);
            IsInitialised = false;
            return closed.IsError ? new RrStatus(closed.Value) : RrStatus.Ok;
        }

        // RREVunkn and RREVothr are reported whether or not they were asked for; every other event
        // only when its bit is in the requested set.
        private static bool IsAskedFor(RrEvent occurred, RrEvent wanted)
        {
            if (occurred == RrEvent.UnknownPort || occurred == RrEvent.Other)
            {
                return true;
            }

            return ((int)occurred & (int)wanted) != 0;
        }

        /// <summary>
        /// A message a wait has taken off the port.
        /// </summary>
        protected sealed class Pending
        {
            internal Pending(
                RrMessageKind kind,
                int connection,
                int peerReference,
                byte[] payload,
                XmsgMagicNumber sender,
                RrEvent occurred)
            {
                Kind = kind;
                Connection = connection;
                PeerReference = peerReference;
                Payload = payload;
                Sender = sender;
                Event = occurred;
            }

            /// <summary>
            /// Gets the message kind.
            /// </summary>
            internal RrMessageKind Kind { get; }

            /// <summary>
            /// Gets this endpoint's own reference for the connection.
            /// </summary>
            internal int Connection { get; }

            /// <summary>
            /// Gets the reference the sender uses for the connection.
            /// </summary>
            internal int PeerReference { get; }

            /// <summary>
            /// Gets the data that followed the header.
            /// </summary>
            internal byte[] Payload { get; }

            /// <summary>
            /// Gets the sending port's magic number.
            /// </summary>
            internal XmsgMagicNumber Sender { get; }

            /// <summary>
            /// Gets the event this message is.
            /// </summary>
            internal RrEvent Event { get; }
        }
    }
}
