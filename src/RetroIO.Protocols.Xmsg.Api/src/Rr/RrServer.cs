//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// A request-response server: registers a name, accepts connections, answers requests.
    /// </summary>
    /// <remarks>
    /// Implements the server half of chapter 4 over <see cref="XmsgKernel"/> and
    /// <see cref="XroutDirectory"/>. The lifecycle is the manual's: initialise and be named, then
    /// loop on <see cref="Wait"/>, taking each event with its matching call.
    /// The wire framing is ours, not ND's - see <see cref="RrMessageKind"/>.
    /// </remarks>
    public sealed class RrServer : RrEndpointBase, IRrServer
    {
        private readonly XroutDirectory _directory;
        private readonly Queue<Pending> _pending;

        private string _name;
        private int _capacity;

        /// <summary>
        /// Initialises a server on a kernel and a directory.
        /// </summary>
        /// <param name="kernel">
        /// The kernel the server's port belongs to.
        /// </param>
        /// <param name="directory">
        /// The XROUT stand-in the server registers its name with.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="directory"/> is null.
        /// </exception>
        public RrServer(XmsgKernel kernel, XroutDirectory directory)
            : base(kernel)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            _directory = directory;
            _pending = new Queue<Pending>();
            _name = string.Empty;
            _capacity = -1;
        }

        /// <summary>
        /// Gets the registered server name, or an empty string before initialisation.
        /// </summary>
        public string Name
        {
            get { return _name; }
        }

        /// <inheritdoc/>
        public RrStatus Initialise(string serverName)
        {
            return Initialise(serverName, -1);
        }

        /// <summary>
        /// Initialises the server as a connection port with a session capacity.
        /// </summary>
        /// <param name="serverName">
        /// The name clients will address.
        /// </param>
        /// <param name="capacity">
        /// The number of simultaneous connections to accept, or -1 for a plain named port with no
        /// admission control.
        /// </param>
        /// <returns>
        /// The completion status.
        /// </returns>
        /// <remarks>
        /// A capacity registers the name with XSCRS rather than XSNAM, so XROUT stops forwarding
        /// letters once the free-connection count reaches zero instead of piling them onto a server
        /// that cannot take them.
        /// </remarks>
        public RrStatus Initialise(string serverName, int capacity)
        {
            if (serverName == null)
            {
                throw new ArgumentNullException(nameof(serverName));
            }

            RrStatus opened = OpenOwnPort();
            if (!opened.IsOk)
            {
                return opened;
            }

            XmsgMagicNumber magic;
            XmsgStatus converted = Kernel.ConvertPortToMagic(Port, out magic);
            if (converted.IsError)
            {
                return new RrStatus(converted.Value);
            }

            XroutError registered = capacity < 0
                ? _directory.RegisterName(serverName, Kernel, magic)
                : _directory.RegisterConnectionPort(serverName, Kernel, magic, capacity, true);

            if (registered != XroutError.XRSOK)
            {
                CloseOwnPort();
                return new RrStatus((int)registered);
            }

            _name = serverName;
            _capacity = capacity;
            return RrStatus.Ok;
        }

        /// <inheritdoc/>
        public RrStatus Wait(RrEvent wanted, TimeSpan timeout, out RrEvent actual)
        {
            actual = RrEvent.None;

            RrMessageKind kind;
            int connection;
            byte[] payload;
            XmsgMagicNumber sender;
            bool returned;

            if (!TryReceiveFramed(out kind, out connection, out payload, out sender, out returned))
            {
                // Nothing pending. There is no scheduler to suspend on, so a wait that would have
                // blocked reports a timeout instead - see XmsgKernel's remarks.
                actual = RrEvent.Timeout;
                return RrStatus.Ok;
            }

            if (returned)
            {
                // A secure message came back: the peer is gone.
                _pending.Enqueue(new Pending(RrMessageKind.DisconnectRequest, connection, payload, sender));
                actual = RrEvent.DisconnectIndication;
                return RrStatus.Ok;
            }

            switch (kind)
            {
                case RrMessageKind.ConnectRequest:
                    actual = RrEvent.ConnectionIndication;
                    break;
                case RrMessageKind.Request:
                    actual = RrEvent.RequestIndication;
                    break;
                case RrMessageKind.DisconnectRequest:
                    actual = RrEvent.DisconnectIndication;
                    break;
                case RrMessageKind.DisconnectConfirm:
                    actual = RrEvent.DisconnectConfirmation;
                    break;
                default:
                    // A message on our port that the request-response layer does not own.
                    actual = RrEvent.UnknownPort;
                    break;
            }

            _pending.Enqueue(new Pending(kind, connection, payload, sender));
            return RrStatus.Ok;
        }

        /// <inheritdoc/>
        public RrStatus AcceptConnectionIndication(out int connection, out byte[] clientData)
        {
            connection = 0;
            clientData = Array.Empty<byte>();

            Pending? item = Take(RrMessageKind.ConnectRequest);
            if (item == null)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            // The server allocates the connection identifier; the client adopts it from the accept.
            connection = AddConnection(item.Sender);
            clientData = item.Payload;
            return RrStatus.Ok;
        }

        /// <inheritdoc/>
        public RrStatus SendConnectionResponse(int connection, bool accept, ReadOnlySpan<byte> serverData)
        {
            XmsgMagicNumber peer;
            if (!TryGetConnection(connection, out peer))
            {
                return new RrStatus((int)XmsgError.XEIMA);
            }

            RrStatus sent = SendFramed(
                connection,
                accept ? RrMessageKind.ConnectAccept : RrMessageKind.ConnectReject,
                serverData);

            if (!accept)
            {
                RemoveConnection(connection);
                ReleaseCapacity();
            }

            return sent;
        }

        /// <inheritdoc/>
        public RrStatus GetRequest(out int connection, out byte[] request)
        {
            connection = 0;
            request = Array.Empty<byte>();

            Pending? item = Take(RrMessageKind.Request);
            if (item == null)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            connection = item.Connection;
            request = item.Payload;
            return RrStatus.Ok;
        }

        /// <inheritdoc/>
        public RrStatus SendResponse(int connection, ReadOnlySpan<byte> response)
        {
            return SendFramed(connection, RrMessageKind.Response, response);
        }

        /// <inheritdoc/>
        public RrStatus RequestDisconnect(int connection)
        {
            RrStatus sent = SendFramed(connection, RrMessageKind.DisconnectRequest, ReadOnlySpan<byte>.Empty);
            RemoveConnection(connection);
            ReleaseCapacity();
            return sent;
        }

        /// <inheritdoc/>
        public RrStatus AcceptDisconnectIndication(out int connection)
        {
            connection = 0;

            Pending? item = Take(RrMessageKind.DisconnectRequest);
            if (item == null)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            connection = item.Connection;

            // Acknowledge only if the connection is still ours to acknowledge.
            XmsgMagicNumber peer;
            if (TryGetConnection(connection, out peer))
            {
                SendFramed(connection, RrMessageKind.DisconnectConfirm, ReadOnlySpan<byte>.Empty);
                RemoveConnection(connection);
                ReleaseCapacity();
            }

            return RrStatus.Ok;
        }

        /// <inheritdoc/>
        public RrStatus Abort(int connection)
        {
            RemoveConnection(connection);
            ReleaseCapacity();
            return RrStatus.Ok;
        }

        /// <inheritdoc/>
        public RrStatus End()
        {
            if (_name.Length > 0)
            {
                _directory.ClearName(_name);
                _name = string.Empty;
            }

            _pending.Clear();
            return CloseOwnPort();
        }

        private void ReleaseCapacity()
        {
            // Giving a connection back is XSNSP, and it is what re-opens the name to new callers.
            if (_capacity >= 0 && _name.Length > 0)
            {
                _directory.AdjustFreeConnections(_name, 1);
            }
        }

        private Pending? Take(RrMessageKind kind)
        {
            if (_pending.Count == 0)
            {
                return null;
            }

            Pending item = _pending.Peek();
            if (item.Kind != kind)
            {
                return null;
            }

            return _pending.Dequeue();
        }

        private sealed class Pending
        {
            internal Pending(RrMessageKind kind, int connection, byte[] payload, XmsgMagicNumber sender)
            {
                Kind = kind;
                Connection = connection;
                Payload = payload;
                Sender = sender;
            }

            internal RrMessageKind Kind { get; }

            internal int Connection { get; }

            internal byte[] Payload { get; }

            internal XmsgMagicNumber Sender { get; }
        }
    }
}
