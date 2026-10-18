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
    /// A request-response client: connects to a server by name, sends requests, reads responses.
    /// </summary>
    /// <remarks>
    /// Implements the client half of chapter 4 over <see cref="XmsgKernel"/> and
    /// <see cref="XroutDirectory"/>. Both levels the manual describes are here: the low-level calls
    /// for a program that must keep several requests outstanding, and <see cref="Select"/> and
    /// <see cref="Transact"/>, which fold a whole sequence into one blocking call and are what the
    /// manual recommends when their restrictions are acceptable.
    /// The wire framing is ours, not ND's - see <see cref="RrMessageKind"/>.
    /// </remarks>
    public sealed class RrClient : RrEndpointBase, IRrClient
    {
        private readonly XroutDirectory _directory;
        private readonly Queue<Pending> _pending;

        /// <summary>
        /// Initialises a client on a kernel and a directory.
        /// </summary>
        /// <param name="kernel">
        /// The kernel the client's port belongs to.
        /// </param>
        /// <param name="directory">
        /// The XROUT stand-in that forwards the client's letters.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="directory"/> is null.
        /// </exception>
        public RrClient(XmsgKernel kernel, XroutDirectory directory)
            : base(kernel)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            _directory = directory;
            _pending = new Queue<Pending>();
        }

        /// <summary>
        /// Initialises the request-response layer for this client (RRPCINIT).
        /// </summary>
        /// <returns>
        /// OK when the client's own port was opened on the kernel, otherwise the kernel's error.
        /// </returns>
        public RrStatus Initialise()
        {
            return OpenOwnPort();
        }

        /// <summary>
        /// Takes the next message off the client's port and reports which event it is (RRPBWAIT).
        /// </summary>
        /// <param name="wanted">
        /// The set of events to wait for. This implementation does not read it: whatever message
        /// is next on the port is reported.
        /// </param>
        /// <param name="timeout">
        /// How long to wait. This implementation does not read it and never blocks.
        /// </param>
        /// <param name="actual">
        /// On return, the event that occurred:
        ///  - <see cref="RrEvent.Timeout"/> when no message is waiting.
        ///  - <see cref="RrEvent.DisconnectIndication"/> for a message of ours that came back
        ///    undelivered, or a disconnect request from the server.
        ///  - <see cref="RrEvent.ConnectionConfirmation"/> for a connect accept or reject.
        ///  - <see cref="RrEvent.ResponseIndication"/> for a response.
        ///  - <see cref="RrEvent.DisconnectConfirmation"/> for a disconnect confirm.
        ///  - <see cref="RrEvent.UnknownPort"/> for any other message kind.
        /// </param>
        /// <returns>
        /// Always OK.
        /// </returns>
        /// <remarks>
        /// The message taken is kept so the matching call (for example
        /// <see cref="GetResponse"/>) can pick it up.
        /// </remarks>
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
                actual = RrEvent.Timeout;
                return RrStatus.Ok;
            }

            if (returned)
            {
                _pending.Enqueue(new Pending(RrMessageKind.DisconnectRequest, connection, payload, sender));
                actual = RrEvent.DisconnectIndication;
                return RrStatus.Ok;
            }

            switch (kind)
            {
                case RrMessageKind.ConnectAccept:
                case RrMessageKind.ConnectReject:
                    actual = RrEvent.ConnectionConfirmation;
                    break;
                case RrMessageKind.Response:
                    actual = RrEvent.ResponseIndication;
                    break;
                case RrMessageKind.DisconnectRequest:
                    actual = RrEvent.DisconnectIndication;
                    break;
                case RrMessageKind.DisconnectConfirm:
                    actual = RrEvent.DisconnectConfirmation;
                    break;
                default:
                    actual = RrEvent.UnknownPort;
                    break;
            }

            _pending.Enqueue(new Pending(kind, connection, payload, sender));
            return RrStatus.Ok;
        }

        /// <summary>
        /// Asks a named server for a connection (RRPCCNRQ).
        /// </summary>
        /// <param name="systemName">
        /// The system the server runs on, or <c>null</c> for the local system. This implementation
        /// does not read it: the letter goes to the directory by server name only.
        /// </param>
        /// <param name="serverName">
        /// The server's name.
        /// </param>
        /// <param name="clientData">
        /// User data to send with the request, typically an identification the server will check.
        /// </param>
        /// <param name="connection">
        /// On return, always zero. The connection identifier is chosen by the server and is only
        /// known after <see cref="ConfirmConnection"/>.
        /// </param>
        /// <returns>
        /// OK when the letter was forwarded; the error code XENOP when the client is not
        /// initialised; the kernel's error when the client's magic number cannot be found; or the
        /// directory's error when the letter could not be forwarded.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serverName"/> is <c>null</c>.
        /// </exception>
        /// <remarks>
        /// The connection is not usable until the confirmation event has been received and
        /// processed.
        /// </remarks>
        public RrStatus RequestConnection(
            string? systemName,
            string serverName,
            ReadOnlySpan<byte> clientData,
            out int connection)
        {
            connection = 0;

            if (serverName == null)
            {
                throw new ArgumentNullException(nameof(serverName));
            }

            if (!IsInitialised)
            {
                return new RrStatus((int)XmsgError.XENOP);
            }

            XmsgMagicNumber mine;
            XmsgStatus converted = Kernel.ConvertPortToMagic(Port, out mine);
            if (converted.IsError)
            {
                return new RrStatus(converted.Value);
            }

            // The letter goes to XROUT BY NAME. We never learn the server's address here - the
            // server learns ours from the arriving letter and answers if it chooses to.
            byte[] letter = new byte[HeaderSize + clientData.Length];
            letter[0] = (byte)RrMessageKind.ConnectRequest;
            clientData.CopyTo(new Span<byte>(letter, HeaderSize, clientData.Length));

            XroutError forwarded = _directory.SendLetter(serverName, mine, letter);
            if (forwarded != XroutError.XRSOK)
            {
                return new RrStatus((int)forwarded);
            }

            // The connection is not usable until the confirmation arrives and names it.
            return RrStatus.Ok;
        }

        /// <summary>
        /// Completes connection establishment after the confirmation event (RRPCCNCF).
        /// </summary>
        /// <param name="connection">
        /// On return, the connection identifier the server chose, now recorded on this side too;
        /// zero when the server refused or nothing was waiting.
        /// </param>
        /// <param name="serverData">
        /// On return, any user data the server returned with its answer, accept or reject.
        /// </param>
        /// <returns>
        /// OK when the server accepted; the error code XRNSP when it refused; XENIM when the next
        /// message taken by <see cref="Wait"/> is not a connect answer.
        /// </returns>
        public RrStatus ConfirmConnection(out int connection, out byte[] serverData)
        {
            connection = 0;
            serverData = Array.Empty<byte>();

            Pending? item = TakeEither(RrMessageKind.ConnectAccept, RrMessageKind.ConnectReject);
            if (item == null)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            if (item.Kind == RrMessageKind.ConnectReject)
            {
                serverData = item.Payload;
                return new RrStatus((int)XroutError.XRNSP);
            }

            // Adopt the identifier the server chose, so both sides name the connection the same.
            connection = item.Connection;
            AddConnection(connection, item.Sender);
            serverData = item.Payload;
            return RrStatus.Ok;
        }

        /// <summary>
        /// Sends a request without waiting for the response (RRPCSNRQ).
        /// </summary>
        /// <param name="connection">
        /// The established connection to send on.
        /// </param>
        /// <param name="request">
        /// The request bytes.
        /// </param>
        /// <returns>
        /// OK when the request was sent; XEIMA when <paramref name="connection"/> is not open; or
        /// the kernel's error.
        /// </returns>
        /// <remarks>
        /// The request is sent as a secure message, so an undeliverable one comes back.
        /// </remarks>
        public RrStatus SendRequest(int connection, ReadOnlySpan<byte> request)
        {
            return SendFramed(connection, RrMessageKind.Request, request);
        }

        /// <summary>
        /// Takes the response after a response-indication event (RRPCGTRS).
        /// </summary>
        /// <param name="connection">
        /// On return, the connection the response arrived on; zero when there was none.
        /// </param>
        /// <param name="response">
        /// On return, the response bytes; empty when there was none.
        /// </param>
        /// <returns>
        /// OK, or XENIM when the next message taken by <see cref="Wait"/> is not a response.
        /// </returns>
        public RrStatus GetResponse(out int connection, out byte[] response)
        {
            connection = 0;
            response = Array.Empty<byte>();

            Pending? item = Take(RrMessageKind.Response);
            if (item == null)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            connection = item.Connection;
            response = item.Payload;
            return RrStatus.Ok;
        }

        /// <summary>
        /// Connects to a server in one call (the high-level select).
        /// </summary>
        /// <param name="systemName">
        /// The system the server runs on, or <c>null</c> for the local system. Passed to
        /// <see cref="RequestConnection"/>, which does not read it.
        /// </param>
        /// <param name="serverName">
        /// The server's name.
        /// </param>
        /// <param name="clientData">
        /// User data to send with the connection request.
        /// </param>
        /// <param name="serverData">
        /// On return, any user data the server returned.
        /// </param>
        /// <param name="connection">
        /// On return, the established connection.
        /// </param>
        /// <returns>
        /// The status of <see cref="ConfirmConnection"/>; the error from
        /// <see cref="RequestConnection"/>; or XENIM when the next message on the port is not a
        /// connection confirmation.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serverName"/> is <c>null</c>.
        /// </exception>
        /// <remarks>
        /// Runs a connection request, a wait and a connection confirmation back to back. The wait
        /// does not block, so this only succeeds when the server's answer is already queued when
        /// the request call returns.
        /// </remarks>
        public RrStatus Select(
            string? systemName,
            string serverName,
            ReadOnlySpan<byte> clientData,
            out byte[] serverData,
            out int connection)
        {
            serverData = Array.Empty<byte>();
            connection = 0;

            RrStatus requested = RequestConnection(systemName, serverName, clientData, out connection);
            if (!requested.IsOk)
            {
                return requested;
            }

            RrEvent actual;
            RrStatus waited = Wait(RrEvent.ConnectionConfirmation, TimeSpan.Zero, out actual);
            if (!waited.IsOk)
            {
                return waited;
            }

            if (actual != RrEvent.ConnectionConfirmation)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            return ConfirmConnection(out connection, out serverData);
        }

        /// <summary>
        /// Sends a request and returns its response in one call (the high-level send).
        /// </summary>
        /// <param name="connection">
        /// The established connection.
        /// </param>
        /// <param name="request">
        /// The request bytes.
        /// </param>
        /// <param name="response">
        /// On return, the response bytes.
        /// </param>
        /// <returns>
        /// The status of <see cref="GetResponse"/>; the error from <see cref="SendRequest"/>; or
        /// XENIM when the next message on the port is not a response.
        /// </returns>
        /// <remarks>
        /// The wait does not block, so this only succeeds when the response is already queued when
        /// the send returns.
        /// </remarks>
        public RrStatus Transact(int connection, ReadOnlySpan<byte> request, out byte[] response)
        {
            response = Array.Empty<byte>();

            RrStatus sent = SendRequest(connection, request);
            if (!sent.IsOk)
            {
                return sent;
            }

            RrEvent actual;
            RrStatus waited = Wait(RrEvent.ResponseIndication, TimeSpan.Zero, out actual);
            if (!waited.IsOk)
            {
                return waited;
            }

            if (actual != RrEvent.ResponseIndication)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            int answered;
            return GetResponse(out answered, out response);
        }

        /// <summary>
        /// Starts an orderly disconnect (RRPCDCRQ).
        /// </summary>
        /// <param name="connection">
        /// The connection to close.
        /// </param>
        /// <returns>
        /// The status of sending the disconnect request: OK, XEIMA when the connection is not
        /// open, or the kernel's error.
        /// </returns>
        /// <remarks>
        /// The connection is forgotten on this side whether or not the send worked.
        /// </remarks>
        public RrStatus RequestDisconnect(int connection)
        {
            RrStatus sent = SendFramed(connection, RrMessageKind.DisconnectRequest, ReadOnlySpan<byte>.Empty);
            RemoveConnection(connection);
            return sent;
        }

        /// <summary>
        /// Acknowledges a disconnect started by the server (RRPCDCIN).
        /// </summary>
        /// <param name="connection">
        /// On return, the connection that was closed; zero when there was none.
        /// </param>
        /// <returns>
        /// OK, or XENIM when the next message taken by <see cref="Wait"/> is not a disconnect
        /// request.
        /// </returns>
        /// <remarks>
        /// When the connection is still open here, a disconnect confirm is sent and the connection
        /// is forgotten; the result of that send is not reported.
        /// </remarks>
        public RrStatus AcceptDisconnectIndication(out int connection)
        {
            connection = 0;

            Pending? item = Take(RrMessageKind.DisconnectRequest);
            if (item == null)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            connection = item.Connection;

            XmsgMagicNumber peer;
            if (TryGetConnection(connection, out peer))
            {
                SendFramed(connection, RrMessageKind.DisconnectConfirm, ReadOnlySpan<byte>.Empty);
                RemoveConnection(connection);
            }

            return RrStatus.Ok;
        }

        /// <summary>
        /// Drops a connection immediately, without the orderly exchange (RRPCABRT).
        /// </summary>
        /// <param name="connection">
        /// The connection to abort.
        /// </param>
        /// <returns>
        /// Always OK.
        /// </returns>
        /// <remarks>
        /// Only this side forgets the connection; nothing is sent to the server.
        /// </remarks>
        public RrStatus Abort(int connection)
        {
            RemoveConnection(connection);
            return RrStatus.Ok;
        }

        /// <summary>
        /// Releases everything the client holds (RRPCEND).
        /// </summary>
        /// <returns>
        /// OK, or the kernel's error when closing the client's port fails.
        /// </returns>
        /// <remarks>
        /// Drops every message kept by <see cref="Wait"/>, forgets every connection and closes the
        /// client's port.
        /// </remarks>
        public RrStatus End()
        {
            _pending.Clear();
            return CloseOwnPort();
        }

        private Pending? Take(RrMessageKind kind)
        {
            if (_pending.Count == 0 || _pending.Peek().Kind != kind)
            {
                return null;
            }

            return _pending.Dequeue();
        }

        private Pending? TakeEither(RrMessageKind first, RrMessageKind second)
        {
            if (_pending.Count == 0)
            {
                return null;
            }

            RrMessageKind kind = _pending.Peek().Kind;
            if (kind != first && kind != second)
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
