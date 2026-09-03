using System;
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Link;
using RetroIO.Link.Framing;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// A minimal DCE-side X.25 peer for interop tests: a listening <see cref="TcpLinkTransport"/> plus an
    /// <see cref="XotFrameCodec"/> and an <see cref="X25Layer"/> that auto-accepts incoming calls and echoes
    /// any received data back on the same circuit. Built from the same clean stack as the client under test,
    /// so tests exercise real on-the-wire packets over a loopback TCP connection.
    /// </summary>
    internal sealed class LoopbackX25Peer : IX25PacketSink, IDisposable
    {
        private readonly TcpLinkTransport _transport;
        private readonly XotFrameCodec _codec;
        private readonly X25Layer _x25;

        /// <summary>
        /// The OS-assigned TCP port the peer is listening on.
        /// </summary>
        public int Port => _transport.Port;

        /// <summary>
        /// Starts the peer listening on an ephemeral loopback port.
        /// </summary>
        /// <param name="localAddress">
        /// The X.121 address the peer answers as.
        /// </param>
        public LoopbackX25Peer(string localAddress = "200")
        {
            _transport = new TcpLinkTransport(NullLogger.Instance, "peer", isServer: true, "127.0.0.1", 0);
            _codec = new XotFrameCodec("peer", new FrameSink(_transport));
            _x25 = new X25Layer(this, new X25Config { LocalAddress = localAddress, LciBase = 1, LciCount = 16 });

            // Bytes up: transport -> codec -> whole packets -> layer (all on the peer's read thread).
            _transport.DataReceived += (name, bytes) => _codec.ProcessBytes(bytes);
            _codec.FrameReceived += (id, frame) => _x25.ProcessPacket(frame);

            // Accept every incoming call, then echo every data packet straight back.
            _x25.IncomingCall += (layer, lci, called, calling, cud) => layer.AcceptCall(lci);
            _x25.DataReceived += (layer, lci, data, length, qBit, mBit, dBit) =>
            {
                byte[] echo = new byte[length];
                Array.Copy(data, 0, echo, 0, length);
                layer.SendData(lci, echo, qBit, mBit, dBit);
            };

            if (!_transport.Open())
            {
                throw new InvalidOperationException("LoopbackX25Peer could not start listening.");
            }
        }

        /// <inheritdoc/>
        public void SendPacket(ReadOnlySpan<byte> packet)
        {
            _codec.SendFrame(packet);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _transport.Close();
            _transport.Dispose();
        }

        /// <summary>
        /// Adapts the transport to the codec's send interface.
        /// </summary>
        private sealed class FrameSink : IFrameTransport
        {
            private readonly TcpLinkTransport _transport;

            /// <summary>
            /// Creates the sink over a transport.
            /// </summary>
            /// <param name="transport">
            /// The transport that writes framed bytes to the wire.
            /// </param>
            public FrameSink(TcpLinkTransport transport)
            {
                _transport = transport;
            }

            /// <inheritdoc/>
            public void Send(ReadOnlySpan<byte> bytes)
            {
                _transport.Write(bytes);
            }
        }
    }
}
