//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

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

        /// <summary>
        /// Initialises the request-response layer and registers the server name (RRPBINIT).
        /// </summary>
        /// <param name="serverName">
        /// The name clients will address, equivalent to an XMSG port name. No client can connect
        /// before this has succeeded.
        /// </param>
        /// <returns>
        /// OK when the port is open and the name is registered; otherwise the kernel's or the
        /// directory's error.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serverName"/> is <c>null</c>.
        /// </exception>
        /// <remarks>
        /// Registers a plain named port with no session capacity, the same as calling the
        /// two-argument overload with a capacity of -1.
        /// </remarks>
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

        /// <summary>
        /// Waits for the next event the server asked for (RRPBWAIT).
        /// </summary>
        /// <param name="wanted">
        /// The set of events to wait for, as a bit mask. Only these are reported, apart from an
        /// unknown-port message, which always is. Events not asked for are kept for a later wait.
        /// </param>
        /// <param name="timeout">
        /// How long to wait. Zero polls once. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
        /// waits until an event comes.
        /// </param>
        /// <param name="actual">
        /// On return, the event that occurred:
        ///  - <see cref="RrEvent.Timeout"/> when the time ran out with nothing to report.
        ///  - <see cref="RrEvent.DisconnectIndication"/> for a message of ours that came back
        ///    undelivered, or a disconnect request from the client.
        ///  - <see cref="RrEvent.ConnectionIndication"/> for a connect request.
        ///  - <see cref="RrEvent.RequestIndication"/> for a request.
        ///  - <see cref="RrEvent.DisconnectConfirmation"/> for a disconnect confirm.
        ///  - <see cref="RrEvent.UnknownPort"/> for any other message kind.
        /// </param>
        /// <returns>
        /// Always OK.
        /// </returns>
        /// <remarks>
        /// Exactly one message is taken per call, so a server must keep calling until the queue
        /// drains. The message taken is kept so the matching call (for example
        /// <see cref="GetRequest"/>) can pick it up. The rules are those of the shared
        /// implementation, <see cref="RrEndpointBase.WaitForEvent"/>.
        /// </remarks>
        public RrStatus Wait(RrEvent wanted, TimeSpan timeout, out RrEvent actual)
        {
            return WaitForEvent(wanted, timeout, out actual);
        }

        /// <inheritdoc />
        protected override RrEvent EventFor(RrMessageKind kind)
        {
            switch (kind)
            {
                case RrMessageKind.ConnectRequest:
                    return RrEvent.ConnectionIndication;
                case RrMessageKind.Request:
                    return RrEvent.RequestIndication;
                case RrMessageKind.DisconnectRequest:
                    return RrEvent.DisconnectIndication;
                case RrMessageKind.DisconnectConfirm:
                    return RrEvent.DisconnectConfirmation;
                default:
                    // A message on our port that the request-response layer does not own.
                    return RrEvent.UnknownPort;
            }
        }

        /// <summary>
        /// Takes the details of an arrived connection request (RRPSCNI).
        /// </summary>
        /// <param name="connection">
        /// On return, the identifier of the new connection, chosen by this server and used by
        /// every later call about it; zero when there was no request.
        /// </param>
        /// <param name="clientData">
        /// On return, any user data the client sent with its connection request; empty when none.
        /// </param>
        /// <returns>
        /// OK, or XENIM when the next message taken by <see cref="Wait"/> is not a connect
        /// request.
        /// </returns>
        /// <remarks>
        /// The connection is recorded against the client's port as soon as this returns OK, before
        /// <see cref="SendConnectionResponse"/> is called.
        /// </remarks>
        public RrStatus AcceptConnectionIndication(out int connection, out byte[] clientData)
        {
            connection = 0;
            clientData = Array.Empty<byte>();

            Pending? item = TakeTaken(RrMessageKind.ConnectRequest);
            if (item == null)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            // The server numbers its side of the connection itself. The client's own number is in
            // the request, and goes back with every message so the client hears it under that one.
            connection = AddConnection(item.Sender, item.PeerReference);
            clientData = item.Payload;
            return RrStatus.Ok;
        }

        /// <summary>
        /// Answers a connection request (RRPSCNR).
        /// </summary>
        /// <param name="connection">
        /// The connection being answered.
        /// </param>
        /// <param name="accept">
        /// <c>true</c> to establish the connection, <c>false</c> to refuse it.
        /// </param>
        /// <param name="serverData">
        /// User data to return to the client with the answer; may be empty.
        /// </param>
        /// <returns>
        /// The status of sending the answer: OK, XEIMA when <paramref name="connection"/> is not
        /// open, or the kernel's error.
        /// </returns>
        /// <remarks>
        /// On a refusal the connection is forgotten and, for a server with a session capacity, one
        /// free connection is given back to the directory.
        /// </remarks>
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

        /// <summary>
        /// Takes the bytes of an arrived request (RRPSGTRQ).
        /// </summary>
        /// <param name="connection">
        /// On return, the connection the request arrived on; zero when there was none.
        /// </param>
        /// <param name="request">
        /// On return, the request bytes; empty when there was none.
        /// </param>
        /// <returns>
        /// OK, or XENIM when the next message taken by <see cref="Wait"/> is not a request.
        /// </returns>
        public RrStatus GetRequest(out int connection, out byte[] request)
        {
            connection = 0;
            request = Array.Empty<byte>();

            Pending? item = TakeTaken(RrMessageKind.Request);
            if (item == null)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            connection = item.Connection;
            request = item.Payload;
            return RrStatus.Ok;
        }

        /// <summary>
        /// Sends the response to a request (RRPSSNRS).
        /// </summary>
        /// <param name="connection">
        /// The connection to answer on.
        /// </param>
        /// <param name="response">
        /// The response bytes.
        /// </param>
        /// <returns>
        /// OK when the response was sent; XEIMA when <paramref name="connection"/> is not open; or
        /// the kernel's error.
        /// </returns>
        /// <remarks>
        /// A client may have only one outstanding request per connection, so the response answers
        /// the request most recently taken from that connection. It is sent as a secure message.
        /// </remarks>
        public RrStatus SendResponse(int connection, ReadOnlySpan<byte> response)
        {
            return SendFramed(connection, RrMessageKind.Response, response);
        }

        /// <summary>
        /// Starts an orderly disconnect (RRPSDCRQ).
        /// </summary>
        /// <param name="connection">
        /// The connection to close.
        /// </param>
        /// <returns>
        /// The status of sending the disconnect request: OK, XEIMA when the connection is not
        /// open, or the kernel's error.
        /// </returns>
        /// <remarks>
        /// The connection is forgotten on this side whether or not the send worked, and for a
        /// server with a session capacity one free connection is given back to the directory.
        /// </remarks>
        public RrStatus RequestDisconnect(int connection)
        {
            RrStatus sent = SendFramed(connection, RrMessageKind.DisconnectRequest, ReadOnlySpan<byte>.Empty);
            RemoveConnection(connection);
            ReleaseCapacity();
            return sent;
        }

        /// <summary>
        /// Acknowledges a disconnect started by the client (RRPSDCIN).
        /// </summary>
        /// <param name="connection">
        /// On return, the connection that was closed; zero when there was none.
        /// </param>
        /// <returns>
        /// OK, or XENIM when the next message taken by <see cref="Wait"/> is not a disconnect
        /// request.
        /// </returns>
        /// <remarks>
        /// When the connection is still open here, a disconnect confirm is sent, the connection is
        /// forgotten and, for a server with a session capacity, one free connection is given back
        /// to the directory. The result of that send is not reported.
        /// </remarks>
        public RrStatus AcceptDisconnectIndication(out int connection)
        {
            connection = 0;

            Pending? item = TakeTaken(RrMessageKind.DisconnectRequest);
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

        /// <summary>
        /// Drops a connection immediately, without the orderly exchange (RRPSABRT).
        /// </summary>
        /// <param name="connection">
        /// The connection to abort.
        /// </param>
        /// <returns>
        /// Always OK.
        /// </returns>
        /// <remarks>
        /// Nothing is sent to the client. The connection is forgotten and, for a server with a
        /// session capacity, one free connection is given back to the directory - even when
        /// <paramref name="connection"/> was not open.
        /// </remarks>
        public RrStatus Abort(int connection)
        {
            RemoveConnection(connection);
            ReleaseCapacity();
            return RrStatus.Ok;
        }

        /// <summary>
        /// Releases everything the server holds (RRPSEND).
        /// </summary>
        /// <returns>
        /// OK, or the kernel's error when closing the server's port fails.
        /// </returns>
        /// <remarks>
        /// Clears the server's name from the directory, drops every message kept by
        /// <see cref="Wait"/>, forgets every connection and closes the server's port.
        /// </remarks>
        public RrStatus End()
        {
            if (_name.Length > 0)
            {
                _directory.ClearName(_name);
                _name = string.Empty;
            }

            ClearPending();
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
    }
}
