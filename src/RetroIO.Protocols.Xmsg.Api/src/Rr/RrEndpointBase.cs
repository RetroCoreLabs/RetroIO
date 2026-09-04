using System;
using System.Collections.Generic;

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
    /// </remarks>
    public abstract class RrEndpointBase
    {
        /// <summary>
        /// The size of the request-response header that precedes every payload.
        /// </summary>
        protected const int HeaderSize = XmsgMessageBuffer.HeaderSize;

        private readonly Dictionary<int, XmsgMagicNumber> _connections;

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
        /// Records a connection against the peer's magic number.
        /// </summary>
        /// <param name="peer">
        /// The peer port's magic number.
        /// </param>
        /// <returns>
        /// The new connection identifier.
        /// </returns>
        protected int AddConnection(XmsgMagicNumber peer)
        {
            int id = _nextConnection++;
            _connections.Add(id, peer);
            return id;
        }

        /// <summary>
        /// Records a connection under an identifier the peer chose.
        /// </summary>
        /// <param name="id">
        /// The connection identifier.
        /// </param>
        /// <param name="peer">
        /// The peer port's magic number.
        /// </param>
        protected void AddConnection(int id, XmsgMagicNumber peer)
        {
            _connections[id] = peer;
            if (id >= _nextConnection)
            {
                _nextConnection = id + 1;
            }
        }

        /// <summary>
        /// Looks a connection up.
        /// </summary>
        /// <param name="id">
        /// The connection identifier.
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
        /// The connection identifier.
        /// </param>
        protected void RemoveConnection(int id)
        {
            _connections.Remove(id);
        }

        /// <summary>
        /// Sends one framed message to a connection's peer.
        /// </summary>
        /// <param name="connection">
        /// The connection to send on.
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

            return SendFramedTo(peer, connection, kind, payload);
        }

        /// <summary>
        /// Sends one framed message to an explicit peer, for the case where no connection exists yet.
        /// </summary>
        /// <param name="peer">
        /// The destination magic number.
        /// </param>
        /// <param name="connection">
        /// The connection identifier to stamp into the header.
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
            XmsgMagicNumber peer, int connection, RrMessageKind kind, ReadOnlySpan<byte> payload)
        {
            XmsgMessageIdentifier message;
            XmsgStatus reserved = Kernel.ReserveBuffer(
                HeaderSize + payload.Length, XmsgBufferOptions.None, out message);
            if (reserved.IsError)
            {
                return new RrStatus(reserved.Value);
            }

            Span<byte> header = stackalloc byte[HeaderSize];
            header[0] = (byte)kind;
            header[1] = 0;
            header[2] = (byte)(connection >> 8);
            header[3] = (byte)(connection & 0xFF);
            header[4] = 0;
            header[5] = 0;

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
        /// On return, the connection identifier from the header.
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
        protected bool TryReceiveFramed(
            out RrMessageKind kind,
            out int connection,
            out byte[] payload,
            out XmsgMagicNumber sender,
            out bool returned)
        {
            kind = RrMessageKind.None;
            connection = 0;
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
        /// Closes this endpoint's port and forgets every connection.
        /// </summary>
        /// <returns>
        /// The completion status.
        /// </returns>
        protected RrStatus CloseOwnPort()
        {
            _connections.Clear();

            if (!IsInitialised)
            {
                return RrStatus.Ok;
            }

            XmsgStatus closed = Kernel.ClosePort(Port);
            IsInitialised = false;
            return closed.IsError ? new RrStatus(closed.Value) : RrStatus.Ok;
        }
    }
}
