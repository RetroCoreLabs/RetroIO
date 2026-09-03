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

        /// <inheritdoc/>
        public RrStatus Initialise()
        {
            return OpenOwnPort();
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public RrStatus SendRequest(int connection, ReadOnlySpan<byte> request)
        {
            return SendFramed(connection, RrMessageKind.Request, request);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public RrStatus RequestDisconnect(int connection)
        {
            RrStatus sent = SendFramed(connection, RrMessageKind.DisconnectRequest, ReadOnlySpan<byte>.Empty);
            RemoveConnection(connection);
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

            XmsgMagicNumber peer;
            if (TryGetConnection(connection, out peer))
            {
                SendFramed(connection, RrMessageKind.DisconnectConfirm, ReadOnlySpan<byte>.Empty);
                RemoveConnection(connection);
            }

            return RrStatus.Ok;
        }

        /// <inheritdoc/>
        public RrStatus Abort(int connection)
        {
            RemoveConnection(connection);
            return RrStatus.Ok;
        }

        /// <inheritdoc/>
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
