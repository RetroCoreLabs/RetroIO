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
    /// A request-response client: connects to a server by name, sends requests, reads responses.
    /// </summary>
    /// <remarks>
    /// Implements the client half of chapter 4 over <see cref="XmsgKernel"/> and
    /// <see cref="XroutDirectory"/>. Both levels the manual describes are here: the low-level calls
    /// for a program that must keep several requests outstanding, and the high-level select and
    /// transact calls, which fold a whole sequence into one blocking call and are what the
    /// manual recommends when their restrictions are acceptable.
    /// The wire framing is ours, not ND's - see <see cref="RrMessageKind"/>.
    /// </remarks>
    public sealed class RrClient : RrEndpointBase, IRrClient
    {
        private readonly XroutDirectory _directory;

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
        /// Waits for the next event the client asked for (RRPBWAIT).
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
        /// <see cref="GetResponse"/>) can pick it up. The rules are those of the shared
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
                case RrMessageKind.ConnectAccept:
                case RrMessageKind.ConnectReject:
                    return RrEvent.ConnectionConfirmation;
                case RrMessageKind.Response:
                    return RrEvent.ResponseIndication;
                case RrMessageKind.DisconnectRequest:
                    return RrEvent.DisconnectIndication;
                case RrMessageKind.DisconnectConfirm:
                    return RrEvent.DisconnectConfirmation;
                default:
                    return RrEvent.UnknownPort;
            }
        }

        /// <summary>
        /// Asks a named server for a connection (RRPCCNRQ).
        /// </summary>
        /// <param name="systemName">
        /// The system the server runs on, or <c>null</c> (or empty) for the local system. A named
        /// system is not implemented: the letter goes to the directory by server name only.
        /// </param>
        /// <param name="serverName">
        /// The server's name.
        /// </param>
        /// <param name="clientData">
        /// User data to send with the request, typically an identification the server will check.
        /// </param>
        /// <param name="connection">
        /// On return, the reference for this connection, to be used in the later calls about it
        /// (the guide's remoteID). It is never zero.
        /// </param>
        /// <returns>
        /// OK when the letter was forwarded; the error code XENOP when the client is not
        /// initialised; the kernel's error when the client's magic number cannot be found; or the
        /// directory's error when the letter could not be forwarded.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serverName"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="systemName"/> names a system: reaching a server on another
        /// system is not implemented, and nothing is sent.
        /// </exception>
        /// <remarks>
        /// <para>
        /// Guide, routine RRPCcnrq: remoteID is the "server reference number to be used in
        /// subsequent calls", written by the call itself. Chapter 4 section 4.3 puts that call at
        /// step 1, before the server has done anything, so the number cannot come from the server;
        /// it is handed out here, by the client's own library. The import list of RRP-B00 agrees:
        /// RRPCcnrq has the identifier as a write parameter, while the server calls read the
        /// identifier from the event. RRanyRemote is 0, so the reference is never 0.
        /// </para>
        /// <para>
        /// The connection is not usable until the confirmation event has been received and
        /// processed. The server numbers its side of the connection itself; both references travel
        /// in every message, so each side hears events under its own.
        /// </para>
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

            if (!string.IsNullOrEmpty(systemName))
            {
                throw new NotSupportedException(
                    "RrClient.RequestConnection to a named system is not implemented: the letter is routed by server name through the local directory only.");
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
            // The reference is ours to hand out, and it goes in the letter so the server's answer
            // can name it. The receiver's reference is 0 because the server has none yet.
            int reference = AllocateReference();
            byte[] letter = new byte[HeaderSize + clientData.Length];
            WriteFramedHeader(letter, RrMessageKind.ConnectRequest, 0, reference);
            clientData.CopyTo(new Span<byte>(letter, HeaderSize, clientData.Length));

            XroutError forwarded = _directory.SendLetter(serverName, mine, letter);
            if (forwarded != XroutError.XRSOK)
            {
                return new RrStatus((int)forwarded);
            }

            // The connection is not usable until the confirmation arrives; the reference is
            // already ours to give back.
            connection = reference;
            return RrStatus.Ok;
        }

        /// <summary>
        /// Completes connection establishment after the confirmation event (RRPCCNCF).
        /// </summary>
        /// <param name="connection">
        /// On return, the reference <see cref="RequestConnection"/> gave for this connection, now
        /// open; zero when the server refused or nothing was waiting.
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

            Pending? item = TakeTakenEither(RrMessageKind.ConnectAccept, RrMessageKind.ConnectReject);
            if (item == null)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            if (item.Kind == RrMessageKind.ConnectReject)
            {
                serverData = item.Payload;
                return new RrStatus((int)XroutError.XRNSP);
            }

            // The answer names our reference (the receiver's field) and carries the server's own
            // number for the connection, which every later message to the server must carry.
            connection = item.Connection;
            AddConnection(connection, item.Sender, item.PeerReference);
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

            Pending? item = TakeTaken(RrMessageKind.Response);
            if (item == null)
            {
                return new RrStatus((int)XmsgError.XENIM);
            }

            connection = item.Connection;
            response = item.Payload;
            return RrStatus.Ok;
        }

        /// <summary>
        /// Connects to a server in one call and does not wait for it (the high-level select).
        /// </summary>
        /// <param name="systemName">
        /// The system the server runs on, or <c>null</c> (or empty) for the local system. A named
        /// system is refused by <see cref="RequestConnection"/>, because it is not implemented.
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
        /// The same as the version with a timeout, called with a timeout of zero.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serverName"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="systemName"/> names a system: reaching a server on another
        /// system is not implemented.
        /// </exception>
        public RrStatus Select(
            string? systemName,
            string serverName,
            ReadOnlySpan<byte> clientData,
            out byte[] serverData,
            out int connection)
        {
            return Select(systemName, serverName, clientData, TimeSpan.Zero, out serverData, out connection);
        }

        /// <summary>
        /// Connects to a server in one call, waiting up to a time for its answer (RRPCSLCT).
        /// </summary>
        /// <param name="systemName">
        /// The system the server runs on, or <c>null</c> (or empty) for the local system. A named
        /// system is refused by <see cref="RequestConnection"/>, because it is not implemented.
        /// </param>
        /// <param name="serverName">
        /// The server's name.
        /// </param>
        /// <param name="clientData">
        /// User data to send with the connection request.
        /// </param>
        /// <param name="timeout">
        /// How long to wait for the server's answer.
        /// </param>
        /// <param name="serverData">
        /// On return, any user data the server returned.
        /// </param>
        /// <param name="connection">
        /// On return, the established connection.
        /// </param>
        /// <returns>
        /// The status of <see cref="ConfirmConnection"/>, or the error from
        /// <see cref="RequestConnection"/>, or one of the codes the guide's definitions file lists
        /// for this call: RRErnttm when the time ran out, RRErdscn when the server disconnected,
        /// RRErunev for any other event.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="serverName"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when <paramref name="systemName"/> names a system: reaching a server on another
        /// system is not implemented.
        /// </exception>
        /// <remarks>
        /// RRP-B00 DEFS, "CODES SPECIFIC to RRPCslct/RRPCcall/RRPCdisc": 17296 not terminated
        /// (timeout), 17297 disconnected, 17298 unexpected event while waiting for the reply. The
        /// guide says the call is a connection request, a wait and a connection confirmation done
        /// "synchronously, i.e., the return does not occur until timeout or until a response is
        /// received from the server".
        /// </remarks>
        public RrStatus Select(
            string? systemName,
            string serverName,
            ReadOnlySpan<byte> clientData,
            TimeSpan timeout,
            out byte[] serverData,
            out int connection)
        {
            serverData = Array.Empty<byte>();
            connection = 0;

            RrStatus requested = RequestConnection(systemName, serverName, clientData, out connection);
            if (!requested.IsOk)
            {
                connection = 0;
                return requested;
            }

            RrEvent actual;
            RrStatus waited = Wait(RrEvent.ConnectionConfirmation | RrEvent.DisconnectIndication, timeout, out actual);
            if (!waited.IsOk)
            {
                connection = 0;
                return waited;
            }

            if (actual != RrEvent.ConnectionConfirmation)
            {
                connection = 0;
                return new RrStatus((int)EventToCallError(actual));
            }

            return ConfirmConnection(out connection, out serverData);
        }

        /// <summary>
        /// Sends a request and returns its response in one call, without waiting for it (the
        /// high-level send).
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
        /// The same as the version with a timeout, called with a timeout of zero.
        /// </returns>
        public RrStatus Transact(int connection, ReadOnlySpan<byte> request, out byte[] response)
        {
            return Transact(connection, request, TimeSpan.Zero, out response);
        }

        /// <summary>
        /// Sends a request and returns its response in one call, waiting up to a time for it
        /// (RRPCCALL).
        /// </summary>
        /// <param name="connection">
        /// The established connection.
        /// </param>
        /// <param name="request">
        /// The request bytes.
        /// </param>
        /// <param name="timeout">
        /// How long to wait for the response.
        /// </param>
        /// <param name="response">
        /// On return, the response bytes.
        /// </param>
        /// <returns>
        /// The status of <see cref="GetResponse"/>, or the error from <see cref="SendRequest"/>,
        /// or one of the codes the guide's definitions file lists for this call: RRErnttm when the
        /// time ran out, RRErdscn when the server disconnected, RRErunev for any other event.
        /// </returns>
        /// <remarks>
        /// The guide: "This call is equivalent to RRPCsnrq+RRPBwait+RRPCgtrs." The codes are
        /// those of <see cref="Select(string, string, ReadOnlySpan{byte}, TimeSpan, out byte[], out int)"/>.
        /// </remarks>
        public RrStatus Transact(int connection, ReadOnlySpan<byte> request, TimeSpan timeout, out byte[] response)
        {
            response = Array.Empty<byte>();

            RrStatus sent = SendRequest(connection, request);
            if (!sent.IsOk)
            {
                return sent;
            }

            RrEvent actual;
            RrStatus waited = Wait(RrEvent.ResponseIndication | RrEvent.DisconnectIndication, timeout, out actual);
            if (!waited.IsOk)
            {
                return waited;
            }

            if (actual != RrEvent.ResponseIndication)
            {
                return new RrStatus((int)EventToCallError(actual));
            }

            int answered;
            return GetResponse(out answered, out response);
        }

        // The status the one-call routines give when the event they waited for did not come.
        private static RrError EventToCallError(RrEvent actual)
        {
            switch (actual)
            {
                case RrEvent.Timeout:
                    return RrError.RRErnttm;
                case RrEvent.DisconnectIndication:
                    return RrError.RRErdscn;
                default:
                    return RrError.RRErunev;
            }
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

            Pending? item = TakeTaken(RrMessageKind.DisconnectRequest);
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
            ClearPending();
            return CloseOwnPort();
        }
    }
}
