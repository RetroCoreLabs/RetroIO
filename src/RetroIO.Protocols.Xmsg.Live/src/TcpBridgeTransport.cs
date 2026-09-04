using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Protocols.Xmsg.Live
{
    /// <summary>
    /// An <see cref="IByteDuplex"/> that connects to the <c>nd100x --hdlc</c> TCP bridge
    /// and pumps the transparent HDLC byte stream in both directions.
    /// </summary>
    /// <remarks>
    /// NOT-LIVE-TESTED: this class talks to a real socket and cannot be exercised against a
    /// live bridge in the unit-test suite. The bridge is a transparent byte pipe
    /// (XMSG-PROTOCOL.md section 1), so this transport only moves bytes - all framing and
    /// protocol logic lives in <see cref="LiveNode"/>, which IS tested over
    /// <see cref="InMemoryDuplex"/>.
    /// </remarks>
    public sealed class TcpBridgeTransport : IByteDuplex, IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        /// <summary>
        /// Initialises the transport around an already-connected TCP client.
        /// </summary>
        /// <param name="client">
        /// The connected TCP client whose stream carries the HDLC bytes.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="client"/> is null.
        /// </exception>
        public TcpBridgeTransport(TcpClient client)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            _client = client;
            _stream = client.GetStream();
        }

        /// <summary>
        /// Connects to the bridge at a host and port and returns a ready transport.
        /// </summary>
        /// <param name="host">
        /// The bridge host name or address.
        /// </param>
        /// <param name="port">
        /// The bridge TCP port.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that cancels the connect attempt.
        /// </param>
        /// <returns>
        /// A task yielding the connected transport.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="host"/> is null.
        /// </exception>
        public static async Task<TcpBridgeTransport> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            // NOT-LIVE-TESTED: real network connect; no bridge is available under test.
            TcpClient client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            return new TcpBridgeTransport(client);
        }

        /// <summary>
        /// Listens on a port and returns a transport for the first bridge that connects.
        /// </summary>
        /// <param name="port">
        /// The TCP port to listen on.
        /// </param>
        /// <param name="cancellationToken">
        /// A token that stops waiting for a connection.
        /// </param>
        /// <returns>
        /// A task yielding the accepted transport.
        /// </returns>
        /// <remarks>
        /// <para><b>Why this exists: to be in the MIDDLE</b></para>
        /// <see cref="ConnectAsync"/> dials out, which makes this node a leaf - it can only ever be
        /// an endpoint of somebody else's link. A relay has to be reachable, so at least one of its
        /// links must be one the peer dials INTO.
        /// <para>
        /// In the live setup that peer is D103, whose <c>RetroCore.ini</c> carries
        /// <c>device add HDLC 1 --connect=localhost:PORT</c>. Pointing that at us instead of at
        /// D100 puts this node between the two, which is the only arrangement in which our relay
        /// carries transit traffic at all.
        /// </para>
        /// <para><b>Ordering</b></para>
        /// The listener must be up before the peer starts, or its connect attempt fails. The
        /// listener is started before this method awaits, so a caller that has received the task
        /// can safely tell the operator to start the machine.
        /// <para>
        /// One connection only. The listener is stopped as soon as a bridge is accepted, because a
        /// second HDLC line to the same peer is not a topology this project models.
        /// </para>
        /// </remarks>
        public static async Task<TcpBridgeTransport> ListenAsync(int port, CancellationToken cancellationToken)
        {
            // NOT-LIVE-TESTED: real network accept; no bridge is available under test.
            TcpListener listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                return new TcpBridgeTransport(client);
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <inheritdoc/>
        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Stream.ReadAsync(byte[], int, int, CancellationToken) already returns Task<int>.
            return _stream.ReadAsync(buffer, offset, count, cancellationToken);
        }

        /// <inheritdoc/>
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            return _stream.WriteAsync(data, cancellationToken).AsTask();
        }

        /// <summary>
        /// Releases the underlying stream and socket.
        /// </summary>
        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }
    }
}
