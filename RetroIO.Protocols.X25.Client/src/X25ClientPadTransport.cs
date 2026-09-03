using System;
using RetroIO.Protocols.X25.Modules;

namespace RetroIO.Protocols.X25.Client
{
    /// <summary>
    /// An <see cref="IPadTransport"/> that drives a <see cref="Core.Modules.PadClient"/> over the clean
    /// <see cref="X25Client"/> (transport + XOT framing + packet layer), so the console PAD runs on the
    /// same clean X.25 stack as the other tools rather than on the legacy runtime.
    /// </summary>
    /// <remarks>
    /// The PAD is a single-circuit terminal, so the adapter tracks just the one virtual circuit it opened.
    /// The owner is responsible for feeding received events back to the PAD's delivery methods by wiring the
    /// client callbacks (see the console PAD program).
    /// </remarks>
    public sealed class X25ClientPadTransport : IPadTransport
    {
        private readonly X25Client _client;
        private X25Vc? _vc;

        /// <summary>
        /// Creates the adapter over an open <see cref="X25Client"/>.
        /// </summary>
        /// <param name="client">
        /// The client that carries the call.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="client"/> is null.
        /// </exception>
        public X25ClientPadTransport(X25Client client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>
        /// The virtual circuit the PAD is currently using, or null when disconnected.
        /// </summary>
        public X25Vc? Circuit => _vc;

        /// <inheritdoc/>
        public bool PlaceCall(string calledAddress, string callingAddress, byte[]? callUserData)
        {
            // The client uses its configured local address as the calling address, which matches the PAD's.
            _vc = _client.Call(calledAddress, callUserData);
            return _vc != null;
        }

        /// <inheritdoc/>
        public bool SendData(ushort lcn, byte[] data, bool mBit, bool qBit, bool dBit)
        {
            X25Vc? vc = _vc;
            if (vc == null)
            {
                return false;
            }

            return _client.Send(vc, data, data.Length, qBit);
        }

        /// <inheritdoc/>
        public void ForwardData(ushort lcn, byte[] data, int length)
        {
            X25Vc? vc = _vc;
            if (vc != null)
            {
                _client.Send(vc, data, length, false);
            }
        }

        /// <inheritdoc/>
        public void ClearCall(ushort lcn, byte cause, byte diagnostic)
        {
            X25Vc? vc = _vc;
            if (vc != null)
            {
                _client.Clear(vc, cause, diagnostic);
                _vc = null;
            }
        }
    }
}
